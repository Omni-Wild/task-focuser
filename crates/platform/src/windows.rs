//! Minimal Win32 window policy and compatibility lock for the original app.

use std::io;
use windows_sys::Win32::{
    Foundation::{
        CloseHandle, ERROR_ALREADY_EXISTS, GetLastError, HANDLE, WAIT_ABANDONED, WAIT_OBJECT_0,
    },
    System::{
        RemoteDesktop::ProcessIdToSessionId,
        Threading::{CreateMutexW, GetCurrentProcessId, ReleaseMutex, WaitForSingleObject},
    },
    UI::WindowsAndMessaging::{
        GWL_EXSTYLE, GetForegroundWindow, GetWindowLongPtrW, HWND_TOPMOST, SWP_NOACTIVATE,
        SWP_NOMOVE, SWP_NOSIZE, SetForegroundWindow, SetWindowLongPtrW, SetWindowPos,
        WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW,
    },
};

pub(crate) struct LegacyGuard(HANDLE);
impl LegacyGuard {
    pub(crate) fn acquire(profile: &str) -> io::Result<Self> {
        let suffix = if profile.is_empty() {
            String::new()
        } else {
            format!(".{profile}")
        };
        let name: Vec<u16> = format!(r"Local\TaskFocuser.v2.instance{suffix}")
            .encode_utf16()
            .chain(Some(0))
            .collect();
        // SAFETY: name is a terminated UTF-16 buffer; the null security pointer requests default user security.
        let handle = unsafe { CreateMutexW(std::ptr::null(), 1, name.as_ptr()) };
        if handle.is_null() {
            return Err(io::Error::last_os_error());
        }
        // SAFETY: GetLastError must immediately follow CreateMutexW to detect an existing mutex.
        let existing = unsafe { GetLastError() } == ERROR_ALREADY_EXISTS;
        if existing {
            // SAFETY: handle is the valid mutex created/opened above; zero timeout never blocks.
            let result = unsafe { WaitForSingleObject(handle, 0) };
            if result != WAIT_OBJECT_0 && result != WAIT_ABANDONED {
                // SAFETY: this branch owns one valid handle and has not acquired the mutex.
                unsafe {
                    CloseHandle(handle);
                }
                return Err(io::Error::new(
                    io::ErrorKind::AlreadyExists,
                    "the original Windows app is running",
                ));
            }
        }
        Ok(Self(handle))
    }
}
impl Drop for LegacyGuard {
    fn drop(&mut self) {
        // SAFETY: the guard owns this handle and the mutex, acquired on the host's main thread.
        unsafe {
            ReleaseMutex(self.0);
            CloseHandle(self.0);
        }
    }
}

pub fn session_id() -> io::Result<u32> {
    let mut session = 0;
    // SAFETY: session is a valid writable u32 and GetCurrentProcessId always identifies this process.
    if unsafe { ProcessIdToSessionId(GetCurrentProcessId(), &mut session) } == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(session)
}

/// The caller must provide a live HWND obtained from its window backend.
pub fn configure_window(hwnd: isize, interactive: bool) -> io::Result<()> {
    set_interactive(hwnd, interactive)?;
    // SAFETY: the host supplies its current window handle; positioning does not move, resize or activate it.
    if unsafe {
        SetWindowPos(
            hwnd as _,
            HWND_TOPMOST,
            0,
            0,
            0,
            0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE,
        )
    } == 0
    {
        return Err(io::Error::last_os_error());
    }
    Ok(())
}

pub fn set_interactive(hwnd: isize, interactive: bool) -> io::Result<()> {
    let pointer = policy(hwnd as _)?;
    // SAFETY: policy() verifies UI thread ownership and no native call is made while updating the policy.
    unsafe {
        (*pointer).interactive = interactive;
    }
    if hwnd == 0 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "the window handle is null",
        ));
    }
    // SAFETY: the host supplies its current HWND. Only this app's extended style flags are changed.
    let mut style = unsafe { GetWindowLongPtrW(hwnd as _, GWL_EXSTYLE) };
    style |= WS_EX_TOOLWINDOW as isize;
    if interactive {
        style &= !(WS_EX_NOACTIVATE as isize);
    } else {
        style |= WS_EX_NOACTIVATE as isize;
    }
    // SAFETY: same live HWND and valid GWL_EXSTYLE index. A zero previous style is valid, so clear the error first.
    unsafe {
        windows_sys::Win32::Foundation::SetLastError(0);
        SetWindowLongPtrW(hwnd as _, GWL_EXSTYLE, style);
    }
    // SAFETY: reads the error from the immediately preceding window-style call.
    let error = unsafe { GetLastError() };
    if error != 0 {
        return Err(io::Error::from_raw_os_error(error as i32));
    }
    Ok(())
}

