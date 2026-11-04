# Task Focuser v2, round 4: the slider moves, the vines overflow, the bird pecks, the corner resizes

Written 2026-11-04 by the Conflux root from Jiří's message after round 3. Rounds 1 to 3 stay binding; this file adds to them and overrides where it says so.

## 1. His words

1. "Move the transparency slider after the colour picker, so it is not confused with the volume icon."
2. "Move the greenery outside the box, or sit halfway on the box and off the box, so it overflows like the bird does into the space around it."
3. "Add an animation quirk that randomly eats a seed off the ground."
4. "When someone is typing into the box the bird has a 20 % chance on each key press to start a chain where it then eats a seed on every character typed, for 2 to 30 characters chosen randomly; this resets after 5 s of inactivity."
5. "Add an expand feature to the bottom-right corner: drag-and-drop change-size, classic behaviour. When it is expanded or changed, so it does not follow the auto size, it can be sent back to auto sizing by clicking the drag-and-drop corner once."

## 2. Behaviour

### 2.1 The bar order (item 1)
Perch slot, cucumber, bird pinned, bird opacity, volume, the five swatches, the opacity slider, `- f +`, plants, lock. Nothing else changes.

### 2.2 The vines overflow (item 2)
- The stem runs *on* the paper's edge line (the bottom edge and the right edge), leaves alternating inside and outside the paper, so about half the greenery hangs outside into the transparent stage, like the bird on the top edge. The sprout at the corner sits on the corner.
- `VinesView` gets `EdgeInset` (a DP, DIPs): the distance from the view's own bottom and right edges to the edge line the stem follows. The note hosts the view stretched over the paper plus an overhang of 15 DIP on the right and the bottom (a negative margin of -15 on those two sides, or an explicit size) and sets `EdgeInset = 15`, so the stem lands on the paper's edge. The view clips nothing; the window's stage must have at least 15 DIP of transparent room right of and below the paper (the shadow margin has 12 today: grow it to 16 where needed, keeping `Bounds`, `PerchPoint` and the drag maths on the visible paper).
- Growth, folding, the toggle and the seed stay as in round 3.

### 2.3 Pecking (items 3 and 4)
- Frames `Peck1` (head down toward the perch line, tail up a cell) and `Peck2` (head down, beak at the ground, a `Seed` glyph of two brown cells `#8A5A2B` at the beak) in `Sprites.cs`; a `Seed` glyph array too.
- `IMonster.Peck()`: one peck of about 160 ms (`Peck1` 60 ms, `Peck2` 60 ms with the seed, `IdleOpen` 40 ms), the seed vanishing with the last frame. Calls faster than that coalesce: a `Peck()` during a peck queues at most one more, so a burst of typing reads as continuous pecking without a backlog. A no-op while any motion, carrying, tumbling, eating or the fly-off runs, or while hidden; allowed during a plain idle fidget (it cancels it).
- The idle quirk: one of the idle fidgets (one in four) is "eats a seed off the ground": a seed appears a few cells in front of the feet, the bird does two pecks, the seed is gone, a tiny happy blink. No sound.
- The typing chain (conductor): `INoteWindow.KeyTyped` is raised once per character inserted or deleted while unlocked (text changes from the keyboard; not for shortcuts). On each `KeyTyped` outside a chain: with 20 % probability start a chain of `N` characters, `N` uniform in 2 to 30. In a chain: every `KeyTyped` calls `monster.Peck()` and counts down; at zero the chain ends. Five seconds without a `KeyTyped` ends a chain early (a timer re-armed on each key). The chain runs only while the bird is out and the note is unlocked; otherwise keys are ignored.

### 2.4 The resize corner (item 5)
- A grip at the paper's bottom-right corner, visible only when unlocked: three short diagonal lines in the ink colour at 45 %, 12 by 12 DIP, the cursor `SizeNWSE` over it. It sits above the vines and the text.
- Drag it: classic resizing, the bottom-right corner follows the cursor, the top-left stays; width and height clamp to the text's natural minimum (so the text is never cut) and to the work area. On release the note is in *manual size*: `State.ManualWidth`/`ManualHeight` (DIPs of the visible paper) are set and the auto-size treats them as the minimum size: the note stays at least that big, and still grows when the text needs more (no scrollbar, ever). Growth keeps the top-right anchor as before.
- Click the grip (press and release with less than 3 DIP of movement): back to auto-size: `ManualWidth`/`ManualHeight` become NaN and the note snaps (animated, 220 ms, ease out) to its natural size, right edge fixed.
- The manual size survives lock/unlock and restarts (persisted), and the vines and the perch follow the paper as always. A manual size smaller than the bar's width is impossible while unlocked (the bar's minimum still applies).

## 3. Contracts

- `NoteState` new: `double ManualWidth = double.NaN`, `double ManualHeight = double.NaN` (persisted as `manualWidth`/`manualHeight`, absent or null when NaN).
- `INoteWindow` gains `event Action? KeyTyped`.
- `IMonster` gains `void Peck()`.
- `VinesView` gains `double EdgeInset` (a DP, default 0 = the stem hugs the view's own edge as in round 3).

## 4. Ownership (round 4)

```
Views/SettingsBar.xaml(.cs), Views/Theme.xaml, Views/NoteWindow.xaml, Views/NoteWindow*.cs   D1 bar+note   2.1, the hosting of 2.2, 2.4 the grip and manual size, KeyTyped
Views/VinesView.cs, Views/VinesPattern.cs                                                     D2 plants     2.2 EdgeInset and the inside/outside leaves
Monster/Sprites.cs (Peck1, Peck2, Seed only), Monster/MonsterWindow*.cs, Monster/Effects.cs, Monster/Grip.cs   D3 bird   2.3 Peck() and the seed fidget
Conductor*.cs                                                                                  D4 conductor  2.3 the typing chain
Core/Contracts.cs, Core/NoteState.cs, Core/StateStore.cs, the compile stubs                    root (done)
```

Rules as before: scratch-copy builds, own files only, no NuGet, cancellable animations, no running the app and no synthetic input on his machine, under about 420 lines per file.

## 5. Amendment 2026-11-04 evening (the vines, his words after seeing round 4)

- "The vine should grow more to the end of the edge: more to the left on the bottom and more up on the right edge." Each vine now reaches about 85 % of its edge (was half).
- "The corner should not have an outward leaf pointing diagonally." The corner knot has no outward diagonal leaf; it is a small inward sprout and the two stems meeting.
- "Make the ending of the vine on each side more progressive rather than abrupt." The last stretch tapers: leaves shrink toward the tip, the spacing opens up, the stem ends in a thin curl or a single small leaf.
- "Move the vine even more to the outside, like it is wrapping the box and just reaching into it a bit." The stem runs one cell outside the edge line; outside leaves reach up to 3 cells beyond it (4 from the line, within the 15 DIP overhang); inside leaves reach at most 2 cells into the paper and are the smaller ones.
