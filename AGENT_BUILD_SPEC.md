# Build Spec: "Nova" — Minimal Always-On-Top Sticky Note for Windows 11

You are an agent (Claude Code / GitHub Copilot Agent). Build the utility described below by following the **steps in order**. Do not skip ahead. After each step, verify it works before moving on. Keep the code **minimal** — no frameworks, no databases, no extra features. One small Python app.

---

## Product summary

A tiny Windows 11 desktop sticky note that:

- Floats above every other app (always-on-top).
- Holds 1–3 short lines of text (think: "123 123 123 123" length, ~40 chars/line).
- Auto-saves the text on every keystroke and on exit.
- Has two modes:
  - **Edit mode** — draggable, editable, shows a subtle border.
  - **Locked mode** — click-through (mouse passes to apps below), not editable, not movable. Only way out is the tray menu.
- Lives in the system tray ("show hidden icons" area) with a right-click menu for: lock/unlock, opacity, color, quit.
- Opacity selectable: 20%, 30%, 40%, 50%, 60%, 70%, 80%, 90%, 100%.
- Color selectable from a FigJam-style **light palette** (listed below).
- Looks like a FigJam sticky: rounded rectangle, light fill, soft border, sized to fit the text plus margins.
- Persists ALL state (text, position, color, opacity, locked flag) to a single JSON file in `%APPDATA%\Nova\state.json`.

**Non-goals:** no rich text, no multiple notes, no markdown, no sync, no images, no settings window. The tray menu IS the settings.

---

## Tech stack (use exactly this)

- **Language:** Python 3.11+
- **GUI:** PySide6 (Qt for Python) — handles frameless window, always-on-top, per-window opacity, click-through, AND the system tray icon. One dependency does everything.
- **Packaging:** PyInstaller (single-file `.exe`, windowed, no console).
- **No other deps.** Don't add `pystray`, `keyboard`, `pywin32`, etc. PySide6 is enough.

`requirements.txt`:
```
PySide6==6.7.*
```

`requirements-dev.txt`:
```
pyinstaller>=6.6
```

---

## FigJam light color palette (use these exact hex values)

| Name        | Hex       |
|-------------|-----------|
| Yellow      | `#FFF9B1` |
| Orange      | `#FFD8A8` |
| Pink        | `#FFCEE0` |
| Red         | `#FCC0B6` |
| Green       | `#D5F692` |
| Teal        | `#BFF0E5` |
| Blue        | `#A6CCF5` |
| Purple      | `#D5C6E0` |
| Gray        | `#E6E6E6` |
| White       | `#FFFFFF` |

Border color = same hue, ~15% darker. For simplicity, derive at runtime: multiply each RGB channel by 0.82, clamp to 0–255.

Default color: **Yellow** (`#FFF9B1`).

---

## Visual style

- Frameless rounded rectangle, **corner radius 10 px**.
- **Padding:** 12 px horizontal, 10 px vertical.
- **Border:** 1.5 px solid, derived darker shade.
- **Drop shadow:** subtle — Qt `QGraphicsDropShadowEffect`, blur 18, offset (0, 2), color `rgba(0,0,0,60)`.
- **Font:** `Inter` if available, fallback `Segoe UI`, weight 500, size 14 px. Text color `#1B1B1F`.
- **Size:** auto-fit to content. Min width 160 px, max width 420 px. Min height = single line + padding. Word-wrap on. Hard cap at **3 visual lines** — if user types more, ignore further input (or just let Qt clip; do NOT grow beyond 3 lines).
- **Cursor:** in edit mode, text cursor over text area, move cursor over padding/edges (whole window draggable from any non-text spot AND from text area when holding `Alt`).

---

## State file

Path: `%APPDATA%\Nova\state.json`

Schema:
```json
{
  "text": "",
  "x": 200,
  "y": 200,
  "color": "#FFF9B1",
  "opacity": 100,
  "locked": false
}
```

- Load on startup. If file missing or corrupt, use defaults and recreate.
- Save on: every text change (debounce 250 ms), move end, color change, opacity change, lock toggle, app quit.
- Write atomically: write to `state.json.tmp`, then `os.replace` over `state.json`.

---

## Behavior details

### Always-on-top
`Qt.WindowStaysOnTopHint | Qt.FramelessWindowHint | Qt.Tool` — `Tool` keeps it off the taskbar.

### Opacity
`self.setWindowOpacity(opacity / 100.0)`. Applies to whole window including text.

