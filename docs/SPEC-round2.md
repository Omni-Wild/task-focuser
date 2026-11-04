# Task Focuser v2, round 2: the bird gets a life

Written 2026-11-04 by the Conflux root from Jiří's voice dump after an hour with round 1 ("I'm giggling here like a child"). Round 1 (`SPEC.md`) stays binding; this file adds to it and overrides where it says so. The monster is now a bird: a parakeet like his own Lenny, who sits on his shoulder, flaps to keep balance when he moves slowly, falls off when he moves fast and flies back.

## 1. His words, ordered

1. "When I unlock and the box expands, the bird clips and teleports to the left side. It should be more fluid: he should take off, flap his wings and get to the proper position quickly. The quickness of animations is just perfect. There can also be a tiny little shake when I go into the edit mode, in and out of it."
2. "In edit mode I expect to double-tap the top bar and it going into lock mode. Keep the icon for the lock."
3. "The notification ball a bit darker red, to better match the classic notification balls."
4. "The bird has four legs. Make it have only two legs because birds have two legs, and then their beak, and their wings are their arms."
5. "My headphones get disconnected when I think or read for a long time, and when I'm dictating. Add an occasional chirp and flap of the wing while it's running, every five minutes, very low volume by default, 10 %. An option to turn the sound on or off and a volume slider that is not linear: the bottom 10 % of the value takes 50 % of the horizontal space so I can fine-tune how quiet it is. A warm, not annoying sound, like a video game sound effect, not screechy. Just a little tiny chirp here and there."
6. "An option to always show the bird, even when it's locked, as an arrow-up on the place where the bird is sitting, so I can choose on the fly."
7. "An option to have the bird either match the transparency of the note or be always fully opaque."
8. "When I move it slowly the bird keeps flapping as now. When I pull it faster the bird loses grip, flies away from it a little and needs to catch the box again. Lenny does the same on my shoulder: when I move rapidly she falls off and flies towards it again. Any direction."
9. "A slice of cucumber in the top bar options that I can drag and drop onto the bird, and it shows a happy bird animation, like it ate it, a couple of hearts or something."
10. "Eye candy: when the bird lands going into edit mode, a bit of dusty, feather-like particles fly off, like the air flow of a landing bird blowing off whatever sat on the note; it can scale with time. Other small semi-random interactions: I click the bird and it moves a little; I click it more and it chirps at me, annoyed. Think of other little eye candy."

Not asked, not built: no sound for the dot, no bird in Dot mode, no cucumber persistence, no settings window.

## 2. Behaviour

### 2.1 Flight instead of teleport (1)
- The conductor no longer snaps the perched bird to a new perch when the note resizes (unlock, lock, typing). When the perch moves by more than 12 DIP while the bird is perched and no drag is running, it calls `monster.FlyTo(perch)`: a small hop up, the flap loop, a glide to the new perch (about 260 ms, `QuadraticEase` in-out), a landing with a pinch of dust (2.10), then idle. During a drag the instant `MoveTo` stays (the bird is holding the note).
- `ArriveAt` keeps its fly-in; if the perch moves while the bird is still arriving, it retargets (already so).
- The shake: on lock and on unlock the note root does a tiny horizontal shake, 2 DIP amplitude, three cycles, 180 ms, `INoteWindow.Shake()`; the note calls it itself inside `SetLocked`. Nothing else shakes.

### 2.2 Double-click the bar locks (2)
- In edit mode a double-click on the bar's empty areas (either row) locks the note. The padlock button stays. A press on a control is never a lock. A double-click on the text keeps its text meaning (word selection).

### 2.3 The dot's red (3)
- Body `#D32F2F`, rim `#A81E1E` (1 DIP), glow `#FF5A5A`; hot keeps the brighter glow. Everything else about the dot stays.

### 2.4 The bird (4)
- Two legs. A small beak (orange `#F2A33A`, two cells, pointing right). Wings are the arms. Eyes, cheeks and the body as before, blue. Every frame redrawn: `IdleOpen`, `IdleBlink`, `FlapUp`, `FlapMid`, `FlapDown`, `ShoveA`, `ShoveB`, plus new frames `Tumble1`, `Tumble2` (flailing, wings spread wide, feet in the air), `Eat1`, `Eat2` (beak open, beak closed on the slice), `Happy` (eyes as happy arcs, a little lean), `Chirp` (beak open, chest out), `TiltL`, `TiltR` (head tilted), `Preen` (head turned into a wing), `Hop` (legs tucked). The icon regenerates from `IdleOpen` (`build.ps1` does it when `Sprites.cs` is newer).
- Small glyphs in the same file for the effects: `Heart` (5 by 4, pink `#FF6FA3`), `Dust` (2 by 2, `#D9D2C2` at 70 %), `Cucumber` (a ring: dark green rim `#3E7D3E`, pale `#DDF3C9` flesh, three seed cells `#B9D98C`; 7 by 7), parsed by the same regex shapes as the frames (`public static readonly string[] Name = { "...", };`).

