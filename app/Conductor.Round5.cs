using System.Windows.Threading;
using TaskFocuser.Monster;

namespace TaskFocuser;

/// <summary>
/// Round 5 of the state machine (docs/SPEC-round5.md): the bird he picked (2.4, the switch) and the perch
/// span that keeps the walk on the note (2.2). Same rules as Conductor.cs: UI thread only, every module call
/// behind Guard or Fail. The sounds by bird are the Sound.Play calls in Round2/Round3, which pass state.Bird.
/// </summary>
public sealed partial class Conductor
{
    // The visible switch: the round-1 leave (about 300 ms), 150 ms of empty sky, the round-1 arrival
    // (450 ms), plus a margin for the landing dust; drags and other motions wait this long.
    const int SwitchMs = 950;
    // A pick that comes while something else moves the bird is retried this often until it can run.
    const int SwitchRetryMs = 250;
    // 2.2: the feet keep 20 DIP clear of each end of the note's top edge.
    const double SpanInset = 20;

    string activeBird = "";          // the id of the skin the monster wears now
    bool switching;                  // a visible switch is running; drags are refused like during busy
    bool switchQueued;               // a retry of a deferred pick is already scheduled
    DispatcherTimer? switchTimer;
    double spanLeft = double.NaN, spanRight = double.NaN;   // last span sent, so SetPerchSpan goes on changes only

    /// <summary>Called from Start before the saved mode is entered: the bird is not out yet, so the skin
    /// swaps silently and the very first arrival is already the bird he picked.</summary>
    void StartRound5()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(SwitchMs) };
        timer.Tick += (_, _) => { timer.Stop(); Guard("SwitchDone", OnSwitchDone); };
        switchTimer = timer;

        var skin = Birds.ById(state.Bird);
        activeBird = skin.Id;
        Guard("SwitchTo.Startup", () => monster.SwitchTo(skin, note.PerchPoint));
    }

    // ---- 2.4 the switch ----

    /// <summary>Called from OnNoteChanged; true when it started a visible switch (the switch places the bird,
    /// so the perch follow is skipped).</summary>
    bool OnBirdPicked()
    {
        if (string.Equals(Birds.ById(state.Bird).Id, activeBird, StringComparison.OrdinalIgnoreCase)) return false;
        return ApplyBird();
    }

    /// <summary>Brings the monster's skin in line with state.Bird; idempotent, so a retry may run it any time.</summary>
    bool ApplyBird()
    {
        if (tornDown) return false;
        var skin = Birds.ById(state.Bird);
        if (string.Equals(skin.Id, activeBird, StringComparison.OrdinalIgnoreCase)) return false;

        bool visible = monster.IsOut;
        // A dock, a drag or a running switch owns the bird, and a bird that is out where it is not wanted is
        // on its way off (a lock mid-leave): a switch now would fight them or land a bird on a locked note.
        // The pick waits and is retried; it is never dropped, since state.Bird already says what he chose.
        if (busy || noteDragging || switching || (visible && !BirdWanted))
        {
            QueueSwitch();
            return false;
        }

        activeBird = skin.Id;
        var perch = note.PerchPoint;
        if (visible)
        {
            // The new bird lands on the perch with the span already current, so its first walk is on the note.
            SyncPerchSpan();
            switching = true;
            switchTimer?.Stop();
            switchTimer?.Start();
            lastPerch = perch;
        }
        // Not out: the monster swaps silently and the next arrival is the new bird.
        monster.SwitchTo(skin, perch);
        return visible;
    }

    void QueueSwitch()
    {
        if (switchQueued) return;
        switchQueued = true;
        After(SwitchRetryMs, "SwitchRetry", () => { switchQueued = false; ApplyBird(); });
    }

    /// <summary>The new bird has landed: gestures are accepted again, and whatever changed meanwhile is caught
    /// up (a lock that came mid-switch sends it off, a perch that moved is followed).</summary>
    void OnSwitchDone()
    {
        switching = false;
        if (monster.IsOut && !BirdWanted) { monster.Leave(); return; }
        if (NoteVisible) CallBirdIn();
    }

    // ---- 2.2 the perch span ----

    /// <summary>
    /// Sends the walk its room: the x range of the note's visible top edge, 20 DIP in from each end. Cheap and
    /// allocation-free, since it runs on every drag move; SetPerchSpan is called only when the span moved.
    /// </summary>
    void SyncPerchSpan()
    {
        var b = note.Bounds;
        if (!(b.Width > 0) || !double.IsFinite(b.Left) || !double.IsFinite(b.Right)) return;   // never shown yet
        double left = b.Left + SpanInset, right = b.Right - SpanInset;
        // During a fold or a snap the paper is mid-animation while PerchPoint already reports where it ends;
        // the contract is that the perch lies inside the span, so the span stretches to hold it. The fold's
        // closing Changed then sends the final span.
        var perch = note.PerchPoint;
        if (double.IsFinite(perch.X))
        {
            left = Math.Min(left, perch.X);
            right = Math.Max(right, perch.X);
        }
        else if (right < left) left = right = (b.Left + b.Right) / 2;   // a note narrower than the two insets
        if (Math.Abs(left - spanLeft) < 0.5 && Math.Abs(right - spanRight) < 0.5) return;
        spanLeft = left;
        spanRight = right;
        monster.SetPerchSpan(left, right);
    }

    /// <summary>After a show the paper has its rect only once laid out, so the span is sent after that pass.</summary>
    void SyncPerchSpanSoon() =>
        app.Dispatcher.InvokeAsync(() => Guard("SyncPerchSpan", SyncPerchSpan), DispatcherPriority.Loaded);
}
