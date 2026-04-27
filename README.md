# Task Focuser

A tiny always-on-top sticky note for Windows 11. Holds 1–3 short lines of text. Lives in the system tray. Can be locked into click-through mode so it floats over everything without getting in the way.

Built from a single dependency (PySide6) and packaged as one `.exe`.

## Features

- Always-on-top, frameless, FigJam-style rounded sticky
- Auto-saves on every keystroke
- Lock mode = true OS-level click-through (mouse passes through to apps below)
- 10-color light palette + adjustable opacity (20%–100%)
- Tray icon mirrors note color
- Survives across restarts (text, position, color, opacity, lock state)

## Download

Grab the latest `Task Focuser.exe` from the [Releases page](../../releases). No install needed — just double-click.

To autostart with Windows: drop a shortcut to `Task Focuser.exe` into `shell:startup`.

## Run from source

```
pip install -r requirements.txt
python -m task_focuser
```

## Build the .exe yourself

```
./build.ps1
```

Output: `dist/Task Focuser.exe` (single-file, windowed, ~49 MB).

## Tech

- Python 3.11+
- PySide6 (Qt for Python) — frameless window, click-through, system tray, all in one dep
- PyInstaller for packaging

State is persisted to `%APPDATA%\Task Focuser\state.json`.
