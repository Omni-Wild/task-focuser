//! Desktop integration for Task Focuser.

pub mod instance;
pub mod monitors;
pub mod sound;
pub mod startup;
pub mod tray;

#[cfg(target_os = "macos")]
pub mod macos;
#[cfg(target_os = "windows")]
pub mod windows;

#[cfg(target_os = "linux")]
pub mod x11;
