# Task Focuser v2, round 5: Lennie and Meggie

Written 2026-11-04 by the Conflux root from Jiří's message after round 4. Rounds 1 to 4 stay binding; this file adds to them and overrides where it says so.

## 1. His words

1. "Make the lock/unlock shake a bit less dramatic."
2. "Add an animation of the bird walking back and forth randomly from time to time, by about 2x of its width in walked length, and then going back."
3. "Create a bird picker. I want to add more birds. Start with an owl, add an owl sound for it and all the owl assets so it works the same way as the original bird. Call the original bird Lennie, call the owl Meggie, and add a grey theme to go with it. Add the bird's name under the bird in the menu (there is a space) and the name itself becomes the picker. In bird-picking mode the transition should be seamless: one bird leaving and the other arriving. There should be a pencil to rename the birds, with a minimum of 3 characters."

## 2. Behaviour

### 2.1 The shake (item 1)
Amplitude 1 DIP (was 2), two cycles, 140 ms (was three in 180). Same curve, same trigger (lock and unlock).

### 2.2 The walk (item 2)
- Frames `Walk1`, `Walk2` per bird (legs alternating, a one-cell bob; facing right, `FlipX` for left).
- An idle fidget (one in five, taking its share from the five old variations): the bird turns to face a random side, walks along the note's top edge about two sprite widths (132 DIP) at about 60 DIP/s with the walk frames at 8 fps, pauses 300 to 800 ms, turns, walks back to the perch, faces right again. The walk is clamped to the perch span the conductor gives it (`SetPerchSpan(left, right)`: the x range the feet may occupy on the note's top edge; the perch is inside it); when there is no room on the chosen side it walks the other way, and when there is room for less than one width it skips the walk. Any motion request (`MoveTo`, `FlyTo`, `ArriveAt`, `Leave`, carrying, a click, a peck, `Ruffle`, `Eat`, `SwitchTo`) cancels the walk: the bird snaps back to its frame at the perch (`MoveTo` and `FlyTo` take the new perch; the others act from where it stands and it returns to the perch on their end). The conductor keeps the span current on every perch change (resize, lock, drag).

