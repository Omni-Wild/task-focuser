//! Genuine layer-shell windows. No compositor configuration is changed.

use std::collections::BTreeMap;
use std::time::Duration;

use iced::{Element, Event, Subscription, Task, Theme, event, window};
use iced_layershell::{
    build_pattern::daemon,
    reexport::{Anchor, KeyboardInteractivity, Layer, NewLayerShellSettings, OutputOption},
    settings::{LayerShellSettings, Settings, StartMode},
    to_layer_message,
};
use task_focuser_core::geometry::{Point, Rect, Size, nearest_monitor};

use crate::{
    app::{App, Message},
    host::{Output, discover_outputs},
    surface::{Startup, SurfaceChange, SurfaceRole},
};

#[to_layer_message(multi)]
#[derive(Debug, Clone)]
enum HostMessage {
    App(Message),
    Event(window::Id, Event),
    ReplicaOpened(window::Id),
    Outputs(Result<Vec<Output>, String>),
}

#[derive(Debug, Clone)]
struct Surface {
    id: window::Id,
    bounds: Rect,
    interactive: bool,
    output: String,
    opened: bool,
    input: Vec<Rect>,
}

struct Host {
    app: App,
    surfaces: BTreeMap<SurfaceRole, Surface>,
    outputs: Vec<Output>,
    pressed: BTreeMap<SurfaceRole, window::Id>,
    replicas: BTreeMap<window::Id, (SurfaceRole, Surface)>,
    inputs: BTreeMap<window::Id, (SurfaceRole, Surface)>,
}

pub fn run(startup: Startup) -> anyhow::Result<()> {
    let outputs = discover_outputs()?;
    let startup = std::cell::RefCell::new(Some(startup));
    daemon(
        move || {
            Host::new(
                startup
                    .borrow_mut()
                    .take()
                    .expect("desktop host boots only once"),
                outputs.clone(),
            )
        },
        "task-focuser",
        Host::update,
        Host::view,
    )
    .subscription(Host::subscription)
    .theme(Theme::Dark)
    .style(|_, _| iced::theme::Style {
        background_color: iced::Color::TRANSPARENT,
        text_color: iced::Color::WHITE,
    })
    .settings(Settings {
        id: Some("dev.taskfocuser.TaskFocuser".into()),
        layer_settings: LayerShellSettings {
            start_mode: StartMode::Background,
            keyboard_interactivity: KeyboardInteractivity::None,
            layer: Layer::Overlay,
            exclusive_zone: -1,
            ..LayerShellSettings::default()
        },
        ..Settings::default()
    })
    .run()?;
    Ok(())
}

impl Host {
    fn new(startup: Startup, outputs: Vec<Output>) -> (Self, Task<HostMessage>) {
        let (mut app, mut changes) = App::new(startup);
        let (task, placement) = app.update(Message::Monitors(
            outputs.iter().map(|output| output.monitor).collect(),
        ));
        changes.extend(placement);
        let mut host = Self {
            app,
            surfaces: BTreeMap::new(),
            outputs,
            pressed: BTreeMap::new(),
            replicas: BTreeMap::new(),
            inputs: BTreeMap::new(),
        };
        let task = Task::batch([host.apply(changes), task.map(HostMessage::App)]);
        (host, task)
    }

    fn role(&self, id: window::Id) -> Option<SurfaceRole> {
        self.surfaces
            .iter()
            .find_map(|(&role, surface)| (surface.id == id).then_some(role))
            .or_else(|| self.replicas.get(&id).map(|(role, _)| *role))
            .or_else(|| self.inputs.get(&id).map(|(role, _)| *role))
    }

