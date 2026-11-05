//! AppKit window levels and foreground restoration for the native desktop host.

use objc2::{MainThreadMarker, rc::Retained};
use objc2_app_kit::{
    NSApplicationActivationOptions, NSFloatingWindowLevel, NSRunningApplication, NSView, NSWindow,
    NSWindowCollectionBehavior, NSWorkspace,
};
use std::io;

fn window(ns_view: usize) -> io::Result<Retained<NSWindow>> {
    MainThreadMarker::new()
        .ok_or_else(|| io::Error::other("AppKit window operations require the main thread"))?;
    if ns_view == 0 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "the native view is null",
        ));
    }
    // SAFETY: the desktop host supplies the live NSView from its current AppKit raw window handle.
    let view = unsafe { &*(ns_view as *const NSView) };
    view.window()
        .ok_or_else(|| io::Error::other("the native view has no window"))
}

pub fn configure_window(ns_view: usize, interactive: bool) -> io::Result<()> {
    let window = window(ns_view)?;
    window.setLevel(NSFloatingWindowLevel);
    window.setHidesOnDeactivate(false);
    window.setCanHide(false);
    window.setCollectionBehavior(
        NSWindowCollectionBehavior::CanJoinAllSpaces
            | NSWindowCollectionBehavior::Stationary
            | NSWindowCollectionBehavior::IgnoresCycle
            | NSWindowCollectionBehavior::FullScreenAuxiliary,
    );
    set_interactive(ns_view, interactive)
}

/// Enables editing or suppresses AppKit activation while preserving native pointer events.
pub fn set_interactive(ns_view: usize, interactive: bool) -> io::Result<()> {
    install_input_policy(ns_view, interactive)?;
    let window = window(ns_view)?;
    install_window_policy(&window, interactive)?;
    if !interactive {
        window.resignKeyWindow();
    }
    Ok(())
}

pub fn capture_foreground() -> Option<i32> {
    MainThreadMarker::new()?;
    NSWorkspace::sharedWorkspace()
        .frontmostApplication()
        .map(|app| app.processIdentifier())
}

pub fn restore_foreground(pid: i32) -> io::Result<()> {
    MainThreadMarker::new()
        .ok_or_else(|| io::Error::other("foreground restoration requires the main thread"))?;
    let app =
        NSRunningApplication::runningApplicationWithProcessIdentifier(pid).ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::NotFound,
                "the previous foreground app has quit",
            )
        })?;
    if !app.activateWithOptions(NSApplicationActivationOptions::empty()) {
        return Err(io::Error::other(
            "macOS did not restore the previous foreground app",
        ));
    }
    Ok(())
}

// These overrides preserve the backend's class, instance storage and inherited event handlers.
// AppKit explicitly supports suppressing window activation through delayed window ordering.
use objc2::{
    msg_send,
    runtime::{AnyClass, AnyObject, Bool, ClassBuilder, Sel},
    sel,
};
use objc2_app_kit::{NSApplication, NSEvent};
use objc2_foundation::NSPoint;
use std::{cell::RefCell, collections::HashMap, ffi::CString};
use task_focuser_core::geometry::Rect;

#[derive(Default)]
struct InputPolicy {
    interactive: bool,
    rectangles: Option<Vec<Rect>>,
}
thread_local! { static INPUT_POLICIES: RefCell<HashMap<usize, InputPolicy>> = RefCell::new(HashMap::new()); }

fn passive(view: &NSView) -> bool {
    INPUT_POLICIES.with(|policies| {
        policies
            .borrow()
            .get(&(view as *const NSView as usize))
            .is_some_and(|policy| !policy.interactive)
    })
}

