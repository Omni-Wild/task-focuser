//! State and behavior shared by Task Focuser's desktop hosts.

pub mod audio;
pub mod companion;
pub mod conductor;
pub mod geometry;
pub mod profile;
pub mod sprites;
pub mod state;
pub mod store;
pub mod vines;

pub use state::{IdleRate, IdleSounds, Mode, NoteState};
