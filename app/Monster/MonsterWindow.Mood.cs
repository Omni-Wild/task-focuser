using System.Windows;
using System.Windows.Threading;
using TaskFocuser.Core;

namespace TaskFocuser.Monster;

// The perched bird's own temper (SPEC-round3 2.3, 2.6): resting and blinking, the click mood that
// turns from happy to annoyed and, pestered enough, into a short sulk flight out of sight and back,
// and the feather ruffle. Kept together because they are all rules about what may interrupt what; for
// the same reason the other two perched entries the conductor calls, Eat and Flap, sit beside Ruffle.
//
// The mood: a click after MoodCalmMs alone starts a fresh streak (and clears every counter); the first
// two clicks of a streak are happy hops with a peep, the third and every later one an annoyed chirp.
// Every annoyed chirp is stamped; when the last FleeChirps of them fall within FleeWindowMs the chirp
// is played as Fume, and when it ends the bird flies off (FlyOff, the leave path without the hide),
// waits AwayTime out of sight (Away), and arrives back at the perch, which MoveTo and FlyTo kept
// current meanwhile; the landing resets the counters. Eat resets them at once.
public partial class MonsterWindow
{
    private const long MoodCalmMs = 2500;     // alone this long: the next click is a happy one again
    private const int AnnoyedFrom = 3;        // the third quick click in a streak is the first annoyed one
    private const int FleeChirps = 5;         // this many annoyed chirps ...
    private const long FleeWindowMs = 12000;  // ... within this long and it has had enough
    private const int RuffleFrameMs = 40;     // about 24 fps
    private const int RuffleFrames = 8;       // 320 ms of shaking
    private const int RuffleSettleMs = 120;   // one IdleOpen before the rest takes over

    private static readonly TimeSpan AwayTime = TimeSpan.FromMilliseconds(1000);

    private readonly DispatcherTimer awayTimer = new(DispatcherPriority.Background) { Interval = AwayTime };
    // The times of the last FleeChirps annoyed chirps, as a ring: annoyedNext is the oldest once it is full.
    private readonly long[] annoyedAt = new long[FleeChirps];
    private int annoyedNext, annoyedCount, clickStreak;
    // How the companion that went away gets about (round 6); the one that comes back may differ after a switch.
    private Locomotion awayLocomotion;
    private long lastClick = long.MinValue / 4;

    public void Eat()
    {
        // The cucumber makes peace at once (SPEC-round3 2.6), even when it cannot eat right now.
        ResetMood();
        Touch();
        // Ignored mid-flight, while carried or tumbling, and while he is dragging by the bird.
        if (!IsVisible || !IsOut || motion != Motion.None || carrying || grip.Tumbling || dragging) return;
        PlayAct(Act.Eat, acts.Eat);
    }

    public void Flap()
    {
        // The keep-alive's visible half: only a resting or fidgeting bird; any real activity wins.
        if (!IsVisible || !IsOut || motion != Motion.None || carrying || grip.Tumbling || pressed) return;
        if (act is not (Act.None or Act.Idle)) return;
        // A napping dog sleeps through the keep-alive (the conductor still plays its sound).
        if (Napping) return;
        // Round 6: a dog wags for the same 700 ms instead (the "wuf" is the conductor's keep-alive sound).
        if (acts.WagBurst is { } wag)
        {
            PlayAct(Act.WagBurst, wag);
            return;
        }
        StopWalk(); // the burst flaps where it stands; the rest after it walks back
        StopAct();
        idleTimer.Stop();
        act = Act.FlapBurst;
        StartFlap();
        actTimer.Interval = TimeSpan.FromMilliseconds(700);
        actTimer.Start();
    }

    /// <summary>Fluffs up and shakes, with the brrrp; the conductor's idle scheduler calls it.</summary>
    public void Ruffle()
    {
        // Only a bird sitting still: any flight, sulk, grip, press or real act wins. A fidget or a
        // blink is not worth keeping over the shake (the same rule as Flap).
        if (!IsVisible || !IsOut || motion != Motion.None || loop != Loop.None || carrying
            || grip.Tumbling || pressed || act is not (Act.None or Act.Idle)) return;
        // A napping dog sleeps through it too: no shake, and no request, so the conductor plays the sound itself.
        if (Napping) return;
        PlayAct(Act.Ruffle, acts.Ruffle);
        ChirpRequested?.Invoke(ChirpKind.Ruffle);
    }