    fn update(&mut self, message: HostMessage) -> Task<HostMessage> {
        match message {
            HostMessage::App(message) => {
                if let Message::SurfaceOpened(role, id) = &message
                    && let Some(surface) = self
                        .surfaces
                        .get_mut(role)
                        .filter(|surface| surface.id == *id)
                {
                    surface.opened = true;
                }
                self.update_app(message)
            }
            HostMessage::ReplicaOpened(id) => {
                if let Some((_, surface)) = self.replicas.get_mut(&id) {
                    surface.opened = true;
                } else if let Some(surface) =
                    self.surfaces.values_mut().find(|surface| surface.id == id)
                {
                    surface.opened = true;
                } else {
                    return window::close(id);
                }
                self.apply(Vec::new())
            }
            HostMessage::Outputs(Ok(outputs)) => {
                if outputs == self.outputs {
                    return Task::none();
                }
                let monitors = outputs.iter().map(|output| output.monitor).collect();
                self.outputs = outputs;
                let update = self.update_app(Message::Monitors(monitors));
                // Existing windows must move to a surviving output after hotplug.
                let changes = self
                    .surfaces
                    .iter()
                    .map(|(&role, surface)| SurfaceChange::Show {
                        role,
                        bounds: surface.bounds,
                        interactive: surface.interactive,
                    })
                    .collect();
                Task::batch([update, self.apply(changes)])
            }
            HostMessage::Outputs(Err(error)) => {
                tracing::error!(%error, "could not read Wayland output geometry");
                Task::none()
            }
            HostMessage::Event(id, mut event) => {
                let Some(role) = self.role(id) else {
                    return Task::none();
                };
                if self.inputs.contains_key(&id)
                    && self.pressed.contains_key(&role)
                    && matches!(event, Event::Window(window::Event::Unfocused))
                {
                    return Task::none();
                }
                if let Some((_, source)) = self.inputs.get(&id)
                    && let (
                        Event::Mouse(iced::mouse::Event::CursorMoved { position }),
                        Some(current),
                    ) = (&mut event, self.surfaces.get(&role))
                {
                    position.x += (source.bounds.x - current.bounds.x) as f32;
                    position.y += (source.bounds.y - current.bounds.y) as f32;
                }
                if matches!(event, Event::Mouse(iced::mouse::Event::ButtonPressed(_))) {
                    self.pressed.insert(role, id);
                }
                let released = matches!(event, Event::Mouse(iced::mouse::Event::ButtonReleased(_)));
                if released {
                    self.pressed.remove(&role);
                }
                let focused = matches!(event, Event::Window(window::Event::Focused));
                let closed = matches!(event, Event::Window(window::Event::Closed));
                if closed
                    && (self.inputs.remove(&id).is_some() || self.replicas.remove(&id).is_some())
                {
                    if self.pressed.get(&role) != Some(&id) {
                        return Task::none();
                    }
                    self.pressed.remove(&role);
                    return self.update_app(Message::Event(
                        role,
                        Event::Mouse(iced::mouse::Event::ButtonReleased(
                            iced::mouse::Button::Left,
                        )),
                    ));
                }
                let update = if closed {
                    self.surfaces.remove(&role);
                    self.update_app(Message::SurfaceClosed(role))
                } else {
                    self.update_app(Message::Event(role, event))
                };
                if focused
                    && self
                        .surfaces
                        .get(&role)
                        .is_some_and(|surface| surface.interactive)
                {
                    // Exclusive requests focus once; editing then uses normal click focus.
                    Task::batch([
                        update,
                        Task::done(HostMessage::KeyboardInteractivityChange {
                            id,
                            keyboard_interactivity: KeyboardInteractivity::OnDemand,
                        }),
                    ])
                } else {
                    update
                }
            }
            // The macro's variants are intercepted by iced_layershell before update.
            _ => Task::none(),
        }
    }

    fn update_app(&mut self, message: Message) -> Task<HostMessage> {
        let (task, changes) = self.app.update(message);
        Task::batch([task.map(HostMessage::App), self.apply(changes)])
    }

