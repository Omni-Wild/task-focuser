//! Native monitor bounds in the desktop host's logical top-left coordinate system.

use std::io;
use task_focuser_core::geometry::{Monitor, Rect};

pub fn monitors() -> io::Result<Vec<Monitor>> {
    let monitors = native::monitors()?;
    if monitors.is_empty() {
        return Err(io::Error::new(
            io::ErrorKind::NotFound,
            "the desktop reported no monitors",
        ));
    }
    Ok(monitors)
}

#[cfg(target_os = "macos")]
mod native {
    use super::*;
    use objc2::MainThreadMarker;
    use objc2_app_kit::NSScreen;
    pub fn monitors() -> io::Result<Vec<Monitor>> {
        if MainThreadMarker::new().is_some() {
            return monitors_on_main();
        }
        let (sender, receiver) = std::sync::mpsc::sync_channel(1);
        let block = block2::RcBlock::new(move || {
            let _ = sender.send(monitors_on_main());
        });
        // SAFETY: this block captures only a Send channel sender and evaluates NSScreen exclusively on the main queue.
        unsafe {
            objc2_foundation::NSOperationQueue::mainQueue().addOperationWithBlock(&block);
        }
        receiver
            .recv_timeout(std::time::Duration::from_secs(2))
            .map_err(|error| {
                io::Error::other(format!("main-thread monitor discovery failed: {error}"))
            })?
    }
    fn monitors_on_main() -> io::Result<Vec<Monitor>> {
        let marker = MainThreadMarker::new()
            .ok_or_else(|| io::Error::other("monitor discovery requires the AppKit main thread"))?;
        let screens = NSScreen::screens(marker);
        let Some(primary) = screens.firstObject() else {
            return Ok(Vec::new());
        };
        let top = primary.frame().origin.y + primary.frame().size.height;
        let rect = |frame: objc2_foundation::NSRect| {
            Rect::new(
                frame.origin.x,
                top - frame.origin.y - frame.size.height,
                frame.size.width,
                frame.size.height,
            )
        };
        Ok(screens
            .iter()
            .map(|screen| Monitor::new(rect(screen.frame()), rect(screen.visibleFrame())))
            .collect())
    }
}

#[cfg(windows)]
mod native {
    use super::*;
    use windows_sys::Win32::{
        Foundation::{LPARAM, RECT},
        Graphics::Gdi::{EnumDisplayMonitors, GetMonitorInfoW, HDC, HMONITOR, MONITORINFO},
    };
    use windows_sys::core::BOOL;
    pub fn monitors() -> io::Result<Vec<Monitor>> {
        struct Collection {
            monitors: Vec<Monitor>,
            error: Option<io::Error>,
        }
        unsafe extern "system" fn collect(
            handle: HMONITOR,
            _: HDC,
            _: *mut RECT,
            data: LPARAM,
        ) -> BOOL {
            // SAFETY: EnumDisplayMonitors receives the live mutable collection pointer below and calls synchronously.
            let collection = unsafe { &mut *(data as *mut Collection) };
            let mut info = MONITORINFO {
                cbSize: std::mem::size_of::<MONITORINFO>() as u32,
                rcMonitor: RECT {
                    left: 0,
                    top: 0,
                    right: 0,
                    bottom: 0,
                },
                rcWork: RECT {
                    left: 0,
                    top: 0,
                    right: 0,
                    bottom: 0,
                },
                dwFlags: 0,
            };
            // SAFETY: info has the required size and valid writable storage; handle comes from EnumDisplayMonitors.
            if unsafe { GetMonitorInfoW(handle, &mut info) } == 0 {
                collection.error = Some(io::Error::last_os_error());
                return 0;
            }
            let scale = match crate::windows::system_scale() {
                Ok(scale) => scale,
                Err(error) => {
                    collection.error = Some(error);
                    return 0;
                }
            };
            let scale_x = scale;
            let scale_y = scale;
            let rect = |rect: RECT| {
                Rect::new(
                    f64::from(rect.left) / scale_x,
                    f64::from(rect.top) / scale_y,
                    f64::from(rect.right - rect.left) / scale_x,
                    f64::from(rect.bottom - rect.top) / scale_y,
                )
            };
            collection
                .monitors
                .push(Monitor::new(rect(info.rcMonitor), rect(info.rcWork)));
            1
        }
        let mut collection = Collection {
            monitors: Vec::new(),
            error: None,
        };
        // SAFETY: callback and collection pointer remain valid for the complete synchronous enumeration.
        let success = unsafe {
            EnumDisplayMonitors(
                std::ptr::null_mut(),
                std::ptr::null(),
                Some(collect),
                (&mut collection as *mut Collection) as LPARAM,
            )
        };
        if let Some(error) = collection.error {
            return Err(error);
        }
        if success == 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(collection.monitors)
    }
}

