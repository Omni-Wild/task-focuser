#!/usr/bin/env bash
set -euo pipefail

cd -- "$(git rev-parse --show-toplevel)"
cargo build --locked --release -p task-focuser
printf 'Built %s\n' "$PWD/target/release/task-focuser"
