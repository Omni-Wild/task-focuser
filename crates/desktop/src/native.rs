//! Transparent, independent native windows driven by the shared controller.

use anyhow::Context;
use std::{
    collections::{BTreeMap, VecDeque},
    sync::{Arc, Mutex},
};

use iced::{Element, Event, Subscription, Task, Theme, event, window};
use std::time::Duration;
use task_focuser_core::geometry::{Monitor, Point, Rect};

use crate::{
    app::{App, Message},
    surface::{Startup, SurfaceChange, SurfaceRole},
};

#[derive(Debug, Clone)]
enum HostMessage {
    App(Message),
    Monitors(Result<Vec<Monitor>, String>),
    Event(window::Id, Event),
    #[cfg(target_os = "windows")]
    Position(SurfaceRole, Result<Point, String>),
    Opened(SurfaceRole, window::Id),
    PolicyApplied(Result<(), String>),
    InputScaled {
        id: window::Id,
        scale: f32,
        rects: Vec<Rect>,
    },
    Configured {
        role: SurfaceRole,
        id: window::Id,
        result: Result<(), String>,
    },
}

#[derive(Debug, Clone)]
struct Surface {
    id: window::Id,
    bounds: Rect,
    interactive: bool,
    opened: bool,
    focused: bool,
    input: Vec<Rect>,
    requested_positions: VecDeque<Point>,
}

struct Host {
    app: App,
    surfaces: BTreeMap<SurfaceRole, Surface>,
    failure: Arc<Mutex<Option<String>>>,
    monitors: Vec<Monitor>,
    #[cfg(target_os = "linux")]
    previous_foreground: Option<u32>,
    #[cfg(target_os = "windows")]
    previous_foreground: Option<isize>,
    #[cfg(target_os = "macos")]
    previous_foreground: Option<i32>,
}

pub fn run(startup: Startup) -> anyhow::Result<()> {
    let failure = Arc::new(Mutex::new(None));
    let result = Arc::clone(&failure);
    let startup = std::cell::RefCell::new(Some(startup));
    iced::daemon(
        move || {
            Host::new(
                startup
                    .borrow_mut()
                    .take()
                    .expect("desktop host boots only once"),
                Arc::clone(&failure),
            )
        },
        Host::update,
        Host::view,
    )
    .title(|_: &Host, _| "Task Focuser".to_owned())
    .subscription(Host::subscription)
    .theme(Theme::Dark)
    .style(|_, _| iced::theme::Style {
        background_color: iced::Color::TRANSPARENT,
        text_color: iced::Color::WHITE,
    })
    .run()
    .context("native window event loop failed")?;
    if let Some(error) = result
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
        .take()
    {
        anyhow::bail!(error);
    }
    Ok(())
}

impl Host {
    fn new(startup: Startup, failure: Arc<Mutex<Option<String>>>) -> (Self, Task<HostMessage>) {
        let monitors = startup.monitors.clone();
        let (app, changes) = App::new(startup);
        let mut host = Self {
            app,
            failure,
            monitors,
            surfaces: BTreeMap::new(),
            #[cfg(any(target_os = "windows", target_os = "macos", target_os = "linux"))]
            previous_foreground: None,
        };
        let task = host.apply(changes);
        (host, task)
    }

    fn role(&self, id: window::Id) -> Option<SurfaceRole> {
        self.surfaces
            .iter()
            .find_map(|(&role, surface)| (surface.id == id).then_some(role))
    }

