# Task Focuser v2, round 6: Rori

Written 2026-11-05 by the Conflux root from Jiří's message. Rounds 1 to 5 and the release pass (`SPEC.md` section 9) stay binding; this file adds to them and overrides where it says so.

## 1. His words

"Add another animal variant. We are getting a puppy (Japanese spitz). We already have a puppy named Rori (reference photos in root). Add Rori in the similar spirit as the birds are, but design custom new animations for it and sounds; otherwise the behaviour should stay the same. For the flying replacement, just make it look like she is chasing its spot on the same spot as birds are sitting. You may draw her as more of a 2 year old Japanese spitz (the webp image is her mother, Lily, roughly 2yo). Surprise me: add some unique dog-like interactions or quirks or eye candies. And also add white mode (colour) to accompany it."

The reference photos: `artifacts/2026-11-05_rori-reference/` (Lily, the mother, in the webp: a 2-year-old Japanese spitz, pure white long fur, a thick ruff and a plumed tail curled over the back, small pointed ears, a fox-like black-nosed muzzle with a smile, dark almond eyes; the three jpgs are Rori as a few-weeks-old puppy, a round white fluff with a short muzzle). We draw her as Lily looks.

## 2. What Rori is

A third companion behind the same `BirdSkin` contract (decision 022): id `rori`, default name `Rori`, picked in the picker like the birds, renamed like them, with her own five sounds and her own frames. Everything the conductor does (arrival on unlock, carrying on a drag, the shove into the dot, the switch, the typing chain, the idle sounds, the moods, the treat, the walk) stays the same code. What changes for a dog is declared on the skin as **traits** (section 3), read by the shared behaviour code; the dog-only acts live in one partial, `Monster/MonsterWindow.Dog.cs`. Decision 026 records this.

### 2.1 Her look (the art, `Monster/DogSprites.cs`)
- The same grid as the birds: 22 columns by 16 rows, `CellSize` 3, **feet on row 15**, facing right (the muzzle points right; `FlipX` mirrors her). The front paws end around column 13 to 14 (the "front toe" the seed fidget aims at is column 14, as for the birds); the hind paws around columns 4 to 6; the plumed tail curls up over her back at the left, reaching rows 3 to 5. Head at the right: pointed ears up (two cells wide, two tall), the muzzle one or two cells forward with a black nose cell at its tip, dark eyes, a smile line, a pink tongue tip in the happy frames.
- Palette (her legend keys must not collide with Lennie's lowercase or Meggie's uppercase: use digits, and symbols if more are needed; `.` is transparent everywhere): fur `#FFFFFF`, fur shade `#DDE1EA` (the underside, the inside of the ruff, the far legs), deep shade `#B9C0CF`, outline `#8C93A5` (soft, so she reads on a white note and on the bar), eyes and nose `#1A1A2E`, an eye glint in fur white, inner ear `#F2B5C4`, tongue `#F0749A`, mouth line `#5A5F72`, kibble `#A8641A`, the bone biscuit `#F3E7C9` with `#C9B98F` outline, the sleep "z" in `#2B2A25`.
- Sitting is her rest pose (`IdleOpen`): sat on her haunches, chest up, tail curled over the back, head turned a little to the viewer, mouth closed in the spitz smile. Read on a 66 by 48 DIP sprite from a metre away: big simple shapes, a clear silhouette (ears, muzzle, tail plume), no fur texture noise beyond two or three shade cells.
- The 23 contract frames, what each means for a dog (the semantics of rounds 2 to 5 hold, so the shared acts work unchanged):