pub fn capture_foreground() -> Option<isize> {
    // SAFETY: GetForegroundWindow has no preconditions and the handle is borrowed, never closed here.
    let handle = unsafe { GetForegroundWindow() };
    (!handle.is_null()).then_some(handle as isize)
}

pub fn restore_foreground(hwnd: isize) -> io::Result<()> {
    // SAFETY: the host passes a borrowed foreground HWND captured for this edit session.
    if unsafe { SetForegroundWindow(hwnd as _) } == 0 {
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "Windows did not allow restoring the previous foreground window",
        ));
    }
    Ok(())
}

use std::ffi::c_void;
use task_focuser_core::geometry::{Point, Rect};
use windows_sys::Win32::Graphics::Gdi::ScreenToClient;
use windows_sys::Win32::{
    Foundation::{HWND, LPARAM, LRESULT, POINT, WPARAM},
    UI::{
        HiDpi::{GetDpiForSystem, GetDpiForWindow},
        Shell::{DefSubclassProc, GetWindowSubclass, RemoveWindowSubclass, SetWindowSubclass},
        WindowsAndMessaging::{
            GetCursorPos, GetWindowRect, GetWindowThreadProcessId, HTTRANSPARENT, MA_NOACTIVATE,
            WM_MOUSEACTIVATE, WM_NCDESTROY, WM_NCHITTEST,
        },
    },
};

const INPUT_SUBCLASS: usize = 0x5446_494e;
struct WindowPolicy {
    interactive: bool,
    scale: f64,
    rectangles: Option<Vec<Rect>>,
}

fn policy(hwnd: HWND) -> io::Result<*mut WindowPolicy> {
    // SAFETY: the current thread ID query has no preconditions.
    let current_thread = unsafe { windows_sys::Win32::System::Threading::GetCurrentThreadId() };
    // SAFETY: the HWND is supplied by the desktop host and this query does not dereference Rust storage.
    if unsafe { GetWindowThreadProcessId(hwnd, std::ptr::null_mut()) } != current_thread {
        return Err(io::Error::other(
            "native window policy must be changed on its owning thread",
        ));
    }
    let mut data = 0;
    // SAFETY: data is writable storage and INPUT_SUBCLASS identifies only this library's callback.
    if unsafe { GetWindowSubclass(hwnd, Some(input_proc), INPUT_SUBCLASS, &mut data) } != 0 {
        return Ok(data as *mut WindowPolicy);
    }
    let pointer = Box::into_raw(Box::new(WindowPolicy {
        interactive: false,
        scale: 1.,
        rectangles: None,
    }));
    // SAFETY: this same-thread callback owns the policy until WM_NCDESTROY; the box pointer remains stable.
    if unsafe { SetWindowSubclass(hwnd, Some(input_proc), INPUT_SUBCLASS, pointer as usize) } == 0 {
        // SAFETY: registration failed, so no callback owns or can access the allocated policy.
        unsafe {
            drop(Box::from_raw(pointer));
        }
        return Err(io::Error::last_os_error());
    }
    if let Err(error) = track_pointer(hwnd, pointer) {
        // SAFETY: registration succeeded but the pointer has not escaped; remove it before freeing the box.
        unsafe {
            RemoveWindowSubclass(hwnd, Some(input_proc), INPUT_SUBCLASS);
            drop(Box::from_raw(pointer));
        }
        return Err(error);
    }
    Ok(pointer)
}