### 2.5 The keep-alive chirp and the sound (5)
- `Core/Sound.cs`: three synthesized sounds, rendered once into `%LOCALAPPDATA%\Task Focuser\sounds\<name>-v1.wav` (44.1 kHz, 16-bit mono) and played through WPF `MediaPlayer` at the given volume. Warm and round, nothing screechy: sine with a touch of second harmonic, soft attack (8 ms) and decay (60 to 120 ms), a tiny vibrato, no clicks (zero-crossing fades). `Keepalive`: two short notes, a budgie "pip-pip" around 1.6 and 2.0 kHz, 70 ms each, 60 ms apart, about 250 ms total. `Happy`: three rising notes. `Annoyed`: one lower, rougher note (a triangle wave with a fast tremolo, 180 ms) that reads as a grumble, still quiet. `Peep`: one very short soft note for a click.
- The keep-alive: while the app runs, in every mode, a `DispatcherTimer` every `ChirpMinutes` (5, a constant) plays `Keepalive` at `state.Volume` when `state.SoundOn`; if the bird is out it also does a short flap burst (`monster.Flap()`). The purpose is the Bluetooth headphones staying awake, so it runs even in Dot and Closed modes; it never runs twice within a minute (a re-arm after any other sound resets the clock).
- Volume: `state.Volume` is 0 to 1, default 0.10. The slider is non-linear: the left half of the track covers 0 to 0.10, the right half 0.10 to 1.0, piecewise linear: position `p` in [0,1] maps to `v = p <= 0.5 ? 0.2 * p : 0.1 + 1.8 * (p - 0.5)` and back. Dragging the slider plays a `Peep` at the new volume (throttled to one per 150 ms) so he hears what he sets.
- `state.SoundOn` toggles everything audible; default true.

### 2.6 The bird stays (6)
- `state.BirdStays` (default false). A toggle at the perch slot of the bar (row 2, under where the bird sits): an arrow-up glyph when off ("the bird leaves on lock"), a small nest or pin glyph when on ("the bird stays"). When on: locking keeps the bird perched (no `Leave`), showing a locked note arrives the bird too, a locked drag end keeps it perched, Dot and Closed modes still send it away. Flipping it to off while locked sends the bird away; flipping it on while locked calls it in.

### 2.7 The bird's opacity (7)
- `state.BirdOpaque` (default true): the bird is fully opaque; false: the bird's window `Opacity` follows the note's (`state.Opacity / 100`). A toggle on row 2 (a glyph of a bird solid vs faded). `monster.SetOpacity(double)` is applied by the conductor on every opacity change and on arrival.

### 2.8 Grip physics (8)
- While carrying (`SetCarrying(true)`), the bird tracks the perch's velocity from the `MoveTo` calls (DIP per second, smoothed over about 60 ms). Below 900 DIP/s it rides the note as now. Above 1400 DIP/s it loses grip: `Tumble` frames, the bird window detaches and lags (its position follows a damped spring toward the perch with a low stiffness and gravity-ish sag of 40 DIP, so it visibly falls behind and dips), until the perch's speed stays under 600 DIP/s for 120 ms or the drag ends; then it flies back: the flap loop, a glide to the perch (about 220 ms), a landing with a pinch of dust, and it carries again. Any direction. The motion runs on `CompositionTarget.Rendering` only while carrying, with no allocations per frame.
- On drag end while tumbling: it flies back to the perch (locked note and `BirdStays` off: it then leaves as before).

### 2.9 The cucumber (9)
- A cucumber slice glyph at the right end of row 2. Press and drag it: a small click-through, non-activating window (`Views/CucumberWindow`, owned by the note) shows the slice under the cursor; release raises `INoteWindow.CucumberDropped(Point cursorScreenDip)`; the conductor checks `monster.IsOut && monster.Contains(p)` and calls `monster.Eat()`; otherwise the slice falls with a quick drop-and-fade (200 ms). The bar's glyph never disappears (an endless supply).
- `Eat()`: `Eat1`/`Eat2` chomping three times (about 500 ms), then `Happy` for 600 ms with three pixel hearts rising from the bird and fading (900 ms), a `Happy` chirp request, then idle.