    fn output_at(&self, bounds: Rect) -> Option<&Output> {
        let monitors: Vec<_> = self.outputs.iter().map(|output| output.monitor).collect();
        nearest_monitor(&monitors, bounds.center()).map(|index| &self.outputs[index])
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
                    self.detach_input(role, bounds, &mut tasks);
                    let output = self.output_at(bounds).cloned();
                    let output_name = output
                        .as_ref()
                        .map(|output| output.name.clone())
                        .unwrap_or_default();
                    let recreate = self
                        .surfaces
                        .get(&role)
                        .is_some_and(|surface| surface.output != output_name);
                    if recreate && let Some(surface) = self.surfaces.remove(&role) {
                        self.replicas.insert(surface.id, (role, surface));
                    }
                    // Reuse an existing visual when its output becomes the primary output.
                    let returning =
                        self.replicas
                            .iter()
                            .find_map(|(&id, (replica_role, surface))| {
                                (*replica_role == role && surface.output == output_name)
                                    .then_some(id)
                            });
                    if let Some(id) = returning
                        && let Some((_, surface)) = self.replicas.remove(&id)
                    {
                        self.surfaces.insert(role, surface);
                        tasks.push(Task::done(HostMessage::App(Message::SurfaceOpened(
                            role, id,
                        ))));
                    }
                    if let Some(surface) = self.surfaces.get_mut(&role) {
                        surface.bounds = bounds;
                        surface.interactive = interactive;
                        let id = surface.id;
                        tasks.push(Task::done(HostMessage::MarginChange {
                            id,
                            margin: margins(bounds.origin(), output.as_ref()),
                        }));
                        tasks.push(Task::done(HostMessage::SizeChange {
                            id,
                            size: dimensions(bounds.size()),
                        }));
                        tasks.push(Task::done(HostMessage::KeyboardInteractivityChange {
                            id,
                            keyboard_interactivity: keyboard(interactive),
                        }));
                    } else {
                        let id = window::Id::unique();
                        self.surfaces.insert(
                            role,
                            Surface {
                                id,
                                bounds,
                                interactive,
                                output: output_name,
                                opened: false,
                                input: Vec::new(),
                            },
                        );
                        let open = Task::done(HostMessage::NewLayerShell {
                            id,
                            settings: NewLayerShellSettings {
                                size: Some(dimensions(bounds.size())),
                                layer: Layer::Overlay,
                                anchor: Anchor::Top | Anchor::Left,
                                exclusive_zone: Some(-1),
                                margin: Some(margins(bounds.origin(), output.as_ref())),
                                keyboard_interactivity: keyboard(interactive),
                                output_option: output.map_or(OutputOption::Active, |output| {
                                    OutputOption::OutputName(output.name)
                                }),
                                namespace: Some(namespace(role).into()),
                                events_transparent: false,
                            },
                        });
                        tasks.push(open.chain(Task::done(HostMessage::App(
                            Message::SurfaceOpened(role, id),
                        ))));
                    }
                }
                SurfaceChange::Hide(role) => {
                    self.pressed.remove(&role);
                    if let Some(surface) = self.surfaces.remove(&role) {
                        tasks.push(window::close(surface.id));
                    }
                }
                SurfaceChange::Move { role, position } => {
                    if let Some(surface) = self.surfaces.get(&role) {
                        let mut bounds = surface.bounds;
                        bounds.x = position.x;
                        bounds.y = position.y;
                        let interactive = surface.interactive;
                        tasks.push(self.apply(vec![SurfaceChange::Show {
                            role,
                            bounds,
                            interactive,
                        }]));
                    }
                }
                SurfaceChange::Resize { role, size } => {
                    if let Some(surface) = self.surfaces.get(&role) {
                        let mut bounds = surface.bounds;
                        bounds.width = size.width;
                        bounds.height = size.height;
                        let interactive = surface.interactive;
                        tasks.push(self.apply(vec![SurfaceChange::Show {
                            role,
                            bounds,
                            interactive,
                        }]));
                    }
                }
                SurfaceChange::Keyboard { role, interactive } => {
                    if let Some(surface) = self.surfaces.get_mut(&role) {
                        surface.interactive = interactive;
                        tasks.push(Task::done(HostMessage::KeyboardInteractivityChange {
                            id: surface.id,
                            keyboard_interactivity: keyboard(interactive),
                        }));
                    }
                }
                SurfaceChange::Focus(role) => {
                    if let Some(surface) = self
                        .surfaces
                        .get(&role)
                        .filter(|surface| surface.interactive)
                    {
                        tasks.push(Task::done(HostMessage::KeyboardInteractivityChange {
                            id: surface.id,
                            keyboard_interactivity: KeyboardInteractivity::Exclusive,
                        }));
                    }
                }
                SurfaceChange::Quit => tasks.push(iced::exit()),
            }
        }
        let finished: Vec<_> = self
            .inputs
            .iter()
            .filter_map(|(&id, (role, _))| {
                (self.pressed.get(role) != Some(&id) || !self.surfaces.contains_key(role))
                    .then_some((id, *role))
            })
            .collect();
        for (id, role) in finished {
            self.inputs.remove(&id);
            tasks.push(window::close(id));
            if let Some(surface) = self.surfaces.get(&role) {
                tasks.push(Task::done(HostMessage::App(Message::SurfaceOpened(
                    role, surface.id,
                ))));
            }
        }
        self.sync_replicas(&mut tasks);
        for (role, source) in self.replicas.values_mut() {
            if let Some(current) = self.surfaces.get(role) {
                let output = self
                    .outputs
                    .iter()
                    .find(|output| output.name == source.output);
                if source.bounds.origin() != current.bounds.origin() {
                    tasks.push(Task::done(HostMessage::MarginChange {
                        id: source.id,
                        margin: margins(current.bounds.origin(), output),
                    }));
                }
                if source.bounds.size() != current.bounds.size() {
                    tasks.push(Task::done(HostMessage::SizeChange {
                        id: source.id,
                        size: dimensions(current.bounds.size()),
                    }));
                }
                source.bounds = current.bounds;
                if source.interactive != current.interactive {
                    source.interactive = current.interactive;
                    tasks.push(Task::done(HostMessage::KeyboardInteractivityChange {
                        id: source.id,
                        keyboard_interactivity: keyboard(current.interactive),
                    }));
                }
            }
        }
        for (&role, surface) in self.surfaces.iter_mut().chain(
            self.replicas
                .values_mut()
                .map(|(role, surface)| (&*role, surface)),
        ) {
            let rects = self.app.input_bounds(role);
            if surface.opened && surface.input != rects {
                surface.input.clone_from(&rects);
                tasks.push(Task::done(HostMessage::SetInputRegion {
                    id: surface.id,
                    callback: iced_layershell::actions::ActionCallback::new(move |region| {
                        for rect in &rects {
                            region.add(
                                rect.x.floor() as i32,
                                rect.y.floor() as i32,
                                rect.width.ceil().max(1.0) as i32,
                                rect.height.ceil().max(1.0) as i32,
                            );
                        }
                    }),
                }));
            }
        }
        Task::batch(tasks)
    }

    fn detach_input(
        &mut self,
        role: SurfaceRole,
        bounds: Rect,
        tasks: &mut Vec<Task<HostMessage>>,
    ) {
        let Some(&id) = self.pressed.get(&role) else {
            return;
        };
        if self.inputs.contains_key(&id) {
            return;
        }
        let original = self
            .surfaces
            .get(&role)
            .filter(|surface| surface.id == id)
            .or_else(|| self.replicas.get(&id).map(|(_, surface)| surface));
        if original.is_none_or(|surface| surface.bounds == bounds) {
            return;
        }
        let source = if self
            .surfaces
            .get(&role)
            .is_some_and(|surface| surface.id == id)
        {
            self.surfaces.remove(&role)
        } else {
            self.replicas.remove(&id).map(|(_, surface)| surface)
        };
        if let Some(source) = source {
            // Pointer motion is relative to this immutable compositor surface until release.
            // Moving it while processing that motion races the compositor's local transform.
            self.inputs.insert(id, (role, source));
            tasks.push(Task::done(HostMessage::SetInputRegion {
                id,
                callback: iced_layershell::actions::ActionCallback::new(|_| {}),
            }));
        }
    }

    // A layer is clipped to one output. Render the same global rectangle on every
    // intersecting output. The separate input source never moves during a gesture.
    fn sync_replicas(&mut self, tasks: &mut Vec<Task<HostMessage>>) {
        let obsolete: Vec<_> = self
            .replicas
            .iter()
            .filter_map(|(&id, (role, source))| {
                let owns_pointer = self.pressed.get(role) == Some(&id);
                let needed = self.surfaces.get(role).is_some_and(|current| {
                    current.output != source.output
                        && self.outputs.iter().any(|output| {
                            output.name == source.output
                                && intersects(current.bounds, output.monitor.bounds)
                        })
                });
                (!owns_pointer && !needed).then_some(id)
            })
            .collect();
        for id in obsolete {
            self.replicas.remove(&id);
            tasks.push(window::close(id));
        }
        for (&role, current) in &self.surfaces {
            for output in &self.outputs {
                if output.name == current.output
                    || !intersects(current.bounds, output.monitor.bounds)
                    || self.replicas.values().any(|(source_role, source)| {
                        *source_role == role && source.output == output.name
                    })
                {
                    continue;
                }
                let id = window::Id::unique();
                let mut surface = current.clone();
                surface.id = id;
                surface.output.clone_from(&output.name);
                surface.opened = false;
                surface.input.clear();
                self.replicas.insert(id, (role, surface));
                tasks.push(
                    Task::done(HostMessage::NewLayerShell {
                        id,
                        settings: NewLayerShellSettings {
                            size: Some(dimensions(current.bounds.size())),
                            layer: Layer::Overlay,
                            anchor: Anchor::Top | Anchor::Left,
                            exclusive_zone: Some(-1),
                            margin: Some(margins(current.bounds.origin(), Some(output))),
                            keyboard_interactivity: keyboard(current.interactive),
                            output_option: OutputOption::OutputName(output.name.clone()),
                            namespace: Some(namespace(role).into()),
                            events_transparent: false,
                        },
                    })
                    .chain(Task::done(HostMessage::ReplicaOpened(id))),
                );
            }
        }
    }

    fn view(&self, id: window::Id) -> Element<'_, HostMessage> {
        if self.inputs.contains_key(&id) {
            return iced::widget::Space::new().into();
        }
        self.role(id).map_or_else(
            || iced::widget::Space::new().into(),
            |role| self.app.view(role).map(HostMessage::App),
        )
    }

    fn subscription(&self) -> Subscription<HostMessage> {
        Subscription::batch([
            self.app.subscription().map(HostMessage::App),
            event::listen_with(|event, _, id| Some(HostMessage::Event(id, event))),
            Subscription::run(output_changes),
        ])
    }
}

