using System.Diagnostics;
using System.Windows;
using TaskFocuser.Core;

namespace TaskFocuser;

/// <summary>
/// Round 2 of the state machine (docs/SPEC-round2.md): the flight instead of the teleport (2.1), the
/// bird's sounds (2.5; round 3's idle scheduler in Conductor.Round3.cs replaced the fixed keep-alive),
/// the bird that stays on lock (2.6), its opacity (2.7), the cucumber drop (2.9) and the bird as a drag
/// handle for the note (2.10). Same rules as Conductor.cs: UI thread only, every module call behind Guard or Fail.
/// </summary>
public sealed partial class Conductor
{
    // A resize that moves the perch further than this is flown (2.1); smaller ones are followed instantly.
    const double FlyThreshold = 12;
    // ArriveAt's fly-in (450 ms) plus a margin: inside it, MoveTo retargets the arrival and a FlyTo
    // would cut it short.
    const int ArriveRetargetMs = 500;

    long arriveStarted;                 // Stopwatch timestamp of the last ArriveAt
    bool birdDragging;                  // the running note drag is held by the bird (2.10)
    Point birdDragLast;                 // previous cursor point of that drag, for NudgeBy's delta
    // What the bar's fields were the last time we looked; Changed says only "something changed".
    bool seenBirdStays, seenBirdOpaque, seenSoundOn;
    int seenOpacity;
    double seenVolume;

    /// <summary>The bird belongs on the note: Note mode, and unlocked or allowed to stay (2.6).</summary>
    bool BirdWanted => state.Mode == Mode.Note && (!state.Locked || state.BirdStays);

    bool NoteVisible => note is Window w && w.IsVisible;

    void StartRound2()
    {
        // Renders the wave files once; off the UI thread so the note shows without waiting.
        Task.Run(() =>
        {
            try { Sound.Warm(); }
            catch (Exception ex) { Debug.WriteLine($"Task Focuser: sound warm-up failed: {ex}"); }
        });

        seenBirdStays = state.BirdStays;
        seenBirdOpaque = state.BirdOpaque;
        seenOpacity = state.Opacity;
        seenSoundOn = state.SoundOn;
        seenVolume = state.Volume;
        Guard("SetOpacity", ApplyBirdOpacity);
        StartIdleSounds();   // round 3 (Conductor.Round3.cs): the random idle sounds instead of the 5-minute chirp

        note.CucumberDropped += p => Guard("note.CucumberDropped", () => OnCucumberDropped(p));
        monster.ChirpRequested += k => Guard("monster.ChirpRequested", () => PlaySound(k));
        monster.DragStarted += p => { try { OnBirdDragStarted(p); } catch (Exception ex) { Fail("monster.DragStarted", ex); } };
        monster.DragMoved += p => { try { OnBirdDragMoved(p); } catch (Exception ex) { Fail("monster.DragMoved", ex); } };
        monster.DragEnded += p => { try { OnBirdDragEnded(p); } catch (Exception ex) { Fail("monster.DragEnded", ex); } };
        // monster.Clicked is informational: the hop, the peep request and the annoyed count are the bird's own.
    }

    // ---- 2.1 flight instead of teleport ----

    /// <summary>Puts a perched bird on <paramref name="perch"/>: flown when far, followed when near.</summary>
    void SettleOn(Point perch)
    {
        SyncPerchSpan();   // round 5: the walk's room follows every perch change
        double dx = perch.X - lastPerch.X, dy = perch.Y - lastPerch.Y;
        bool far = !double.IsNaN(lastPerch.X) && dx * dx + dy * dy > FlyThreshold * FlyThreshold;
        // During a drag the bird holds the note (instant MoveTo); during a sequence it is shoving;
        // during its arrival MoveTo retargets the fly-in, which is already a flight.
        if (far && !Arriving && !noteDragging && !busy)
        {
            monster.FlyTo(perch);
            lastPerch = perch;
        }
        else MoveMonster(perch);
    }