    fn update(&mut self, message: HostMessage) -> Task<HostMessage> {
        match message {
            HostMessage::App(message) => self.update_app(message),
            HostMessage::Monitors(result) => match result {
                Ok(monitors) if monitors != self.monitors => {
                    self.monitors.clone_from(&monitors);
                    self.update_app(Message::Monitors(monitors))
                }
                Err(error) => {
                    tracing::warn!(%error, "could not refresh native monitor geometry");
                    Task::none()
                }
                _ => Task::none(),
            },
            HostMessage::Opened(role, id) => {
                let Some(surface) = self.surfaces.get(&role).filter(|surface| surface.id == id)
                else {
                    return window::close(id);
                };
                configure(id, surface.interactive, surface.bounds)
                    .map(move |result| HostMessage::Configured { role, id, result })
            }
            #[cfg(target_os = "windows")]
            HostMessage::Position(role, result) => match result {
                Ok(position) => self.moved(role, position),
                Err(error) => self.update(HostMessage::PolicyApplied(Err(error))),
            },
            HostMessage::Event(id, event) => {
                #[cfg(target_os = "windows")]
                let mut event = event;
                let Some(role) = self.role(id) else {
                    return Task::none();
                };
                #[cfg(target_os = "windows")]
                if let Event::Mouse(iced::mouse::Event::CursorMoved { position }) = &mut event {
                    match task_focuser_platform::windows::cursor_position() {
                        Ok(cursor) => {
                            if let Some(surface) = self.surfaces.get(&role) {
                                position.x = (cursor.x - surface.bounds.x) as f32;
                                position.y = (cursor.y - surface.bounds.y) as f32;
                            }
                        }
                        Err(error) => {
                            return self.update(HostMessage::PolicyApplied(Err(error.to_string())));
                        }
                    }
                }
                if let Event::Window(window::Event::Moved(position)) = &event {
                    #[cfg(not(target_os = "windows"))]
                    {
                        let position = Point::new(f64::from(position.x), f64::from(position.y));
                        return self.moved(role, position);
                    }
                    #[cfg(target_os = "windows")]
                    {
                        let _ = position;
                        return current_position(id)
                            .map(move |result| HostMessage::Position(role, result));
                    }
                }
                if let Some(surface) = self.surfaces.get_mut(&role) {
                    match event {
                        Event::Window(window::Event::Focused) => surface.focused = true,
                        Event::Window(window::Event::Unfocused) => surface.focused = false,
                        _ => {}
                    }
                }
                if matches!(event, Event::Window(window::Event::Closed)) {
                    let focused = self
                        .surfaces
                        .remove(&role)
                        .is_some_and(|surface| surface.focused);
                    if role == SurfaceRole::Note {
                        self.restore_previous_foreground(focused);
                    }
                    self.update_app(Message::SurfaceClosed(role))
                } else {
                    self.update_app(Message::Event(role, event))
                }
            }
            HostMessage::InputScaled { id, scale, rects } => {
                input_region(id, scale, rects).map(HostMessage::PolicyApplied)
            }
            HostMessage::PolicyApplied(result) => {
                if let Err(error) = result {
                    tracing::error!(%error, "could not update native keyboard policy");
                    *self
                        .failure
                        .lock()
                        .unwrap_or_else(std::sync::PoisonError::into_inner) = Some(error);
                    return iced::exit();
                }
                Task::none()
            }
            HostMessage::Configured { role, id, result } => {
                if let Err(error) = result {
                    tracing::error!(%error, "could not configure native window");
                    *self
                        .failure
                        .lock()
                        .unwrap_or_else(std::sync::PoisonError::into_inner) = Some(error);
                    return Task::batch([window::close(id), iced::exit()]);
                }
                if self
                    .surfaces
                    .get(&role)
                    .is_some_and(|surface| surface.id == id)
                {
                    if let Some(surface) = self.surfaces.get_mut(&role) {
                        surface.opened = true;
                    }
                    let update = self.update_app(Message::SurfaceOpened(role, id));
                    window::set_mode(id, window::Mode::Windowed).chain(update)
                } else {
                    window::close(id)
                }
            }
        }
    }

    fn restore_previous_foreground(&mut self, note_focused: bool) {
        let previous = self.previous_foreground.take();
        if !note_focused {
            return;
        }
        if let Some(previous) = previous {
            #[cfg(target_os = "windows")]
            let result = task_focuser_platform::windows::restore_foreground(previous);
            #[cfg(target_os = "macos")]
            let result = task_focuser_platform::macos::restore_foreground(previous);
            #[cfg(target_os = "linux")]
            let result = task_focuser_platform::x11::restore_foreground(previous);
            if let Err(error) = result {
                tracing::warn!(%error, "could not restore previous foreground window");
            }
        }
    }