### Locked mode
- Set window flag `Qt.WindowTransparentForInput` (true click-through — mouse events pass to apps underneath).
- Must call `self.show()` after changing flags for them to take effect on Windows.
- Visually: hide border, hide caret, dim slightly (NOT by changing opacity — keep user's opacity setting; instead just remove the border so it looks "settled").
- The ONLY way to unlock is the tray menu → "Unlock". Tray icon stays clickable because the tray icon is a separate OS object.

### Edit mode
- Remove `WindowTransparentForInput` flag.
- Show border.
- Text is editable. Window is draggable by clicking anywhere on it (track mouse press/move/release on the widget itself; if the press was inside the QTextEdit's text region without `Alt`, let it edit instead — simplest rule: drag only when press is on the padding area, OR on the text area while `Alt` is held).

### Tray menu (right-click on tray icon)
```
● Lock / Unlock          (toggles)
─────────────────────────
Color ▶  Yellow
         Orange
         Pink
         Red
         Green
         Teal
         Blue
         Purple
         Gray
         White
─────────────────────────
Opacity ▶  100%
           90%
           ...
           20%
─────────────────────────
Show note (if hidden / off-screen — re-centers it)
─────────────────────────
Quit
```
- Current color and current opacity show a checkmark (`QAction.setCheckable(True)` + `setChecked`).
- Left-click on tray icon = toggle lock (quick ADHD-friendly shortcut).

### Tray icon
- Generate at runtime: a 32×32 `QPixmap` painted with the current note color as a rounded square + 1 px dark outline. No icon file shipped. Update the tray icon whenever color changes — gives quick visual feedback.

### Auto-save
- `QTextEdit.textChanged` → start/restart `QTimer.singleShot(250, save_state)`.
- Override `closeEvent` and `QApplication.aboutToQuit` → save synchronously.

### Single-instance
- On startup, try to bind a `QLocalSocket` named `nova-singleton`. If it exists, send "show" and exit. Otherwise create the `QLocalServer` and listen. (Optional but trivial in Qt — skip if it adds complexity.)

---

## File layout

```
nova/
├── README.md
├── requirements.txt
├── requirements-dev.txt
├── pyproject.toml          (optional, for tooling only)
├── nova/
│   ├── __init__.py
│   ├── __main__.py         (entry point: python -m nova)
│   ├── app.py              (QApplication setup, tray, single-instance)
│   ├── note.py             (StickyNote QWidget)
│   ├── state.py            (load/save JSON, defaults)
│   └── palette.py          (color constants + darken helper)
└── build.ps1               (PyInstaller one-liner)
```

Keep total Python under ~400 lines. If you exceed that, you're over-engineering — simplify.

---

# STEP-BY-STEP BUILD PLAN

Do these **in order**. After each step, run the app (where applicable) and confirm the listed acceptance criteria before continuing.

---

## Step 1 — Project skeleton

1. Create the file layout above with empty/stub files.
2. Write `requirements.txt` and `requirements-dev.txt`.
3. Write a 10-line `README.md` with: install (`pip install -r requirements.txt`), run (`python -m nova`), build (`./build.ps1`).
4. `nova/__main__.py` calls `nova.app.main()`.
5. `nova/app.py` has a stub `main()` that just creates a `QApplication`, prints `"Nova up"`, and exits.

**Acceptance:** `python -m nova` prints `Nova up` and exits cleanly.

---

## Step 2 — State persistence

Implement `nova/state.py`:

- `STATE_PATH = Path(os.environ["APPDATA"]) / "Nova" / "state.json"`
- `DEFAULTS = {...}` (see schema above).
- `load() -> dict` — creates dir if missing, returns merged dict (defaults + file). On JSON error, log to stderr and return defaults.
- `save(state: dict) -> None` — atomic write via tmp + `os.replace`.

**Acceptance:** Run a small script (or REPL) that calls `save({...})` then `load()` and gets the same dict back. File appears at `%APPDATA%\Nova\state.json`.

---

## Step 3 — Palette module

Implement `nova/palette.py`:

- `COLORS: list[tuple[str, str]]` — ordered list of `(name, hex)` exactly matching the table above.
- `darken(hex_str: str, factor: float = 0.82) -> str` — returns hex.
- `text_color() -> str` — returns `"#1B1B1F"` (constant; exposed for symmetry).

**Acceptance:** `darken("#FFF9B1")` returns a valid darker hex; round-trip parsing works.

---

## Step 4 — The note window (edit mode only, no lock yet)

Implement `nova/note.py`:

- `class StickyNote(QWidget)`.
- Window flags: `Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool`.
- `setAttribute(Qt.WA_TranslucentBackground)` so rounded corners work.
- A `QTextEdit` child:
  - No frame, no scrollbars (`setVerticalScrollBarPolicy(Qt.ScrollBarAlwaysOff)`, same for horizontal).
  - `setWordWrapMode(QTextOption.WordWrap)`.
  - Font Inter / Segoe UI 14 / weight 500.
  - Placeholder: `"focus..."`.
- `paintEvent`: draw rounded rect (radius 10) filled with current color, 1.5 px border in darkened color. Drop shadow via `QGraphicsDropShadowEffect`.
- Auto-resize: on `textChanged`, recompute `sizeHint` based on `QFontMetrics` + padding, clamped to width 160–420 px, height to fit at most 3 wrapped lines. Call `resize()`.
- Drag: track `mousePressEvent` / `mouseMoveEvent` / `mouseReleaseEvent` on the widget. Drag only if press was outside the text edit's geometry, OR press was inside text edit while `Qt.AltModifier` held. Use `event.globalPosition().toPoint()` deltas.
- On move end → emit a `moved` signal carrying new `(x, y)`.
- On text change → emit `text_changed(str)` (debounced upstream).

Wire it up in `app.py`:
- Load state, create one `StickyNote`, apply text/color/opacity/position from state, show it.
- Connect `text_changed` and `moved` to a `_save_debounced()` that uses a 250 ms `QTimer`.

**Acceptance:**
- Note appears at saved position, with saved text, on top of other windows.
- Typing works, word-wraps, caps at 3 lines visually.
- Dragging from the padding area moves it. Releasing saves position.
- Closing via Alt+F4 or task manager: state.json contains the latest text/position.

---

## Step 5 — System tray icon + menu

In `nova/app.py`:

1. Create `QSystemTrayIcon`. Generate the icon pixmap from current color (32×32 rounded rect + dark outline). Set tooltip `"Nova"`.
2. Build `QMenu`:
   - **Lock / Unlock** action (checkable, label flips based on state).
   - **Color** submenu: one checkable action per color in `palette.COLORS`. Use a `QActionGroup` (exclusive).
   - **Opacity** submenu: actions for 100, 90, 80, …, 20 (`QActionGroup`, exclusive).
   - **Show note** action — moves note to `(100, 100)` and calls `show()/raise_()/activateWindow()`.
   - Separator.
   - **Quit** action — calls `QApplication.quit()`.
3. Left-click on tray icon (`activated` with `Trigger` reason) → toggle lock.
4. On color change: update note's color, repaint, regenerate tray icon pixmap, save state.
5. On opacity change: `note.setWindowOpacity(value/100)`, save state.

**Acceptance:**
- Tray icon visible in "show hidden icons". Right-click shows full menu. Checkmarks reflect current state.
- Changing color updates note AND tray icon immediately and persists.
- Changing opacity is visible immediately and persists.
- Quit cleanly exits and saves.

---

## Step 6 — Lock mode

1. Add `set_locked(bool)` method to `StickyNote`:
   - When locked: add `Qt.WindowTransparentForInput` to window flags, hide the border (set a `_locked` bool used by `paintEvent` to skip the border stroke), clear focus from text edit, make text edit `setReadOnly(True)`.
   - When unlocked: remove `WindowTransparentForInput`, show border, `setReadOnly(False)`.
   - **Important:** changing window flags requires calling `self.show()` again on Windows for them to take effect. Re-apply geometry after.
2. Hook the tray "Lock / Unlock" action to this. Update its label and checkmark. Persist `locked`.
3. On left-click on tray icon, toggle the same way.
4. On startup, if `state.locked == True`, start in locked mode.

**Acceptance:**
- When locked, mouse clicks pass straight through the note to whatever app is underneath. The note is visible but un-clickable.
- Tray icon still works. Unlocking restores edit + drag.
- Restarting the app preserves lock state.

---

## Step 7 — Polish & guards

1. **Off-screen recovery:** on startup, if saved `(x, y)` is outside any available screen geometry, reset to `(100, 100)`. (`QGuiApplication.screens()` → check `availableGeometry`.)
2. **3-line cap (hard):** in `QTextEdit.textChanged`, if `document().blockCount()` > 3 OR text height exceeds 3-line limit, undo the last edit (`textCursor().deletePreviousChar()` or restore previous text). Simplest: keep `_last_text`; if new text exceeds limit, `setPlainText(_last_text)` and restore cursor to end.
3. **Tray-only app:** set `QApplication.setQuitOnLastWindowClosed(False)` so closing the note widget (if it ever happens) doesn't kill the app — only the tray Quit does.
4. **Single instance** (optional): `QLocalServer`/`QLocalSocket` with name `"nova-singleton"`. If another instance exists, exit immediately.
5. **Save on quit:** connect `QApplication.aboutToQuit` to a final synchronous `state.save(...)`.

**Acceptance:**
- Cannot type more than 3 wrapped lines.
- Closing the note widget does not quit the app (still in tray).
- Launching twice doesn't open two notes (if you implemented single instance).
- Note never appears off-screen after a monitor change.

---

## Step 8 — Build to .exe

1. Write `build.ps1`:
   ```powershell
   pip install -r requirements.txt -r requirements-dev.txt
   pyinstaller --noconfirm --onefile --windowed --name Nova --icon NONE -p . nova/__main__.py
   ```
2. Verify `dist\Nova.exe` runs on a clean shell with no Python in PATH (or just double-click it).
3. Document in README how to add it to Windows startup: drop a shortcut into `shell:startup`.

**Acceptance:**
- `dist\Nova.exe` launches the note + tray icon, no console window appears, and quitting via tray works.

---

## Definition of done

- [ ] All 8 steps pass their acceptance criteria.
- [ ] App is < ~400 lines of Python total.
- [ ] One dependency (`PySide6`) at runtime.
- [ ] State persists across restarts for: text, position, color, opacity, lock.
- [ ] Lock mode is true click-through (verify by clicking through it onto a browser).
- [ ] Tray menu is the only settings UI.
- [ ] Looks like a small FigJam sticky.

If any step's acceptance fails, **fix before proceeding**. Do not add features not listed here. If you find yourself wanting to "improve" something — don't. Simplicity is the feature.
