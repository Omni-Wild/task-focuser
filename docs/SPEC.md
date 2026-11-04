# Task Focuser v2: the spec

Written 2026-11-04 by the Conflux root for the build crew. One desktop app, Windows 11, .NET 9 WPF, C#. A post-it note taped to the monitor, with a small blue pixel monster for company.

## 1. The brief (Jiří's words, lightly ordered)

- "A floating textbox window that looks like a post-it note. I can type into it when I get the focus and I can make it *locked*: I will not be able to type into it unless I unlock it in the widget window. It is for me to type 1-3 lines of text, or more if I need to, of what I need to be focusing on currently. Helps me stay sharp when I am splitting between 10 tasks (ADHD)."
- "Focus on making it as simple and as intuitive as possible."
- "When locked, make it unlock by double clicking into it. Dragging it or hovering on it when it's locked should not change its look. Dragging should still be allowed in lock."
- "When unlocked show the settings for a transparency slider, a color picker from 5 predefined colors, a setting for changing the font. All settings visible when it's unlocked, as the top bar of the post-it note."
- "Make it auto expand based on the text, to the left and down. No scroll bar."
- "Make it take as little space as possible, within reason, I want some padding."
- "I treat it as a literal physical post-it note that I tape to my monitor; when I need the area I just move it out of the way."
- "If I start dragging it, a red glowing dot appears in the top right corner of whichever monitor it is being dragged about, and when I place it onto the dot it minimizes there. Then only the small glowing dot hovers there, looking like a glorified notification red circle. When I double click this dot it spits out the note next to it. I can also drag the dot; when I start to drag it a bit shows at the bottom right and if I drag it there it closes the dot. I revert this in the tray (the arrow-up thingy at the bottom right of Windows): there I do *start* or something and it brings back the dot to its original place."
- "Make it not eat much resources but still some cutesy animation, bouncy playful animations."
- "A little blue monster with pixel art looking kind of like the Claude picture (a boxy orange creature with two eyes and four legs). Animate it a bit: when I enter the edit it flies from the top of nowhere onto the top bar of the note and with it the edit bar appears, super swift and fluid, like 165 fps. When I am dragging it around the little bird drags it while it flaps its wings. When I put it into the notification dot, the bird quickly shoves it in there."
- "Nice, playful, cutesy, kind of Japanese style, but also quick and sharp."

Non-goals: no rich text, no multiple notes, no sync, no markdown, no settings window, no click-through mode (the old app had one; dropped on his word: locked notes stay draggable).

## 2. The product in one picture

```
   locked (resting)                       unlocked (editing)
   +----------------------+                     ,-(monster perched on the bar)
   |  Write Michal        |               +--[ o ]--------------------------------+
   |  Toggl               |               | (o)------ [#][#][#][#][#]  Segoe v A- A+  [lock] |
   |  Prezentace pro SLSP |               |---------------------------------------|
   +----------------------+               |  Write Michal                         |
                                          |  Toggl                                |
                                          |  Prezentace pro SLSP|                 |
                                          +---------------------------------------+
```

Three modes, persisted:
- `Note`: the note is on screen (locked or unlocked).
- `Dot`: the note is tucked into the red dot; only the dot is on screen.
- `Closed`: nothing is on screen; the tray icon remains. Tray "Start the dot" brings the dot back to its original place (the top-right corner of the monitor the note last lived on).

## 3. Behaviour, in detail

