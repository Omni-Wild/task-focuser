# pointer coordinates on entry

Task Focuser vendors `iced_layershell` 0.19.1 to preserve pointer coordinates
when entering a layer surface. Upstream converts `CursorEnter { x, y }` into
Iced's positionless `CursorEntered` event, so a click immediately after entry
has no cursor position until the pointer moves again.

The source is the published `iced_layershell` 0.19.1 crate from
[`waycrate/exwlshelleventloop`](https://github.com/waycrate/exwlshelleventloop/tree/v0.19.1/iced_layershell).
Its archive SHA-256 is
`51b59e8af02bf4c51db2ee5d130293e6e1e5ac4ae9f74a1e57990a19221aa0d5`.
The checksum was verified against `Cargo.lock` before copying the normalized
`Cargo.toml`, `README.md`, and complete `src` and `tests` directories. The
upstream MIT license from release `v0.19.1` is included in [LICENSE](LICENSE).

The functional change is in `src/conversion.rs`. Both `CursorEnter` and
`CursorMoved` now emit Iced's `CursorMoved` event with the supplied coordinates,
using the existing application-scale conversion. The conversion API returns
one optional event, so it cannot emit separate enter and position events.
Iced widgets determine pointer presence from the resulting position.

One scoped `#[allow(unused_imports)]` attribute preserves the upstream
`StreamExt` import in `src/multi_window.rs`, which is unused with this project's
release features. It changes no runtime behavior. Remove the allowance when
upstream scopes the import to the features that use it.

Remove this override when an upstream release preserves entry coordinates in
the Iced event stream. Native pointer validation must cover entry followed
immediately by clicking or dragging, without an extra motion event.
