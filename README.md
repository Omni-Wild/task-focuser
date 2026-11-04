# Task Focuser

A post-it note taped to your monitor. One always-on-top sticky with the one to three lines you are focusing on right now, a lock so you cannot type into it by accident, a red dot in the corner to tuck it away, and a small pixel bird that keeps you company and keeps your Bluetooth headphones awake.

Windows 10/11, one `.exe`, no installer, no account, no network. MIT licensed.

<p align="center"><img src="docs/demo.gif" width="360" alt="Task Focuser in action: the dot, the note, the bird, the owl"></p>

## Download and run

1. Go to the [latest release](../../releases/latest) and download **`TaskFocuser.exe`** (about 70 MB, everything included).
   If you already have the .NET 9 or 10 Desktop Runtime, `TaskFocuser-lite.exe` (under 1 MB) does the same.
2. Put it in a folder you will keep (for example `Documents\TaskFocuser`), not in `Downloads`, because "Start with Windows" remembers where the exe is.
3. Double-click it. The exe is not code-signed, so Windows SmartScreen says "Windows protected your PC" the first time: click **More info**, then **Run anyway**. That happens once. Your browser may also ask whether to keep the download; keep it. If your PC has Smart App Control turned on, it blocks unsigned apps outright and you would have to turn that off in Windows Security.
4. The note appears. Type into it. Everything is saved as you type.

To have it start with Windows: right-click the bird icon in the tray (under the `^` arrow at the bottom right of the taskbar) and tick **Start with Windows**. If you later move the exe, the app repoints the autostart to the new place the next time you run it.

## How to use it

- **Locked** (the resting state): the text cannot be changed and the note never takes the keyboard away from the app you are working in. Drag it anywhere. **Double-click it to unlock.**
- **Unlocked**: the settings bar appears on top and the bird lands on it. Type freely, Enter makes a new line. Lock it again with the padlock, `Esc` or `Ctrl+L`, or double-click an empty spot of the bar.
- **The bar**, left to right: the cucumber (drag it onto the bird), bird stays when locked, bird opacity, sound (click = on/off, hover = volume and how often the bird makes idle sounds), the colours, the opacity slider, `- f +` (font size, `f` cycles the font), the plants, the padlock. Under the bird: its name, which opens the bird picker.
- **Font size**: `Ctrl+=` / `Ctrl+-` (11 to 28).
- **Size**: the note grows to the left and down as you type. Drag the bottom-right corner to set a minimum size; click the corner once to go back to auto-size.
- **The dot**: start dragging the note and a red dot appears in the top-right corner of the monitor. Drop the note on it and the note tucks into the dot. Double-click the dot to pop the note back out. The dot remembers where you last left it.
- **Closing**: drag the dot and a dark X appears in the bottom-right corner. Drop the dot on it and everything is hidden; only the tray icon stays. Left-click the tray icon to get the note back.
- **The tray menu**: Show note, Tuck into the dot, Start the dot, Lock/Unlock, Start with Windows, Quit.

## The bird

Two birds live in the app: **Lennie** the budgie and **Meggie** the owl. Click the name under the bird to switch, or rename them. The bird sits on the bar while you edit, flies off when you lock (or stays, if you tell it to), carries the note when you drag it, loses its grip if you yank it, and now and then blinks, preens, walks along the edge or pecks seeds. Click it and it hops; bug it five times and it flies off for a second.

**The sound.** Every five minutes the bird makes a quiet chirp. That keeps Bluetooth headphones from going to sleep during long silent stretches, which is why the feature exists. Hover over the speaker on the bar to set the volume (the default is 10 %) and whether the idle sounds are a chirp, a feather ruffle or both, and how often. Click the speaker to turn all sound off. Sound needs Windows Media Player, which Windows includes; on a Windows "N" edition install the Media Feature Pack, otherwise the bird is simply silent.

## Where your note lives

`%APPDATA%\Task Focuser\state.json` holds the text, position, colour, opacity, font, lock state and the bird settings. Delete the file to start from scratch. The generated sounds are cached in `%LOCALAPPDATA%\Task Focuser\sounds`.

## Command line

Only one Task Focuser runs. Starting the exe again with a switch sends it to the running one: `--show`, `--lock`, `--unlock`, `--dot`, `--close`, `--start`, `--quit`. Example: `TaskFocuser.exe --dot`.

## Build it yourself

You need the .NET 9 SDK.

```
powershell -ExecutionPolicy Bypass -File app\build.ps1 -SelfContained
```

That produces `app\dist\TaskFocuser.exe`. Without `-SelfContained` you get the small framework-dependent exe. `app\install.ps1` builds, copies the exe to `C:\Tools\TaskFocuser`, registers the autostart and a Start-menu shortcut, and launches it; with `-ExePath <downloaded exe>` it does the same for a release exe without building. The source is in `app/` with a folder map in `app/README.md`.

## How it was made

The app was built in one day, 2026-11-04, in five rounds by crews of Claude agents working from the specs in `docs/` (`SPEC.md` for the base, `SPEC-round2.md` to `SPEC-round5.md` for the bird and everything after), with the reasons behind each design call in `context/decisions.md`. It replaced a small Python sticky note (tag `v1.0.0` in this repo) and reads that app's state file unchanged.

## License

MIT, see `LICENSE`.