### 3.1 The note, locked
- A pastel rectangle, corner radius 6, a thin darker "glue strip" 6 DIP tall along the top edge (a real post-it has one), soft shadow (offset 0,3; blur 10; black at 30 %). No border line. Text colour `#2B2A25`.
- Text is read-only, the caret is hidden, the mouse cursor over it is the arrow, hover changes nothing, pressing changes nothing. Selection is impossible.
- Left-press and move anywhere on it drags it. The window must not steal keyboard focus from the app he is working in: the note carries `WS_EX_NOACTIVATE` while locked.
- Double-click anywhere on it unlocks it (and does not start a drag).
- Padding: 14 DIP left and right, 10 DIP top (below the strip) and bottom. Minimum width 140 DIP. Maximum width 520 DIP, after which lines wrap. Height follows the lines. No scrollbars ever.
- Growth anchor: the top-right corner stays put, the note grows to the left and down (his words). When the left edge would cross the monitor's work-area edge, the left edge pins there and the right edge moves instead. The note never leaves the work area of its monitor on its own; a drag may still push it partly off (he decides where it sits), but the resting position restored at startup is clamped into a visible work area.
- Always on top, no taskbar button, not in Alt-Tab (`WS_EX_TOOLWINDOW`).

### 3.2 The note, unlocked
- The settings bar is the note's top bar, 34 DIP tall, in the strip colour (the note colour darkened 12 %). It carries, left to right: the monster's perch (an empty 40 DIP slot at the left end where it lands), the opacity slider (20 to 100 %, about 70 DIP wide, no label), five colour swatches (circles 14 DIP, the chosen one ringed), the font picker (a compact dropdown of the five fonts, or a button that cycles through them with the name shown; the builder decides which is simpler and cleaner), `A-` and `A+` (font size 11 to 28, step 1, default 15), and the lock button at the right end (a small padlock glyph).
- The text is editable, caret visible, Enter inserts a newline. On unlock the text box gets focus with the caret at the end.
- Drag: only by the bar (its empty areas and the monster), since the text area needs the mouse for the caret. While dragging, the monster carries the note (3.5).
- Lock: the padlock button, `Esc`, or `Ctrl+L`. The bar slides up and away (the note shrinks from the left, the right edge fixed), the monster flies off, the window drops `WS_EX_NOACTIVATE` back on. (Since the release pass: `Alt+F4` on the unlocked note locks it too, instead of closing the window; see 9.2.)
- The minimum width while unlocked is the bar's natural width (about 330 DIP), so the bar never wraps or clips.
- `Ctrl+=` / `Ctrl+-` change the font size too.

### 3.3 Unlock and the monster's arrival
- On unlock: the bar unfolds downward from the strip (height 0 to 34 with a slight overshoot, 220 ms), and at the same time the monster flies in from above the top of the screen to its perch at the left end of the bar, overshooting a little and settling (about 450 ms, swift). It then sits there, idling (an eye blink every few seconds, nothing more, so the idle cost is near zero).
- On lock: the reverse, the monster hops off upward and vanishes, the bar folds up.

### 3.4 Dragging the note
- On drag start (either mode): the monster arrives (if not already perched) and starts flapping, perched on the note's top edge at the perch slot (24 DIP in from the left, the same spot as on the bar), "holding" it by the corner. The note follows the cursor exactly (no lag, no easing: it is in his hand). At 165 Hz the window position updates on every mouse-move message.
- The red dot arms at the top-right corner of the work area of the monitor under the cursor (centre 28 DIP in from the right and 28 DIP down from the top of the work area). If the cursor crosses to another monitor, the dot moves to that monitor's corner (hide here, show there).
- When the cursor is within 44 DIP of the dot's centre, the dot goes "hot" (swells, brighter glow) to say "drop it here".
- Drop while hot: the dock sequence (3.6). Drop elsewhere: the dot disarms (shrinks to nothing, 150 ms), the position is saved. If the note was locked, the monster flies off; if unlocked, it hops back to the perch on the bar.