### 2.10 Eye candy (10)
- Landing dust: on every landing (`ArriveAt`, `FlyTo`, the grip catch) a few `Dust` particles burst sideways from the feet and fade (300 ms). The amount scales with how long the bird was away: 3 particles under a minute, up to 10 after an hour (linear in log time, capped). No particles at all when a landing follows another within 2 s.
- Clicks: the bird is clickable (its window is no longer click-through; it stays non-activating; transparent pixels still pass clicks through). A click with no movement: a small hop and a `Peep`. Three or more clicks within 3 s: `Chirp` frame, a shake of the bird and an `Annoyed` chirp request; the counter resets after 3 s of peace. A press that moves more than 3 DIP drags the note by the bird: the bird raises `DragStarted/DragMoved/DragEnded(Point)` and the conductor moves the note with `note.NudgeBy(delta)` and treats it exactly like a note drag (dot arming, carrying, docking).
- Idle variations while perched (single-shot timers, 20 to 60 s apart, never while a motion runs): a head tilt left or right (600 ms), a preen (800 ms), a tiny hop in place, a glance (blink twice). Cheap: one timer, one frame swap.
- Sounds come from the conductor only: the bird raises `ChirpRequested(ChirpKind)` and the conductor plays it at `state.Volume` when `state.SoundOn`.

### 2.11 The bar (rows)
- Two rows of 30 DIP, 62 DIP in all with the divider. Row 1 as before: perch slot (40 DIP, empty), opacity slider, five swatches, font, `A-`, `A+`, padlock. Row 2: the bird-stays toggle under the perch slot, the bird-opacity toggle, the sound on/off toggle, the volume slider (the same width as the opacity slider), a spacer, the cucumber slice at the right end. Glyphs are small vector `Path`s in the ink colour at 70 % (full when on); tooltips name each ("Bird stays when locked", "Bird always opaque", "Chirp every 5 minutes", "Chirp volume", "Drag onto the bird").
- The unlocked minimum width grows to the wider row (about 354 DIP stays if row 2 fits in it; keep it under 380).

## 3. Contracts (the code form is `Core/Contracts.cs`)

- `NoteState` new fields: `bool BirdStays = false`, `bool BirdOpaque = true`, `bool SoundOn = true`, `double Volume = 0.10` (0 to 1). `Normalize` clamps `Volume`.
- `INoteWindow` additions: `void Shake()`, `void NudgeBy(Vector delta)` (moves the visible note by delta, updates `State.X/Y`, raises nothing: the conductor is the caller), `event Action<Point>? CucumberDropped`.
- `IMonster` additions: `void FlyTo(Point perch)`, `void SetOpacity(double opacity)`, `void Eat()`, `void Flap()` (a 700 ms flap burst while perched, then idle), `bool Contains(Point screenDip)` (the sprite's bounds, generous by 6 DIP), `event Action? Clicked` (informational; the hop and the annoyed count are the bird's own), `event Action<ChirpKind>? ChirpRequested`, `event Action<Point>? DragStarted/DragMoved/DragEnded` (cursor in screen DIPs, the 3 DIP threshold, capture, release or lost capture).
- `enum ChirpKind { Keepalive, Happy, Annoyed, Peep }` and `static class Sound { void Play(ChirpKind kind, double volume); void Warm(); }` in `Core/Sound.cs` (`Warm` renders the files if missing, called once at startup on a background thread).
- `IDot`: unchanged.

## 4. Ownership (round 2)

```
Monster/Sprites.cs                         B7 bird art        the redraw of 2.4 plus the glyphs; offscreen PNG renders for self-review, no window shown
Monster/MonsterWindow.xaml(.cs)            B8 bird behaviour  2.1 FlyTo, 2.8 grip physics, 2.9 Eat, 2.10 dust, clicks, idle variations, Flap, SetOpacity, Contains, the drag events; may add Monster/Effects.cs (hearts, dust) and Monster/Grip.cs
Core/Sound.cs                              B9 sound           2.5 synthesis, files, MediaPlayer, the volume map helpers (VolumeToSlider, SliderToVolume)
Views/SettingsBar.xaml(.cs), Views/Theme.xaml, Views/NoteWindow*.cs, Views/CucumberWindow.xaml(.cs)   B10 note bar   2.2, 2.6/2.7 toggles, 2.5 sound toggle and slider, 2.9 the slice and its drag window, 2.1 Shake, NudgeBy, CucumberDropped
Conductor.cs, Views/DotWindow.xaml(.cs)    B11 conductor      2.1 FlyTo calls, 2.3 the red, 2.5 the keep-alive timer and Sound.Warm, 2.6 BirdStays logic, 2.7 SetOpacity, 2.9 the drop, 2.10 ChirpRequested and the bird drag as a note drag
Core/Contracts.cs, Core/NoteState.cs       root (done)        the contract additions, with compile stubs in the owned files
```

Rules as in round 1 (scratch-copy builds, own files only, no NuGet, cancellable animations, under about 400 lines per file), plus one from Jiří: **no running the app and no synthetic input on his machine during this round**; compile-check only, and the sprite artist renders offscreen. He starts the exe himself.

## 5. Out of scope, next round
- The native memory growth per show/hide cycle (round 1 known gap) and the idle render-thread cost: a performance round when he is away from the machine.
