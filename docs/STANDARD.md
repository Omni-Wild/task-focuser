# The companion standard: carrying the bird into the next utility

Written 2026-11-04 by the Conflux root at Jiří's ask after five rounds of Task Focuser v2 ("the coolest thing I ever built or helped build"). A proposal, not a decision: it says what Task Focuser settled, what of it is already portable, and how the next little utility would pick it up. Read it before starting a new utility with a companion; decide the open questions at the end on the first one.

## 1. What a utility of his looks like (the shape that worked)

- **One folder, light layer.** `personal-projects/<slug>/` (or its own repo once it outgrows this one): `app/` (the source and the scripts), `docs/SPEC.md` plus one `SPEC-roundN.md` per round, `context/` (`state.md`, `log.md`, `decisions.md`), `README.md`, `CLAUDE.md`. No skills layer, no inputs: a utility has no input stream and no project agent to run (Task Focuser decision 007).
- **The stack.** .NET 9 WPF, C#, no NuGet, one framework-dependent exe (0.4 MB) published by `build.ps1`, installed by `install.ps1` to `C:\Tools\<Name>\` with a Run key and a Start-menu shortcut. State in `%APPDATA%\<Name>\state.json`, written atomically, debounced, every field with a default so an old file never breaks. Single instance with a named pipe, so a second launch forwards switches (`--show`, `--lock`, `--quit`, ...) to the running one: that is how scripts and tests drive it without a mouse. A tray icon always.
- **Windows that behave.** Borderless transparent layered windows, topmost, no taskbar button, `WS_EX_TOOLWINDOW` (out of Alt-Tab), `WS_EX_NOACTIVATE` whenever the window must not steal his keyboard, system-DPI aware so both screens share one coordinate system (`Core/Win32.cs`, `Core/Monitors.cs`).
- **Motion rules.** Everything quick (150 to 450 ms), small overshoot, every animation cancellable and removed when done (`FillBehavior.Stop` or `BeginAnimation(dp, null)`), no timers at idle, pulses capped at 30 fps, the app-wide frame rate at the monitor's 165. These rules are what keep a cute app cheap.
- **The why next to the what.** `context/decisions.md` with one row per call and its reason; `docs/SPEC-roundN.md` opening with his words verbatim. Five rounds later every behaviour still traces to a sentence he said.

## 2. The companion kit: what is already portable

The bird is a module with a narrow contract. Nothing in it knows about post-its.

| Piece | Files | What it is |
|---|---|---|
| The skin contract | `Monster/BirdSkin.cs`, `Monster/Birds.cs` | A bird = an id, a default name, a legend (cell character to colour) and 23 named frames with fixed meanings (`IdleOpen`, `IdleBlink`, `FlapUp/Mid/Down`, `ShoveA/B`, `Tumble1/2`, `Eat1/2`, `Happy`, `Chirp`, `TiltL/R`, `Preen`, `Hop`, `Ruffle1/2`, `Peck1/2`, `Walk1/2`), 22x16 cells of 3 DIP, feet on row 15, facing right. Adding a bird is art plus five sounds, never behaviour code (decision 022). |
| The art | `Monster/Sprites.cs` (Lennie, the glyphs), `Monster/OwlSprites.cs` (Meggie) | String grids, parsed by `tools/make-icon.ps1` for the app icon and rendered by `SpriteView`. Legend keys never collide across birds (lowercase Lennie, uppercase Meggie), so one brush map serves all. |
| The renderer | `Monster/SpriteView.cs` | Crisp cells, one baked drawing per frame, flip for facing. |
| The behaviour | `Monster/MonsterWindow*.cs`, `Effects.cs`, `Grip.cs` | Arrive, perch, carry (flap), lose grip on fast moves and fly back, shove into a dot, leave, fly to a moved perch, eat with hearts, peck seeds, ruffle, walk, idle fidgets, dust on landing, clicks and moods (the fly-off after five screams), the seamless switch between birds, opacity, a perch span. Its own clickable, non-activating window above the host. |
| The voice | `Core/Sound.cs` | Five synthesized sounds per bird (keepalive, happy, annoyed, peep, ruffle), rendered once to WAV, played through WPF `MediaPlayer` at a non-linear volume; the idle scheduler's ranges (often, mid, rarely). |
| The contract | `Core/Contracts.cs`, `IMonster` | What the host calls: `ArriveAt`, `MoveTo`, `FlyTo`, `SetCarrying`, `Shove`, `Leave`, `SwitchTo`, `SetPerchSpan`, `SetOpacity`, `Eat`, `Peck`, `Ruffle`, `Flap`, `Contains`; what the bird raises: `Clicked`, `ChirpRequested`, `DragStarted/Moved/Ended`. |