### 3.5 The monster
- Pixel art, blue, in the spirit of the Claude mascot: a boxy body wider than tall, two eyes, four short legs; ours adds two small wings. Palette: body `#4F8DFF`, outline `#1F4FBF`, belly `#9CC4FF`, eyes white with `#1A1A2E` pupils, wings `#BFE0FF` with the outline colour. A grid of about 16 by 14 cells drawn at 3x (one cell = 3 DIP) with no smoothing (`NearestNeighbor`). It lives in its own transparent, click-through, non-activating window owned by the note so it can fly across the screen and sit above the note.
- Frames: `Idle` (2 frames: eyes open, blink), `Flap` (3 frames: wings up, mid, down, cycled at about 14 fps), `Shove` (2 frames: leaning forward with the wings back). Frame timers run only while a looping state is active; the idle blink is a single-shot timer with a random 2 to 5 s interval.
- Motion is WPF animation on the window's `Left`/`Top` with easing (`BackEase`/`ElasticEase` out for arrivals, `QuadraticEase` in for departures). `Timeline.DesiredFrameRate` is overridden to 165 app-wide so the render thread is not capped at 60.

### 3.6 The dock sequence (note into the dot)
1. Drop while hot. The monster darts from the perch to the dot (120 ms), leaning in (`Shove` frames).
2. The note squashes toward the dot: scale to 0.08 around the top-right corner and move so its top-right lands on the dot centre, `QuadraticEase` in, 200 ms. The note window hides at the end.
3. The dot "catches": a quick squash-and-stretch (scale 1.4 then 0.9 then 1.0, 260 ms), and settles into its resting look: an 18 DIP red circle (`#FF3B3B`) with a soft outer glow, pulsing slowly (opacity of the glow 0.5 to 1.0 over 2.4 s, loop, `DesiredFrameRate` 30 on that storyboard so a resting dot costs nothing noticeable).
4. The monster hops off and vanishes. Mode becomes `Dot`; the dot's centre is saved.

### 3.7 The dot
- Resting: as above. Non-activating, always on top, no taskbar presence. Tooltip "Task Focuser: double-click to pop the note out".
- Double-click: the dot pops (squash 0.7, stretch 1.3, settle, 220 ms) and the note pops out beside it: the note's top-right corner 24 DIP left of and 12 DIP below the dot centre, clamped into the work area, scaling 0.2 to 1.0 from the dot's direction with `BackEase` out (280 ms). The dot stays visible until the note is out, then vanishes (the note is out, nothing is tucked). Mode becomes `Note`. If the note is unlocked, the monster arrives at the perch as in 3.3.
- Drag the dot: it follows the cursor. On drag start the close target appears at the bottom-right corner of the work area of the dot's monitor (centre 36 DIP in from the right and 36 DIP up from the bottom of the work area): a 40 DIP dark grey disc (`#3A3A3A` at 90 %) with a white X glyph, fading in 150 ms. When the cursor is within 44 DIP of it, it goes hot (swells to 48 DIP, turns `#D23B3B`). Drop while hot: the dot shrinks to nothing (160 ms), the target fades out, mode becomes `Closed`. Drop elsewhere: the dot rests where dropped and the centre is saved.
- The dot follows the cursor across monitors like the note does (the close target moves with it).

