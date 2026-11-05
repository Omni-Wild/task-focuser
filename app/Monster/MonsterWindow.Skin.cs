using System.Windows;

namespace TaskFocuser.Monster;

// The bird's skin (SPEC-round5 2.3, 2.4): which bird this window draws, the per-bird step tables every
// act and loop plays, and the switch from one bird to the other.
//
// The switch reuses the sulk's machinery on purpose: the old bird climbs away on the leave path without
// the hide (FlyOff), the skin swaps once it is out of sight (BeginAway), and after SwitchGap the new bird
// flies in from up there with the round-1 arrival (OnAwayTick, ArriveAt). So IsOut stays true throughout,
// an ArriveAt meanwhile only retargets the landing, and Leave() during it simply does not come back (the
// FlyOff and Away cases of Leave). A pending skin (switchSkin) is never dropped: whatever cuts the switch
// short (a hide, a Leave, StopEverything) puts it on, so the next arrival is the chosen bird.
// Round 6: the dog takes the same road along the ground (she sprints off to the side and runs back in
// from there, Ground.cs); a switch between a bird and her starts the newcomer where it enters (OnAwayTick).
public partial class MonsterWindow
{
    private static readonly TimeSpan SwitchGap = TimeSpan.FromMilliseconds(150);

    private ActSet acts = ActSet.For(Birds.Lennie);
    // The bird the running switch will put on once the old one is out of sight; null when none is pending.
    private BirdSkin? switchSkin;

    /// <summary>The bird this window draws; set through <see cref="SwitchTo"/>.</summary>
    public BirdSkin Skin { get; private set; } = Birds.Lennie;

    public void SwitchTo(BirdSkin skin, Point perch)
    {
        ArgumentNullException.ThrowIfNull(skin);
        // Already this bird, or already on its way to becoming it.
        if (ReferenceEquals(skin, switchSkin ?? Skin)) return;
        if (switchSkin is not null)
        {
            // Mid-switch, the old bird still climbing (or a shove or a leave still running): only the
            // bird that comes back changes.
            switchSkin = skin;
            this.perch = perch;
            return;
        }
        if (!IsVisible)
        {
            // Locked, unpinned, Dot or Closed: silent, and the next arrival is the new bird.
            ApplySkin(skin);
            return;
        }
        this.perch = perch;
        switchSkin = skin;
        // Leaving for good, or shoving into the dot (whose callback the conductor waits for): the old
        // bird finishes what it does, and the hide that follows puts the new skin on.
        if (!IsOut || motion == Motion.Shove || loop == Loop.Bump) return;
        if (motion == Motion.FlyOff) return; // sulking upward already: BeginAway swaps when it is unseen
        if (motion == Motion.Away)
        {
            // Sulking out of sight: swap now and come back after the switch gap, not the sulk's second.
            ApplyPendingSkin();
            awayTimer.Stop();
            awayTimer.Interval = SwitchGap;
            awayTimer.Start();
            return;
        }
        // The visible switch: whatever it did ends (an act, a walk, a flight, a press), the temper is
        // calm again, and the old bird leaves upward from where it is, fading.
        AbortPress();
        CancelActivity();
        ResetMood();
        StartLeavePath(Motion.FlyOff); // the old one leaves its own way: a bird upward, the dog off to the side
        UpdateRenderHook();
    }

    /// <summary>Puts on the skin a switch left pending, if any.</summary>
    private void ApplyPendingSkin()
    {
        if (switchSkin is not { } s) return;
        switchSkin = null;
        ApplySkin(s);
    }

    private void ApplySkin(BirdSkin skin)
    {
        Skin = skin;
        acts = ActSet.For(skin);
        // Round 6: the tumble's spring keeps a dog on the perch line, and her ground snack is the kibble.
        grip.Ground = skin.Locomotion == Locomotion.Ground;
        effects.SetSeedGlyph(acts.SeedGlyph);
        ResetMood();
        // Unseen (hidden, or waiting above the screen): show the new bird's idle now. A visible bird
        // (only the shove case) changes with its next frame, which every loop and act sets anyway.
        if (!IsVisible || motion == Motion.Away) Sprite.Frame = skin.IdleOpen;
    }

    /// <summary>
    /// One bird's step tables, built once per bird (and its frames baked once) on first use, so a
    /// switch costs a dictionary lookup and an act never allocates. The timings are the round-2 to
    /// round-4 ones; only the frames come from the skin.
    /// </summary>
    private sealed class ActSet
    {
        private static readonly Dictionary<BirdSkin, ActSet> Made = new(ReferenceEqualityComparer.Instance);