extern "C-unwind" fn delay_ordering(view: &NSView, _: Sel, event: &NSEvent) -> Bool {
    if passive(view) {
        return Bool::YES;
    }
    let Some(parent) = view.class().superclass() else {
        return Bool::NO;
    };
    // SAFETY: the injected class directly subclasses the original view, whose selector accepts NSEvent and returns BOOL.
    unsafe { msg_send![super(view, parent), shouldDelayWindowOrderingForEvent: event] }
}
extern "C-unwind" fn accepts_first_mouse(view: &NSView, _: Sel, event: &NSEvent) -> Bool {
    if passive(view) {
        return Bool::YES;
    }
    let Some(parent) = view.class().superclass() else {
        return Bool::NO;
    };
    // SAFETY: acceptsFirstMouse: has the unchanged NSEvent argument and BOOL return signature.
    unsafe { msg_send![super(view,parent), acceptsFirstMouse: event] }
}
extern "C-unwind" fn dealloc_view(view: &AnyObject, _: Sel) {
    INPUT_POLICIES.with(|policies| {
        policies
            .borrow_mut()
            .remove(&(view as *const AnyObject as usize));
    });
    let Some(parent) = view.class().superclass() else {
        return;
    };
    // SAFETY: the no-ivar subclass owns no Objective-C storage; the backend's original dealloc retains its original allocation and layout.
    unsafe {
        let _: () = msg_send![super(view, parent), dealloc];
    }
}
fn prevent_ordering(view: &NSView) {
    if passive(view)
        && let Some(marker) = MainThreadMarker::new()
    {
        NSApplication::sharedApplication(marker).preventWindowOrdering();
    }
}
extern "C-unwind" fn mouse_down(view: &NSView, _: Sel, event: &NSEvent) {
    prevent_ordering(view);
    let Some(parent) = view.class().superclass() else {
        return;
    };
    // SAFETY: superclass is the unmodified backend view and mouseDown: uses the original NSEvent signature.
    unsafe {
        let _: () = msg_send![super(view, parent), mouseDown: event];
    }
}
extern "C-unwind" fn right_mouse_down(view: &NSView, _: Sel, event: &NSEvent) {
    prevent_ordering(view);
    let Some(parent) = view.class().superclass() else {
        return;
    };
    // SAFETY: superclass is the unmodified backend view and rightMouseDown: uses the original NSEvent signature.
    unsafe {
        let _: () = msg_send![super(view, parent), rightMouseDown: event];
    }
}
extern "C-unwind" fn other_mouse_down(view: &NSView, _: Sel, event: &NSEvent) {
    prevent_ordering(view);
    let Some(parent) = view.class().superclass() else {
        return;
    };
    // SAFETY: superclass is the unmodified backend view and otherMouseDown: uses the original NSEvent signature.
    unsafe {
        let _: () = msg_send![super(view, parent), otherMouseDown: event];
    }
}
extern "C-unwind" fn hit_test(view: &NSView, _: Sel, point: NSPoint) -> *mut AnyObject {
    // SAFETY: hitTest runs synchronously for a live view on the AppKit main thread; its retained parent is used only for coordinate conversion.
    let parent_view = unsafe { view.superview() };
    let local = view.convertPoint_fromView(point, parent_view.as_deref());
    let inside = INPUT_POLICIES.with(|policies| {
        policies
            .borrow()
            .get(&(view as *const NSView as usize))
            .and_then(|policy| policy.rectangles.as_ref())
            .is_none_or(|rectangles| {
                rectangles.iter().any(|rect| {
                    rect.contains(task_focuser_core::geometry::Point::new(local.x, local.y))
                })
            })
    });
    if !inside {
        return std::ptr::null_mut();
    }
    let Some(parent) = view.class().superclass() else {
        return std::ptr::null_mut();
    };
    // SAFETY: hitTest: has the original NSPoint argument and nullable object return; original backend behavior is retained inside the input region.
    unsafe { msg_send![super(view, parent), hitTest: point] }
}

fn install_input_policy(ns_view: usize, interactive: bool) -> io::Result<()> {
    window(ns_view)?;
    // SAFETY: window() checked main-thread use and the host supplies a live NSView raw handle.
    let view = unsafe { &*(ns_view as *const NSView) };
    let object: &AnyObject = view;
    let parent = object.class();
    if !parent
        .name()
        .to_string_lossy()
        .starts_with("TaskFocuserInput_")
    {
        let name = CString::new(format!(
            "TaskFocuserInput_{}",
            parent.name().to_string_lossy()
        ))
        .map_err(io::Error::other)?;
        let class = if let Some(class) = AnyClass::get(&name) {
            class
        } else {
            let mut builder = ClassBuilder::new(&name, parent)
                .ok_or_else(|| io::Error::other("could not create native input policy class"))?;
            // SAFETY: every override uses AppKit's exact selector signature, adds no ivars, and forwards inherited event handling to the original backend class.
            unsafe {
                builder.add_method(
                    sel!(acceptsFirstMouse:),
                    accepts_first_mouse as extern "C-unwind" fn(_, _, _) -> _,
                );
                builder.add_method(sel!(dealloc), dealloc_view as extern "C-unwind" fn(_, _));
                builder.add_method(
                    sel!(shouldDelayWindowOrderingForEvent:),
                    delay_ordering as extern "C-unwind" fn(_, _, _) -> _,
                );
                builder.add_method(
                    sel!(mouseDown:),
                    mouse_down as extern "C-unwind" fn(_, _, _),
                );
                builder.add_method(
                    sel!(rightMouseDown:),
                    right_mouse_down as extern "C-unwind" fn(_, _, _),
                );
                builder.add_method(
                    sel!(otherMouseDown:),
                    other_mouse_down as extern "C-unwind" fn(_, _, _),
                );
                builder.add_method(
                    sel!(hitTest:),
                    hit_test as extern "C-unwind" fn(_, _, _) -> _,
                );
            }
            builder.register()
        };
        if class.instance_size() != parent.instance_size() {
            return Err(io::Error::other(
                "native input policy changed the view layout",
            ));
        }
        // SAFETY: this main-thread-only subclass directly extends the current class, adds no ivars, has identical instance size, and keeps method signatures compatible.
        let previous = unsafe { AnyObject::set_class(object, class) };
        assert_eq!(previous, parent, "native view class changed concurrently");
    }
    INPUT_POLICIES.with(|policies| {
        policies
            .borrow_mut()
            .entry(ns_view)
            .or_default()
            .interactive = interactive
    });
    Ok(())
}

