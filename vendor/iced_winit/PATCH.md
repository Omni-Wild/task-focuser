# macOS startup activation

Task Focuser vendors `iced_winit` 0.14.1 so starting the background utility does
not request activation over another foreground app before the native host can
configure its windows. Iced's public runner does not expose winit's event-loop
builder.

The published crate comes from
[`iced-rs/iced` commit `38237dd294da3256c2ac4d3f8288fc73956c91e0`](https://github.com/iced-rs/iced/tree/38237dd294da3256c2ac4d3f8288fc73956c91e0/winit).
Its crates.io archive SHA-256 is
`7589888c8e951899cc688247a69933bb7a0511f0b4b2e122ac3fcd5dacb37f72`.
The archive checksum was verified against `Cargo.lock` before copying its
normalized `Cargo.toml`, `README.md`, and complete `src` directory. The upstream
MIT license from the same commit is included in [LICENSE](LICENSE).

The startup change is in `src/lib.rs`, before building the event loop.
On macOS, it calls the public winit methods
`with_activation_policy(ActivationPolicy::Accessory)` and
`with_activate_ignoring_other_apps(false)`. These hide the utility from the Dock
and stop winit from requesting activation over another foreground app at launch.
The builder's returned mutable reference is explicitly discarded to satisfy
the upstream crate's `unused_results` lint.
An explicit editor focus request can still activate the app later. Other targets
use the same unconfigured builder as upstream. Window click policy remains the
native host's responsibility.

Two scoped `#[allow(deprecated)]` attributes preserve upstream's `try_next()`
channel calls with futures 0.3.34. They cover the pending-control match arm and
the next-event binding. Replacing these calls with `try_recv()` would change
their `Result<Option<T>, _>` semantics. Remove the allowances when updating to
an upstream version that handles the new channel API.

Remove this override when Iced exposes these event-loop options, or when its
runner provides equivalent configurable behavior. Keep the override limited to
the startup calls and the two compatibility allowances when updating the
upstream crate.
