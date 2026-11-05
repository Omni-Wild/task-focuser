# Rust migration

The Rust app reads the existing Windows v2 state file. The original WPF source is available in [Git history](https://github.com/Omni-Wild/task-focuser/tree/67b0db1/app). Feature and platform parity still require verification.

## State and isolation

The default state file is:

- Windows: `%APPDATA%\Task Focuser\state.json`
- macOS: `~/Library/Application Support/Task Focuser/state.json`
- Linux: `$XDG_CONFIG_HOME/task-focuser/state.json`, or `~/.config/task-focuser/state.json` when `XDG_CONFIG_HOME` is unset or relative.

The app uses the corresponding local cache directory for generated assets: `%LOCALAPPDATA%\Task Focuser`, `~/Library/Caches/Task Focuser`, or `$XDG_CACHE_HOME/task-focuser` with `~/.cache/task-focuser` as the Linux fallback. Audio is generated in memory.

For an isolated run, set `TASKFOCUSER_DATA_DIR` to a separate directory. State and generated assets stay under that directory. `TASKFOCUSER_PROFILE` selects a named state file and cache within the chosen data location; its name may contain letters, digits, hyphens, and underscores. An isolated profile does not change login startup settings. Use a disposable directory when testing so existing notes are not changed.

## Linux desktops

On Linux, the default selects layer-shell when a Wayland display is available. This requires a compositor that supports layer-shell, such as Hyprland or Sway. GNOME Wayland does not offer the same overlay support. The native X11 host requires `DISPLAY`; select it with `--backend native` in an X11 session. Original Windows fonts may be unavailable on other systems, where the app uses fallback fonts. Desktop behavior still needs verification on each compositor.