/// Limits native view hit testing to the visible controls while leaving effects uncut.
/// AppKit expresses view coordinates in logical points, so `scale` is not applied here.
pub fn configure_input_region(ns_view: usize, _scale: f64, rectangles: &[Rect]) -> io::Result<()> {
    window(ns_view)?;
    install_mouse_monitors()?;
    if rectangles.iter().any(|rect| {
        ![rect.x, rect.y, rect.width, rect.height]
            .iter()
            .all(|value| value.is_finite())
            || rect.width < 0.
            || rect.height < 0.
    }) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "the input region contains invalid coordinates",
        ));
    }
    INPUT_POLICIES.with(|policies| {
        policies.borrow_mut().entry(ns_view).or_default().rectangles = Some(rectangles.to_vec())
    });
    update_hit_testing();
    Ok(())
}

thread_local! { static WINDOW_POLICIES: RefCell<HashMap<usize,bool>> = RefCell::new(HashMap::new()); }
extern "C-unwind" fn can_become_key(window: &AnyObject, _: Sel) -> Bool {
    let interactive = WINDOW_POLICIES.with(|policies| {
        policies
            .borrow()
            .get(&(window as *const AnyObject as usize))
            .copied()
            .unwrap_or(true)
    });
    if !interactive {
        return Bool::NO;
    }
    let Some(parent) = window.class().superclass() else {
        return Bool::NO;
    };
    // SAFETY: the backend's canBecomeKeyWindow selector takes no arguments and returns BOOL.
    unsafe { msg_send![super(window, parent), canBecomeKeyWindow] }
}
extern "C-unwind" fn can_become_main(window: &AnyObject, _: Sel) -> Bool {
    let interactive = WINDOW_POLICIES.with(|policies| {
        policies
            .borrow()
            .get(&(window as *const AnyObject as usize))
            .copied()
            .unwrap_or(true)
    });
    if !interactive {
        return Bool::NO;
    }
    let Some(parent) = window.class().superclass() else {
        return Bool::NO;
    };
    // SAFETY: the backend's canBecomeMainWindow selector takes no arguments and returns BOOL.
    unsafe { msg_send![super(window, parent), canBecomeMainWindow] }
}
extern "C-unwind" fn dealloc_window(window: &AnyObject, _: Sel) {
    WINDOW_POLICIES.with(|policies| {
        policies
            .borrow_mut()
            .remove(&(window as *const AnyObject as usize));
    });
    let Some(parent) = window.class().superclass() else {
        return;
    };
    // SAFETY: the no-ivar subclass owns no native storage, and forwards destruction to the original backend class exactly once.
    unsafe {
        let _: () = msg_send![super(window, parent), dealloc];
    }
}
fn install_window_policy(window: &NSWindow, interactive: bool) -> io::Result<()> {
    let object: &AnyObject = window;
    let parent = object.class();
    if !parent
        .name()
        .to_string_lossy()
        .starts_with("TaskFocuserWindow_")
    {
        let name = CString::new(format!(
            "TaskFocuserWindow_{}",
            parent.name().to_string_lossy()
        ))
        .map_err(io::Error::other)?;
        let class = if let Some(class) = AnyClass::get(&name) {
            class
        } else {
            let mut builder = ClassBuilder::new(&name, parent)
                .ok_or_else(|| io::Error::other("could not create native window policy class"))?;
            // SAFETY: all overrides retain the original NSWindow signatures and add no ivars.
            unsafe {
                builder.add_method(
                    sel!(canBecomeKeyWindow),
                    can_become_key as extern "C-unwind" fn(_, _) -> _,
                );
                builder.add_method(
                    sel!(canBecomeMainWindow),
                    can_become_main as extern "C-unwind" fn(_, _) -> _,
                );
                builder.add_method(sel!(dealloc), dealloc_window as extern "C-unwind" fn(_, _));
            }
            builder.register()
        };
        if class.instance_size() != parent.instance_size() {
            return Err(io::Error::other(
                "native window policy changed the backend layout",
            ));
        }
        // SAFETY: this main-thread-only class directly subclasses the original backend window, preserves its storage and delegates, and has identical method encodings.
        let previous = unsafe { AnyObject::set_class(object, class) };
        assert_eq!(previous, parent, "native window class changed concurrently");
    }
    WINDOW_POLICIES.with(|policies| {
        policies
            .borrow_mut()
            .insert(object as *const AnyObject as usize, interactive)
    });
    Ok(())
}