fn namespace(role: SurfaceRole) -> &'static str {
    match role {
        SurfaceRole::Note => "task-focuser-note",
        SurfaceRole::Dot => "task-focuser-dot",
        SurfaceRole::CloseTarget => "task-focuser-close-target",
        SurfaceRole::Bird => "task-focuser-bird",
        SurfaceRole::Cucumber => "task-focuser-cucumber",
    }
}

fn keyboard(interactive: bool) -> KeyboardInteractivity {
    if interactive {
        KeyboardInteractivity::OnDemand
    } else {
        KeyboardInteractivity::None
    }
}

fn dimensions(size: Size) -> (u32, u32) {
    (
        size.width.round().clamp(1.0, u32::MAX as f64) as u32,
        size.height.round().clamp(1.0, u32::MAX as f64) as u32,
    )
}

// Protocol order is top, right, bottom, left. The macro documentation incorrectly
// labels its second entry as left. Coordinates remain output-local logical pixels.
fn margins(position: Point, output: Option<&Output>) -> (i32, i32, i32, i32) {
    let origin = output.map_or(Point::default(), |output| output.monitor.bounds.origin());
    (
        (position.y - origin.y).round() as i32,
        0,
        0,
        (position.x - origin.x).round() as i32,
    )
}

fn intersects(a: Rect, b: Rect) -> bool {
    a.x < b.right() && a.right() > b.x && a.y < b.bottom() && a.bottom() > b.y
}

