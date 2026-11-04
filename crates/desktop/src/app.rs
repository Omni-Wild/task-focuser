//! Shared native controller. Window hosts apply the resulting desktop operations.

use std::{
    collections::BTreeMap,
    time::{Duration, Instant},
};

use crate::surface::{Services, Startup, SurfaceChange, SurfaceRole};
use iced::{
    Element, Event, Subscription, Task,
    keyboard::{self, Key, key},
    mouse,
    widget::text_editor,
    window,
};
use task_focuser_core::{
    audio::{IdleSoundScheduler, SoundKind},
    companion::{Companion, CompanionEvent},
    conductor::{Command, Conductor, DragTarget, Effect},
    geometry::{self, Monitor, NoteSizing, Point, Rect},
    sprites::BirdId,
    state::{FONTS, IdleRate, IdleSounds, MAX_FONT_SIZE, MIN_FONT_SIZE, Mode, NoteState},
    store::{StateStore, StoreError},
};

pub const PAD: f64 = 12.0;
pub const OVERHANG: f64 = 16.0;
pub const BAR_WIDTH: f64 = 438.0;
pub const EDITOR_ID: &str = "note-editor";
pub const RENAME_ID: &str = "bird-name";

#[derive(Debug, Clone)]
pub enum Message {
    Event(SurfaceRole, Event),
    SurfaceOpened(SurfaceRole, window::Id),
    SurfaceClosed(SurfaceRole),
    Monitors(Vec<Monitor>),
    HostMoved(SurfaceRole, Point),
    FocusEditor,
    Command(Command),
    Edit(text_editor::Action),
    Undo(bool),
    Tick(Instant),
    Forwarded(Vec<String>),
    TrayAction(task_focuser_platform::tray::TrayAction),
    ServiceError(String),
    Save(u64),
    Press(SurfaceRole),
    DoubleClick(SurfaceRole),
    Release,
    ResizePress,
    CucumberPress,
    Color(&'static str),
    Opacity(u8),
    Font(i32),
    FontSize(i16),
    ToggleStays,
    ToggleOpaque,
    ToggleSound,
    TogglePlants,
    VolumeHover(bool),
    VolumePopupHover(bool),
    OpenVolume(u64),
    CloseVolume(u64),
    Volume(f64),
    IdleSounds(IdleSounds),
    IdleRate(IdleRate),
    TogglePicker,
    PickBird(&'static str),
    Rename(&'static str),
    RenameText(String),
    FinishRename(bool),
    BirdPress,
    BirdHover(bool),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum GestureKind {
    Note,
    Bird,
    Dot,
    Resize,
    Cucumber,
}
#[derive(Debug, Clone, Copy)]
struct Gesture {
    kind: GestureKind,
    origin: Point,
    initial: Rect,
    active: bool,
}
#[derive(Debug, Clone)]
struct UndoEntry {
    text: String,
    cursor: text_editor::Cursor,
}
#[derive(Debug, Clone, Copy)]
enum TransitionKind {
    Dock(Point),
    Pop(Point),
    Vanish(Point),
}
#[derive(Debug, Clone, Copy)]
struct Transition {
    kind: TransitionKind,
    paper: Rect,
    started: Instant,
    duration: Duration,
}
#[derive(Debug, Clone, Copy)]
struct LayoutAnimation {
    from: Rect,
    to: Rect,
    from_unfold: f64,
    to_unfold: f64,
    started: Instant,
    duration: Duration,
}
#[derive(Debug, Clone, Copy)]
struct LeafAnimation {
    from: f64,
    to: f64,
    started: Instant,
    duration: Duration,
}

#[derive(Clone)]
struct ServiceEvents(
    std::sync::Arc<
        std::sync::Mutex<Option<iced::futures::channel::mpsc::UnboundedReceiver<Message>>>,
    >,
);
impl std::hash::Hash for ServiceEvents {
    fn hash<H: std::hash::Hasher>(&self, state: &mut H) {
        std::sync::Arc::as_ptr(&self.0).hash(state);
    }
}
fn service_stream(events: &ServiceEvents) -> iced::futures::stream::BoxStream<'static, Message> {
    use iced::futures::StreamExt;
    match events.0.lock() {
        Ok(mut receiver) => match receiver.take() {
            Some(receiver) => receiver.boxed(),
            None => iced::futures::stream::empty().boxed(),
        },
        Err(error) => iced::futures::stream::once(std::future::ready(Message::ServiceError(
            error.to_string(),
        )))
        .boxed(),
    }
}
fn service_bridge(services: &mut Services) -> ServiceEvents {
    let (sender, receiver) = iced::futures::channel::mpsc::unbounded();
    let ipc = services.instance.take_receiver();
    let tray = std::mem::replace(&mut services.tray_actions, std::sync::mpsc::channel().1);
    let ipc_sender = sender.clone();
    if let Err(error) = std::thread::Builder::new()
        .name("focus-commands".into())
        .spawn(move || {
            while let Ok(arguments) = ipc.recv() {
                if ipc_sender
                    .unbounded_send(Message::Forwarded(arguments))
                    .is_err()
                {
                    break;
                }
            }
        })
    {
        let _ = sender.unbounded_send(Message::ServiceError(format!(
            "Could not listen for commands: {error}"
        )));
    }
    let tray_sender = sender.clone();
    if let Err(error) = std::thread::Builder::new()
        .name("focus-tray".into())
        .spawn(move || {
            while let Ok(action) = tray.recv() {
                if tray_sender
                    .unbounded_send(Message::TrayAction(action))
                    .is_err()
                {
                    break;
                }
            }
        })
    {
        let _ = sender.unbounded_send(Message::ServiceError(format!(
            "Could not listen to the tray: {error}"
        )));
    }
    let diagnostics = services.sound.take_diagnostics();
    let sound_sender = sender.clone();
    if let Err(error) = std::thread::Builder::new()
        .name("focus-audio-errors".into())
        .spawn(move || {
            while let Ok(diagnostic) = diagnostics.recv() {
                if sound_sender
                    .unbounded_send(Message::ServiceError(diagnostic))
                    .is_err()
                {
                    break;
                }
            }
        })
    {
        let _ = sender.unbounded_send(Message::ServiceError(format!(
            "Could not report sound errors: {error}"
        )));
    }
    ServiceEvents(std::sync::Arc::new(std::sync::Mutex::new(Some(receiver))))
}

pub struct App {
    pub(crate) conductor: Conductor,
    pub(crate) editor: text_editor::Content,
    pub(crate) monitors: Vec<Monitor>,
    store: StateStore,
    services: Services,
    service_events: ServiceEvents,
    last_tray_state: Option<task_focuser_platform::tray::TrayState>,
    pub(crate) companion: Companion,
    idle_sound: IdleSoundScheduler,
    last_peep: Instant,
    pending_peep: bool,
    last_perch: Point,
    bird_wanted: bool,
    typing_chain: u8,
    typing_at: Instant,
    random: u64,
    pub(crate) windows: BTreeMap<SurfaceRole, window::Id>,
    pub(crate) surfaces: BTreeMap<SurfaceRole, Rect>,
    pub(crate) dot_target: Option<DragTarget>,
    pub(crate) close_target: Option<DragTarget>,
    cursors: BTreeMap<SurfaceRole, Point>,
    gesture: Option<Gesture>,
    save_generation: u64,
    saved_generation: u64,
    pub(crate) error: Option<String>,
    pub(crate) volume_open: bool,
    volume_hover: bool,
    popup_hover: bool,
    volume_generation: u64,
    pub(crate) picker_open: bool,
    pub(crate) renaming: Option<&'static str>,
    pub(crate) rename_text: String,
    undo: Vec<UndoEntry>,
    redo: Vec<UndoEntry>,
    last_edit: Option<Instant>,

    last_tick: Instant,
    transition: Option<Transition>,
    layout_animation: Option<LayoutAnimation>,
    shake_started: Option<Instant>,
    leaf_animation: Option<LeafAnimation>,
    save_deadline: Option<Instant>,
    pub(crate) cucumber_fall: Option<(Point, Instant)>,
}

impl App {
    pub fn new(mut startup: Startup) -> (Self, Vec<SurfaceChange>) {
        let editor = text_editor::Content::with_text(&startup.state.text);
        let perch = Point::new(startup.state.x + 32., startup.state.y);
        let companion = Companion::new(BirdId::from_id(&startup.state.bird), perch);
        let seed = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(1, |time| time.as_nanos() as u64);
        let mut idle_sound = IdleSoundScheduler::new(seed);
        idle_sound.configure(
            startup.state.sound_on,
            startup.state.idle_rate,
            startup.state.idle_sounds,
        );
        let service_events = service_bridge(&mut startup.services);
        let diagnostics = startup.services.diagnostics.join("\n");
        let monitors = if startup.monitors.is_empty() {
            vec![Monitor::new(
                geometry::FALLBACK_WORK_AREA,
                geometry::FALLBACK_WORK_AREA,
            )]
        } else {
            startup.monitors
        };
        let mut app = Self {
            conductor: Conductor::new(startup.state),
            editor,
            monitors: monitors.clone(),
            store: startup.store,
            services: startup.services,
            service_events,
            last_tray_state: None,
            companion,
            idle_sound,
            last_peep: Instant::now() - Duration::from_secs(1),
            pending_peep: false,
            last_perch: perch,
            bird_wanted: false,
            typing_chain: 0,
            typing_at: Instant::now(),
            random: seed.max(1),
            windows: BTreeMap::new(),
            surfaces: BTreeMap::new(),
            dot_target: None,
            close_target: None,
            cursors: BTreeMap::new(),
            gesture: None,
            save_generation: 0,
            saved_generation: 0,
            error: (!diagnostics.is_empty()).then_some(diagnostics),
            volume_open: false,
            volume_hover: false,
            popup_hover: false,
            volume_generation: 0,
            picker_open: false,
            renaming: None,
            rename_text: String::new(),
            undo: Vec::new(),
            redo: Vec::new(),
            last_edit: None,
            last_tick: Instant::now(),
            transition: None,
            layout_animation: None,
            shake_started: None,
            leaf_animation: None,
            save_deadline: None,
            cucumber_fall: None,
        };
        app.conductor.set_monitors(monitors);
        app.measure(false);
        let effects = app.conductor.restore();
        let mut changes = Vec::new();
        app.effects(effects, &mut changes);
        for command in startup.commands {
            let effects = app.conductor.handle(command);
            app.effects(effects, &mut changes);
        }
        app.sync_companion();
        app.sync_surfaces(&mut changes);
        (app, changes)
    }

    pub fn state(&self) -> &NoteState {
        self.conductor.state()
    }
    pub fn flush(&mut self) -> Result<(), StoreError> {
        self.store.flush(self.conductor.state())?;
        self.saved_generation = self.save_generation;
        self.save_deadline = None;
        Ok(())
    }

    pub fn view(&self, role: SurfaceRole) -> Element<'_, Message> {
        crate::view::view(self, role)
    }

    pub fn subscription(&self) -> Subscription<Message> {
        let mut interval = self.companion.next_update_in();
        if let Some(sound) = self.idle_sound.deadline_in() {
            interval = Some(interval.map_or(sound, |current| current.min(sound)));
        }
        if self.gesture.is_some()
            || self.cucumber_fall.is_some()
            || self.pending_peep
            || self.transition.is_some()
            || self.layout_animation.is_some()
            || self.shake_started.is_some()
            || self.leaf_animation.is_some()
        {
            interval = Some(Duration::from_millis(16));
        }
        if let Some(deadline) = self.save_deadline {
            let wait = deadline.saturating_duration_since(Instant::now());
            interval = Some(interval.map_or(wait, |current| current.min(wait)));
        }
        let frames = interval.map_or_else(Subscription::none, |interval| {
            iced::time::every(interval.max(Duration::from_millis(1))).map(Message::Tick)
        });
        Subscription::batch([
            frames,
            Subscription::run_with(self.service_events.clone(), service_stream),
        ])
    }

    pub fn update(&mut self, message: Message) -> (Task<Message>, Vec<SurfaceChange>) {
        self.advance(Instant::now());
        let mut changes = Vec::new();
        let mut tasks = Vec::new();
        let mut changed = false;
        let had_gesture = self.gesture.is_some_and(|gesture| {
            gesture.active && matches!(gesture.kind, GestureKind::Note | GestureKind::Bird)
        });
        let was_locked = self.state().locked;
        let old_paper = self.paper_bounds();
        let old_unfold = self.unfold();
        let old_plants = self.state().plants_on;
        let old_leaves = self.vines_extent();
        let old_audio = (
            self.state().sound_on,
            self.state().idle_rate,
            self.state().idle_sounds,
            self.state().volume,
        );
        match message {
            Message::SurfaceOpened(role, id) => {
                self.windows.insert(role, id);
                if role == SurfaceRole::Note && !self.state().locked && self.gesture.is_none() {
                    changes.push(SurfaceChange::Focus(SurfaceRole::Note));
                    tasks.push(delay(Duration::from_millis(15), Message::FocusEditor));
                }
            }
            Message::FocusEditor
                if self.state().mode == Mode::Note
                    && !self.state().locked
                    && self.renaming.is_none()
                    && self.gesture.is_none() =>
            {
                tasks.push(self.focus_editor());
            }
            Message::HostMoved(role, position) => {
                if let Some(bounds) = self.surfaces.get_mut(&role) {
                    bounds.x = position.x;
                    bounds.y = position.y;
                }
                match role {
                    SurfaceRole::Note => {
                        let mut bounds = self.conductor.note_bounds();
                        bounds.x = position.x + PAD;
                        bounds.y = position.y + PAD;
                        self.conductor.set_note_bounds(bounds);
                        changed = true;
                    }
                    SurfaceRole::Dot if self.state().mode == Mode::Dot => {
                        changed |= self
                            .conductor
                            .adopt_dot_position(Point::new(position.x + 48., position.y + 48.));
                    }
                    _ => {}
                }
            }
            Message::SurfaceClosed(role) => {
                self.windows.remove(&role);
            }
            Message::Monitors(monitors) => {
                if !monitors.is_empty() {
                    self.monitors = monitors.clone();
                    self.conductor.set_monitors(monitors);
                    let effects = self.conductor.display_changed();
                    self.effects(effects, &mut changes);
                    self.measure(false);
                }
            }
            Message::Command(command) => {
                self.close_popups();
                let effects = self.handle_command(command);
                changed = effects.contains(&Effect::Persist);
                self.effects(effects, &mut changes);
                self.measure(true);
            }
            Message::Edit(action) if !self.state().locked => {
                if matches!(action, text_editor::Action::Click(_)) {
                    changed |= self.finish_rename(true);
                    self.picker_open = false;
                }
                if self.renaming.is_some() {
                    return (Task::none(), changes);
                }
                let before = self.editor.text();
                let cursor = self.editor.cursor();
                let edit = action.is_edit();
                if matches!(action, text_editor::Action::Scroll { .. }) {
                    self.reset_editor_scroll();
                } else {
                    self.editor.perform(action);
                }
                let after = self.editor.text();
                if before != after {
                    if edit
                        && self
                            .last_edit
                            .is_none_or(|time| time.elapsed() > Duration::from_millis(500))
                    {
                        self.undo.push(UndoEntry {
                            text: before,
                            cursor,
                        });
                        if self.undo.len() > 200 {
                            self.undo.remove(0);
                        }
                    }
                    self.last_edit = Some(Instant::now());
                    self.redo.clear();
                    self.conductor.state_mut().text = after;
                    self.measure(true);
                    self.key_typed();
                    changed = true;
                }
            }
            Message::Undo(redo) if !self.state().locked => {
                let entry = if redo {
                    self.redo.pop()
                } else {
                    self.undo.pop()
                };
                if let Some(entry) = entry {
                    let current = UndoEntry {
                        text: self.editor.text(),
                        cursor: self.editor.cursor(),
                    };
                    if redo {
                        self.undo.push(current);
                    } else {
                        self.redo.push(current);
                    }
                    self.editor = text_editor::Content::with_text(&entry.text);
                    self.editor.move_to(entry.cursor);
                    self.conductor.state_mut().text = entry.text;
                    self.last_edit = None;
                    self.measure(true);
                    changed = true;
                }
            }
            Message::Event(role, event) => match event {
                Event::Mouse(mouse::Event::CursorMoved { position }) => {
                    let bounds = self.surfaces.get(&role).copied().unwrap_or_default();
                    let cursor = Point::new(
                        bounds.x + f64::from(position.x),
                        bounds.y + f64::from(position.y),
                    );
                    self.cursors.insert(role, cursor);
                    if self.gesture.is_some() {
                        self.move_gesture(cursor, &mut changes);
                    }
                }
                Event::Mouse(mouse::Event::ButtonReleased(mouse::Button::Left)) => {
                    changed |= self.end_gesture(&mut changes, true);
                }
                Event::Window(window::Event::Focused)
                    if role == SurfaceRole::Note
                        && !self.state().locked
                        && self.renaming.is_none()
                        && self.gesture.is_none() =>
                {
                    tasks.push(delay(Duration::from_millis(15), Message::FocusEditor));
                }
                Event::Window(window::Event::CloseRequested) if role == SurfaceRole::Note => {
                    let effects = self.conductor.handle(Command::Lock);
                    self.effects(effects, &mut changes);
                    self.close_popups();
                    self.measure(true);
                    changed = true;
                }
                Event::Window(window::Event::Unfocused) => {
                    changed |= self.finish_rename(true);
                    self.close_popups();
                    changed |= self.end_gesture(&mut changes, false);
                }
                Event::Keyboard(keyboard::Event::KeyPressed { key, modifiers, .. })
                    if role == SurfaceRole::Note =>
                {
                    if self.renaming.is_some() {
                        if key == Key::Named(key::Named::Escape) {
                            self.finish_rename(false);
                            tasks.push(self.focus_editor());
                        }
                    } else if !self.state().locked
                        && (key == Key::Named(key::Named::Escape)
                            || (modifiers.command() && key.as_ref() == Key::Character("l")))
                    {
                        let effects = self.conductor.handle(Command::Lock);
                        self.effects(effects, &mut changes);
                        self.close_popups();
                        self.measure(true);
                        changed = true;
                    }
                }
                _ => {}
            },
            Message::Press(role) => {
                let kind = match role {
                    SurfaceRole::Note => Some(GestureKind::Note),
                    SurfaceRole::Dot => Some(GestureKind::Dot),
                    _ => None,
                };
                if let Some(kind) = kind {
                    self.start_gesture(kind, role);
                }
            }
            Message::DoubleClick(role) => {
                self.gesture = None;
                let command = match role {
                    SurfaceRole::Note => Command::ToggleLock,
                    SurfaceRole::Dot => Command::Show,
                    _ => Command::Show,
                };
                let effects = self.handle_command(command);
                self.effects(effects, &mut changes);
                self.close_popups();
                self.measure(true);
                changed = true;
            }
            Message::Release => changed |= self.end_gesture(&mut changes, true),
            Message::ResizePress if !self.state().locked => {
                self.start_gesture(GestureKind::Resize, SurfaceRole::Note)
            }
            Message::CucumberPress if !self.state().locked => {
                self.start_gesture(GestureKind::Cucumber, SurfaceRole::Note)
            }
            Message::Color(color) => {
                self.conductor.state_mut().color = color.into();
                changed = true;
            }
            Message::Opacity(opacity) => {
                self.conductor.state_mut().opacity = opacity.clamp(20, 100);
                changed = true;
            }
            Message::Font(step) => {
                let index = FONTS
                    .iter()
                    .position(|font| *font == self.state().font)
                    .unwrap_or(0) as i32;
                self.conductor.state_mut().font =
                    FONTS[(index + step).rem_euclid(FONTS.len() as i32) as usize].into();
                self.measure(true);
                changed = true;
            }
            Message::FontSize(step) => {
                self.conductor.state_mut().font_size = (self.state().font_size as i16 + step)
                    .clamp(MIN_FONT_SIZE as i16, MAX_FONT_SIZE as i16)
                    as u16;
                self.measure(true);
                changed = true;
            }
            Message::ToggleStays => {
                self.conductor.state_mut().bird_stays = !self.state().bird_stays;
                changed = true;
            }
            Message::ToggleOpaque => {
                self.conductor.state_mut().bird_opaque = !self.state().bird_opaque;
                changed = true;
            }
            Message::ToggleSound => {
                self.conductor.state_mut().sound_on = !self.state().sound_on;
                changed = true;
            }
            Message::TogglePlants => {
                self.conductor.state_mut().plants_on = !self.state().plants_on;
                changed = true;
            }
            Message::VolumeHover(hover) => {
                self.volume_hover = hover;
                self.volume_generation += 1;
                let generation = self.volume_generation;
                if hover && !self.volume_open && !self.picker_open {
                    tasks.push(Task::perform(
                        async move {
                            tokio::time::sleep(Duration::from_millis(250)).await;
                        },
                        move |_| Message::OpenVolume(generation),
                    ));
                } else if !hover {
                    tasks.push(delay(
                        Duration::from_millis(300),
                        Message::CloseVolume(generation),
                    ));
                }
            }
            Message::VolumePopupHover(hover) => {
                self.popup_hover = hover;
                self.volume_generation += 1;
                if !hover {
                    tasks.push(delay(
                        Duration::from_millis(300),
                        Message::CloseVolume(self.volume_generation),
                    ));
                }
            }
            Message::OpenVolume(generation)
                if generation == self.volume_generation
                    && self.volume_hover
                    && !self.state().locked
                    && !self.picker_open =>
            {
                self.volume_open = true;
            }
            Message::CloseVolume(generation)
                if generation == self.volume_generation
                    && !self.volume_hover
                    && !self.popup_hover =>
            {
                self.volume_open = false;
            }
            Message::Volume(position) => {
                self.conductor.state_mut().volume =
                    (task_focuser_core::audio::slider_to_volume(position) * 1000.).round() / 1000.;
                if let Err(error) = self.services.sound.set_volume(self.state().volume as f32) {
                    self.report(error.to_string());
                }
                if self.state().sound_on {
                    if self.last_peep.elapsed() >= Duration::from_millis(150) {
                        self.play(SoundKind::Peep);
                        self.last_peep = Instant::now();
                    } else {
                        self.pending_peep = true;
                    }
                }
                changed = true;
            }
            Message::IdleSounds(sounds) => {
                self.conductor.state_mut().idle_sounds = sounds;
                changed = true;
            }
            Message::IdleRate(rate) => {
                self.conductor.state_mut().idle_rate = rate;
                changed = true;
            }
            Message::TogglePicker => {
                changed |= self.finish_rename(true);
                self.picker_open = !self.picker_open;
                self.volume_open = false;
            }
            Message::PickBird(bird) => {
                self.finish_rename(true);
                self.conductor.state_mut().bird = bird.into();
                self.picker_open = false;
                changed = true;
            }
            Message::Rename(bird) => {
                changed |= self.finish_rename(true);
                self.renaming = Some(bird);
                self.rename_text = self.state().bird_name(bird).to_owned();
                tasks.push(iced::widget::operation::focus(RENAME_ID));
                tasks.push(iced::widget::operation::select_all(RENAME_ID));
            }
            Message::RenameText(name) => {
                self.rename_text = name.chars().take(14).collect();
            }
            Message::FinishRename(commit) => {
                changed |= self.finish_rename(commit);
                tasks.push(self.focus_editor());
            }
            Message::BirdPress => self.start_gesture(GestureKind::Bird, SurfaceRole::Bird),
            Message::Tick(now) => {
                if !self.cucumber_dragging()
                    && self.cucumber_fall.is_some_and(|(_, start)| {
                        now.duration_since(start) > Duration::from_millis(220)
                    })
                {
                    self.cucumber_fall = None;
                }
            }
            Message::Forwarded(arguments) => {
                for command in Command::parse_args(arguments) {
                    let effects = self.handle_command(command);
                    changed |= effects.contains(&Effect::Persist);
                    self.effects(effects, &mut changes);
                    self.close_popups();
                    self.measure(true);
                }
            }
            Message::TrayAction(action) => {
                use task_focuser_platform::tray::TrayAction;
                let command = match action {
                    TrayAction::ShowNote => Some(Command::Show),
                    TrayAction::Tuck => Some(Command::Dot),
                    TrayAction::StartDot => Some(Command::Start),
                    TrayAction::ToggleLock => Some(Command::ToggleLock),
                    TrayAction::Quit => Some(Command::Quit),
                    TrayAction::ToggleStartup => {
                        let enabled = !self.state().start_with_windows;
                        match self.services.login_startup.set_enabled(enabled) {
                            Ok(()) => {
                                self.conductor.state_mut().start_with_windows = enabled;
                                changed = true;
                            }
                            Err(error) => self.report(error.to_string()),
                        };
                        None
                    }
                };
                if let Some(command) = command {
                    let effects = self.handle_command(command);
                    changed |= effects.contains(&Effect::Persist);
                    self.effects(effects, &mut changes);
                    self.close_popups();
                    self.measure(true);
                }
            }
            Message::ServiceError(diagnostic) => self.report(diagnostic),
            Message::Save(generation)
                if generation == self.save_generation && generation != self.saved_generation =>
            {
                match self.flush() {
                    Ok(()) => self.error = None,
                    Err(error) => {
                        tracing::error!(%error, "Could not save note");
                        self.error = Some(format!("Could not save your note: {error}"));
                    }
                }
            }
            _ => {}
        }
        if old_plants != self.state().plants_on {
            self.leaf_animation = Some(LeafAnimation {
                from: old_leaves,
                to: if self.state().plants_on { 1. } else { 0. },
                started: Instant::now(),
                duration: Duration::from_millis(if self.state().plants_on { 320 } else { 260 }),
            });
        }
        if was_locked != self.state().locked {
            self.layout_animation = Some(LayoutAnimation {
                from: old_paper,
                to: self.conductor.note_bounds(),
                from_unfold: old_unfold,
                to_unfold: if self.state().locked { 0. } else { 1. },
                started: Instant::now(),
                duration: Duration::from_millis(180),
            });
            self.shake_started = Some(Instant::now());
            self.end_gesture(&mut changes, false);
            self.cucumber_fall = None;
            changes.push(SurfaceChange::Keyboard {
                role: SurfaceRole::Note,
                interactive: !self.state().locked,
            });
            if !self.state().locked {
                changes.push(SurfaceChange::Focus(SurfaceRole::Note));
                tasks.push(self.focus_editor());
            }
        }
        if had_gesture
            && self.gesture.is_none()
            && self.state().mode == Mode::Note
            && !self.state().locked
        {
            changes.push(SurfaceChange::Focus(SurfaceRole::Note));
            tasks.push(delay(Duration::from_millis(15), Message::FocusEditor));
        }
        if (old_audio.0, old_audio.1, old_audio.3)
            != (
                self.state().sound_on,
                self.state().idle_rate,
                self.state().volume,
            )
        {
            self.idle_sound.configure(
                self.state().sound_on,
                self.state().idle_rate,
                self.state().idle_sounds,
            );
        }
        self.idle_sound.set_sounds(self.state().idle_sounds);
        if changed {
            self.save_generation += 1;
            self.save_deadline = Some(Instant::now() + Duration::from_millis(300));
            tasks.push(delay(
                Duration::from_millis(300),
                Message::Save(self.save_generation),
            ));
        }
        while let Some(diagnostic) = self.services.sound.try_diagnostic() {
            self.report(diagnostic);
        }
        self.sync_companion();
        self.update_tray();
        self.sync_surfaces(&mut changes);
        (Task::batch(tasks), changes)
    }

    fn focus_editor(&mut self) -> Task<Message> {
        self.editor
            .perform(text_editor::Action::Move(text_editor::Motion::DocumentEnd));
        iced::widget::operation::focus(EDITOR_ID)
    }

    fn finish_rename(&mut self, commit: bool) -> bool {
        let Some(bird) = self.renaming.take() else {
            return false;
        };
        let name = self.rename_text.trim();
        if commit && name.chars().count() >= 3 {
            self.conductor
                .state_mut()
                .bird_names
                .insert(bird.into(), name.into());
            true
        } else {
            false
        }
    }

    fn close_popups(&mut self) {
        if self.finish_rename(true) {
            self.save_generation += 1;
            self.save_deadline = Some(Instant::now() + Duration::from_millis(300));
        }
        self.volume_open = false;
        self.picker_open = false;
        self.volume_generation += 1;
    }

    fn start_gesture(&mut self, kind: GestureKind, role: SurfaceRole) {
        if self.conductor.is_busy() || self.companion.is_switching() {
            return;
        }
        let origin = self
            .cursors
            .get(&role)
            .copied()
            .unwrap_or_else(|| self.conductor.note_bounds().origin());
        self.gesture = Some(Gesture {
            kind,
            origin,
            initial: self.conductor.note_bounds(),
            active: false,
        });
    }

    fn move_gesture(&mut self, cursor: Point, changes: &mut Vec<SurfaceChange>) {
        let Some(mut gesture) = self.gesture else {
            return;
        };
        if !gesture.active {
            if (cursor.x - gesture.origin.x).abs() <= 3.
                && (cursor.y - gesture.origin.y).abs() <= 3.
            {
                return;
            }
            gesture.active = true;
            self.close_popups();
            let effects = match gesture.kind {
                GestureKind::Note | GestureKind::Bird => {
                    self.companion
                        .set_self_drag(gesture.kind == GestureKind::Bird);
                    self.conductor.begin_note_drag(gesture.origin)
                }
                GestureKind::Dot => self.conductor.begin_dot_drag(gesture.origin),
                _ => Vec::new(),
            };
            self.effects(effects, changes);
        }
        match gesture.kind {
            GestureKind::Note | GestureKind::Bird => {
                let effects = self.conductor.move_note_drag(cursor);
                self.effects(effects, changes);
            }
            GestureKind::Dot => {
                let effects = self.conductor.move_dot_drag(cursor);
                self.effects(effects, changes);
            }
            GestureKind::Resize => {
                let area = geometry::work_area_at(&self.monitors, cursor);
                let state = self.conductor.state_mut();
                state.manual_width = Some(
                    (gesture.initial.width + cursor.x - gesture.origin.x)
                        .max(1.)
                        .min((area.right() - state.x).max(1.)),
                );
                state.manual_height = Some(
                    (gesture.initial.height + cursor.y - gesture.origin.y)
                        .max(1.)
                        .min((area.bottom() - state.y).max(1.)),
                );
                self.measure(false);
            }
            GestureKind::Cucumber => self.cucumber_fall = Some((cursor, Instant::now())),
        }
        self.gesture = Some(gesture);
    }

    fn end_gesture(&mut self, changes: &mut Vec<SurfaceChange>, released: bool) -> bool {
        let Some(gesture) = self.gesture.take() else {
            return false;
        };
        let role = match gesture.kind {
            GestureKind::Dot => SurfaceRole::Dot,
            GestureKind::Bird => SurfaceRole::Bird,
            _ => SurfaceRole::Note,
        };
        let cursor = self.cursors.get(&role).copied().unwrap_or(gesture.origin);
        if !gesture.active {
            if gesture.kind == GestureKind::Resize && released {
                let from = self.paper_bounds();
                let unfold = self.unfold();
                self.conductor.state_mut().manual_width = None;
                self.conductor.state_mut().manual_height = None;
                self.measure(true);
                self.layout_animation = Some(LayoutAnimation {
                    from,
                    to: self.conductor.note_bounds(),
                    from_unfold: unfold,
                    to_unfold: unfold,
                    started: Instant::now(),
                    duration: Duration::from_millis(220),
                });
                return true;
            }
            if gesture.kind == GestureKind::Bird && released {
                self.companion.click();
            }
            return false;
        }
        self.companion.set_self_drag(false);
        if !released && gesture.kind == GestureKind::Cucumber {
            self.cucumber_fall = None;
            return false;
        }
        let effects = match gesture.kind {
            GestureKind::Note | GestureKind::Bird => self.conductor.end_note_drag(cursor),
            GestureKind::Dot => self.conductor.end_dot_drag(cursor),
            GestureKind::Cucumber => {
                self.cucumber_fall = Some((cursor, Instant::now()));
                let bird = self.companion.position();
                if self.companion.visible()
                    && !self.companion.is_switching()
                    && Rect::new(bird.x - 33., bird.y - 48., 66., 48.).contains(cursor)
                {
                    self.companion.eat();
                }
                Vec::new()
            }
            GestureKind::Resize => {
                let bounds = self.conductor.note_bounds();
                self.conductor.state_mut().manual_width = Some(bounds.width);
                self.conductor.state_mut().manual_height = Some(bounds.height);
                Vec::new()
            }
        };
        self.effects(effects, changes);
        true
    }

    fn handle_command(&mut self, command: Command) -> Vec<Effect> {
        if command == Command::Dot && self.companion.is_switching() {
            return Vec::new();
        }
        if matches!(command, Command::Close | Command::Quit) {
            if self
                .gesture
                .is_some_and(|gesture| gesture.kind == GestureKind::Resize && gesture.active)
            {
                let bounds = self.conductor.note_bounds();
                self.conductor.state_mut().manual_width = Some(bounds.width);
                self.conductor.state_mut().manual_height = Some(bounds.height);
            }
            self.gesture = None;
            self.cucumber_fall = None;
            self.companion.set_self_drag(false);
            self.companion.release(self.perch());
        }
        self.conductor.handle(command)
    }

    fn effects(&mut self, effects: Vec<Effect>, changes: &mut Vec<SurfaceChange>) {
        for effect in effects {
            match effect {
                Effect::Persist => {
                    self.save_generation += 1;
                    self.save_deadline = Some(Instant::now() + Duration::from_millis(300));
                }
                Effect::Dock { target } => {
                    self.transition = Some(Transition {
                        kind: TransitionKind::Dock(target),
                        paper: self.paper_bounds(),
                        started: Instant::now(),
                        duration: Duration::from_millis(220),
                    });
                    self.conductor.set_busy(true);
                    self.companion.shove(target);
                }
                Effect::PopOut { origin } => {
                    self.transition = Some(Transition {
                        kind: TransitionKind::Pop(origin),
                        paper: self.conductor.note_bounds(),
                        started: Instant::now(),
                        duration: Duration::from_millis(280),
                    });
                    self.conductor.set_busy(true);
                }
                Effect::Vanish => {
                    if let Some(center) = self.conductor.dot_center() {
                        self.transition = Some(Transition {
                            kind: TransitionKind::Vanish(center),
                            paper: self.conductor.note_bounds(),
                            started: Instant::now(),
                            duration: Duration::from_millis(180),
                        });
                        self.conductor.set_busy(true);
                    }
                }
                Effect::DotTarget(target) => self.dot_target = target,
                Effect::CloseTarget(target) => self.close_target = target,
                Effect::BringToFront if !self.state().locked => {
                    changes.push(SurfaceChange::Focus(SurfaceRole::Note))
                }
                Effect::Carrying(true) => {
                    let perch = self.perch();
                    self.companion.carry(perch);
                }
                Effect::Carrying(false) => {
                    let perch = self.perch();
                    self.companion.release(perch);
                }
                Effect::Quit => {
                    if let Err(error) = self.flush() {
                        tracing::error!(%error, "Could not save note before exiting");
                    }
                    changes.push(SurfaceChange::Quit);
                }
                _ => {}
            }
        }
    }

    fn measure(&mut self, anchor_right: bool) {
        let state = self.state();
        let text = if state.text.is_empty() {
            " "
        } else {
            &state.text
        };
        let font = crate::view::note_font(&state.font);
        let font_size = f32::from(state.font_size);
        let natural = crate::view::measure_text(text, font, font_size, f32::INFINITY).width;
        let size = geometry::measure_note(
            f64::from(natural),
            NoteSizing {
                unfold: if state.locked { 0. } else { 1. },
                bar_width: BAR_WIDTH,
                editor_chrome: 0.,
                manual_width: state.manual_width,
                manual_height: state.manual_height,
            },
            |width| {
                f64::from(crate::view::measure_text(text, font, font_size, width as f32).height)
            },
        );
        let old = self.conductor.note_bounds();
        let bounds = if anchor_right {
            geometry::place_note(
                old,
                size,
                geometry::work_area_at(&self.monitors, old.center()),
            )
        } else {
            Rect::from_origin_size(old.origin(), size)
        };
        self.conductor.set_note_bounds(bounds);
        self.reset_editor_scroll();
        if let Some(animation) = &mut self.layout_animation {
            animation.to = bounds;
        }
    }

    fn reset_editor_scroll(&mut self) {
        let action = editor_scroll_to_top(&self.state().text);
        self.editor.perform(action);
    }

    fn report(&mut self, diagnostic: String) {
        tracing::error!(%diagnostic, "Task Focuser service failed");
        self.error = Some(diagnostic);
    }

    fn play(&mut self, kind: SoundKind) {
        if self.state().sound_on {
            if let Err(error) = self.services.sound.play(
                kind,
                self.state().volume as f32,
                self.state().active_bird_id(),
            ) {
                self.report(error.to_string());
            }
            self.idle_sound.restart();
        }
    }

    fn advance(&mut self, now: Instant) {
        if self.save_deadline.is_some_and(|deadline| now >= deadline)
            && self.saved_generation != self.save_generation
        {
            self.save_deadline = None;
            if let Err(error) = self.flush() {
                self.report(format!("Could not save your note: {error}"));
            }
        }
        let elapsed = now.saturating_duration_since(self.last_tick);
        self.last_tick = now;
        if self
            .transition
            .is_some_and(|motion| now.saturating_duration_since(motion.started) >= motion.duration)
        {
            self.transition = None;
            self.conductor.set_busy(false);
        }
        if self
            .layout_animation
            .is_some_and(|motion| now.saturating_duration_since(motion.started) >= motion.duration)
        {
            self.layout_animation = None;
            self.reset_editor_scroll();
        }
        if self
            .shake_started
            .is_some_and(|start| now.saturating_duration_since(start) >= Duration::from_millis(140))
        {
            self.shake_started = None;
        }
        if self
            .leaf_animation
            .is_some_and(|motion| now.saturating_duration_since(motion.started) >= motion.duration)
        {
            self.leaf_animation = None;
        }
        self.companion.update(elapsed);
        if self.pending_peep
            && now.saturating_duration_since(self.last_peep) >= Duration::from_millis(150)
        {
            self.pending_peep = false;
            self.last_peep = now;
            self.play(SoundKind::Peep);
        }
        if let Some(kind) = self.idle_sound.update(elapsed) {
            if self.companion.visible() && self.gesture.is_none() && !self.companion.is_switching()
            {
                if kind == SoundKind::Ruffle {
                    self.companion.ruffle();
                } else {
                    self.companion.flap();
                }
            }
            // Consume an emitted ruffle request before the direct playback so the idle sound is heard once.
            for event in self.companion.drain_events() {
                if let CompanionEvent::Sound(sound) = event
                    && sound != kind
                {
                    self.play(sound);
                }
            }
            self.play(kind);
        }
        for event in self.companion.drain_events() {
            match event {
                CompanionEvent::Sound(kind) => self.play(kind),
                CompanionEvent::ShoveDone => self.companion.leave(),
                CompanionEvent::Landed => {}
            }
        }
    }

    fn perch(&self) -> Point {
        let note = self.conductor.note_bounds();
        Point::new(note.x + 32., note.y)
    }

    fn sync_companion(&mut self) {
        let note = self.conductor.note_bounds();
        let perch = self.perch();
        let wanted = self.conductor.bird_wanted();
        if self
            .transition
            .is_some_and(|motion| matches!(motion.kind, TransitionKind::Dock(_)))
        {
            return;
        }
        let bird = BirdId::from_id(&self.state().bird);
        self.companion.set_pinned(self.state().bird_stays);
        self.companion.set_opacity(if self.state().bird_opaque {
            1.
        } else {
            f64::from(self.state().opacity) / 100.
        });
        self.companion
            .set_perch_span(note.x + 20., note.right() - 20.);
        if self.companion.bird() != bird && !self.companion.is_switching() && self.gesture.is_none()
        {
            self.companion.switch_to(bird);
        }
        if wanted {
            if !self.bird_wanted {
                if self.companion.visible() {
                    self.companion.fly_to(perch);
                } else {
                    self.companion.arrive_at(perch);
                }
            } else if perch != self.last_perch {
                if self.conductor.is_note_dragging() {
                    self.companion.carry(perch);
                } else if !self.companion.is_arriving()
                    && perch.distance_squared(self.last_perch) > 144.
                {
                    self.companion.fly_to(perch);
                } else {
                    self.companion.move_to(perch);
                }
            }
        } else if self.bird_wanted {
            self.companion.leave();
        }
        if self.state().locked || self.state().mode != Mode::Note {
            self.typing_chain = 0;
        }
        self.bird_wanted = wanted;
        self.last_perch = perch;
    }

    fn key_typed(&mut self) {
        if !self.companion.visible() || self.conductor.is_busy() {
            self.typing_chain = 0;
            return;
        }
        if self.typing_at.elapsed() > Duration::from_secs(5) {
            self.typing_chain = 0;
        }
        self.typing_at = Instant::now();
        if self.typing_chain == 0 {
            self.random ^= self.random << 13;
            self.random ^= self.random >> 7;
            self.random ^= self.random << 17;
            self.typing_chain = 2 + (self.random % 29) as u8;
        } else {
            self.typing_chain -= 1;
            self.companion.peck();
        }
    }

    fn update_tray(&mut self) {
        use task_focuser_platform::tray::{TrayMode, TrayState};
        let state = TrayState {
            mode: match self.state().mode {
                Mode::Note => TrayMode::Note,
                Mode::Dot => TrayMode::Dot,
                Mode::Closed => TrayMode::Closed,
            },
            locked: self.state().locked,
            start_at_login: self.state().start_with_windows,
        };
        if self.last_tray_state != Some(state) {
            if let Some(tray) = &mut self.services.tray
                && let Err(error) = tray.update(state)
            {
                self.report(error.to_string());
                return;
            }
            self.last_tray_state = Some(state);
        }
    }

    pub(crate) fn cucumber_dragging(&self) -> bool {
        self.gesture
            .is_some_and(|gesture| gesture.kind == GestureKind::Cucumber && gesture.active)
    }

    /// Local interactive regions omit decorative transparent margins and companion particles.
    pub fn input_bounds(&self, role: SurfaceRole) -> Vec<Rect> {
        match role {
            SurfaceRole::Note if !self.conductor.is_busy() => {
                let paper = self.paper_bounds();
                let mut regions = vec![Rect::new(PAD, PAD, paper.width, paper.height)];
                if self.volume_open {
                    regions.push(Rect::new(PAD + 126., PAD + 44., 164., 84.));
                }
                if self.picker_open {
                    regions.push(Rect::new(PAD + 2., PAD + 42., 166., 78.));
                }
                regions
            }
            SurfaceRole::Dot => vec![Rect::new(28., 28., 40., 40.)],
            SurfaceRole::Bird if self.companion.visible() => {
                let stage = self.surfaces.get(&role).copied().unwrap_or_default();
                let bird = self.companion.position();
                let shift = self.companion.sprite_offset();
                vec![Rect::new(
                    bird.x - 33. + shift.x - stage.x,
                    bird.y - 48. + shift.y - stage.y,
                    66.,
                    48.,
                )]
            }
            _ => Vec::new(),
        }
    }

    pub(crate) fn paper_bounds(&self) -> Rect {
        if let Some(motion) = self.transition
            && let TransitionKind::Dock(target) = motion.kind
        {
            let t = progress(motion.started, motion.duration).powi(2);
            return Rect::new(
                lerp(motion.paper.x, target.x - motion.paper.width, t),
                lerp(motion.paper.y, target.y, t),
                motion.paper.width,
                motion.paper.height,
            );
        }
        if let Some(motion) = self.layout_animation {
            let t = 1. - (1. - progress(motion.started, motion.duration)).powi(3);
            return Rect::new(
                lerp(motion.from.x, motion.to.x, t),
                lerp(motion.from.y, motion.to.y, t),
                lerp(motion.from.width, motion.to.width, t),
                lerp(motion.from.height, motion.to.height, t),
            );
        }
        self.conductor.note_bounds()
    }
    pub(crate) fn unfold(&self) -> f64 {
        self.layout_animation
            .map_or(if self.state().locked { 0. } else { 1. }, |motion| {
                lerp(
                    motion.from_unfold,
                    motion.to_unfold,
                    1. - (1. - progress(motion.started, motion.duration)).powi(3),
                )
            })
    }
    pub(crate) fn note_scale(&self) -> f32 {
        self.transition.map_or(1., |motion| {
            let t = progress(motion.started, motion.duration);
            match motion.kind {
                TransitionKind::Dock(_) => (1. - 0.92 * t * t) as f32,
                TransitionKind::Pop(_) => {
                    let q = t - 1.;
                    (0.2 + 0.8 * (1. + 1.5 * q * q * q + 0.5 * q * q)) as f32
                }
                _ => 1.,
            }
        })
    }
    pub(crate) fn note_alpha(&self) -> f32 {
        self.transition.map_or(1., |motion| match motion.kind {
            TransitionKind::Dock(_) => 1.,
            TransitionKind::Pop(_) => (motion.started.elapsed().as_secs_f32() / 0.14).min(1.),
            _ => 1.,
        })
    }
    pub(crate) fn note_shake(&self) -> f32 {
        self.shake_started.map_or(0., |start| {
            let t = (start.elapsed().as_secs_f32() / 0.14).min(1.);
            (t * std::f32::consts::TAU * 2.).sin() * (1. - 0.65 * t)
        })
    }
    pub(crate) fn vines_extent(&self) -> f64 {
        self.leaf_animation
            .map_or(if self.state().plants_on { 1. } else { 0. }, |motion| {
                lerp(
                    motion.from,
                    motion.to,
                    1. - (1. - progress(motion.started, motion.duration)).powi(3),
                )
            })
    }
    fn note_visible(&self) -> bool {
        self.state().mode == Mode::Note
            || self
                .transition
                .is_some_and(|motion| matches!(motion.kind, TransitionKind::Dock(_)))
    }
    pub(crate) fn dot_scale(&self) -> f32 {
        self.transition.map_or(1., |motion| {
            let t = progress(motion.started, motion.duration) as f32;
            match motion.kind {
                TransitionKind::Vanish(_) => 1. - t,
                TransitionKind::Pop(_) => 1. + 0.8 * (t * std::f32::consts::PI).sin(),
                _ => 1.,
            }
        })
    }

    pub(crate) fn note_stage(&self) -> Rect {
        let paper = self.paper_bounds();
        let popup_height = if self.volume_open {
            120.
        } else if self.picker_open {
            142.
        } else {
            0.
        };
        Rect::new(
            paper.x - PAD,
            paper.y - PAD,
            paper.width + PAD + OVERHANG,
            (paper.height + PAD + OVERHANG).max(if self.volume_open {
                PAD + 44. + 84. + 4.
            } else {
                popup_height
            }),
        )
    }

    fn sync_surfaces(&mut self, changes: &mut Vec<SurfaceChange>) {
        let mut wanted = BTreeMap::new();
        if self.note_visible() {
            wanted.insert(SurfaceRole::Note, (self.note_stage(), !self.state().locked));
        }
        let dot = if self.state().mode == Mode::Dot {
            self.conductor.dot_center().map(|center| (center, false))
        } else {
            self.dot_target.map(|target| (target.center, true))
        }
        .or_else(|| {
            self.transition.and_then(|motion| match motion.kind {
                TransitionKind::Pop(center) | TransitionKind::Vanish(center) => {
                    Some((center, false))
                }
                _ => None,
            })
        });
        if let Some((center, _)) = dot {
            wanted.insert(
                SurfaceRole::Dot,
                (Rect::new(center.x - 48., center.y - 48., 96., 96.), false),
            );
        }
        if let Some(target) = self.close_target {
            wanted.insert(
                SurfaceRole::CloseTarget,
                (
                    Rect::new(target.center.x - 40., target.center.y - 40., 80., 80.),
                    false,
                ),
            );
        }
        if self.companion.visible() || !self.companion.effects().is_empty() {
            let feet = self.companion.position();
            // The original companion reserves a fixed stage for hearts and landing dust.
            let bounds = Rect::new(feet.x - 70., feet.y - 110., 140., 110.);
            wanted.insert(SurfaceRole::Bird, (bounds, false));
        }
        if let Some((cursor, _)) = self.cucumber_fall {
            wanted.insert(
                SurfaceRole::Cucumber,
                (Rect::new(cursor.x - 14., cursor.y - 14., 28., 52.), false),
            );
        }
        for role in self.surfaces.keys().copied().collect::<Vec<_>>() {
            if !wanted.contains_key(&role) {
                self.surfaces.remove(&role);
                changes.push(SurfaceChange::Hide(role));
            }
        }
        for (role, (bounds, interactive)) in wanted {
            match self.surfaces.insert(role, bounds) {
                None => changes.push(SurfaceChange::Show {
                    role,
                    bounds,
                    interactive,
                }),
                Some(old) => {
                    if old.origin() != bounds.origin() {
                        changes.push(SurfaceChange::Move {
                            role,
                            position: bounds.origin(),
                        });
                    }
                    if old.size() != bounds.size() {
                        changes.push(SurfaceChange::Resize {
                            role,
                            size: bounds.size(),
                        });
                    }
                }
            }
        }
    }
}

impl Drop for App {
    fn drop(&mut self) {
        if self.save_generation != self.saved_generation
            && let Err(error) = self.flush()
        {
            tracing::error!(%error,"Could not save note while closing");
        }
    }
}

fn delay(duration: Duration, message: Message) -> Task<Message> {
    Task::perform(
        async move {
            tokio_sleep(duration).await;
            message
        },
        |message| message,
    )
}

async fn tokio_sleep(duration: Duration) {
    tokio::time::sleep(duration).await;
}

fn progress(start: Instant, duration: Duration) -> f64 {
    (start.elapsed().as_secs_f64() / duration.as_secs_f64()).clamp(0., 1.)
}
fn lerp(from: f64, to: f64, t: f64) -> f64 {
    from + (to - from) * t
}

/// The note fits its complete text. Clear scrolling retained from a smaller editing viewport.
/// A character can occupy at most one visual line, so this also covers wrapped physical lines.
fn editor_scroll_to_top(text: &str) -> text_editor::Action {
    let lines = text
        .chars()
        .count()
        .saturating_add(1)
        .min(i32::MAX as usize) as i32;
    text_editor::Action::Scroll { lines: -lines }
}

#[cfg(test)]
mod tests {
    use super::*;
    use iced::advanced::text::{Editor as _, LineHeight, Wrapping, highlighter::PlainText};

    #[test]
    fn growing_native_editor_reveals_prefix_without_changing_selection_or_text() {
        let text = format!("Long URL https://example.invalid/{}", "unbroken".repeat(40));
        let font = crate::view::note_font("Segoe UI");
        let size = 15.;
        let width = 492.;
        let full_height = crate::view::measure_text(&text, font, size, width)
            .height
            .ceil();
        let mut editor = iced::advanced::graphics::text::Editor::with_text(&text);
        let mut highlighter = PlainText;
        editor.update(
            iced::Size::new(width, 18.),
            font,
            iced::Pixels(size),
            LineHeight::Relative(1.2),
            Wrapping::WordOrGlyph,
            &mut highlighter,
        );
        editor.perform(text_editor::Action::Move(text_editor::Motion::DocumentEnd));
        editor.perform(text_editor::Action::Select(text_editor::Motion::Left));
        editor.update(
            iced::Size::new(width, 18.),
            font,
            iced::Pixels(size),
            LineHeight::Relative(1.2),
            Wrapping::WordOrGlyph,
            &mut highlighter,
        );
        let cursor = editor.cursor();
        editor.update(
            iced::Size::new(width, full_height),
            font,
            iced::Pixels(size),
            LineHeight::Relative(1.2),
            Wrapping::WordOrGlyph,
            &mut highlighter,
        );
        assert!(
            editor.buffer().scroll().vertical > 0.,
            "native editor retained its smaller viewport offset"
        );
        editor.perform(editor_scroll_to_top(&text));
        editor.update(
            iced::Size::new(width, full_height),
            font,
            iced::Pixels(size),
            LineHeight::Relative(1.2),
            Wrapping::WordOrGlyph,
            &mut highlighter,
        );
        assert_eq!(editor.buffer().scroll().line, 0);
        assert_eq!(editor.buffer().scroll().vertical, 0.);
        assert_eq!(
            editor.cursor(),
            cursor,
            "scroll normalization must preserve caret and selection"
        );
        assert_eq!(editor.line(0).expect("URL line").text, text);
    }
}
