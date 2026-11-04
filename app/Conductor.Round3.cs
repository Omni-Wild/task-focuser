using System.Diagnostics;
using System.Windows.Threading;
using TaskFocuser.Core;

namespace TaskFocuser;

/// <summary>
/// Round 3 of the state machine (docs/SPEC-round3.md): the idle sound scheduler that replaced round 2's
/// fixed 5-minute keep-alive (2.3), and the bird called in at once because the note now reports its final
/// perch in advance (2.2). Same rules as Conductor.cs: UI thread only, every module call behind Guard or Fail.
/// </summary>
public sealed partial class Conductor
{
    // A ruffle request from the bird this soon after the scheduler played the ruffle itself is that same
    // ruffle (a bird that raises its request a frame late); it must not sound twice.
    const int RuffleEchoMs = 400;

    // One timer for every idle sound, re-armed with a fresh random interval each time (2.3). Background
    // priority: a chirp can wait for a running animation frame, never the other way round.
    DispatcherTimer? idleTimer;
    IdleRate seenIdleRate;
    bool ruffleHeard;       // the bird asked for its ruffle sound during our monster.Ruffle() call
    long ruffleDirectAt;    // Stopwatch timestamp of the scheduler's own ruffle play; 0 when none is pending

    // ---- 2.3 the idle sounds ----

    void StartIdleSounds()
    {
        seenIdleRate = state.IdleRate;
        idleTimer = new DispatcherTimer(DispatcherPriority.Background);
        idleTimer.Tick += (_, _) => Guard("idle sound", OnIdleSound);
        RedrawIdle();
    }

    /// <summary>The fuzzy ranges of 2.3, in minutes. Every range starts above 60 s, so re-drawing after any
    /// sound is enough to keep an idle sound from following another within a minute.</summary>
    static (double Min, double Max) IdleRange(IdleRate rate) => rate switch
    {
        IdleRate.Often => (1.5, 3),
        IdleRate.Rarely => (8, 15),
        _ => (3, 6),
    };

    /// <summary>Re-arms the idle timer with a fresh interval from the current range; called after every sound.</summary>
    void RedrawIdle()
    {
        if (idleTimer is null || tornDown) return;
        idleTimer.Stop();
        // Sound off: nothing to play, so nothing ticks (SPEC 3.11); turning it on comes back here via OnSettingsChanged.
        if (!state.SoundOn) return;
        var (min, max) = IdleRange(state.IdleRate);
        idleTimer.Interval = TimeSpan.FromMinutes(min + (max - min) * Random.Shared.NextDouble());
        idleTimer.Start();
    }

    void OnIdleSound()
    {
        // Runs in every mode: the purpose is still the Bluetooth headphones staying awake (round 2's reason).
        try { if (state.SoundOn) PlayIdleSound(); }
        finally { RedrawIdle(); }
    }

    void PlayIdleSound()
    {
        bool ruffle = state.IdleSounds switch
        {
            IdleSounds.Chirp => false,
            IdleSounds.Ruffle => true,
            _ => Random.Shared.Next(2) == 1,
        };
        // The bird acts only when nothing else moves it: a dock, a drag or a fly-in must not be broken.
        bool birdFree = monster.IsOut && !busy && !noteDragging && !switching && !Arriving;

        if (!ruffle)
        {
            Sound.Play(ChirpKind.Keepalive, state.Volume, state.Bird);
            if (birdFree) monster.Flap();
            return;
        }

        if (birdFree)
        {
            // The bird's ruffle raises its own Ruffle request at the start, which PlaySound plays.
            ruffleHeard = false;
            monster.Ruffle();
            if (ruffleHeard) return;
        }
        // The bird is away, busy, or declined (Ruffle is a no-op mid-motion): the sound alone, so the
        // headphones still get their sound.
        Sound.Play(ChirpKind.Ruffle, state.Volume, state.Bird);
        ruffleDirectAt = Stopwatch.GetTimestamp();
    }

    /// <summary>Marks a bird ruffle request as heard; true when the scheduler already played that ruffle.</summary>
    bool TakeRuffleRequest()
    {
        ruffleHeard = true;
        bool echo = ruffleDirectAt != 0 && Stopwatch.GetElapsedTime(ruffleDirectAt).TotalMilliseconds < RuffleEchoMs;
        ruffleDirectAt = 0;
        return echo;
    }

    /// <summary>Called from OnSettingsChanged. IdleSounds is read at each tick and PlantsOn is the note's own,
    /// so only a new IdleRate needs anything here: the pending interval came from the old range.</summary>
    void OnIdleSettingsChanged()
    {
        if (state.IdleRate == seenIdleRate) return;
        seenIdleRate = state.IdleRate;
        RedrawIdle();
    }

    // ---- 2.2 the arrival ----

    /// <summary>
    /// Calls the bird in right away: the note reports the perch of its target layout (2.2), so the bird flies
    /// straight to its final spot. Falls back to the deferred ArriveSoon when the note is not on screen yet
    /// or has no perch yet. <paramref name="followLayout"/> (after a show, never after an unlock): one deferred
    /// look after the first layout pass, which moves a bird mid-arrival onto a perch the text layout shifted
    /// (MoveTo retargets the fly-in) and does nothing when the perch held. Not after an unlock, where that
    /// look would land inside the unfold.
    /// </summary>
    void ArriveNow(bool followLayout)
    {
        var perch = note.PerchPoint;
        if (!NoteVisible || !double.IsFinite(perch.X) || !double.IsFinite(perch.Y)) { ArriveSoon(); return; }
        CallBirdIn();
        if (followLayout) app.Dispatcher.InvokeAsync(() => Guard("ArriveNow.Follow", FollowPerch), DispatcherPriority.Loaded);
    }

    // Only the fallback of ArriveNow: a note not yet on screen has its perch once it has been laid out,
    // so this waits for the next layout pass.
    void ArriveSoon() => app.Dispatcher.InvokeAsync(() => Guard("ArriveSoon", CallBirdIn), DispatcherPriority.Loaded);

    /// <summary>Brings the bird to the perch if it belongs there now. Round 2: a bird already out (BirdStays
    /// across an unlock) flies over instead of arriving again.</summary>
    void CallBirdIn()
    {
        // Round 5: mid-switch the new bird is already on its way; the switch's end calls this again.
        if (state.Mode != Mode.Note || !BirdWanted || noteDragging || busy || switching) return;
        if (monster.IsOut) SettleOn(note.PerchPoint); else ArriveAt(note.PerchPoint);
    }

    void FollowPerch()
    {
        if (state.Mode == Mode.Note) SyncPerchSpan();   // round 5: after the show's layout pass, bird or not
        if (state.Mode == Mode.Note && BirdWanted && !busy && !noteDragging && !switching && monster.IsOut) SettleOn(note.PerchPoint);
    }
}