fn output_changes() -> impl iced::futures::Stream<Item = HostMessage> {
    iced::stream::channel(
        1,
        |mut sender: iced::futures::channel::mpsc::Sender<HostMessage>| async move {
            use iced::futures::SinkExt;
            let mut previous = read_outputs().await;
            loop {
                tokio::time::sleep(Duration::from_secs(2)).await;
                let current = read_outputs().await;
                if current != previous {
                    previous.clone_from(&current);
                    if sender.send(HostMessage::Outputs(current)).await.is_err() {
                        break;
                    }
                }
            }
        },
    )
}

async fn read_outputs() -> Result<Vec<Output>, String> {
    tokio::task::spawn_blocking(discover_outputs)
        .await
        .map_err(|error| error.to_string())?
        .map_err(|error| error.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;
    use task_focuser_core::geometry::Monitor;

    #[test]
    fn layer_margins_preserve_negative_monitor_origins_and_straddling_positions() {
        let bounds = Rect::new(-1920.0, -200.0, 1920.0, 1080.0);
        let output = Output {
            name: "DP-2".into(),
            monitor: Monitor::new(bounds, bounds),
        };
        assert_eq!(
            margins(Point::new(-1800.0, -150.0), Some(&output)),
            (50, 0, 0, 120)
        );
        assert_eq!(
            margins(Point::new(-1940.0, -215.0), Some(&output)),
            (-15, 0, 0, -20)
        );
        assert_eq!(dimensions(Size::new(140.2, 80.8)), (140, 81));
    }
}
