//! X11 focus policy and input-only shape for transparent native surfaces.

use std::io;
use task_focuser_core::geometry::Rect;
use x11rb::{
    CURRENT_TIME,
    connection::Connection,
    properties::WmHints,
    protocol::{
        shape::{ConnectionExt as _, SK, SO},
        xproto::{
            AtomEnum, ClientMessageEvent, ClipOrdering, ConfigureWindowAux, ConnectionExt as _,
            EventMask, InputFocus, PropMode, Rectangle, StackMode,
        },
    },
    wrapper::ConnectionExt as _,
};

pub fn configure_window(xid: u32, interactive: bool) -> io::Result<()> {
    let (connection, screen) = x11rb::connect(None).map_err(io::Error::other)?;
    let _ = screen;
    let mut hints = WmHints::get(&connection, xid)
        .map_err(io::Error::other)?
        .reply()
        .map_err(io::Error::other)?
        .unwrap_or_default();
    hints.input = Some(interactive);
    hints
        .set(&connection, xid)
        .map_err(io::Error::other)?
        .check()
        .map_err(io::Error::other)?;
    let atom = |name: &str| {
        connection
            .intern_atom(false, name.as_bytes())
            .map_err(io::Error::other)?
            .reply()
            .map(|reply| reply.atom)
            .map_err(io::Error::other)
    };
    let state = atom("_NET_WM_STATE")?;
    let values = [
        atom("_NET_WM_STATE_ABOVE")?,
        atom("_NET_WM_STATE_SKIP_TASKBAR")?,
        atom("_NET_WM_STATE_SKIP_PAGER")?,
    ];
    connection
        .change_property32(PropMode::REPLACE, xid, state, AtomEnum::ATOM, &values)
        .map_err(io::Error::other)?
        .check()
        .map_err(io::Error::other)?;
    connection
        .change_property32(
            PropMode::REPLACE,
            xid,
            atom("_NET_WM_WINDOW_TYPE")?,
            AtomEnum::ATOM,
            &[atom("_NET_WM_WINDOW_TYPE_UTILITY")?],
        )
        .map_err(io::Error::other)?
        .check()
        .map_err(io::Error::other)?;
    connection
        .configure_window(xid, &ConfigureWindowAux::new().stack_mode(StackMode::ABOVE))
        .map_err(io::Error::other)?
        .check()
        .map_err(io::Error::other)?;
    connection.flush().map_err(io::Error::other)
}

/// Call after mapping a window, when the user explicitly starts editing.
pub fn focus_window(xid: u32) -> io::Result<()> {
    restore_foreground(xid)
}

pub fn capture_foreground() -> Option<u32> {
    let result = (|| {
        let (connection, screen) = x11rb::connect(None).map_err(io::Error::other)?;
        let root = connection.setup().roots[screen].root;
        let atom = connection
            .intern_atom(true, b"_NET_ACTIVE_WINDOW")
            .map_err(io::Error::other)?
            .reply()
            .map_err(io::Error::other)?
            .atom;
        if atom != 0 {
            let reply = connection
                .get_property(false, root, atom, AtomEnum::WINDOW, 0, 1)
                .map_err(io::Error::other)?
                .reply()
                .map_err(io::Error::other)?;
            if let Some(window) = reply
                .value32()
                .and_then(|mut values| values.next())
                .filter(|id| *id > 1)
            {
                return Ok(Some(window));
            }
        }
        connection
            .get_input_focus()
            .map_err(io::Error::other)?
            .reply()
            .map(|reply| (reply.focus > 1).then_some(reply.focus))
            .map_err(io::Error::other)
    })();
    match result {
        Ok(window) => window,
        Err(error) => {
            tracing::warn!(%error,"could not capture X11 foreground window");
            None
        }
    }
}

pub fn restore_foreground(xid: u32) -> io::Result<()> {
    let (connection, screen) = x11rb::connect(None).map_err(io::Error::other)?;
    let root = connection.setup().roots[screen].root;
    let atom = connection
        .intern_atom(false, b"_NET_ACTIVE_WINDOW")
        .map_err(io::Error::other)?
        .reply()
        .map_err(io::Error::other)?
        .atom;
    let event = ClientMessageEvent::new(32, xid, atom, [1, CURRENT_TIME, 0, 0, 0]);
    connection
        .send_event(
            false,
            root,
            EventMask::SUBSTRUCTURE_REDIRECT | EventMask::SUBSTRUCTURE_NOTIFY,
            event,
        )
        .map_err(io::Error::other)?
        .check()
        .map_err(io::Error::other)?;
    connection
        .set_input_focus(InputFocus::PARENT, xid, CURRENT_TIME)
        .map_err(io::Error::other)?
        .check()
        .map_err(io::Error::other)?;
    connection.flush().map_err(io::Error::other)
}

/// Changes pointer hit testing without clipping the rendered transparent effects stage.
pub fn configure_input_region(xid: u32, scale: f64, rects: &[Rect]) -> io::Result<()> {
    let rectangles = physical_rectangles(scale, rects)?;
    let (connection, _) = x11rb::connect(None).map_err(io::Error::other)?;
    let version = connection
        .shape_query_version()
        .map_err(io::Error::other)?
        .reply()
        .map_err(io::Error::other)?;
    if (version.major_version, version.minor_version) < (1, 1) {
        return Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "XShape 1.1 is required for transparent input regions",
        ));
    }
    connection
        .shape_rectangles(
            SO::SET,
            SK::INPUT,
            ClipOrdering::UNSORTED,
            xid,
            0,
            0,
            &rectangles,
        )
        .map_err(io::Error::other)?
        .check()
        .map_err(io::Error::other)?;
    connection.flush().map_err(io::Error::other)
}

fn physical_rectangles(scale: f64, rects: &[Rect]) -> io::Result<Vec<Rectangle>> {
    if !scale.is_finite() || scale <= 0. {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "window scale must be positive",
        ));
    }
    rects
        .iter()
        .filter(|rect| rect.width > 0. && rect.height > 0.)
        .map(|rect| {
            let x = (rect.x * scale).floor();
            let y = (rect.y * scale).floor();
            let width = ((rect.x + rect.width) * scale).ceil() - x;
            let height = ((rect.y + rect.height) * scale).ceil() - y;
            if ![x, y, width, height].iter().all(|value| value.is_finite())
                || x < f64::from(i16::MIN)
                || x > f64::from(i16::MAX)
                || y < f64::from(i16::MIN)
                || y > f64::from(i16::MAX)
                || width > f64::from(u16::MAX)
                || height > f64::from(u16::MAX)
            {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "input region is outside X11 coordinate limits",
                ));
            }
            Ok(Rectangle {
                x: x as i16,
                y: y as i16,
                width: width as u16,
                height: height as u16,
            })
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn input_region_rounds_outward_for_fractional_scale() {
        let rectangles =
            physical_rectangles(1.5, &[Rect::new(0.5, 1.5, 10., 20.)]).expect("valid rectangle");
        assert_eq!(
            rectangles
                .iter()
                .map(|rect| (rect.x, rect.y, rect.width, rect.height))
                .collect::<Vec<_>>(),
            vec![(0, 2, 16, 31)]
        );
        assert!(physical_rectangles(f64::NAN, &[]).is_err());
        assert!(physical_rectangles(1., &[Rect::new(40_000., 0., 1., 1.)]).is_err());
    }
}