#[cfg(target_os = "linux")]
mod native {
    use super::*;
    use x11rb::{
        connection::Connection,
        protocol::{
            randr::ConnectionExt as _,
            xproto::{AtomEnum, ConnectionExt as _, Window},
        },
        rust_connection::RustConnection,
    };
    fn property(
        connection: &RustConnection,
        root: Window,
        name: &str,
        kind: AtomEnum,
    ) -> io::Result<Vec<u32>> {
        let atom = connection
            .intern_atom(true, name.as_bytes())
            .map_err(io::Error::other)?
            .reply()
            .map_err(io::Error::other)?
            .atom;
        if atom == 0 {
            return Ok(Vec::new());
        }
        let reply = connection
            .get_property(false, root, atom, kind, 0, 1024)
            .map_err(io::Error::other)?
            .reply()
            .map_err(io::Error::other)?;
        Ok(reply
            .value32()
            .map(|values| values.collect())
            .unwrap_or_default())
    }
    pub fn monitors() -> io::Result<Vec<Monitor>> {
        let (connection, screen) = x11rb::connect(None).map_err(io::Error::other)?;
        let root = connection.setup().roots[screen].root;
        let current = property(
            &connection,
            root,
            "_NET_CURRENT_DESKTOP",
            AtomEnum::CARDINAL,
        )?
        .first()
        .copied()
        .unwrap_or(0) as usize;
        let workareas = property(&connection, root, "_NET_WORKAREA", AtomEnum::CARDINAL)?;
        let desktop_work = workareas.chunks_exact(4).nth(current).map(|v| {
            Rect::new(
                f64::from(v[0] as i32),
                f64::from(v[1] as i32),
                f64::from(v[2]),
                f64::from(v[3]),
            )
        });
        let scale = x11_scale(&connection, root)?;
        let reply = connection
            .randr_get_monitors(root, true)
            .map_err(io::Error::other)?
            .reply()
            .map_err(io::Error::other)?;
        Ok(reply
            .monitors
            .into_iter()
            .filter(|monitor| monitor.width > 0 && monitor.height > 0)
            .map(|monitor| {
                let bounds = Rect::new(
                    f64::from(monitor.x),
                    f64::from(monitor.y),
                    f64::from(monitor.width),
                    f64::from(monitor.height),
                );
                let work = desktop_work
                    .map(|area| intersection(bounds, area))
                    .unwrap_or(bounds);
                let logical = |rect: Rect| {
                    Rect::new(
                        rect.x / scale,
                        rect.y / scale,
                        rect.width / scale,
                        rect.height / scale,
                    )
                };
                Monitor::new(logical(bounds), logical(work))
            })
            .collect())
    }
    fn intersection(a: Rect, b: Rect) -> Rect {
        let x = a.x.max(b.x);
        let y = a.y.max(b.y);
        let right = a.right().min(b.right());
        let bottom = a.bottom().min(b.bottom());
        if right <= x || bottom <= y {
            a
        } else {
            Rect::new(x, y, right - x, bottom - y)
        }
    }
    fn x11_scale(connection: &RustConnection, root: Window) -> io::Result<f64> {
        if let Ok(value) = std::env::var("WINIT_X11_SCALE_FACTOR")
            && value != "randr"
        {
            return value
                .parse::<f64>()
                .ok()
                .filter(|scale| scale.is_finite() && *scale > 0.)
                .ok_or_else(|| {
                    io::Error::new(
                        io::ErrorKind::InvalidInput,
                        "WINIT_X11_SCALE_FACTOR must be a positive number",
                    )
                });
        }
        let reply = connection
            .get_property(
                false,
                root,
                AtomEnum::RESOURCE_MANAGER,
                AtomEnum::STRING,
                0,
                65536,
            )
            .map_err(io::Error::other)?
            .reply()
            .map_err(io::Error::other)?;
        let resources = String::from_utf8_lossy(&reply.value);
        let dpi = resources
            .lines()
            .filter_map(|line| line.split_once(':'))
            .find_map(|(key, value)| {
                (key.trim() == "Xft.dpi")
                    .then(|| value.trim().parse::<f64>().ok())
                    .flatten()
            })
            .filter(|dpi| dpi.is_finite() && *dpi > 0.);
        Ok(dpi.map(|dpi| dpi / 96.).unwrap_or(1.))
    }
}