unsafe extern "system" fn input_proc(
    hwnd: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
    _id: usize,
    data: usize,
) -> LRESULT {
    let pointer = data as *mut WindowPolicy;
    if message == WM_NCDESTROY {
        untrack_pointer(hwnd);
        // SAFETY: the callback owns the boxed policy, and removing itself prevents any later callback from reusing it.
        unsafe {
            RemoveWindowSubclass(hwnd, Some(input_proc), INPUT_SUBCLASS);
            drop(Box::from_raw(pointer));
        }
        // SAFETY: forwards the original destruction message once to the remaining backend subclass chain.
        return unsafe { DefSubclassProc(hwnd, message, wparam, lparam) };
    }
    // SAFETY: the stable box is allocated by policy() and only accessed by its owning UI thread.
    let policy = unsafe { &*pointer };
    if message == WM_MOUSEACTIVATE && !policy.interactive {
        return MA_NOACTIVATE as LRESULT;
    }
    if message == WM_NCHITTEST
        && let Some(rectangles) = &policy.rectangles
    {
        let mut point = POINT {
            x: (lparam as u32 & 0xffff) as i16 as i32,
            y: ((lparam as u32 >> 16) & 0xffff) as i16 as i32,
        };
        // SAFETY: point is writable storage, and the callback's HWND is live until WM_NCDESTROY.
        if unsafe { ScreenToClient(hwnd, &mut point) } != 0 {
            let local = Point::new(
                f64::from(point.x) / policy.scale,
                f64::from(point.y) / policy.scale,
            );
            if !rectangles.iter().any(|rect| rect.contains(local)) {
                return HTTRANSPARENT as LRESULT;
            }
        }
    }
    // SAFETY: all remaining messages keep their original parameters and backend dispatch chain.
    unsafe { DefSubclassProc(hwnd, message, wparam, lparam) }
}

pub fn configure_input_region(hwnd: isize, scale: f64, rectangles: &[Rect]) -> io::Result<()> {
    if !scale.is_finite()
        || scale <= 0.
        || rectangles.iter().any(|rect| {
            ![rect.x, rect.y, rect.width, rect.height]
                .iter()
                .all(|value| value.is_finite())
                || rect.width < 0.
                || rect.height < 0.
        })
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "the input region contains invalid coordinates",
        ));
    }
    let pointer = policy(hwnd as HWND)?;
    // SAFETY: policy() checks ownership, and no backend call occurs while this mutable reference is live.
    let policy = unsafe { &mut *pointer };
    policy.scale = scale;
    policy.rectangles = Some(rectangles.to_vec());
    let mut cursor = POINT { x: 0, y: 0 };
    // SAFETY: cursor is valid writable storage, queried before native hit testing is applied.
    if unsafe { GetCursorPos(&mut cursor) } == 0 {
        return Err(io::Error::last_os_error());
    }
    update_passthrough(hwnd as HWND, pointer, cursor)
}

pub fn system_scale() -> io::Result<f64> {
    // SAFETY: this query has no pointer or state preconditions.
    let dpi = unsafe { GetDpiForSystem() };
    if dpi == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(f64::from(dpi) / 96.)
}
pub fn window_scale(hwnd: isize) -> io::Result<f64> {
    // SAFETY: the host supplies the current live HWND.
    let dpi = unsafe { GetDpiForWindow(hwnd as HWND) };
    if dpi == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(f64::from(dpi) / 96.)
}
pub fn get_position(hwnd: isize) -> io::Result<Point> {
    Ok(window_bounds(hwnd)?.origin())
}
pub fn window_bounds(hwnd: isize) -> io::Result<Rect> {
    let mut rect = windows_sys::Win32::Foundation::RECT {
        left: 0,
        top: 0,
        right: 0,
        bottom: 0,
    };
    // SAFETY: rect is valid writable storage and the host supplies its live window handle.
    if unsafe { GetWindowRect(hwnd as HWND, &mut rect) } == 0 {
        return Err(io::Error::last_os_error());
    }
    let scale = system_scale()?;
    Ok(Rect::new(
        f64::from(rect.left) / scale,
        f64::from(rect.top) / scale,
        f64::from(rect.right - rect.left) / scale,
        f64::from(rect.bottom - rect.top) / scale,
    ))
}
pub fn cursor_position() -> io::Result<Point> {
    let mut point = POINT { x: 0, y: 0 };
    // SAFETY: point is valid writable storage.
    if unsafe { GetCursorPos(&mut point) } == 0 {
        return Err(io::Error::last_os_error());
    }
    let scale = system_scale()?;
    Ok(Point::new(
        f64::from(point.x) / scale,
        f64::from(point.y) / scale,
    ))
}
fn physical(value: f64, scale: f64) -> io::Result<i32> {
    let value = (value * scale).round();
    if !value.is_finite() || value < f64::from(i32::MIN) || value > f64::from(i32::MAX) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "the window position is outside desktop coordinates",
        ));
    }
    Ok(value as i32)
}
pub fn set_position(hwnd: isize, point: Point) -> io::Result<()> {
    let scale = system_scale()?;
    let x = physical(point.x, scale)?;
    let y = physical(point.y, scale)?;
    // SAFETY: positioning a current host HWND preserves size, stacking and keyboard activation.
    if unsafe {
        SetWindowPos(
            hwnd as HWND,
            std::ptr::null_mut(),
            x,
            y,
            0,
            0,
            SWP_NOSIZE | SWP_NOACTIVATE | windows_sys::Win32::UI::WindowsAndMessaging::SWP_NOZORDER,
        )
    } == 0
    {
        return Err(io::Error::last_os_error());
    }
    Ok(())
}
pub fn set_bounds(hwnd: isize, bounds: Rect) -> io::Result<()> {
    let scale = system_scale()?;
    let x = physical(bounds.x, scale)?;
    let y = physical(bounds.y, scale)?;
    let width = physical(bounds.width, scale)?;
    let height = physical(bounds.height, scale)?;
    if width <= 0 || height <= 0 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "window size must be positive",
        ));
    }
    // SAFETY: the host supplies a live borderless HWND; native pixels are derived from the shared system-DPI coordinate convention.
    if unsafe {
        SetWindowPos(
            hwnd as HWND,
            std::ptr::null_mut(),
            x,
            y,
            width,
            height,
            SWP_NOACTIVATE | windows_sys::Win32::UI::WindowsAndMessaging::SWP_NOZORDER,
        )
    } == 0
    {
        return Err(io::Error::last_os_error());
    }
    Ok(())
}