| Frame | The bird | Rori |
|---|---|---|
| `IdleOpen` | perched | sitting, eyes open |
| `IdleBlink` | blink | sitting, eyes closed |
| `FlapUp` `FlapMid` `FlapDown` | the wing beat, cycled Up, Mid, Down, Mid | **the run**: stretched (all legs out, ears back), mid (legs gathering, body level), bunched (legs under, back arched, ears up); the body bobs one cell between them; feet stay on or above row 15 |
| `ShoveA` `ShoveB` | leaning in with wings back | nose and front paws pushing, rump up, tail straight out; B leans in one more cell |
| `Tumble1` `Tumble2` | tumbling | a rolling fluff ball: ears and tail flopping, legs out at odd angles, two turns of the roll |
| `Eat1` `Eat2` | chomping | the bone biscuit in her mouth, jaw open and shut, cheeks puffed |
| `Happy` | happy with hearts | tongue out, eyes squinted shut in a smile, tail plume raised higher |
| `Chirp` | the annoyed chirp | the bark: mouth wide open, ears flat back, leaning forward |
| `TiltL` `TiltR` | head tilt | **the head tilt**, the dog classic: the whole head rolled 15 degrees, ears following, one eyebrow cell up |
| `Preen` | preening | scratching behind the ear with a hind leg, eyes closed, head tipped toward the leg |
| `Hop` | a hop | a bounce, all four paws off the ground, ears up |
| `Ruffle1` `Ruffle2` | fluffed and shaking | **the shake-off**: the body blurred wide, ears and tail flung left in 1 and right in 2, fur spiky |
| `Peck1` `Peck2` | pecking at a seed | sniffing: nose to the ground in 1, a kibble in her mouth in 2 (the kibble drawn into the frame at the mouth, as the seed is in the birds' Peck2) |
| `Walk1` `Walk2` | walking | the trot: diagonal legs alternating, a one-cell bob, tail bouncing |

- Her extras (section 3, `Dog` record), nine more frames: `Wag1`, `Wag2` (sitting, the tail plume swung left and right; everything else `IdleOpen`), `Bow` (the play bow: chest and front legs flat on the ground, rump and tail up, mouth open happy), `Beg` (sat up on the hind legs, front paws up and together, head tilted, pleading eyes), `Skid` (braking: front legs straight forward, rump down, ears and fur pushed forward), `Lie` (lying down sphinx-style, head up, tail along the body), `Sleep1`, `Sleep2` (lying with the head on the paws, eyes closed; 2 has the chest one cell fuller, the breath), `Stretch` (the wake-up: front legs out flat, rump up, a yawn).
- Glyphs in `DogSprites` too: `Bone` (a biscuit bone, 10 by 5 cells, used in the bar and as the carried treat, section 2.6), `Kibble` (3 by 2 cells, her seed on the ground), `Zz` (a 5 by 5 "z").
- Make a contact sheet: render every frame to a PNG (a small PowerShell script after `tools/make-icon.ps1`, which parses a sprites file by regex and paints cells) and look at it, at 1x and 3x; fix what does not read; at least two passes. Every frame has exactly 16 rows of exactly 22 characters, only legend characters.

### 2.2 Ground locomotion (the "flying replacement")
Rori never flies. Where a bird takes the air, she runs, on the line of the perch (the note's top edge), with the run frames:
- **Arrival** (`ArriveAt` when hidden): she runs in from off-screen at the side of the perch's monitor that is nearer to the perch, at the perch's height (her feet on the perch line all the way), facing the direction she runs, 450 ms in all as the birds' arrival so the switch budget holds, on her own linear clock rather than the birds' `BackEase`: one pace for the first three quarters, then the `Skid` frame while she brakes over the last stretch (at most 24 DIP, a tenth of the way on a short turnaround) and stops exactly on the spot (the review found the shared BackEase crossed the screen in 150 ms and then drifted backwards for 300 ms), then the landing dust of 2.10 at her paws, then she turns to face right and sits. An `ArriveAt` while she is visible (a cancelled leave, a tumble cut short) turns her around from where she stands, as for the birds.
- **Leave** (`Leave`, the sulk `FlyOff`, the switch): a little jump-start, then a sprint off toward the nearer side edge, fading over the second half of the 300 ms; the window hides at the end (or waits unseen for the sulk's second and the switch gap, exactly like a bird above the screen). `OffscreenAbove` has a ground twin, `OffscreenSide`; the leave path and the away wait pick by the skin's locomotion.
- **FlyTo** (the perch moved under her, a resize): the run with no lift.
- **Carrying** (the drag): this is the chase he asked for. While the note moves she runs at the perch with the run cycle (12 fps; 18 fps above `Grip.FastSpeed`, as the fast flap), **facing the direction of motion** (`FlipX` follows the horizontal velocity sign, with a dead band of 80 DIP/s so a jittery hand does not flip her), and her window **trails the perch** by up to about 24 DIP opposite the direction of motion (proportional to speed, saturating, growing in over the first 150 ms of a drag so the first pull visibly leaves her behind), easing back onto the perch within about 100 ms when the drag slows: she is running after the spot. While the note is held still (speed under 40 DIP/s for 150 ms) she does not run on the spot: she stands facing the way she last ran, tail wagging (`Wag1`/`Wag2` at 4 fps). The grip physics of round 2 stay: a fast pull makes her lose hold and tumble behind on the spring (`Sag` is 0 on the ground: she tumbles along the perch line, not below it), and the catch (`StartCatch`) is a run back with no lift.
- **Shove** (into the dot): the same path and timing as the birds, with the shove frames and the bump.
- No per-frame allocations in any of this; the render hook runs only while she moves, as today.

### 2.3 Her acts
The shared acts (eat, hop, annoyed, fume, the flap burst, the ruffle, the peck, the seed fidget, the five idle variations, the walk) run unchanged with her frames; the names stay. Dog-only, in `MonsterWindow.Dog.cs`, each on the act machine of Effects.cs (so a flight, a drag, a click, a hide cuts it like any act) and drawn in the fidget roll of `MonsterWindow.Peck.cs` only when the skin has `Dog` extras:
- **The wag** (common, a share like the seed): `Wag1`/`Wag2` alternating 150 ms, six swings. Also what `Flap()` plays for a dog (the keep-alive's visible half, 700 ms), with the "wuf".
- **The play bow** (uncommon): `Bow` 700 ms with a 2 DIP forward shift of the sprite, then `IdleOpen`.
- **Zoomies** (rare, one in fifteen fidgets, only when the perch span is at least two sprite widths): the walk machinery at four times the walk speed (240 DIP/s) with the run frames at 12 fps, out to the far end of the span, the `Skid` frame with a dust puff at the turn, back past the perch to the other end, skid and dust again, then back to the perch and sit. Any interruption cuts it like a walk.
- **The nap** (rare, and only when nothing touched her for 3 minutes: no click, no drag, no key (`Peck`), no treat; the dog partial keeps that timestamp): `Lie` 600 ms, then `Sleep1`/`Sleep2` alternating every 1.2 s for 10 to 20 s, a `Zz` glyph rising from above her head and fading every 2.4 s (a fourth particle kind in `Effects`, pooled like the hearts), then `Stretch` 500 ms, then `IdleOpen`. No blink during it (the act is running). Any cut ends it at once (the z particles finish fading on their own).
- **Begging** (`IMonster.Beg(bool)`, new): while he carries the treat (from the drag's first move until the drop or its cancel), she sits up in `Beg`; on `Beg(false)` without an eat she goes back to rest. Birds ignore the call. Only a resting or fidgeting dog begs; a drag, a flight or a real act wins and the call is dropped.
- **The clicks** (the mood of round 3): first two clicks a bounce with the "hm?", from the third the bark with the shake, the fifth bark in twelve seconds the run-off. Unchanged rules, her frames and sounds.

### 2.4 Her sounds (`Core/Sound.cs`, voice `rori`)
Five sounds, the same kinds, rendered like the others (synthesized, fixed seeds, 44.1 kHz mono, the same peaks: 0.50 for keepalive and happy, 0.40 for annoyed, 0.45 for peep, the ruffle peak 0.398), nothing sampled:
- `Keepalive`: one soft "wuf": a small bark of about 110 ms, a breathy burst with a pitch falling from about 650 to 380 Hz over its length, a short noise onset (10 ms, band-passed around 1.5 kHz) and a round body, a 15 ms attack, a tail of 60 ms. Puppy-sized, quiet, no edge.
- `Happy`: "yip-yip": two short bright yips (70 ms each, 90 ms apart) rising 900 to 1300 Hz, the second a step higher, a little noise at each onset.
- `Annoyed`: the grumble-growl: 260 ms, a rough low tone gliding 130 to 105 Hz with a 24 Hz tremolo and a touch of band-limited noise, the quietest of the set.
- `Peep` (the click and the slider preview): a tiny questioning whine "hm?": 90 ms, a soft tone rising 620 to 880 Hz, a slow attack of 20 ms.
- `Ruffle`: the shake-off: ears flapping, "fwup-fwup-fwup-fwup": a train of four to five soft low thumps at about 9 Hz (a 120 Hz tone burst with a 25 ms decay each) under a band-limited noise swish (the fur), 420 ms in all, the thumps slowing a little toward the end.
- Render each to the scratch folder and check: length, peak, start and end at silence, no clipping; look at the envelope (a text plot of the RMS per 5 ms is enough) and fix what does not read as described.

### 2.5 The white note
A seventh swatch: `White #FAFAF8` (`Palette.Colors`; the bar shows seven). The strip and the bar are the usual 12 % darker (`#DCDCDA`). Nothing else changes; the ink stays `#2B2A25`.

### 2.6 The treat follows the companion
The cucumber slice is Lennie's and Meggie's treat. With Rori active the bar's handle shows her `Bone` biscuit and the carried slice is the bone too (`BirdSkin.Treat`, a sprite frame; null = the cucumber drawing of Theme.xaml). The bar and the carried window swap the image when `state.Bird` changes (on `Changed`, from the conductor or the bar itself). The drop on her is the eat as before, with hearts and the "yip-yip".

## 3. Contracts (root, done before the crew starts)

- `Monster/BirdSkin.cs`: `enum Locomotion { Air, Ground }`; `Locomotion Locomotion { get; init; }` (default `Air`); `string[]? Treat { get; init; }`; `DogExtras? Dog { get; init; }` with `record DogExtras(string[] Wag1, string[] Wag2, string[] Bow, string[] Beg, string[] Skid, string[] Lie, string[] Sleep1, string[] Sleep2, string[] Stretch)`; `All` includes the extras when present (so `Prebake` bakes them).
- `Monster/Birds.cs`: `Rori` (`DogSprites`, `Locomotion.Ground`, `Treat = DogSprites.Bone`, `Dog = ...`), `All` = Lennie, Meggie, Rori.
- `Monster/DogSprites.cs`: stub frames that compile (the art builder replaces the file whole), plus `Bone`, `Kibble`, `Zz`.
- `Core/Contracts.cs`: `IMonster.Beg(bool begging)`; `INoteWindow` gains `event Action? TreatDragStarted` and `event Action? TreatDragCancelled` (the drop is the existing `CucumberDropped`).
- `Core/Palette.cs`: the White swatch.
- `Core/Sound.cs` stays the sound builder's whole file (`Voices` gains `rori` there).

## 4. Ownership (round 6)

```
Monster/DogSprites.cs                                                         A1 art        every frame, the extras, the glyphs, the contact sheet
Monster/MonsterWindow*.cs, Monster/Grip.cs, Monster/Effects.cs,
  Monster/SpriteView.cs, new Monster/MonsterWindow.Dog.cs                     B1 monster    the locomotion trait, the chase, the dog acts, Beg, the Zz particle
Core/Sound.cs                                                                 C1 sound      Rori's five sounds
Views/SettingsBar.xaml(.cs), Views/Theme.xaml, Views/CucumberWindow.xaml(.cs),
  Views/NoteWindow.Cucumber.cs, Views/BirdPicker.xaml(.cs), new Views/SpriteImage.cs,
  Conductor*.cs, app/README.md                                                D1 views+conductor   the treat swap, the treat drag events, Beg wiring, three rows, seven swatches, the README section
Core/Contracts.cs, Core/Palette.cs, Monster/BirdSkin.cs, Monster/Birds.cs, the stubs   root (done)
```

Rules as before: scratch-copy builds (`%TEMP%\tf-build-<name>\app`), own files only, no NuGet, cancellable animations, no timers at rest, no running the app and no windows opened on his machine, under about 420 lines per file (a new partial when a file would pass it), comments say why.

## 5. Review notes (2026-11-05, the same evening)

- **A lost capture mid treat-drag is a cancel, not a drop, for every companion.** Before this round a capture lost to Alt-Tab or another window counted as a drop at the cursor and could feed the bird; now only a real button release drops (`CucumberDropped`), and every other end raises `TreatDragCancelled`, because the begging of 2.3 needs one unambiguous end for every drag.
- **The dog's arrival curve** is her own (2.2): the birds' `BackEase` overshoot, read on the ground, was a teleport and a moonwalk.
- **Idle sounds do not wake a nap.** The keep-alive wag burst and the shake-off skip their visible half while the nap table plays (the sound still plays), so she sleeps through; any touch still wakes her.
- **The hearts and the kibble have per-skin spots**: the hearts rise over her head (12 DIP right of the window centre), the kibble lands 2 to 3 cells in front of the front toe (the birds' seed 3 to 5), because her mouth in `Peck2` sits further forward than a beak.
- **The bar speaks the companion's name**: "Rori stays when locked", "Drag onto Rori", "Which idle sounds Rori makes", and `wuf / shake / both` for the idle-sound choices of a dog.
- **The art is the birds' idiom, not a portrait (his word after the first install, 22:00):** "the face is too broken, make it more like the original design for the bird but keep it as a dog, don't try to actually draw the dog, that was my bad idea." Rori is now Lennie's boxy body in white with his big front-on eyes (rimmed in the deep shade so the white shows on white fur), two pointed ears, a black nose where the beak is, a small tail curl and four stubby legs; the 2.1 paragraph on Lily's muzzle and legs is superseded by this. Her front toe sits at column 16 now (the kibble lands one to three cells ahead of it; `Peck2` still matches gap 3).