    // ---- 2.5 sound ----

    /// <summary>True during the fly-in of the last ArriveAt, when MoveTo retargets it and nothing may cut it.</summary>
    bool Arriving => Stopwatch.GetElapsedTime(arriveStarted).TotalMilliseconds < ArriveRetargetMs;

    /// <summary>Every bird sound request goes through here (the idle sounds and the slider's peeps do not).</summary>
    void PlaySound(ChirpKind kind)
    {
        // Round 3: the idle scheduler may already have played this ruffle itself (Conductor.Round3.cs).
        if (kind == ChirpKind.Ruffle && TakeRuffleRequest()) { RedrawIdle(); return; }
        if (!state.SoundOn) return;
        Sound.Play(kind, state.Volume, state.Bird);   // round 5: the active bird's voice
        // Any other sound kept the headphones awake just now; the next idle sound is a fresh interval away.
        RedrawIdle();
    }

    // ---- 2.6, 2.7: the bar's toggles, seen through note.Changed ----

    /// <summary>Reacts to the bar's round-2 fields; true when it already placed the bird (skip the perch follow).</summary>
    bool OnSettingsChanged()
    {
        if (state.BirdOpaque != seenBirdOpaque || state.Opacity != seenOpacity)
        {
            seenBirdOpaque = state.BirdOpaque;
            seenOpacity = state.Opacity;
            ApplyBirdOpacity();
        }

        // The slider plays its own peeps; a moved volume or a flipped sound toggle counts as a sound.
        if (state.SoundOn != seenSoundOn || state.Volume != seenVolume)
        {
            seenSoundOn = state.SoundOn;
            seenVolume = state.Volume;
            RedrawIdle();
        }
        OnIdleSettingsChanged();   // round 3: a new IdleRate re-draws from its range

        if (state.BirdStays == seenBirdStays) return false;
        seenBirdStays = state.BirdStays;
        // Unlocked, the flag changes nothing visible; the next lock reads it.
        if (state.Mode != Mode.Note || !state.Locked || busy || noteDragging) return false;
        if (!state.BirdStays)
        {
            if (monster.IsOut) monster.Leave();
            return true;
        }
        if (NoteVisible && !monster.IsOut) { ArriveAt(note.PerchPoint); return true; }
        return false;
    }

    void ApplyBirdOpacity() => monster.SetOpacity(state.BirdOpaque ? 1.0 : state.Opacity / 100.0);

    // ---- 2.9 the cucumber ----

    void OnCucumberDropped(Point p)
    {
        // Round 6 (2.3): the begging ends first, so a dog that was begging eats and one that was missed sits down.
        EndBegging();
        // A bird away or mid-shove gets nothing; the note lets the slice fall by itself.
        if (busy || switching) return;   // round 5: nor a bird mid-switch
        if (monster.IsOut && monster.Contains(p)) monster.Eat();
    }

    // ---- 2.10 the bird as a drag handle: exactly a note drag, the note moved by us ----

    void OnBirdDragStarted(Point p)
    {
        birdDragging = true;
        birdDragLast = p;
        OnNoteDragStarted(p);
        // Refused (busy, or not Note mode): the bird's moves and end are not a drag of ours.
        if (!noteDragging) birdDragging = false;
    }

    void OnBirdDragMoved(Point p)
    {
        if (!birdDragging) return;
        NudgeTo(p);
        OnNoteDragMoved(p);
    }

    void OnBirdDragEnded(Point p)
    {
        if (!birdDragging) return;
        birdDragging = false;
        // The release may land a few DIP past the last move; NudgeBy keeps State.X/Y exact for the save.
        NudgeTo(p);
        OnNoteDragEnded(p);
    }

    void NudgeTo(Point cursor)
    {
        var delta = cursor - birdDragLast;
        birdDragLast = cursor;
        if (delta.X != 0 || delta.Y != 0) note.NudgeBy(delta);
    }
}
