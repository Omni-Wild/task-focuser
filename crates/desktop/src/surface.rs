//! Data shared by the UI controller and desktop window hosts.

use task_focuser_core::{
    conductor::Command,
    geometry::{Monitor, Point, Rect, Size},
    profile::AppPaths,
    state::NoteState,
    store::StateStore,
};

/// The host is selected before the event loop starts.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Backend {
    Native,
    #[cfg(target_os = "linux")]
    Wayland,
}

/// A window's purpose stays stable when a hidden native window is recreated.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub enum SurfaceRole {
    Note,
    Dot,
    CloseTarget,
    Bird,
    Cucumber,
}

/// Coordinates describe desktop logical pixels, including negative monitor origins.
#[derive(Debug, Clone, PartialEq)]
pub enum SurfaceChange {
    Show {
        role: SurfaceRole,
        bounds: Rect,
        interactive: bool,
    },
    Hide(SurfaceRole),
    Move {
        role: SurfaceRole,
        position: Point,
    },
    Resize {
        role: SurfaceRole,
        size: Size,
    },
    Keyboard {
        role: SurfaceRole,
        interactive: bool,
    },
    Focus(SurfaceRole),
    Quit,
}

pub struct Services {
    pub instance: task_focuser_platform::instance::Instance,
    pub login_startup: task_focuser_platform::startup::Startup,
    pub tray: Option<task_focuser_platform::tray::Tray>,
    pub tray_actions: std::sync::mpsc::Receiver<task_focuser_platform::tray::TrayAction>,
    pub sound: task_focuser_platform::sound::Sound,
    pub diagnostics: Vec<String>,
}

/// Startup IO completes before the event loop. The controller receives valid state.
pub struct Startup {
    pub paths: AppPaths,
    pub state: NoteState,
    pub monitors: Vec<Monitor>,
    pub store: StateStore,
    pub commands: Vec<Command>,
    pub backend: Backend,
    pub services: Services,
}
