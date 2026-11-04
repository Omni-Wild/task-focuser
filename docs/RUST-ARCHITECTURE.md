# Rust desktop architecture

Task Focuser uses one Rust state and behavior implementation with two desktop hosts. The native host uses Iced and winit on Windows, macOS, and X11. On Linux its compiled window backend is X11, including XWayland when `DISPLAY` is available. The Wayland host uses Iced's layer-shell integration on compositors that implement `zwlr_layer_shell_v1`, including Hyprland and Sway.

## Workspace

| Crate | Responsibility |
| --- | --- |
| `task-focuser-core` | Compatible saved state, profile paths, persistence, geometry, note/dot transitions, bird behavior, sprite frames, and synthesized PCM samples. |
| `task-focuser-platform` | Single-instance command forwarding, autostart, tray integration, native focus rules, and audio playback. |
| `task-focuser` | Shared Iced editor and settings, surface lifecycle, event routing, native host, and Wayland layer-shell host. |

The core does not depend on Iced or a running desktop. Tests can load fixture state, move notes between synthetic monitors, and advance bird animations with explicit durations. Runtime tests must use an isolated data directory and profile.

## Window hosts

A regular Wayland toplevel cannot choose its absolute position or require always-on-top placement. The layer-shell host therefore creates small overlay surfaces, anchors them to the output's top-left, and changes their margins when they move. These surfaces reserve no screen area. Locked notes receive pointer events with keyboard interactivity disabled. Editing enables on-demand keyboard focus. Click-through decoration surfaces use empty input regions.

The native host uses undecorated transparent windows with always-on-top placement. Native platform code handles focus and task-switcher behavior that portable window flags cannot express.

Surface roles describe the note, dot, close target, bird, and dragged cucumber. The shared UI emits surface changes for showing, hiding, moving, resizing, focus, and keyboard interactivity. Hosts translate those changes into window operations; the domain conductor does not send native window commands.

Both hosts use Iced 0.14. The published `iced_layershell` 0.19.1 dependency uses the same Iced 0.14 core, runtime, renderer, and program crates, so widgets and tasks remain shared. Linux enables Tiny Skia for software rendering. Windows and macOS enable WGPU because their transparent overlay windows require per-pixel alpha. These targets do not fall back to Tiny Skia on GPU failure. Procedural sprite assets do not enable image file codecs.

## Compatibility and scope

The state schema remains version 2 and keeps the existing JSON field names and Windows data location. Optional coordinates represent the old unplaced `NaN` values without writing invalid JSON. Profiles isolate saved state, cache, and command forwarding. An explicit data directory also disables autostart changes.

Native Windows, macOS, and compositor behavior needs validation on the corresponding operating system. GNOME Wayland does not implement the required layer-shell protocol. It cannot provide the same overlay semantics through this host. X11 remains the Linux fallback where an X11 session is available.

Dependencies are locked in `Cargo.lock`. Rust 1.94 and edition 2024 are the build baseline. Workspace lint rules reject unchecked `unwrap` calls and undocumented unsafe blocks.

The lockfile pins `winit-core` and `winit-common` to `0.31.0-beta.2`, matching the layer-shell crates' published lockfiles. Later beta releases changed `NativeKeyCode` to a non-exhaustive enum, which does not compile with `iced_exdevtools` 0.19.1. Keep locked builds until those crates support the newer API together.

The workspace patches only `iced_winit` 0.14.1. Its macOS event-loop builder selects the accessory activation policy and disables activation on startup, before any overlay window is created. [The patch notes](../vendor/iced_winit/PATCH.md) record its source and scope. Other platforms use the upstream behavior.

The Linux layer-shell renderer explicitly enables Softbuffer Wayland buffers. This is independent of winit, whose Linux host enables X11 only. A narrow [`iced_layershell` patch](../vendor/iced_layershell/PATCH.md) preserves the pointer coordinates supplied on entry, so the first click works before the pointer moves again.