    fn moved(&mut self, role: SurfaceRole, position: Point) -> Task<HostMessage> {
        let Some(surface) = self.surfaces.get_mut(&role) else {
            return Task::none();
        };
        if let Some(index) = surface
            .requested_positions
            .iter()
            .position(|requested| requested.distance_squared(position) < 4.0)
        {
            surface.requested_positions.drain(..=index);
            return Task::none();
        }
        if surface.bounds.origin().distance_squared(position) < 4.0 {
            return Task::none();
        }
        surface.requested_positions.clear();
        surface.bounds.x = position.x;
        surface.bounds.y = position.y;
        self.update_app(Message::HostMoved(role, position))
    }

    fn update_app(&mut self, message: Message) -> Task<HostMessage> {
        let (task, changes) = self.app.update(message);
        Task::batch([task.map(HostMessage::App), self.apply(changes)])
    }

    fn apply(&mut self, changes: Vec<SurfaceChange>) -> Task<HostMessage> {
        let mut tasks = Vec::new();
        for change in changes {
            match change {
                SurfaceChange::Show {
                    role,
                    bounds,
                    interactive,
                } => {
                    if let Some(surface) = self.surfaces.get_mut(&role) {
                        surface.bounds = bounds;
                        surface.requested_positions.push_back(bounds.origin());
                        surface.interactive = interactive;
                        tasks.push(set_bounds(surface.id, bounds));
                        tasks.push(
                            configure(surface.id, interactive, surface.bounds)
                                .map(HostMessage::PolicyApplied),
                        );
                    } else {
                        let (id, open) = window::open(settings(bounds));
                        self.surfaces.insert(
                            role,
                            Surface {
                                id,
                                bounds,
                                interactive,
                                opened: false,
                                focused: false,
                                input: Vec::new(),
                                requested_positions: VecDeque::from([bounds.origin()]),
                            },
                        );
                        tasks.push(open.map(move |id| HostMessage::Opened(role, id)));
                    }
                }
                SurfaceChange::Hide(role) => {
                    let surface = self.surfaces.remove(&role);
                    if role == SurfaceRole::Note {
                        self.restore_previous_foreground(
                            surface.as_ref().is_some_and(|surface| surface.focused),
                        );
                    }
                    if let Some(surface) = surface {
                        tasks.push(window::close(surface.id));
                    }
                }
                SurfaceChange::Move { role, position } => {
                    if let Some(surface) = self.surfaces.get_mut(&role) {
                        surface.requested_positions.push_back(position);
                        if surface.requested_positions.len() > 32 {
                            surface.requested_positions.pop_front();
                        }
                        surface.bounds.x = position.x;
                        surface.bounds.y = position.y;
                        tasks.push(set_bounds(surface.id, surface.bounds));
                    }
                }
                SurfaceChange::Resize { role, size } => {
                    if let Some(surface) = self.surfaces.get_mut(&role) {
                        surface.bounds.width = size.width;
                        surface.bounds.height = size.height;
                        tasks.push(set_bounds(surface.id, surface.bounds));
                    }
                }
                SurfaceChange::Keyboard { role, interactive } => {
                    if let Some(surface) = self.surfaces.get_mut(&role) {
                        surface.interactive = interactive;
                        tasks.push(
                            configure(surface.id, interactive, surface.bounds)
                                .map(HostMessage::PolicyApplied),
                        );
                        if role == SurfaceRole::Note && !interactive {
                            let focused = surface.focused;
                            self.restore_previous_foreground(focused);
                        }
                    }
                }
                SurfaceChange::Focus(role) => {
                    if let Some(surface) = self
                        .surfaces
                        .get(&role)
                        .filter(|surface| surface.interactive)
                    {
                        #[cfg(target_os = "linux")]
                        if self.previous_foreground.is_none() {
                            self.previous_foreground =
                                task_focuser_platform::x11::capture_foreground();
                        }
                        #[cfg(target_os = "windows")]
                        if self.previous_foreground.is_none() {
                            self.previous_foreground =
                                task_focuser_platform::windows::capture_foreground();
                        }
                        #[cfg(target_os = "macos")]
                        if self.previous_foreground.is_none() {
                            self.previous_foreground =
                                task_focuser_platform::macos::capture_foreground();
                        }
                        if surface.opened {
                            tasks.push(focus(surface.id));
                        }
                    }
                }
                SurfaceChange::Quit => tasks.push(iced::exit()),
            }
        }
        for (&role, surface) in &mut self.surfaces {
            let rects = self.app.input_bounds(role);
            if surface.opened && surface.input != rects {
                surface.input.clone_from(&rects);
                let id = surface.id;
                tasks.push(
                    window::scale_factor(id).map(move |scale| HostMessage::InputScaled {
                        id,
                        scale,
                        rects: rects.clone(),
                    }),
                );
            }
        }
        Task::batch(tasks)
    }