        // Up, level, down, level: the wing beat reads as a loop instead of a snap back to the top. For a dog
        // the same three frames are the run (stretched, gathered, bunched), so the loop is her gallop.
        public readonly string[][] FlapCycle;
        public readonly (string[] Frame, int Ms, Cue Cue)[] Eat, Hop, Annoyed, Peck, Seed, Ruffle;
        public readonly (string[] Frame, int Ms, Cue Cue)[][] IdleVariations;
        // Round 6: the beat's pace (a bird 14 and 20 fps, the dog's run 12 and 18), the ground snack, and the
        // braking frame of the arrival's overshoot and the zoomies' turns (a bird never shows it).
        public readonly TimeSpan FlapTick, FastFlapTick;
        public readonly string[] SeedGlyph, Skid;
        // Where the eat's hearts rise from (window DIPs): a bird's head is over the window centre, the dog's
        // about 12 DIP to the right of it (from the centre the left heart came up over her tail).
        public readonly Point HeartsAt;
        // The dog's own tables (MonsterWindow.Dog.cs), null for a bird, so a bird can never draw them.
        public readonly (string[] Frame, int Ms, Cue Cue)[]? Wag, WagBurst, Bow, Beg;
        public readonly (string[] Frame, int Ms, Cue Cue)[][]? Naps;

        public static ActSet For(BirdSkin skin)
        {
            if (Made.TryGetValue(skin, out var hit)) return hit;
            SpriteView.Prebake(skin.All);
            // Her ground glyphs too, so the first kibble or z does not build a drawing mid-act.
            if (skin.Dog is not null) SpriteView.Prebake(new[] { DogSprites.Kibble, DogSprites.Zz });
            var made = new ActSet(skin);
            Made[skin] = made;
            return made;
        }

        private ActSet(BirdSkin s)
        {
            FlapCycle = new[] { s.FlapUp, s.FlapMid, s.FlapDown, s.FlapMid };
            bool ground = s.Locomotion == Locomotion.Ground;
            FlapTick = ground ? RunTick : MonsterWindow.FlapTick;
            FastFlapTick = ground ? FastRunTick : MonsterWindow.FastFlapTick;
            SeedGlyph = s.Dog is null ? Sprites.Seed : DogSprites.Kibble;
            Skid = s.Dog?.Skid ?? s.FlapMid;
            HeartsAt = new Point(WinW / 2 + (ground ? DogHeartsShift : 0), SpriteY + 3 * Sprites.CellSize);
            if (s.Dog is { } d)
            {
                Wag = DogWag(d, 6, 150);
                WagBurst = DogWag(d, 5, 140); // the keep-alive's 700 ms
                Bow = new[] { (d.Bow, 700, Cue.Lean) };
                // One step held with no timer (Ms 0): she sits up until Beg(false) or anything else cuts it.
                Beg = new[] { (d.Beg, 0, Cue.None) };
                Naps = DogNaps(d);
            }

            // Three chomps in about 500 ms, then the happy face with hearts for 600 ms.
            Eat = new[]
            {
                (s.Eat1, 83, Cue.None), (s.Eat2, 83, Cue.None),
                (s.Eat1, 83, Cue.None), (s.Eat2, 83, Cue.None),
                (s.Eat1, 83, Cue.None), (s.Eat2, 85, Cue.None),
                (s.Happy, 600, Cue.Hearts),
            };
            Hop = new[] { (s.Hop, 180, Cue.Hop) };
            Annoyed = new[] { (s.Chirp, 420, Cue.Shake) };
            IdleVariations = new[]
            {
                new[] { (s.TiltL, 600, Cue.None) },
                new[] { (s.TiltR, 600, Cue.None) },
                new[] { (s.Preen, 800, Cue.None) },
                new[] { (s.Hop, 180, Cue.IdleHop) },
                // The glance: a double blink.
                new[] { (s.IdleBlink, 110, Cue.None), (s.IdleOpen, 140, Cue.None), (s.IdleBlink, 110, Cue.None) },
            };

            // One peck, about 160 ms; the seed is drawn into Peck2 itself, so it shows exactly for that frame.
            Peck = new[] { (s.Peck1, 60, Cue.None), (s.Peck2, 60, Cue.None), (s.IdleOpen, 40, Cue.None) };

            // The seed fidget: the seed is put down and noticed for a beat, then two pecks. On each Peck2
            // the ground seed is hidden, because the frame's own seed at the beak is then that seed (picked
            // up); after the first peck it is back on the ground (dropped), after the second it stays gone
            // (eaten). Then the happy blink. No sound: idle sounds are the conductor's, by his settings.
            Seed = new[]
            {
                (s.IdleOpen, 220, Cue.SeedShow),
                (s.Peck1, 60, Cue.None), (s.Peck2, 60, Cue.SeedHide), (s.IdleOpen, 40, Cue.SeedShow),
                (s.Peck1, 60, Cue.None), (s.Peck2, 60, Cue.SeedHide), (s.IdleOpen, 40, Cue.None),
                (s.IdleBlink, 120, Cue.None),
            };

            // The ruffle (SPEC-round3 2.3): alternate poses with a one-DIP jitter, then one settle frame.
            Ruffle = new (string[] Frame, int Ms, Cue Cue)[RuffleFrames + 1];
            for (int i = 0; i < RuffleFrames; i++)
                Ruffle[i] = ((i & 1) == 0 ? s.Ruffle1 : s.Ruffle2, RuffleFrameMs, Cue.Jitter);
            Ruffle[RuffleFrames] = (s.IdleOpen, RuffleSettleMs, Cue.Settle);
        }
    }
}
