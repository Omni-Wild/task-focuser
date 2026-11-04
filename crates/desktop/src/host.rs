//! Selects the window system before either event loop is created.

use anyhow::Context;

use crate::surface::{Backend, Startup};

pub fn run(startup: Startup) -> anyhow::Result<()> {
    match startup.backend {
        Backend::Native => crate::native::run(startup).context("native desktop host failed"),
        #[cfg(target_os = "linux")]
        Backend::Wayland => crate::wayland::run(startup).context("Wayland layer host failed"),
    }
}

/// An output name is required when placing a layer on a particular monitor.
#[cfg(target_os = "linux")]
#[derive(Debug, Clone, PartialEq)]
pub(crate) struct Output {
    pub name: String,
    pub monitor: task_focuser_core::geometry::Monitor,
}

#[cfg(target_os = "linux")]
pub(crate) fn discover_outputs() -> anyhow::Result<Vec<Output>> {
    output_discovery::discover()
}

#[cfg(target_os = "linux")]
mod output_discovery {
    use super::Output;
    use anyhow::{Context, bail};
    use std::{
        collections::BTreeMap,
        io::{Read, Write},
        os::unix::net::UnixStream,
        path::PathBuf,
        time::Duration,
    };
    use task_focuser_core::geometry::{Monitor, Rect};
    use wayland_client::{
        Connection, Dispatch, QueueHandle, WEnum, delegate_noop,
        protocol::{
            wl_output::{self, WlOutput},
            wl_registry::{self, WlRegistry},
        },
    };
    use wayland_protocols::xdg::xdg_output::zv1::client::{
        zxdg_output_manager_v1::ZxdgOutputManagerV1,
        zxdg_output_v1::{self, ZxdgOutputV1},
    };

    #[derive(Default)]
    struct Info {
        name: String,
        x: i32,
        y: i32,
        width: i32,
        height: i32,
        scale: i32,
        logical: Option<Rect>,
        logical_position: Option<(i32, i32)>,
        rotated: bool,
    }

    #[derive(Default)]
    struct Discovery {
        outputs: BTreeMap<u32, (WlOutput, Info)>,
        manager: Option<ZxdgOutputManagerV1>,
    }

    impl Dispatch<WlRegistry, ()> for Discovery {
        fn event(
            state: &mut Self,
            registry: &WlRegistry,
            event: wl_registry::Event,
            _: &(),
            _: &Connection,
            qh: &QueueHandle<Self>,
        ) {
            match event {
                wl_registry::Event::Global {
                    name,
                    interface,
                    version,
                } if interface == "wl_output" => {
                    let output = registry.bind::<WlOutput, _, _>(name, version.min(4), qh, name);
                    state.outputs.insert(
                        name,
                        (
                            output,
                            Info {
                                scale: 1,
                                ..Info::default()
                            },
                        ),
                    );
                }
                wl_registry::Event::Global {
                    name,
                    interface,
                    version,
                } if interface == "zxdg_output_manager_v1" => {
                    state.manager = Some(registry.bind(name, version.min(3), qh, ()));
                }
                _ => {}
            }
        }
    }

    impl Dispatch<WlOutput, u32> for Discovery {
        fn event(
            state: &mut Self,
            _: &WlOutput,
            event: wl_output::Event,
            name: &u32,
            _: &Connection,
            _: &QueueHandle<Self>,
        ) {
            let Some((_, info)) = state.outputs.get_mut(name) else {
                return;
            };
            match event {
                wl_output::Event::Geometry {
                    x, y, transform, ..
                } => {
                    info.x = x;
                    info.y = y;
                    info.rotated = matches!(
                        transform,
                        WEnum::Value(
                            wl_output::Transform::_90
                                | wl_output::Transform::_270
                                | wl_output::Transform::Flipped90
                                | wl_output::Transform::Flipped270
                        )
                    );
                }
                wl_output::Event::Mode {
                    flags: WEnum::Value(flags),
                    width,
                    height,
                    ..
                } if flags.contains(wl_output::Mode::Current) => {
                    info.width = width;
                    info.height = height;
                }
                wl_output::Event::Scale { factor } => info.scale = factor.max(1),
                wl_output::Event::Name { name } => info.name = name,
                _ => {}
            }
        }
    }

    impl Dispatch<ZxdgOutputV1, u32> for Discovery {
        fn event(
            state: &mut Self,
            _: &ZxdgOutputV1,
            event: zxdg_output_v1::Event,
            name: &u32,
            _: &Connection,
            _: &QueueHandle<Self>,
        ) {
            let Some((_, info)) = state.outputs.get_mut(name) else {
                return;
            };
            match event {
                zxdg_output_v1::Event::LogicalPosition { x, y } => {
                    info.logical_position = Some((x, y));
                    if let Some(rect) = &mut info.logical {
                        rect.x = x as f64;
                        rect.y = y as f64;
                    }
                }
                zxdg_output_v1::Event::LogicalSize { width, height } => {
                    let (x, y) = info.logical_position.unwrap_or((info.x, info.y));
                    info.logical = Some(Rect::new(x as f64, y as f64, width as f64, height as f64));
                }
                zxdg_output_v1::Event::Name { name } => info.name = name,
                _ => {}
            }
        }
    }
    delegate_noop!(Discovery: ignore ZxdgOutputManagerV1);