use block2::RcBlock;
use objc2_app_kit::NSEventMask;
use std::ptr::NonNull;
struct MouseMonitors {
    global: Retained<AnyObject>,
    local: Retained<AnyObject>,
}
impl Drop for MouseMonitors {
    fn drop(&mut self) {
        // SAFETY: both tokens are retained values returned by NSEvent monitor registration on this main thread.
        unsafe {
            NSEvent::removeMonitor(&self.global);
            NSEvent::removeMonitor(&self.local);
        }
    }
}
thread_local! {static MOUSE_MONITORS:RefCell<Option<MouseMonitors>>=const{RefCell::new(None)};}
fn install_mouse_monitors() -> io::Result<()> {
    MOUSE_MONITORS.with(|monitors| {
        if monitors.borrow().is_some() {
            return Ok(());
        }
        let mask = NSEventMask::MouseMoved
            | NSEventMask::LeftMouseDragged
            | NSEventMask::RightMouseDragged
            | NSEventMask::OtherMouseDragged
            | NSEventMask::LeftMouseDown
            | NSEventMask::RightMouseDown
            | NSEventMask::OtherMouseDown
            | NSEventMask::LeftMouseUp
            | NSEventMask::RightMouseUp
            | NSEventMask::OtherMouseUp;
        let global_block = RcBlock::new(|_: NonNull<NSEvent>| {
            update_hit_testing();
        });
        let global = NSEvent::addGlobalMonitorForEventsMatchingMask_handler(mask, &global_block)
            .ok_or_else(|| io::Error::other("could not monitor desktop pointer movement"))?;
        let local_block = RcBlock::new(|event: NonNull<NSEvent>| {
            update_hit_testing();
            event.as_ptr()
        });
        // SAFETY: the local monitor returns the unchanged valid event pointer and never retains or consumes it.
        let local =
            unsafe { NSEvent::addLocalMonitorForEventsMatchingMask_handler(mask, &local_block) };
        let Some(local) = local else {
            // SAFETY: global is the monitor token returned above and has not been removed.
            unsafe {
                NSEvent::removeMonitor(&global);
            }
            return Err(io::Error::other("could not monitor app pointer movement"));
        };
        *monitors.borrow_mut() = Some(MouseMonitors { global, local });
        Ok(())
    })
}
fn update_hit_testing() {
    if MainThreadMarker::new().is_none() {
        tracing::error!("AppKit pointer callback arrived outside the main thread");
        return;
    }
    // Preserve an ongoing drag's input target until its buttons are released.
    if NSEvent::pressedMouseButtons() != 0 {
        return;
    }
    let mouse = NSEvent::mouseLocation();
    let views = INPUT_POLICIES.with(|policies| {
        policies
            .borrow()
            .iter()
            .filter_map(|(view, policy)| {
                policy
                    .rectangles
                    .as_ref()
                    .map(|rectangles| (*view, rectangles.clone()))
            })
            .collect::<Vec<_>>()
    });
    for (address, rectangles) in views {
        // SAFETY: entries are installed for live backend views and removed by that view's synchronous main-thread dealloc override.
        let view = unsafe { &*(address as *const NSView) };
        let Some(window) = view.window() else {
            continue;
        };
        let point = window.convertPointFromScreen(mouse);
        let local = view.convertPoint_fromView(point, None);
        let inside = rectangles
            .iter()
            .any(|rect| rect.contains(task_focuser_core::geometry::Point::new(local.x, local.y)));
        window.setIgnoresMouseEvents(!inside);
    }
}
