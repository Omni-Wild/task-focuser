# Task Focuser v2, round 3: one row, plants, a ruffle, a bird with moods

Written 2026-11-04 by the Conflux root from Jiří's message after an hour with round 2 ("omg it's so amazing"). Rounds 1 and 2 (`SPEC.md`, `SPEC-round2.md`) stay binding; this file adds to them and overrides where it says so.

## 1. His words, ordered

1. "The circle that shows which colour is selected is off-centre."
2. "Make font be just an /f/ icon, same behaviour."
3. "Move the cucumber closer to the bird. The final bar: Cucumber | Bird pinned or not | Bird opacity sync | volume button that brings the slider when I hover onto it | opacity slider | colour picker | font switch button with the +/- size icons around it, like `- f +` | and finally the lock button." Plus (item 7): "an option to disable the plants, between the font and the lock."
4. "For the idle sounds add a thing where the bird 'otřepalo se', wiggled its feathers, a satisfying brrrrrp sound. Then an option in the widget itself where I choose which background idle sounds I want: only chirp, only brrrrrp, or both, and a fuzzy often / mid / rarely option for how often they occur; ranges, not an exact time."
5. "Make the red dot a flat graphic, glow much less, almost no glow."
6. "Pixel-art green vines or plants growing on the bottom-right corner, sprawling a bit along the bottom edge towards the bottom-left and up the right edge towards the top-right, done spreading in the middle of both paths. A little decoration, semi-dynamic: the bigger the box, the more leaves; it auto-folds and expands as I type."
7. "When I keep bugging the bird and it screamed at me like 5 times in a short period, it should fly off for like a second and fly back to its place. If I didn't annoy it for 2-3 s and click again, it should be happy again, not bite me, unless I give it a cucumber."
8. "When the bird is unpinned it flies down to the exact middle of the locked box and then teleports to the proper spot on the left. We know how big the box will be when unlocked, so the bird should fly naturally to its spot."

## 2. Behaviour

