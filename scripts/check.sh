#!/usr/bin/env bash
set -euo pipefail

cd -- "$(git rev-parse --show-toplevel)"
cargo fmt --all -- --check
cargo clippy --locked --workspace --all-targets -- -D warnings
cargo test --locked --workspace
