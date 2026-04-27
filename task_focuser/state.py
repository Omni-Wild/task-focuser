import json
import os
import sys
from pathlib import Path

STATE_PATH = Path(os.environ["APPDATA"]) / "Task Focuser" / "state.json"
_LEGACY_STATE_PATH = Path(os.environ["APPDATA"]) / "Nova" / "state.json"

DEFAULTS: dict = {
    "text": "",
    "x": 200,
    "y": 200,
    "color": "#FFF9B1",
    "opacity": 100,
    "locked": False,
}


def load() -> dict:
    STATE_PATH.parent.mkdir(parents=True, exist_ok=True)
    if not STATE_PATH.exists() and _LEGACY_STATE_PATH.exists():
        try:
            STATE_PATH.write_bytes(_LEGACY_STATE_PATH.read_bytes())
        except OSError as exc:
            print(f"[task_focuser] legacy state migration failed ({exc})", file=sys.stderr)
    if not STATE_PATH.exists():
        return dict(DEFAULTS)
    try:
        with STATE_PATH.open("r", encoding="utf-8") as f:
            data = json.load(f)
        if not isinstance(data, dict):
            raise ValueError("state.json root is not an object")
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        print(f"[task_focuser] state load failed ({exc}); using defaults", file=sys.stderr)
        return dict(DEFAULTS)
    merged = dict(DEFAULTS)
    merged.update(data)
    return merged


def save(state: dict) -> None:
    STATE_PATH.parent.mkdir(parents=True, exist_ok=True)
    tmp = STATE_PATH.with_suffix(STATE_PATH.suffix + ".tmp")
    with tmp.open("w", encoding="utf-8") as f:
        json.dump(state, f, indent=2)
        f.flush()
        os.fsync(f.fileno())
    os.replace(tmp, STATE_PATH)