    fn view(&self, id: window::Id) -> Element<'_, HostMessage> {
        self.role(id).map_or_else(
            || iced::widget::Space::new().into(),
            |role| self.app.view(role).map(HostMessage::App),
        )
    }

    fn subscription(&self) -> Subscription<HostMessage> {
        Subscription::batch([
            self.app.subscription().map(HostMessage::App),
            event::listen_with(|event, _, id| Some(HostMessage::Event(id, event))),
            Subscription::run(monitor_changes),
        ])
    }
}

fn settings(bounds: Rect) -> window::Settings {
    window::Settings {
        size: iced::Size::new(bounds.width.max(1.0) as f32, bounds.height.max(1.0) as f32),
        position: window::Position::Specific(iced::Point::new(bounds.x as f32, bounds.y as f32)),
        visible: false,
        resizable: false,
        minimizable: false,
        decorations: false,
        transparent: true,
        level: window::Level::AlwaysOnTop,
        exit_on_close_request: false,
        platform_specific: platform_settings(),
        ..window::Settings::default()
    }
}

fn platform_settings() -> window::settings::PlatformSpecific {
    #[cfg(target_os = "windows")]
    {
        window::settings::PlatformSpecific {
            skip_taskbar: true,
            drag_and_drop: false,
            ..Default::default()
        }
    }
    #[cfg(target_os = "macos")]
    {
        window::settings::PlatformSpecific {
            title_hidden: true,
            titlebar_transparent: true,
            fullsize_content_view: true,
        }
    }
    #[cfg(target_os = "linux")]
    {
        window::settings::PlatformSpecific {
            application_id: "dev.taskfocuser.TaskFocuser".into(),
            override_redirect: true,
        }
    }
}

fn configure(id: window::Id, interactive: bool, bounds: Rect) -> Task<Result<(), String>> {
    #[cfg(not(target_os = "windows"))]
    let _ = bounds;
    window::run(id, move |window| {
        let handle = window.window_handle().map_err(|error| error.to_string())?;
        match handle.as_raw() {
            #[cfg(target_os = "windows")]
            raw_window_handle::RawWindowHandle::Win32(handle) => {
                task_focuser_platform::windows::configure_window(handle.hwnd.get(), interactive)
                    .and_then(|()| {
                        task_focuser_platform::windows::set_bounds(handle.hwnd.get(), bounds)
                    })
                    .map_err(|error| error.to_string())
            }
            #[cfg(target_os = "macos")]
            raw_window_handle::RawWindowHandle::AppKit(handle) => {
                task_focuser_platform::macos::configure_window(
                    handle.ns_view.as_ptr() as usize,
                    interactive,
                )
                .map_err(|error| error.to_string())
            }
            #[cfg(target_os = "linux")]
            raw_window_handle::RawWindowHandle::Xlib(handle) => {
                task_focuser_platform::x11::configure_window(handle.window as u32, interactive)
                    .map_err(|error| error.to_string())
            }
            #[cfg(target_os = "linux")]
            raw_window_handle::RawWindowHandle::Xcb(handle) => {
                task_focuser_platform::x11::configure_window(handle.window.get(), interactive)
                    .map_err(|error| error.to_string())
            }
            _ => Err(
                "unexpected native window handle; choose the Wayland backend for Wayland sessions"
                    .into(),
            ),
        }
    })
}