    pub(super) fn discover() -> anyhow::Result<Vec<Output>> {
        let connection =
            Connection::connect_to_env().context("connect to Wayland for output discovery")?;
        let mut queue = connection.new_event_queue::<Discovery>();
        let qh = queue.handle();
        let _registry = connection.display().get_registry(&qh, ());
        let mut state = Discovery::default();
        queue
            .roundtrip(&mut state)
            .context("enumerate Wayland globals")?;
        let mut logical_outputs = Vec::new();
        if let Some(manager) = &state.manager {
            for (&name, (output, _)) in &state.outputs {
                logical_outputs.push(manager.get_xdg_output(output, &qh, name));
            }
        }
        queue
            .roundtrip(&mut state)
            .context("read Wayland output geometry")?;
        let mut outputs = Vec::with_capacity(state.outputs.len());
        for (_, (_, info)) in state.outputs {
            let bounds = info.logical.unwrap_or_else(|| {
                let (width, height) = if info.rotated {
                    (info.height, info.width)
                } else {
                    (info.width, info.height)
                };
                Rect::new(
                    info.x as f64,
                    info.y as f64,
                    width as f64 / info.scale as f64,
                    height as f64 / info.scale as f64,
                )
            });
            if bounds.width <= 0.0 || bounds.height <= 0.0 || info.name.is_empty() {
                bail!("Wayland output has incomplete logical geometry or name");
            }
            outputs.push(Output {
                name: info.name,
                monitor: Monitor::new(bounds, bounds),
            });
        }
        if outputs.is_empty() {
            bail!("the Wayland compositor reported no outputs");
        }
        apply_hyprland_work_areas(&mut outputs)?;
        Ok(outputs)
    }

    // Wayland has no standard work-area query. Hyprland reports each reserved edge.
    fn apply_hyprland_work_areas(outputs: &mut [Output]) -> anyhow::Result<()> {
        let Some(signature) = std::env::var_os("HYPRLAND_INSTANCE_SIGNATURE") else {
            return Ok(());
        };
        if signature.to_string_lossy().contains('/') {
            bail!("invalid Hyprland instance signature");
        }
        let runtime = std::env::var_os("XDG_RUNTIME_DIR")
            .map(PathBuf::from)
            .context("Hyprland session has no XDG_RUNTIME_DIR")?;
        let socket_path = runtime.join("hypr").join(&signature).join(".socket.sock");
        let mut stream = UnixStream::connect(&socket_path).with_context(|| {
            format!(
                "connect to Hyprland monitor socket {}",
                socket_path.display()
            )
        })?;
        stream.set_read_timeout(Some(Duration::from_secs(1)))?;
        stream.set_write_timeout(Some(Duration::from_secs(1)))?;
        stream.write_all(b"j/monitors")?;
        let mut bytes = Vec::new();
        stream
            .take(1024 * 1024)
            .read_to_end(&mut bytes)
            .context("read Hyprland monitors")?;
        let monitors: serde_json::Value =
            serde_json::from_slice(&bytes).context("parse Hyprland monitor response")?;
        let monitors = monitors
            .as_array()
            .context("Hyprland monitor response is not an array")?;
        for output in outputs {
            let Some(monitor) = monitors
                .iter()
                .find(|monitor| monitor["name"].as_str() == Some(output.name.as_str()))
            else {
                continue;
            };
            let reserved = monitor["reserved"]
                .as_array()
                .context("Hyprland monitor has no reserved edge geometry")?;
            if reserved.len() != 4 {
                bail!("Hyprland reserved edge geometry needs four entries");
            }
            let edge = |index: usize| {
                reserved[index]
                    .as_f64()
                    .context("Hyprland reserved edge is not numeric")
            };
            let (left, top, right, bottom) = (edge(0)?, edge(1)?, edge(2)?, edge(3)?);
            let bounds = output.monitor.bounds;
            output.monitor.work_area = Rect::new(
                bounds.x + left,
                bounds.y + top,
                (bounds.width - left - right).max(1.0),
                (bounds.height - top - bottom).max(1.0),
            );
        }
        Ok(())
    }
}

/// Reads actual monitor geometry before restoring any saved window positions.
pub fn monitors(backend: Backend) -> anyhow::Result<Vec<task_focuser_core::geometry::Monitor>> {
    match backend {
        #[cfg(target_os = "linux")]
        Backend::Wayland => Ok(discover_outputs()?
            .into_iter()
            .map(|output| output.monitor)
            .collect()),
        Backend::Native => {
            task_focuser_platform::monitors::monitors().context("read native monitor geometry")
        }
    }
}