### 3.8 The tray icon
- Always present while the app runs. Icon: the monster at 16 and 32 px (the app's `.ico`). Tooltip "Task Focuser".
- Left click: show the note (from `Note`: bring it to the front; from `Dot`: the pop-out of 3.7; from `Closed`: show the note at its saved position).
- Right-click menu: `Show note`, `Tuck into the dot` (from `Note`: the dock sequence without the drag; the dot appears at the top-right of the note's monitor), `Start the dot` (only enabled in `Closed`; the dot rests at the top-right corner of the work area of the monitor the note last lived on, which is "its original place"), a separator, `Lock` / `Unlock` (the label follows the state), `Start with Windows` (checkable, writes the HKCU Run key), a separator, `Quit`. (Since the release pass: the tick also reads Windows' own startup switch under `Explorer\StartupApproved\Run` and clears it when ticked, and a Run value pointing at an exe that no longer exists is repointed at startup; see 9.1.)

### 3.9 Persistence
- `%APPDATA%\Task Focuser\state.json`, UTF-8, atomic write (tmp then replace), debounced 300 ms after the last change, flushed on quit. The old app wrote the same file with `text`, `x`, `y`, `color`, `opacity`, `locked`; the new app reads those keys unchanged so his current note survives the upgrade, and adds `version: 2`, `font`, `fontSize`, `mode`, `dotX`, `dotY`, `startWithWindows`. `x`/`y` are the note's top-left in screen DIPs; `dotX`/`dotY` the dot centre; `NaN`/absent dot = not yet placed.
- Corrupt or missing file: defaults, and the file is rewritten. (Since the release pass: a file that is there but could not be read at startup is copied aside as `state.json.unread-<stamp>` before the first write replaces it, and a test profile keeps its own `state.<name>.json`; see 9.1.)

### 3.10 Startup and single instance
- One instance (a named mutex). A second launch forwards its command line to the first and exits. Arguments the running instance accepts, for scripts and for the tests: `--show`, `--lock`, `--unlock`, `--dot` (tuck), `--close` (mode Closed), `--start` (the tray's "Start the dot"), `--quit`. (Since the release pass: the forwarding pipe's name carries the session id like the mutex, `TASKFOCUSER_PROFILE=<name>` runs a separate copy with its own mutex, pipe and state file, and an exception during startup shows a message box and exits instead of leaving a windowless process holding the mutex; see 9.1.)
- On start: load state, pick the mode: `Note` shows the note (locked state as saved, position clamped into a visible work area), `Dot` rests the dot at the saved centre (clamped), `Closed` shows nothing. The tray icon always.
- DPI: system-DPI aware (WPF's default), so one scale applies to every monitor and screen coordinates are consistent. Both his monitors run 125 %. Per-monitor DPI differences are best effort, not a goal.

### 3.11 Resource budget
- Idle (locked note, or resting dot): no timers except the dot's 30 fps glow pulse in `Dot` mode and the monster's blink in unlocked mode. CPU at idle must read 0 % in Task Manager. Working set under 80 MB.
- Animations run only while something moves. No per-frame allocations in the drag path. Shadows are small (blur 10). Nothing polls. (Since the release pass: the idle-sound timer of round 3 stops while sound is off, the fold's per-frame text measurements are memoised, and the round-1 known gap of native memory growing per show/hide cycle is closed as bounded, not a leak: private bytes plateau near 200 MB after use; see 9.5 and 9.6.)

## 4. Architecture and file ownership

```
app/
  TaskFocuser.csproj           root (done)      net9.0-windows, WPF + WinForms (for NotifyIcon and Screen), single exe
  App.xaml                     root (done)      the resources, StartupUri none (the conductor shows windows)
  App.xaml.cs                  B5 conductor     entry point, the state machine of section 5, wiring everything
  Core/NoteState.cs            root (done)      the persisted state, the Mode enum
  Core/Palette.cs              root (done)      the five colours, the five fonts, limits, the darken helper
  Core/Contracts.cs            root (done)      the interfaces every module implements or consumes (section 6)
  Core/StateStore.cs           B1 core          load/save/migrate, debounce, atomic write
  Core/Monitors.cs             B1 core          work areas and bounds in screen DIPs, clamping, corners
  Core/Win32.cs                B1 core          the few P/Invokes: extended styles (NOACTIVATE, TOOLWINDOW, TRANSPARENT), cursor position
  Core/SingleInstance.cs       B1 core          mutex + named pipe to forward arguments
  Core/StartupRegistry.cs      B1 core          the HKCU Run key toggle
  Core/TrayIcon.cs             B1 core          NotifyIcon wrapper with the menu of 3.8 as events/properties
  Views/NoteWindow.xaml(.cs)   B2 note          the note (3.1 to 3.4 minus the monster), implements INoteWindow
  Views/SettingsBar.xaml(.cs)  B2 note          the bar as a UserControl bound to NoteState
  Views/Theme.xaml             B2 note          slider, button, swatch styles (merged in App.xaml by B2's instruction)
  Views/DotWindow.xaml(.cs)    B3 dot           the dot (3.6 to 3.7), implements IDot
  Views/CloseTargetWindow.xaml(.cs) B3 dot      implements ICloseTarget
  Monster/Sprites.cs           B4 monster       the frames as string grids plus the palette
  Monster/SpriteView.cs        B4 monster       a FrameworkElement that draws one frame crisp at 3x
  Monster/MonsterWindow.xaml(.cs) B4 monster    the window, states and motions of 3.5, implements IMonster
  Assets/monster.ico           B6 tooling       generated from the sprite by tools/make-icon.ps1
  tools/make-icon.ps1          B6 tooling       draws the idle frame into 16/24/32/48/64 px layers, writes the .ico
  build.ps1                    B6 tooling       dotnet publish, single file, framework-dependent, win-x64 -> dist/TaskFocuser.exe
  install.ps1                  B6 tooling       build, stop the old Python "Task Focuser", copy to C:\Tools\TaskFocuser, Run key, Start-menu shortcut, launch
  README.md                    B6 tooling       how to build, install, use; the keyboard and tray reference
```

Rules for every builder:
- Namespace `TaskFocuser`, sub-namespaces `TaskFocuser.Core`, `TaskFocuser.Views`, `TaskFocuser.Monster`. Nullable on, implicit usings on, file-scoped namespaces.
- The skeleton compiles as handed over: every owned file exists as a stub. Replace your stubs; never edit another builder's files. If you need something from another module that the contract does not give, write it in your report, do not reach in.
- Compile-check in your own scratch copy: copy `app/` to `%TEMP%\tf-build-<your-name>\app` and run `dotnet build` there (the other modules are stubs, so it builds). Never run `dotnet build` inside the repo folder: six builders share it.
- No NuGet packages. WPF, WinForms (`UseWindowsForms` is on for `NotifyIcon` and `Screen`), `System.Text.Json`.
- Comments say why, not what. Keep each file under about 400 lines.
- Every animation is cancellable: a new state request stops the running storyboard before starting its own, so a fast user never breaks the state machine.

## 5. The conductor's state machine (B5)

Events in: note `DragStarted/DragMoved/DragEnded`, note `LockedChanged`, note `Changed`, dot `DoubleClicked`, dot `DragStarted/DragMoved/DragEnded`, tray actions, forwarded command-line arguments.

```
Note mode:
  note.DragStarted(p)  -> monster.ArriveAt(note.PerchPoint) if !monster.IsOut; monster.SetCarrying(true)
                          dot.Arm(TopRightCorner(WorkAreaAt(p)))
  note.DragMoved(p)    -> monster.MoveTo(note.PerchPoint)
                          if monitor changed: dot.Arm(new corner)
                          dot.SetHot(dot.Contains(p))
  note.DragEnded(p)    -> monster.SetCarrying(false)
                          if dot.Contains(p): Dock()           else: dot.Disarm(); save; if locked monster.Leave() else monster.MoveTo(perch)
  note.LockedChanged   -> unlocked: monster.ArriveAt(note.PerchPoint)   locked: monster.Leave();  save
  note.Changed         -> save (debounced)
  tray Tuck            -> dot.Arm(TopRightCorner(WorkAreaAt(note centre))); Dock()
  Dock():               monster.Shove(note.PerchPoint, dot.Centre, then: monster.Leave())
                        note.SquashInto(dot.Centre, then: dot.Dock(dot.Centre, then: mode=Dot; save))
Dot mode:
  dot.DoubleClicked    -> dot.Pop(then: note.PopOut(dot.Centre, BesideDot(dot.Centre)); dot.HideDot(); mode=Note; if unlocked monster.ArriveAt(perch); save)
  dot.DragStarted(p)   -> closeTarget.ShowAt(BottomRightCorner(WorkAreaAt(p)))
  dot.DragMoved(p)     -> if monitor changed: closeTarget.ShowAt(new corner); closeTarget.SetHot(closeTarget.Contains(p))
  dot.DragEnded(p)     -> if closeTarget.Contains(p): dot.Vanish(then: closeTarget.HideTarget(); mode=Closed; save)
                          else: closeTarget.HideTarget(); save dot centre
Closed mode:
  tray Start the dot   -> dot.Rest(TopRightCorner(WorkAreaAt(note's saved position))); mode=Dot; save
  tray Show note       -> note.ShowNote(); mode=Note; save
Any mode:
  tray Lock/Unlock     -> note.SetLocked(!locked) (in Note mode; in other modes only flips the state field)
  tray Quit            -> flush save; exit
  --show/--lock/--unlock/--dot/--close/--quit -> the same handlers
```

Save = `StateStore.Save(state)` debounced; positions are written into `state` by the windows themselves (`X`,`Y` by the note after a drag and after growth; `DotX`,`DotY` by the conductor from `dot.Centre`).

## 6. Contracts

See `Core/Contracts.cs`. Summary:

- `INoteWindow`: `NoteState State`, events `DragStarted/DragMoved/DragEnded(Point cursorScreenDip)`, `LockedChanged(bool)`, `Changed()`; `ShowNote()`, `HideNote()`, `SetLocked(bool)`, `Point PerchPoint` (screen DIPs: the top edge of the note at the monster's slot, where its feet go), `Rect Bounds`, `PopOut(Point from, Point topLeft)`, `SquashInto(Point target, Action onDone)`, `BringToFront()`.
- `IMonster`: `ArriveAt(Point perch)`, `MoveTo(Point perch)`, `SetCarrying(bool)`, `Shove(Point from, Point target, Action onDone)`, `Leave()`, `bool IsOut`. The perch is where the feet centre goes; the sprite is drawn with its feet on that point and its body above it.
- `IDot`: `Point Centre`, `Arm(Point)`, `SetHot(bool)`, `Disarm()`, `Dock(Point, Action)`, `Rest(Point)`, `Pop(Action)`, `Vanish(Action)`, `HideDot()`, `Contains(Point)`, events `DoubleClicked`, `DragStarted/DragMoved/DragEnded(Point)`.
- `ICloseTarget`: `Point Centre`, `ShowAt(Point)`, `SetHot(bool)`, `HideTarget()`, `Contains(Point)`.
- `ITray`: events `ShowNote`, `Tuck`, `StartDot`, `ToggleLock`, `ToggleStartWithWindows`, `Quit`; `void Update(Mode mode, bool locked, bool startWithWindows)` (enables/labels the items).
- `Monitors` (static): `Rect WorkAreaAt(Point)`, `Rect BoundsAt(Point)`, `Rect PrimaryWorkArea`, `Rect ClampInto(Rect, Rect)`, `Point TopRightCorner(Rect area)` (the dot's corner per 3.4), `Point BottomRightCorner(Rect area)` (per 3.7), `bool SameMonitor(Point, Point)`, `Point CursorPosition()`.
- `StateStore` (static): `NoteState Load()`, `void Save(NoteState)` (debounced), `void Flush(NoteState)`, `string Path`.
- `StartupRegistry` (static): `bool IsEnabled()`, `void Set(bool)`.
- `SingleInstance`: `static bool TryBecomePrimary(Action<string[]> onForwardedArgs)`, `static void Forward(string[] args)`.
- `Win32` (static): `SetNoActivate(Window, bool)`, `SetToolWindow(Window)`, `SetClickThrough(Window, bool)`, `Point CursorPosition()` (DIPs).

All points and rects in the contracts are screen coordinates in WPF DIPs (the system DPI scale applies, physical = DIP * scale, scale from `VisualTreeHelper.GetDpi` of any shown window or the primary screen).

## 7. Look and feel notes

- The five colours (Palette.cs): Yellow `#FFF59E`, Peach `#FFD3A8`, Pink `#FFC6DD`, Mint `#C4F0C9`, Sky `#BFE3FF`. The strip and the bar are the colour darkened 12 %. The chosen colour is the whole note; nothing else is tinted.
- The five fonts: Segoe UI (default), Segoe Print, Comic Sans MS, Cascadia Code, Bahnschrift. All ship with Windows 11 on his machines.
- Motion is quick: nothing takes longer than 450 ms, most things 150 to 280 ms. Overshoot is small (BackEase amplitude 0.4 to 0.6), never wobbly.
- Playful, Japanese-cute: round shapes, soft shadows, a blink, a hop, a squash. Nothing noisy: no sounds, no particles.
- Keep the note's chrome minimal: no title, no close button, no resize grips. The only chrome is the bar, and only when unlocked.

## 8. Verification (the root does this after integration)

- Build and run; screenshot each state (locked note, unlocked note with bar and monster, dot resting, dot armed/hot, close target).
- Script a drag with `SendInput` from PowerShell: note to the dot, dot to the close target, tray Start.
- Idle CPU and working set from `Get-Process`.
- The old state file loads: his current three lines appear in the new note.

## 9. Release pass (2026-11-04)

Before the first public release (decision 025), five code reviewers and one performance investigator read the whole app. What they found and patched, grouped by module, each with the reason it had to change. Nothing here changes how the note is meant to feel; every item makes it do what the sections above already promise, in a situation the rounds had not met.

### 9.1 Core
- **Start with Windows reads and clears Windows' own switch.** Task Manager's Startup tab and Settings > Apps > Startup never delete the Run value; they write a disabled flag of the same name under `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run`, so the tray tick used to show "on" for an entry Windows skipped, and re-ticking it did nothing. The tick now counts as on only when that flag is absent or enabled, and ticking it clears the flag.
- **A moved exe keeps its autostart.** At startup, a Run value that points at an exe that no longer exists is repointed to the running exe, because a downloaded exe moved or renamed after ticking the box used to kill autostart silently. A value that starts an existing file is left alone.
- **A failed start says so and exits.** An exception during startup now shows a message box and quits, because the dispatcher's handler used to swallow it and leave a windowless process holding the single-instance mutex, so every later double-click on the exe was forwarded into it and lost.
- **The pipe is per session, like the mutex.** The single-instance pipe name carries the session id, because pipe names are machine-wide while the mutex is per session: two accounts on one PC, or an RDP session beside the console, broke forwarding and left a retry spinning every 250 ms.
- **An unread state file is kept, not overwritten.** When `state.json` could not be read at startup (a lock, a cloud folder not mounted yet), the first write copies the existing file aside as `state.json.unread-<yyyyMMdd-HHmmss>` before replacing it, because the defaults used to overwrite his note 300 ms after start. A true first run finds nothing to copy.
- **Test profiles.** `TASKFOCUSER_PROFILE=<name>` (letters, digits, `-` and `_`) runs a separate copy with its own mutex, pipe and state file `state.<name>.json`, so the release tests could drive one beside the real note without touching it. Unset, nothing changes.

### 9.2 The note window
- **Ctrl+Z on a fresh session keeps the note.** The undo stack is cleared after the initial text is set, because WPF counts that set as an undo step and Ctrl+Z used to swap the note for the width-calibration probe "MMMMMMMM", which then got saved.
- **Alt+F4 locks.** On the unlocked note (the active window then) `Alt+F4` now locks it like `Esc`, because closing the window left it dead for the session: every later show threw, and the bird, owned by the note, went with it. The app's own shutdown still closes it.
- **Long words wrap.** A word wider than the note (a URL, an e-mail address at a big size) now wraps in the measurement the way the text box wraps it, because `FormattedText` trimmed it to one line by default, so the paper came out too short and the text ran out below it.
- **Moves made by Windows are adopted.** When Windows moves the window itself (a laptop undocked, a monitor plugged back in, `Win+Shift+arrow`), the paper, the bird's perch and the saved spot follow, because the cached rectangles still pointed at the old monitor and the next drag threw the note back there.
- **Popups close when the bird carries the note.** The bird picker and the volume hover popup are their own windows and used to stay where they opened while the note flew away.

### 9.3 The dot
- **A display change rescues a lost dot.** On a display change (a monitor unplugged, a resolution change), a resting dot on a screen that is gone moves to its spot on the nearest one, because it would otherwise sit out of reach.
- **A remembered spot on a missing monitor falls back to the corner.** At startup and when the dot is re-armed, a remembered spot on a monitor that is gone gives the plain top-right corner, because measuring its offset against the nearest screen's corner clamped it to the far edge, where it was then remembered.
- **A quick drag after a pop-out keeps the dot.** A drag started within 300 ms of a pop-out no longer hides the dot it has just armed, because the pop-out's delayed hide used to fire into the new drag.

### 9.4 The bird
- **A hide ends the carrying.** Hiding the bird resets its carrying state, because a `--close` in the middle of a drag used to leave the next arrival flapping on the perch with the render hook running every frame.
- **A refused drag puts the bird back to rest.** When the conductor refuses a drag started on the bird (during a dock lead, at the end of a bird switch), the bird returns to rest, because it used to keep a flap burst running or freeze in the frame of the act that was cut.

### 9.5 Sound
- **Players follow the audio device.** A player idle for more than 20 s is closed and opened afresh at its next play, so the keep-alive chirp goes to the device that is the default now (headphones reconnected, a switch of output) instead of staying on the old endpoint. Quick repeats, the clicks and the slider's peeps, still reuse the open player.
- **An unreadable sound file heals.** A cache file `MediaPlayer` cannot read is deleted and rendered again on the next play, because it used to stay silent for good and leak a player per attempt.
- **A truncated sound file is caught at warm-up.** A cache file whose length does not match its WAV header is deleted at warm-up and rendered again, because a file cut short outside our own write (a power loss, a disk tool) would play cut or fail on every play.
- **No idle timer while sound is off.** The idle-sound timer stops while sound is off and restarts when it is turned on, because a timer with nothing to play breaks 3.11.

### 9.6 Performance
- **The fold allocates no text layouts.** The two text measurements the fold and the snap make on every frame are memoised on text, font, size, DPI and wrap width, so a fold frame builds no `FormattedText` at all (about seventy line layouts of garbage per fold before). Any change of those inputs measures afresh.
- **No shadow while the paper scales.** The note's drop shadow is switched off during the squash and the pop-out and put back after, because an effect re-rendered under an animated scale makes WPF realise intermediate surfaces at every size it passes through (measured: about 55 MB of private bytes after the first squash and pop, against 3 MB without the effect).
- **The verdict on the round-1 known gap: bounded, not a leak.** Private bytes climb over the first show/hide cycles and then plateau around 200 MB; a plateau near 200 MB after use is normal. It is the GC's gen0 budget under the regions GC, which commits that much before it collects; the managed heap after a collection stays at a few MB. The two suspects written down in round 1, animation clocks left filling after completion and layered-window render targets re-created on `Show`, were both ruled out against the WPF source. This replaces the "native memory grows per cycle" wording of the round-1 known gap.

### 9.7 Packaging and the release
- **The csproj carries what the public exe shows.** `RollForward=Major`, so the small exe runs on the .NET 10 desktop runtime when no 9.x is installed (.NET 9 support ends 2026-11-10); `Company`, `AssemblyTitle` "Task Focuser", a `Description` and `Copyright` "(c) 2026 Jiri Kuldanek. MIT License." for the file properties; `IncludeSourceRevisionInInformationalVersion=false`, so the version reads 2.0.0 rather than 2.0.0 plus a commit hash.
- **install.ps1 -ExePath.** `install.ps1 -ExePath <file>` installs a downloaded release exe without building (it implies `-SkipBuild`), so a machine without the .NET SDK can still get the Run value and the Start-menu shortcut.
- **Two release assets.** `TaskFocuser.exe` is self-contained (about 65 MB, built with `build.ps1 -SelfContained`) and needs nothing installed; `TaskFocuser-lite.exe` is framework-dependent (about 0.4 MB) and needs the .NET 9 or 10 Desktop Runtime. Both are unsigned: SmartScreen asks once (More info, Run anyway), and Smart App Control, where it is on, blocks the exe outright.