    /// <summary>A press and release without movement: a happy hop, an annoyed chirp, or the last straw.</summary>
    private void OnClick()
    {
        long now = Environment.TickCount64;
        // Left alone long enough: whatever came before is forgiven.
        if (now - lastClick >= MoodCalmMs) ResetMood();
        lastClick = now;
        clickStreak++;
        Clicked?.Invoke();
        // Mid-flight, carried, tumbling, with its mouth full or already fuming: the click only counts.
        if (!IsVisible || !IsOut || motion != Motion.None || carrying || grip.Tumbling
            || act is Act.Eat or Act.Fume) return;
        if (clickStreak < AnnoyedFrom)
        {
            PlayAct(Act.Hop, acts.Hop);
            ChirpRequested?.Invoke(ChirpKind.Peep);
            return;
        }
        annoyedAt[annoyedNext] = now;
        annoyedNext = (annoyedNext + 1) % FleeChirps;
        if (annoyedCount < FleeChirps) annoyedCount++;
        bool fed = annoyedCount == FleeChirps && now - annoyedAt[annoyedNext] <= FleeWindowMs;
        // The Annoyed chirp goes with the shake cue, the fifth one included.
        PlayAct(fed ? Act.Fume : Act.Annoyed, acts.Annoyed);
    }

    private void ResetMood()
    {
        clickStreak = 0;
        annoyedCount = 0;
        annoyedNext = 0;
    }

    /// <summary>The sulk: the leave climb from the perch without the hide; false when something holds it.</summary>
    private bool StartFlyOff()
    {
        if (!IsVisible || !IsOut || motion != Motion.None || carrying || grip.Tumbling) return false;
        ResetMood();
        // A press that did not move yet (the next click on its way) is simply dropped.
        AbortPress();
        CancelActivity();
        StartLeavePath(Motion.FlyOff);
        UpdateRenderHook();
        return true;
    }

    /// <summary>Out of sight (the climb faded it out): wait without beating wings nobody sees.</summary>
    private void BeginAway()
    {
        StopLoop();
        motion = Motion.Away;
        // Read before the swap below: OnAwayTick must know whether the one waiting up here (or off to the
        // side) gets about the way the one that comes back does.
        awayLocomotion = Skin.Locomotion;
        // The same climb serves the bird switch (SPEC-round5 2.4): unseen now, so the new bird is put
        // on here and comes in after the short switch gap instead of the sulk's second.
        bool switching = switchSkin is not null;
        if (switching) ApplyPendingSkin();
        awayTimer.Interval = switching ? SwitchGap : AwayTime;
        awayTimer.Start();
    }

    private void OnAwayTick(object? sender, EventArgs e)
    {
        awayTimer.Stop();
        if (motion != Motion.Away || !IsVisible) return;
        // Away has no clock, so this only clears the state; the arrival then flies in from up here to
        // wherever the perch is now and lands with dust.
        StopMotion();
        // Round 6: a dog runs back in from the side of the perch's monitor nearer the perch, and a switch
        // between a bird and the dog starts the newcomer where it enters (a bird above, a dog at the side).
        if (OnGround || Skin.Locomotion != awayLocomotion)
        {
            var start = ArrivalStart(perch);
            Left = start.X;
            Top = start.Y;
        }
        ArriveAt(perch);
    }

    // The ruffle's step table is per bird (round 5): MonsterWindow.Skin.cs, acts.Ruffle.

    // ---- rest ---------------------------------------------------------------------------------

    /// <summary>Perched with nothing to do: carrying keeps flapping, otherwise sit, blink now and then, and fidget rarely.</summary>
    private void EnterRest()
    {
        if (carrying)
        {
            StartFlap();
            return;
        }
        // A walk still on its way ends here too; a bird left off its perch (a walk cut by an act)
        // walks home first, and that return ends in this rest again.
        StopWalk();
        StopLoop();
        StopAct();
        Sprite.FlipX = false;
        Sprite.Frame = Skin.IdleOpen;
        if (stray && StartReturn()) return;
        ScheduleBlink();
        ScheduleIdle();
    }

    private void ScheduleBlink()
    {
        blinking = false;
        blinkTimer.Interval = TimeSpan.FromMilliseconds(Random.Shared.Next(2000, 5001));
        blinkTimer.Start();
    }

    private void OnBlinkTick(object? sender, EventArgs e)
    {
        blinkTimer.Stop();
        if (motion != Motion.None || loop != Loop.None || act != Act.None || walkPhase != WalkPhase.None || !IsVisible) return;
        if (!blinking)
        {
            blinking = true;
            Sprite.Frame = Skin.IdleBlink;
            blinkTimer.Interval = BlinkHold;
            blinkTimer.Start();
        }
        else
        {
            Sprite.Frame = Skin.IdleOpen;
            ScheduleBlink();
        }
    }
}