fn input_region(id: window::Id, scale: f32, rects: Vec<Rect>) -> Task<Result<(), String>> {
    #[cfg(target_os = "linux")]
    {
        window::run(id, move |window| {
            let handle = window.window_handle().map_err(|error| error.to_string())?;
            let xid = match handle.as_raw() {
                raw_window_handle::RawWindowHandle::Xlib(handle) => handle.window as u32,
                raw_window_handle::RawWindowHandle::Xcb(handle) => handle.window.get(),
                _ => return Err("unexpected X11 window handle".into()),
            };
            task_focuser_platform::x11::configure_input_region(xid, f64::from(scale), &rects)
                .map_err(|error| error.to_string())
        })
    }
    #[cfg(target_os = "macos")]
    {
        window::run(id, move |window| {
            let handle = window.window_handle().map_err(|error| error.to_string())?;
            match handle.as_raw() {
                raw_window_handle::RawWindowHandle::AppKit(handle) => {
                    task_focuser_platform::macos::configure_input_region(
                        handle.ns_view.as_ptr() as usize,
                        f64::from(scale),
                        &rects,
                    )
                    .map_err(|error| error.to_string())
                }
                _ => Err("unexpected macOS window handle".into()),
            }
        })
    }
    #[cfg(target_os = "windows")]
    {
        window::run(id, move |window| {
            let handle = window.window_handle().map_err(|error| error.to_string())?;
            match handle.as_raw() {
                raw_window_handle::RawWindowHandle::Win32(handle) => {
                    task_focuser_platform::windows::configure_input_region(
                        handle.hwnd.get(),
                        f64::from(scale),
                        &rects,
                    )
                    .map_err(|error| error.to_string())
                }
                _ => Err("unexpected Windows window handle".into()),
            }
        })
    }
}

fn focus(id: window::Id) -> Task<HostMessage> {
    #[cfg(target_os = "linux")]
    {
        window::run(id, |window| {
            let handle = window.window_handle().map_err(|error| error.to_string())?;
            let xid = match handle.as_raw() {
                raw_window_handle::RawWindowHandle::Xlib(handle) => handle.window as u32,
                raw_window_handle::RawWindowHandle::Xcb(handle) => handle.window.get(),
                _ => return Err("unexpected X11 window handle".into()),
            };
            task_focuser_platform::x11::focus_window(xid).map_err(|error| error.to_string())
        })
        .map(HostMessage::PolicyApplied)
    }
    #[cfg(not(target_os = "linux"))]
    {
        window::gain_focus(id)
    }
}

fn monitor_changes() -> impl iced::futures::Stream<Item = HostMessage> {
    iced::stream::channel(
        1,
        |mut sender: iced::futures::channel::mpsc::Sender<HostMessage>| async move {
            use iced::futures::SinkExt;
            let mut previous = read_monitors().await;
            loop {
                tokio::time::sleep(Duration::from_secs(2)).await;
                let current = read_monitors().await;
                if current != previous {
                    previous.clone_from(&current);
                    if sender.send(HostMessage::Monitors(current)).await.is_err() {
                        break;
                    }
                }
            }
        },
    )
}

async fn read_monitors() -> Result<Vec<Monitor>, String> {
    tokio::task::spawn_blocking(task_focuser_platform::monitors::monitors)
        .await
        .map_err(|error| error.to_string())?
        .map_err(|error| error.to_string())
}

fn set_bounds(id: window::Id, bounds: Rect) -> Task<HostMessage> {
    #[cfg(target_os = "windows")]
    {
        window::run(id, move |window| {
            let handle = window.window_handle().map_err(|error| error.to_string())?;
            match handle.as_raw() {
                raw_window_handle::RawWindowHandle::Win32(handle) => {
                    task_focuser_platform::windows::set_bounds(handle.hwnd.get(), bounds)
                        .map_err(|error| error.to_string())
                }
                _ => Err("unexpected Windows window handle".into()),
            }
        })
        .map(HostMessage::PolicyApplied)
    }
    #[cfg(not(target_os = "windows"))]
    {
        Task::batch([
            window::move_to(id, iced::Point::new(bounds.x as f32, bounds.y as f32)),
            window::resize(
                id,
                iced::Size::new(bounds.width as f32, bounds.height as f32),
            ),
        ])
    }
}

#[cfg(target_os = "windows")]
fn current_position(id: window::Id) -> Task<Result<Point, String>> {
    window::run(id, move |window| {
        let handle = window.window_handle().map_err(|error| error.to_string())?;
        match handle.as_raw() {
            raw_window_handle::RawWindowHandle::Win32(handle) => {
                task_focuser_platform::windows::get_position(handle.hwnd.get())
                    .map_err(|error| error.to_string())
            }
            _ => Err("unexpected Windows window handle".into()),
        }
    })
}