pub(crate) fn current_user_sid() -> io::Result<String> {
    use windows_sys::Win32::{
        Foundation::LocalFree,
        Security::Authorization::ConvertSidToStringSidW,
        Security::{GetTokenInformation, TOKEN_QUERY, TOKEN_USER, TokenUser},
        System::Threading::{GetCurrentProcess, OpenProcessToken},
    };
    let mut token = std::ptr::null_mut();
    // SAFETY: token is a valid output pointer and only read access to this process token is requested.
    if unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) } == 0 {
        return Err(io::Error::last_os_error());
    }
    struct Token(HANDLE);
    impl Drop for Token {
        fn drop(&mut self) {
            // SAFETY: the RAII token owns one valid process-token handle.
            unsafe {
                CloseHandle(self.0);
            }
        }
    }
    let token = Token(token);
    let mut size = 0;
    // SAFETY: the null buffer and zero length request the required byte count.
    unsafe {
        GetTokenInformation(token.0, TokenUser, std::ptr::null_mut(), 0, &mut size);
    }
    if size == 0 {
        return Err(io::Error::last_os_error());
    }
    // usize storage provides at least TOKEN_USER's pointer alignment.
    let mut storage = vec![0usize; (size as usize).div_ceil(std::mem::size_of::<usize>())];
    // SAFETY: storage is aligned and has at least size bytes; TOKEN_USER stays borrowed until SID conversion finishes.
    if unsafe {
        GetTokenInformation(
            token.0,
            TokenUser,
            storage.as_mut_ptr() as *mut c_void,
            size,
            &mut size,
        )
    } == 0
    {
        return Err(io::Error::last_os_error());
    }
    // SAFETY: GetTokenInformation initialized a properly aligned TOKEN_USER in storage above.
    let user = unsafe { &*(storage.as_ptr() as *const TOKEN_USER) };
    let mut sid = std::ptr::null_mut();
    // SAFETY: user SID is live in storage and sid receives a LocalAlloc-owned UTF-16 string.
    if unsafe { ConvertSidToStringSidW(user.User.Sid, &mut sid) } == 0 {
        return Err(io::Error::last_os_error());
    }
    let mut length = 0;
    // SAFETY: successful SID conversion returned a terminated UTF-16 string, valid until LocalFree below.
    while unsafe { *sid.add(length) } != 0 {
        length += 1;
    }
    // SAFETY: length was measured within the returned terminated SID allocation.
    let text = String::from_utf16(unsafe { std::slice::from_raw_parts(sid, length) })
        .map_err(io::Error::other);
    // SAFETY: ConvertSidToStringSidW transfers the string allocation for release by LocalFree.
    unsafe {
        LocalFree(sid as *mut c_void);
    }
    text
}