### 2.3 Two birds (item 3)
- A bird is a `BirdSkin` (`Monster/BirdSkin.cs`): an id, a default name, a legend and the 23 frames (`IdleOpen`, `IdleBlink`, `FlapUp`, `FlapMid`, `FlapDown`, `ShoveA`, `ShoveB`, `Tumble1`, `Tumble2`, `Eat1`, `Eat2`, `Happy`, `Chirp`, `TiltL`, `TiltR`, `Preen`, `Hop`, `Ruffle1`, `Ruffle2`, `Peck1`, `Peck2`, `Walk1`, `Walk2`). `Birds` (`Monster/Birds.cs`) lists them: `Lennie` (the budgie: the frames in `Sprites.cs`) and `Meggie` (the owl: `Monster/OwlSprites.cs`, the same field names). Glyphs (heart, dust, cucumber, seed) stay shared in `Sprites.cs`.
- Meggie, the owl: grey theme. Body `#9A9AA6`, outline `#4A4A58`, a lighter face disc and belly `#D6D6DE`, wings `#B8B8C4`, big round yellow eyes `#F5C542` with black pupils, a small dark-orange hooked beak `#D98A2B`, two ear tufts, stubby legs `#D98A2B`. Same grid (22x16), feet on row 15, facing right, the same frame semantics (so every behaviour works unchanged). The owl's legend uses keys that do not collide with Lennie's (uppercase letters), since the sprite view merges both legends.
- The grey note colour: a sixth swatch `Grey #E3E1DC` in the palette (the bar shows six).
- Sounds per bird: `Sound.Play(kind, volume, birdId)`, files `<birdId>-<kind>-v1.wav`. Lennie keeps the round-2/3 sounds (re-rendered under the new names). Meggie: `Keepalive` = a soft "hoo-hoo", two round low notes (about 390 then 330 Hz, 140 ms each, 90 ms apart, a sine with a breathy second harmonic and a slow attack of 25 ms); `Happy` = three hoots rising a little; `Annoyed` = a short rough screech-grumble around 550 Hz with fast tremolo, still quiet; `Peep` = one short soft "hu" (330 Hz, 70 ms); `Ruffle` = the round-3 ruffle an octave lower (about 210 Hz, 360 ms). The ranges and the volume rules are unchanged.
- The picker: the bird's name under the bird, in the perch slot of the bar (small text, 10 to 11 px, ink at 75 %, centred in the slot's lower half; the slot widens to 56 DIP so a 12-character name fits with ellipsis beyond). Click the name: a popup under the slot (like the volume popup, but click-toggled, closing on a click elsewhere, on lock and when the note hides) with one row per bird: a portrait (the skin's `IdleOpen` drawn at 1 DIP per cell, 22x16), the name (bold for the active bird), and a pencil glyph. Click a row (not the pencil) to switch: the popup closes, the conductor swaps birds (2.4). Click the pencil: the name becomes a text box in place, pre-filled, 3 to 14 characters, letters, digits, spaces and the common accents; Enter commits, Esc cancels, a blur commits when valid; under 3 characters the box gets a red-ish border and does not commit. The committed name writes `state.BirdNames[id]` and raises `Changed`; the slot shows the new name.
- `state.Bird` (the active id, default `lennie`), `state.BirdNames` (id to name; absent = the default name). Persisted.

### 2.4 The switch (item 3, "seamless")
- `IMonster.SwitchTo(BirdSkin skin, Point perch)`: when the bird is out: the current bird leaves (the round-1 leave, upward and fading, about 300 ms); once hidden the skin swaps; the new bird arrives at `perch` (the round-1 arrival from above with dust), 150 ms after the old one is gone. When the bird is not out (locked, unpinned, Dot or Closed mode): the skin swaps silently and the next arrival is the new bird. `IsOut` stays true throughout a visible switch (the conductor treats it as busy for drags: a drag that starts mid-switch is refused until the new bird has landed).
- The conductor: on `Changed`, when `state.Bird` differs from the active skin, `monster.SwitchTo(Birds.ById(state.Bird), note.PerchPoint)`; at startup the skin is set before the first arrival. Sounds use `state.Bird`.

## 3. Contracts

- `Monster/BirdSkin.cs` (root): the class above. `Monster/Birds.cs` (root): `Lennie`, `Meggie`, `All`, `ById(string)` (unknown id = Lennie), `MergedLegend` (both legends plus the glyph colours, for the sprite view).
- `IMonster` gains `void SwitchTo(BirdSkin skin, Point perch)` and `void SetPerchSpan(double left, double right)`.
- `Sound.Play(ChirpKind kind, double volume, string bird)`; the two-argument form means Lennie.
- `NoteState` gains `string Bird = "lennie"`, `Dictionary<string, string> BirdNames` (persisted as `bird` and `birdNames`).
- `Palette.Colors` gains `Grey #E3E1DC`.

## 4. Ownership (round 5)

```
Monster/OwlSprites.cs (every frame), Monster/Sprites.cs (Walk1, Walk2 only; delete Sprites.Walk.cs)   E1 owl art
Monster/MonsterWindow*.cs, Monster/Effects.cs, Monster/Grip.cs, Monster/SpriteView.cs                E2 bird        the skin refactor, SwitchTo, SetPerchSpan, the walk
Views/SettingsBar.xaml(.cs), Views/Theme.xaml, Views/NoteWindow*.cs, Views/BirdPicker.xaml(.cs)        E3 bar         the name, the picker, the rename, the six swatches, the softer shake
Core/Sound.cs, Conductor*.cs                                                                           E4 sound+conductor   the owl sounds, per-bird files, the switch, the span, the sounds by bird
Core/Contracts.cs, Core/NoteState.cs, Core/StateStore.cs, Core/Palette.cs, Monster/BirdSkin.cs, Monster/Birds.cs, the stubs   root (done)
```

Rules as before: scratch-copy builds, own files only, no NuGet, cancellable animations, no running the app and no synthetic input on his machine, under about 420 lines per file.

## 5. Amendment (queued while the round was being set up)

- "The locked version should not respect the resize: it should compact, and when unlocked it goes to its desired size." The manual floor of round 4 applies only while unlocked; a locked note is its auto size (the text plus padding). Lock shrinks to it with the fold, unlock grows back to the floor with the unfold; `PerchPoint` anticipates both.
- "The vines in the locked compact mode should still be shown normally and still overflow the box." Locked, the vines keep their full look and depth (the stem outside the edge, the leaves inside and out), only shorter because the edges are shorter; nothing clips them. If the locked layout clips the overhang today, the stage keeps its 16 DIP margins in both states.
