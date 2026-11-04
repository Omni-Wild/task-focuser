#!/usr/bin/env bash
set -euo pipefail

cd -- "$(git rev-parse --show-toplevel)"
if [[ $# -gt 1 ]]; then
    echo "Usage: scripts/test-native.sh [path-to-task-focuser]" >&2
    exit 2
fi
if [[ $# -eq 0 ]]; then
    cargo build --locked -p task-focuser
fi

# The harness creates its own compositor, session bus, state, and runtime sockets.
# Never inherit the user's compositor or activation bus into the test session.
exec env -u WAYLAND_DISPLAY -u WAYLAND_SOCKET -u DISPLAY -u HYPRLAND_INSTANCE_SIGNATURE -u SWAYSOCK \
    -u DBUS_SESSION_BUS_ADDRESS -u XDG_SESSION_ID \
    dbus-run-session -- python3 tests/native/run.py "${1:-target/debug/task-focuser}"