### 2.1 The bar: one row again (items 1 to 3, 7)
- One row, 34 DIP, in this order from the left: the perch slot (40 DIP, empty; the bird sits on its top edge), the cucumber slice, the bird-pinned toggle (round 2's bird-stays: arrow-up = leaves on lock, nest = stays), the bird-opacity toggle, the volume button, the opacity slider, the five swatches, `-` `f` `+` (the `f` is a small italic serif f glyph that cycles the font on click and goes back on right-click; `-` and `+` step the size), the plants toggle (a leaf glyph; on = plants shown), the padlock. Nothing else; row 2 is gone.
- The swatch ring sits exactly centred on the chosen swatch (item 1: the ring was offset; both the ellipse and its ring share one centre and the ring is drawn as a stroke on the same ellipse or a sibling with identical bounds).
- The volume button: a speaker glyph (muted when `SoundOn` is false). Click toggles `SoundOn`. Hover for 250 ms opens a small popup under it (the note's colour, the strip's darker edge, 8 DIP padding) that stays open while the mouse is over the button or the popup and closes 300 ms after it leaves both; the popup holds: the non-linear volume slider (round 2's, with the peeps), under it two small choice rows of text toggles in the ink colour (the chosen one underlined and full ink, the others at 60 %): `chirp` `brrrp` `both` (`IdleSounds`) and `often` `mid` `rarely` (`IdleRate`). Every change writes the state and raises `Changed`.
- The unlocked minimum width stays under 400 DIP. Keep every round-2 behaviour of the controls (tooltips, the cucumber drag, double-click on empty bar locks).

### 2.2 The bird's spot is known in advance (item 8)
- `INoteWindow.PerchPoint` returns the perch of the *target* layout: while the bar unfolds or folds, the paper's final left edge is already known (the note computes the target width before animating), so the perch is reported where it will be, not where the animated edge is now. The conductor's arrival after unlock then flies straight to the final spot. The conductor calls `ArriveAt(note.PerchPoint)` right when `LockedChanged(false)` fires (no layout wait needed any more; keep the deferred call only as a fallback when the window is not yet shown).
- During the unfold the bird's `MoveTo` is not called with intermediate perches (the conductor only follows the perch on `Changed`, and `Changed` is not raised by the unfold animation). B8's `MoveTo` during an arrival retargets rather than snaps; both together remove the teleport.

### 2.3 The ruffle and the idle sounds (item 4)
- `ChirpKind.Ruffle`: the "brrrrp" of a bird shaking its feathers: about 320 ms, a soft low-mid tone (around 420 Hz with a slight downward glide) amplitude-modulated at about 32 Hz (the rolling "rrr"), a touch of noise-like second harmonic, 10 ms attack, 80 ms tail, peak about -8 dBFS. Satisfying, not buzzy.
- `IMonster.Ruffle()`: the bird fluffs up and shakes: `Ruffle1`/`Ruffle2` frames (feathers out, body a cell wider; wings slightly out) alternating at about 24 fps for 320 ms with a 1 DIP horizontal jitter, then a settle (one `IdleOpen`), and it raises `ChirpRequested(ChirpKind.Ruffle)` at the start. A no-op while any motion or act runs or while hidden.
- The idle sound scheduler (conductor) replaces round 2's fixed 5-minute keep-alive: when `SoundOn`, after a random interval from the `IdleRate` range it plays one idle sound chosen by `IdleSounds` (`Chirp` = the keep-alive chirp with a flap burst when the bird is out; `Ruffle` = the ruffle sound, with `monster.Ruffle()` when the bird is out, the sound alone when it is not; `Both` = a coin flip each time), then draws the next interval. Ranges: `Often` 1.5 to 3 min, `Mid` 3 to 6 min, `Rarely` 8 to 15 min. Default `Both`, `Mid`. Any other sound (a click peep, a slider peep, a happy chirp) re-draws the next interval so an idle sound never follows within 60 s of another sound. Runs in every mode while the app runs (the headphones reason stands).

### 2.4 The flat dot (item 5)
- The resting dot: a flat disc `#D32F2F`, a 1 DIP rim `#A81E1E`, a 2 DIP soft halo at 12 % opacity at most; the pulse becomes a slow, barely visible brightness breath (the disc's opacity 0.92 to 1.0 over 3 s) and keeps the 30 fps cap. Armed and hot keep their sizes (26 and 34 DIP) with the same flat look and a halo at 20 % at most. The close target is unchanged.

### 2.5 The plants (item 6)
- `Views/VinesView.cs`: a `FrameworkElement` that fills the paper's area (added by the bar builder as the lowest layer above the paper fill, under the text, `IsHitTestVisible="False"`). It draws procedural pixel-art vines in 3 DIP cells: one vine rooted at the bottom-right corner running left along the bottom edge, one running up along the right edge, each to the middle of its edge, with leaves (two shades of green `#3E7D3E` and `#5FA85F`, highlights `#8FD48F`, the occasional tiny pink bud `#FF9BC1` one cell) every 4 to 7 cells, alternating sides, with a little wobble. The pattern is deterministic (seeded by the paper size's rounded cells, so it does not shimmer on each repaint).
- Semi-dynamic: the number of leaves follows the edge length, so a bigger note has more. Growth animates: on any size change the visible extent of each vine eases from the previous extent to the new one over 320 ms (grows when the note grows, folds back when it shrinks); when `Enabled` flips off the vines fold back to the corner (260 ms) and when on they grow out. Cheap: one pre-built `DrawingGroup` per (size, extent step) is too much; instead draw directly in `OnRender` from the cell list and animate a single `Extent` dependency property (0 to 1) that clips how many cells are drawn; no timers while static.
- The vines stay off the text: the padding keeps the text 14 DIP from the edges and the vines use at most 2 cells (6 DIP) of depth along each edge plus leaves up to 4 cells; where the text box would overlap, the vines are simply under it (the text box has no background, so the vines show through between lines; that is the point).
- `state.PlantsOn` (default true) toggled from the bar.

### 2.6 The bird's mood (item 7)
- Clicks: a click is a happy hop with a `Peep` when the bird has been left alone for 2.5 s or more. Clicks closer than that count up; the third and every later click within the window is an annoyed chirp. Five annoyed chirps within 12 s: the bird flies off (`Leave`-like, upward, about 300 ms), stays away about 1 s, and flies back to the perch (`ArriveAt`), then its counter resets. `Eat()` (the cucumber) resets the counter at once and makes it happy. The counter also resets after 2.5 s without a click.

## 3. Contracts (the code form is `Core/Contracts.cs`, `Core/NoteState.cs`, `Core/Sound.cs`)

- `NoteState` new: `IdleSounds IdleSounds = IdleSounds.Both`, `IdleRate IdleRate = IdleRate.Mid`, `bool PlantsOn = true`; enums `IdleSounds { Chirp, Ruffle, Both }`, `IdleRate { Often, Mid, Rarely }`. `BirdStays` keeps its name (the "pinned" toggle).
- `ChirpKind` gains `Ruffle`.
- `IMonster` gains `void Ruffle()`.
- `INoteWindow.PerchPoint` semantics change as in 2.2 (no signature change).
- `VinesView` (new, `TaskFocuser.Views`): `public bool Enabled { get; set; }` (a DP), draws from its own `ActualWidth`/`ActualHeight`; the bar builder hosts it.

## 4. Ownership (round 3)

```
Views/SettingsBar.xaml(.cs), Views/Theme.xaml, Views/NoteWindow*.cs, Views/NoteWindow.xaml, Views/CucumberWindow*   C1 bar      2.1, 2.2 (PerchPoint), hosting VinesView, the plants toggle, the volume popup
Views/VinesView.cs (+ Views/VinesPattern.cs if needed)                                                             C2 plants   2.5
Monster/Sprites.cs (Ruffle1, Ruffle2 only), Monster/MonsterWindow.xaml(.cs), Monster/Grip.cs, Monster/Effects.cs   C3 bird     2.3 Ruffle(), 2.6 the mood
Core/Sound.cs, Conductor.cs, Conductor.Round2.cs, Views/DotWindow.xaml(.cs)                                        C4 sound+conductor+dot   2.3 the sound and the scheduler, 2.2 the arrival call, 2.4
Core/Contracts.cs, Core/NoteState.cs, Core/StateStore.cs                                                           root (done) the contract additions, persistence, compile stubs
```

Rules as before: scratch-copy builds, own files only, no NuGet, cancellable animations, no running the app and no synthetic input on his machine (he tests it himself), under about 400 lines per file.