/// Preserve the original app's one system-DPI desktop coordinate system.
/// Call before constructing any native window, tray icon or event loop.
pub fn initialize_dpi() -> io::Result<()> {
    use windows_sys::Win32::{
        System::Threading::GetCurrentProcess,
        UI::HiDpi::{
            DPI_AWARENESS_CONTEXT_SYSTEM_AWARE, GetProcessDpiAwareness, PROCESS_SYSTEM_DPI_AWARE,
            SetProcessDpiAwarenessContext,
        },
    };
    // SAFETY: this process-wide setting is applied once during startup, before any window exists.
    if unsafe { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_SYSTEM_AWARE) } != 0 {
        return Ok(());
    }
    let mut awareness = 0;
    // SAFETY: awareness is valid writable storage; the process handle is borrowed, not owned.
    let result = unsafe { GetProcessDpiAwareness(GetCurrentProcess(), &mut awareness) };
    if result >= 0 && awareness == PROCESS_SYSTEM_DPI_AWARE {
        return Ok(());
    }
    Err(io::Error::other(
        "Task Focuser requires system-DPI awareness before the first window is created",
    ))
}

use std::{
    cell::{Cell, RefCell},
    collections::HashMap,
};
use windows_sys::Win32::{
    Foundation::HINSTANCE,
    System::LibraryLoader::GetModuleHandleW,
    UI::WindowsAndMessaging::{
        CallNextHookEx, HHOOK, MSLLHOOKSTRUCT, SetWindowsHookExW, UnhookWindowsHookEx, WH_MOUSE_LL,
        WS_EX_LAYERED, WS_EX_TRANSPARENT,
    },
};
thread_local! {
    static TRACKED_WINDOWS:RefCell<HashMap<isize,*mut WindowPolicy>>=RefCell::new(HashMap::new());
    static POINTER_HOOK:Cell<HHOOK>=const{Cell::new(std::ptr::null_mut())};
}