What the **host** must provide, and nothing more:
- a **perch**: a point on its top edge where the feet go, reported for the *final* layout during any animation (the round-3 lesson: aim where the edge will be, not where it is);
- a **span**: the x range the feet may occupy, kept current on every resize or move;
- **events** for the bird to react to: edit mode on and off (arrive and leave), drag start, move and end (carry; the bird's own drag events move the host back through a `NudgeBy`), keystrokes (the peck chain), a drop target for the cucumber;
- a **conductor**: one state machine that owns the sequencing (busy flags, watchdogs, the dock), plays sounds at the user's volume and persists the companion's fields: `Bird`, `BirdNames`, `BirdStays`, `BirdOpaque`, `SoundOn`, `Volume`, `IdleSounds`, `IdleRate`.

## 3. The recipe for the next utility

1. **Copy the kit**: `Monster/`, `Core/Sound.cs`, `Core/Win32.cs`, `Core/Monitors.cs`, `Core/Contracts.cs` (the `IMonster` half), the companion fields of `NoteState`, the sprite part of `tools/make-icon.ps1`. Keep the namespaces; the kit has no reference back into `Views/`.
2. **Give the host a perch**: whatever the utility's main surface is (a bar, a panel, a card), expose `PerchPoint`, `Bounds` and a span; raise the drag and edit events the bird listens to.
3. **Write the conductor** from Task Focuser's `Conductor*.cs`: keep the handlers named after events, the `Guard`/`Fail` wrappers, the busy flag with its watchdog, the idle sound scheduler, the keep-alive reason (his headphones). Drop what the utility has no use for (the dot, the cucumber) without touching the bird.
4. **Reuse the bar pieces** when the utility has a settings strip: the name-under-the-bird picker with rename, the volume button with its hover popup, the two bird toggles (`Views/BirdPicker.xaml`, the volume `Popup` in `SettingsBar.xaml`).
5. **Round by round**: his words at the top of `docs/SPEC-roundN.md`, the root writes contracts and compile stubs, builders own files and build in scratch copies, compile-only while he is at the machine, the harness with synthetic input when he is away, install with `install.ps1`.

## 4. Making it a real standard (the step after the next utility)

- **A shared project.** Lift the kit into `personal-projects/_companion/` as `Companion.csproj` (a class library, still no NuGet) referenced by each utility's `.csproj`; the utilities stop copying files and pick up new birds and fixes by rebuilding. The host contract becomes an interface in the library (`ICompanionHost`: perch, span, events); the conductor's bird half moves in with it.
- **A birds folder.** One file per bird in the library (`Birds/Lennie.cs`, `Birds/Meggie.cs`), a contact-sheet script that renders every bird's frames for review, and the five-sound set per bird in `Sound.cs` keyed by id. A new bird is one art file, one sound block, one registry line.
- **A utility template.** `_utility-template/` beside the project template: the folder shape of section 1 with the kit referenced, the conductor skeleton, `install.ps1`, the README outline, `CLAUDE.md` with the rules of this file. Scaffolding a utility becomes a copy plus a rename.
- **The performance round first.** The round-1 known gap (native memory growing per show/hide cycle, the render thread sometimes ticking at idle) (closed by the release pass of 2026-11-04, SPEC section 9.6: bounded, not a leak) belongs to the kit, so any later finding gets fixed once, in the library, before the second utility inherits it.

## 5. Open for him to decide (on the first new utility, not before)

1. Shared project or copy: the library is cleaner, the copy is simpler for a one-off. Recommendation: copy for the second utility, library from the third.
2. Where the kit lives: inside Conflux (`personal-projects/_companion/`) or its own repo like the game. Recommendation: inside Conflux until a utility leaves the repo.
3. Scope: personal utilities only, or also the DX Heroes tooling. Recommendation: personal; a work tool follows dx-business rules and would carry the bird only on his explicit call.
4. The kit's name. The folder is `Monster/` for history; the thing is a companion. Recommendation: `Companion`, with the birds keeping their names.
