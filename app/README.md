# Task Focuser v2

A post-it note that floats above everything on your screen, holding the one to three lines you are focusing on right now. Locked, it is a read-only note you can drag out of the way; double-click to unlock and type. Drag it onto the red dot that appears in the monitor's top-right corner and it tucks itself away there. A small pixel companion (Lennie the budgie or Meggie the owl since round 5, or Rori the Japanese spitz since round 6) keeps you company while you edit and carries the note when you move it. It replaces the old Python Task Focuser and keeps its note.

## Install

```
powershell -ExecutionPolicy Bypass -File app\install.ps1
```

This builds the app, stops the old Python app and any running Task Focuser, copies the exe to `C:\Tools\TaskFocuser\TaskFocuser.exe`, turns on start with Windows (HKCU Run value `Task Focuser`), creates the Start-menu shortcut `Task Focuser` (and removes the old app's `Task Focuser.exe` shortcut), and launches it. If a process cannot be stopped, the script prints the `Stop-Process` command to run yourself.

| Switch | Does |
|---|---|
| `-InstallDir <path>` | Install somewhere else (default `C:\Tools\TaskFocuser`). |
| `-NoAutostart` | Leave the Run key alone. |
| `-NoLaunch` | Do not start the app at the end. |
| `-SkipBuild` | Install the `dist\TaskFocuser.exe` that is already built. |
| `-ExePath <file>` | Install this exe (a downloaded release) instead of building; no .NET SDK needed. Implies `-SkipBuild`. |
| `-DryRun` | Build, then only print what the other steps would do. |

Building needs the .NET 9 SDK. The framework-dependent exe needs the .NET 9 or .NET 10 Desktop Runtime (it rolls forward to 10 when no 9.x is installed); the self-contained one needs nothing.

**Without the script.** The script is a convenience, not a requirement: download `TaskFocuser.exe` from the release, put it wherever you like and run it. The tray's `Start with Windows` toggle covers autostart on its own (it writes the same HKCU Run value). If you move or rename the exe after ticking it, the next start from the new place repoints the Run value there. If you switched the app off in Task Manager's Startup tab or in Settings > Apps > Startup, Windows keeps the Run value and only marks it disabled; the tray tick shows that as off, and ticking it again switches it back on (the install script alone does not clear that switch). The exe is unsigned: SmartScreen asks the first time (More info, Run anyway), and Smart App Control, where it is on, blocks it outright.

## Use

- **Locked** (the resting state): the text cannot be changed and the note never takes the keyboard from the app you are in. Drag it anywhere. Double-click it to unlock.
- **Unlocked**: the settings bar slides out on top, the monster lands on it. Type freely, Enter is a new line. The bar has opacity, five colours, the font, `A-` / `A+` for the size, and the padlock. Drag the note by the bar.
- **Lock again**: the padlock, `Esc` or `Ctrl+L`. `Alt+F4` on the unlocked note locks it too; it never closes the note (quit from the tray).
- **Font size**: `Ctrl+=` / `Ctrl+-` (11 to 28).
- **The note grows** to the left and down as you type; no scrollbars.
- **The dot**: start dragging the note and a red dot appears in the top-right corner of the monitor under the cursor. Drop the note on it and the note tucks into the dot. Double-click the dot to pop the note back out next to it.
- **Closing the dot**: start dragging the dot and a dark X target appears in the bottom-right corner of that monitor. Drop the dot on it and everything is hidden; only the tray icon stays.
- **The tray icon** (the monster, under the arrow at the bottom right): left click shows the note. Right-click: `Show note`, `Tuck into the dot`, `Start the dot` (brings the dot back to the top-right corner after you closed it), `Lock` / `Unlock`, `Start with Windows`, `Quit`.

## Command line

Only one Task Focuser runs. Starting the exe again with a switch sends it to the running one and exits:

| Switch | Does |
|---|---|
| `--show` | Show the note. |
| `--lock` / `--unlock` | Lock or unlock it. |
| `--dot` | Tuck the note into the dot. |
| `--close` | Hide everything (tray only). |
| `--start` | Bring the dot back to its original place (the tray's "Start the dot"). |
| `--quit` | Save and quit. |

Example: `C:\Tools\TaskFocuser\TaskFocuser.exe --dot`

"Only one" means one per Windows session: another account on the same PC, or an RDP session beside the console, runs its own. For testing beside your real note, set `TASKFOCUSER_PROFILE=<name>` (letters, digits, `-` and `_`) before starting the exe: that copy has its own instance, its own forwarding and its own state file `state.<name>.json`, and the switches above reach it only from a launch with the same variable set.

If the app cannot start (an exception during startup), it says so in a message box and exits rather than lingering without a window.

## Where the state lives

`%APPDATA%\Task Focuser\state.json`: the text, colour, opacity, font, size, position, lock, mode and dot position. It is the same file the old Python app wrote, so your current note carries over. Delete it to start from the defaults. If the file is there but cannot be read at startup (locked, or in a cloud folder that is not up yet), the app copies it aside as `state.json.unread-<stamp>` before its first save, so your note is never overwritten by the defaults.

## Build

```
powershell -ExecutionPolicy Bypass -File app\build.ps1
```

Produces `app\dist\TaskFocuser.exe`, one framework-dependent file (about 0.4 MB). `-SelfContained` bundles the .NET runtime instead (about 65 MB) for a machine without it.

A release ships both as assets: `TaskFocuser.exe` is the self-contained build (about 65 MB, `build.ps1 -SelfContained`), the one to hand to anyone; `TaskFocuser-lite.exe` is the framework-dependent build (about 0.4 MB) for a machine with the .NET 9 or 10 Desktop Runtime. The build first regenerates `Assets\monster.ico` when it is missing or older than `Monster\Sprites.cs`; to do that by hand: `powershell -ExecutionPolicy Bypass -File app\tools\make-icon.ps1` (add `-PreviewPath x.png` to also save the 64 px picture).

## Folder map

```
app/
  TaskFocuser.csproj           net9.0-windows, WPF + WinForms (for NotifyIcon and Screen), single exe, rolls forward to .NET 10
  App.xaml                     the resources, StartupUri none (the conductor shows windows)
  App.xaml.cs                  entry point, the state machine, wiring everything
  Core/NoteState.cs            the persisted state, the Mode enum
  Core/Palette.cs              the five colours, the five fonts, limits, the darken helper
  Core/Contracts.cs            the interfaces every module implements or consumes
  Core/StateStore.cs           load/save/migrate, debounce, atomic write, the copy aside of an unread file, the profile's file
  Core/Monitors.cs             work areas and bounds in screen DIPs, clamping, corners
  Core/Win32.cs                the few P/Invokes: extended styles, cursor position
  Core/SingleInstance.cs       mutex + named pipe (both per session) to forward arguments, TASKFOCUSER_PROFILE
  Core/StartupRegistry.cs      the HKCU Run key toggle, Windows' own startup switch, the moved-exe repair
  Core/TrayIcon.cs             NotifyIcon wrapper with the tray menu
  Views/NoteWindow.xaml(.cs)   the note
  Views/SettingsBar.xaml(.cs)  the bar as a UserControl bound to NoteState
  Views/Theme.xaml             slider, button, swatch styles
  Views/DotWindow.xaml(.cs)    the dot
  Views/CloseTargetWindow.xaml(.cs)  the close target
  Monster/Sprites.cs           the frames as string grids plus the palette
  Monster/SpriteView.cs        draws one frame crisp at 3x
  Monster/MonsterWindow.xaml(.cs)    the monster's window, states and motions
  Assets/monster.ico           generated from the sprite by tools/make-icon.ps1
  tools/make-icon.ps1          draws the idle frame into 16/24/32/48/64 px layers, writes the .ico
  build.ps1                    dotnet publish, single file, framework-dependent (or -SelfContained), win-x64 -> dist/TaskFocuser.exe
  install.ps1                  build (or take -ExePath), stop the old app, copy to C:\Tools\TaskFocuser, Run key, Start-menu shortcut, launch
  README.md                    this file
```

The design and the reasons behind it: `docs/SPEC.md`.

## The bird (round 2, 2026-11-04)

The companion is a budgie. When the note is unlocked it sits on the bar; the second row of the bar is its settings:

| Control | Does |
|---|---|
| Arrow-up / nest (under the bird) | Bird leaves on lock / bird stays when locked. |
| Solid / faded bird | Bird always opaque / bird follows the note's transparency. |
| Speaker | The chirp every 5 minutes on or off (it keeps Bluetooth headphones awake). |
| Volume slider | Non-linear: the left half covers 0 to 10 %, the right half 10 to 100 %. Default 10 %. Drag it to hear a peep at that level. |
| Cucumber slice | Drag it onto the bird; it eats and is happy. Endless supply. |

- Double-click the bar's empty area to lock (the padlock still works).
- Drag the note by the bird, or by the bar. Pull fast and the bird loses grip, tumbles behind and flies back to catch the note.
- Click the bird: it hops. Three clicks in three seconds: it chirps at you, annoyed.
- The bird lands with a puff of dust, blinks, tilts its head and preens now and then. Nothing runs when it is hidden.
- Sounds are synthesized once into `%LOCALAPPDATA%\Task Focuser\sounds\` (keepalive, happy, annoyed, peep).

The spec for this round: `../docs/SPEC-round2.md`.

## Round 3 (2026-11-04, later)

- The bar is one row: cucumber, bird pinned, bird opacity, volume (click = sound on/off; hover = the volume slider plus the idle-sound choices `chirp / brrrp / both` and `often / mid / rarely`), opacity, colours, `- f +` (the `f` cycles the font, right-click goes back), plants on/off, lock.
- Idle sounds come at random intervals: often 1.5 to 3 min, mid 3 to 6, rarely 8 to 15. "brrrp" is the bird ruffling its feathers.
- The dot is flat, almost no glow.
- Pixel-art vines grow from the note's bottom-right corner along two edges; more leaves on a bigger note; they grow and fold as the note resizes; the leaf button hides them.
- Bug the bird five times and it flies off for a second; leave it alone for 2.5 s and it is happy again. A cucumber always helps.

## Round 4 (2026-11-04, evening)

- Bar order: cucumber, bird pinned, bird opacity, volume, colours, opacity, `- f +`, plants, lock.
- The vines straddle the note's edge, half outside like the bird.
- The bird pecks seeds off the ground now and then, and while you type it may start a chain: a 20 % chance per key, then one peck per character for 2 to 30 characters; 5 s of silence ends it.
- Resize: drag the bottom-right corner grip (unlocked only). The size you set is a floor: the note never shrinks below it but still grows with the text, no scrollbar. Click the grip once to go back to auto-size.

## Round 5 (2026-11-04, night)

- Two birds: Lennie the budgie and Meggie the owl (grey, with her own hoots; a grey note colour to match). The name under the bird on the bar is the picker: click it, pick a row, and one bird flies off while the other arrives. The pencil renames a bird (3 to 14 characters).
- The bird walks back and forth along the top edge now and then, about two of its widths and back.
- The lock/unlock shake is gentler.
- A size set with the corner grip applies only while unlocked; a locked note always compacts. The vines keep their full look either way.
- The dot remembers where you last dropped it; dragging the note or "Start the dot" brings it back there (as the same offset from the corner on the other monitor). Until you move it, it sits in the top-right corner.

## Round 6 (2026-11-05)

- A third companion: Rori, a white Japanese spitz (drawn as her mother Lily looks at two: the ruff, the plumed tail curled over her back, the fox-like smile). Pick her in the picker like the birds; it has three rows now, and she is renamed the same way.
- She never flies. She runs in along the note's top edge and skids to a stop with a puff of dust, runs off to the side when she leaves, and when you drag the note she chases her spot, facing the way you pull and trailing a little behind; held still, she stands and wags.
- Everything the birds do she does in her own way: the head tilt, the scratch behind the ear, the shake-off instead of the ruffle, sniffing for kibble while you type, the trot instead of the walk. Her own on top: the wag, the play bow, now and then zoomies to the end of the note and back, and a nap (a few z's, then a stretch) when nothing has touched her for three minutes.
- Her treat is a bone biscuit: the bar shows it in place of the cucumber while she is the companion. Pick it up and she sits up and begs; drop it on her and she eats it, happy; drop it anywhere else, or lock or hide the note mid-drag, and she sits back down. The treat follows whichever companion is picked: the birds keep the cucumber.
- If another window takes the mouse while you carry the treat (Alt-Tab, a popup), the treat falls where it is and counts as put away, never as a drop on her; before, a lost mouse counted as a release.
- Her own five sounds, all synthesized: a soft "wuf", "yip-yip", a small grumble, a questioning "hm?" and the "fwup-fwup" of the shake. The volume popup's idle-sound choices read `wuf / shake / both` while she is the companion.
- A seventh colour: white (`#FAFAF8`), with the usual slightly darker strip and bar.

The spec for this round: `../docs/SPEC-round6.md`.