fn track_pointer(hwnd: HWND, pointer: *mut WindowPolicy) -> io::Result<()> {
    POINTER_HOOK.with(|hook| {
        if hook.get().is_null() {
            // SAFETY: the null module name requests this executable's borrowed module handle.
            let module = unsafe { GetModuleHandleW(std::ptr::null()) };
            if module.is_null() {
                return Err(io::Error::last_os_error());
            }
            // SAFETY: the callback lives in this executable and runs on this UI thread's native message loop.
            let handle = unsafe {
                SetWindowsHookExW(WH_MOUSE_LL, Some(pointer_hook), module as HINSTANCE, 0)
            };
            if handle.is_null() {
                return Err(io::Error::last_os_error());
            }
            hook.set(handle);
        }
        Ok(())
    })?;
    TRACKED_WINDOWS.with(|windows| windows.borrow_mut().insert(hwnd as isize, pointer));
    Ok(())
}
fn untrack_pointer(hwnd: HWND) {
    let empty = TRACKED_WINDOWS.with(|windows| {
        let mut windows = windows.borrow_mut();
        windows.remove(&(hwnd as isize));
        windows.is_empty()
    });
    if empty {
        POINTER_HOOK.with(|hook|{let handle=hook.replace(std::ptr::null_mut());if !handle.is_null(){
        // SAFETY: the thread-local state owns this registered native hook and removes it once after its final window closes.
        if unsafe{UnhookWindowsHookEx(handle)}==0{tracing::warn!(error=%io::Error::last_os_error(),"could not remove pointer hit-test hook");}
    }});
    }
}
unsafe extern "system" fn pointer_hook(code: i32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    if code >= 0 {
        // SAFETY: low-level mouse hook callbacks with a nonnegative code receive a live MSLLHOOKSTRUCT for this call.
        let mouse = unsafe { &*(lparam as *const MSLLHOOKSTRUCT) };
        let windows = TRACKED_WINDOWS.with(|windows| {
            windows
                .borrow()
                .iter()
                .map(|(hwnd, policy)| (*hwnd, *policy))
                .collect::<Vec<_>>()
        });
        for (hwnd, policy) in windows {
            if let Err(error) = update_passthrough(hwnd as HWND, policy, mouse.pt) {
                tracing::warn!(%error,"could not update transparent window hit testing");
            }
        }
    }
    // SAFETY: forwards the original hook message unchanged to the rest of the desktop's hook chain.
    unsafe { CallNextHookEx(std::ptr::null_mut(), code, wparam, lparam) }
}
fn update_passthrough(hwnd: HWND, pointer: *mut WindowPolicy, mut cursor: POINT) -> io::Result<()> {
    // SAFETY: the window's native subclass owns this pointer and the hook runs on the same thread.
    let policy = unsafe { &*pointer };
    let Some(rectangles) = &policy.rectangles else {
        return Ok(());
    };
    // SAFETY: cursor is writable local storage and hwnd is a currently tracked app window.
    if unsafe { ScreenToClient(hwnd, &mut cursor) } == 0 {
        return Err(io::Error::last_os_error());
    }
    let local = Point::new(
        f64::from(cursor.x) / policy.scale,
        f64::from(cursor.y) / policy.scale,
    );
    // Keep receiving the current drag until the backend releases capture.
    // SAFETY: GetCapture only queries this thread's current mouse capture.
    let captured = unsafe { windows_sys::Win32::UI::Input::KeyboardAndMouse::GetCapture() } == hwnd;
    let outside = !captured && !rectangles.iter().any(|rect| rect.contains(local));
    // SAFETY: hwnd is live and only this app's extended transparency flags are changed.
    let current = unsafe { GetWindowLongPtrW(hwnd, GWL_EXSTYLE) };
    let next = if outside {
        current | (WS_EX_LAYERED | WS_EX_TRANSPARENT) as isize
    } else {
        current & !(WS_EX_TRANSPARENT as isize)
    };
    if current == next {
        return Ok(());
    }
    // SAFETY: clear the ambiguous-zero error before applying a valid extended style to this app's window.
    unsafe {
        windows_sys::Win32::Foundation::SetLastError(0);
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, next);
    }
    // SAFETY: reads the result of the immediately preceding style operation.
    let error = unsafe { GetLastError() };
    if error != 0 {
        return Err(io::Error::from_raw_os_error(error as i32));
    }
    if next & WS_EX_LAYERED as isize != 0 && current & WS_EX_LAYERED as isize == 0 {
        // SAFETY: initializing constant window opacity keeps the rendered surface visible after enabling layered hit testing.
        if unsafe {
            windows_sys::Win32::UI::WindowsAndMessaging::SetLayeredWindowAttributes(
                hwnd,
                0,
                255,
                windows_sys::Win32::UI::WindowsAndMessaging::LWA_ALPHA,
            )
        } == 0
        {
            return Err(io::Error::last_os_error());
        }
    }
    Ok(())
}

/// Compare existing directory identities, including path casing and junction aliases.
pub(crate) fn same_directory(
    first: &std::path::Path,
    second: &std::path::Path,
) -> io::Result<bool> {
    use std::{
        fs::OpenOptions,
        os::windows::{fs::OpenOptionsExt, io::AsRawHandle},
    };
    use windows_sys::Win32::Storage::FileSystem::{
        BY_HANDLE_FILE_INFORMATION, FILE_FLAG_BACKUP_SEMANTICS, FILE_READ_ATTRIBUTES,
        GetFileInformationByHandle,
    };
    fn identity(path: &std::path::Path) -> io::Result<(u32, u32, u32)> {
        let directory = OpenOptions::new()
            .read(true)
            .access_mode(FILE_READ_ATTRIBUTES)
            .custom_flags(FILE_FLAG_BACKUP_SEMANTICS)
            .open(path)?;
        let mut info = std::mem::MaybeUninit::<BY_HANDLE_FILE_INFORMATION>::uninit();
        // SAFETY: the file owns a live directory handle; info is properly aligned writable storage for the complete native structure.
        if unsafe {
            GetFileInformationByHandle(directory.as_raw_handle() as HANDLE, info.as_mut_ptr())
        } == 0
        {
            return Err(io::Error::last_os_error());
        }
        // SAFETY: GetFileInformationByHandle initialized the full structure on success.
        let info = unsafe { info.assume_init() };
        Ok((
            info.dwVolumeSerialNumber,
            info.nFileIndexHigh,
            info.nFileIndexLow,
        ))
    }
    Ok(identity(first)? == identity(second)?)
}
