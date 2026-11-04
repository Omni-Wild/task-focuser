using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using TaskFocuser.Core;
using TaskFocuser.Monster;
using TaskFocuser.Views;

namespace TaskFocuser;

/// <summary>
/// The state machine of docs/SPEC.md section 5. It owns no visuals; it only decides which module moves
/// when. Handlers are named after the spec's events so the two read side by side. Lives on the UI thread.
/// The round-2 additions (docs/SPEC-round2.md: flight, sounds, bird stays, bird drag, cucumber) live
/// in Conductor.Round2.cs, round 3's (docs/SPEC-round3.md: the idle sounds, the direct arrival) in
/// Conductor.Round3.cs, so this file stays the round-1 machine with a few hooks.
/// </summary>
public sealed partial class Conductor
{
    // Hand-off delays between modules (SPEC 3.6): long enough for the previous motion to read on screen.
    const int ArmLeadMs = 180;       // the dot's arm animation before the shove (tray Tuck)
    const int ArriveLeadMs = 300;    // the monster's arrival before it shoves a locked note
    const int PopOutMs = 300;        // the note's pop-out (SPEC 3.7: 280 ms) before the dot goes
    // A module that never calls back would otherwise block every gesture for good.
    const int BusyWatchdogMs = 2000;
    const double BesideDotGapX = 24, BesideDotGapY = 12;
    // Half the box kept clear around a dot centre; equals the corner inset of SPEC 3.4.
    const double DotMargin = 28;
    static readonly Size FallbackNoteSize = new(200, 80);

    NoteState state = new();
    INoteWindow note = null!;
    IMonster monster = null!;
    IDot dot = null!;
    ICloseTarget closeTarget = null!;
    ITray tray = null!;

    bool started, ready, tornDown;
    bool busy;                       // a dock, pop or vanish sequence is running; gestures are dropped, never queued
    int sequence;                    // identifies the running sequence for its watchdog
    bool noteDragging, dotDragging;  // only a drag whose start we accepted gets its moves and end
    bool dotHot, closeHot;           // last value sent, so SetHot is called on changes only
    Point dotArmedAt, closeShownAt;  // cursor where the dot / close target was last placed (monitor tracking)
    Point lastPerch = new(double.NaN, double.NaN);
    readonly List<string[]> earlyArgs = new();
    readonly Application app;

    public Conductor(Application app) => this.app = app;

    // ---- startup and exit (SPEC 3.10) ----

    /// <summary>Called once this process is the primary instance.</summary>
    public void Start(string[] startupArgs)
    {
        state = StateStore.Load();
        state.Normalize();
        // The Run key is the truth, read together with Windows' own startup switch (Task Manager, Settings);
        // an entry left pointing at an exe that was moved or renamed since is repointed here first.
        StartupRegistry.RepairIfMoved();
        state.StartWithWindows = StartupRegistry.IsEnabled();

        var noteWindow = new NoteWindow(state);
        var monsterWindow = new MonsterWindow();
        note = noteWindow;
        monster = monsterWindow;
        dot = new DotWindow();
        closeTarget = new CloseTargetWindow();
        tray = new TrayIcon();
        started = true;
        OwnMonsterByNote(noteWindow, monsterWindow);
        Subscribe();
        SubscribeDisplayChanges();
        StartRound2();
        StartRound4();   // round 4 (Conductor.Round4.cs): the typing chain
        StartRound5();   // round 5 (Conductor.Round5.cs): the picked bird's skin, before the first arrival

        Guard("EnterSavedMode", EnterSavedMode);
        ready = true;
        HandleArgs(startupArgs, forwarded: false);
        foreach (var args in earlyArgs) HandleArgs(args, forwarded: true);
        earlyArgs.Clear();
    }

    void EnterSavedMode()
    {
        switch (state.Mode)
        {
            case Mode.Note:
                ShowNoteAtSavedPosition();
                break;
            case Mode.Dot:
                // A saved spot on a monitor unplugged since the last run is not clamped to that edge of the
                // nearest screen (it would rest at a far edge and be remembered there): the home corner instead.
                var saved = new Point(state.DotX, state.DotY);
                dot.Rest(state.HasDotPosition && Monitors.IsOnScreen(saved, DotMargin) ? ClampDotCentre(saved) : HomeCorner());
                StoreDotCentre();
                break;
            case Mode.Closed:
                break;
        }
        Commit();
    }

    static void OwnMonsterByNote(Window noteWindow, Window monsterWindow)
    {
        // Owned windows stay above their owner, so the perched monster never slips behind the note.
        // WPF refuses an owner without an HWND, and in Dot or Closed mode the note is shown only later.
        void Attach()
        {
            if (monsterWindow.Owner is not null) return;
            try { monsterWindow.Owner = noteWindow; }
            catch (InvalidOperationException ex) { Debug.WriteLine($"Task Focuser: monster owner not set: {ex.Message}"); }
        }
        if (new WindowInteropHelper(noteWindow).Handle != IntPtr.Zero) Attach();
        else noteWindow.SourceInitialized += (_, _) => Attach();
    }

    void Subscribe()
    {
        // The drag events fire on every mouse move; these lambdas capture only `this`, so they are
        // allocated once here and the drag path allocates nothing.
        // A press on the note itself ends any bird drag whose end we never heard (self-healing); while a
        // bird drag runs, the note's own move and end events are not ours (the bird holds the capture).
        note.DragStarted += p => { try { birdDragging = false; OnNoteDragStarted(p); } catch (Exception ex) { Fail("note.DragStarted", ex); } };
        note.DragMoved += p => { try { if (!birdDragging) OnNoteDragMoved(p); } catch (Exception ex) { Fail("note.DragMoved", ex); } };
        note.DragEnded += p => { try { if (!birdDragging) OnNoteDragEnded(p); } catch (Exception ex) { Fail("note.DragEnded", ex); } };
        note.LockedChanged += l => { try { OnNoteLockedChanged(l); } catch (Exception ex) { Fail("note.LockedChanged", ex); } };
        note.Changed += () => Guard("note.Changed", OnNoteChanged);

        dot.DoubleClicked += () => Guard("dot.DoubleClicked", OnDotDoubleClicked);
        dot.DragStarted += p => { try { OnDotDragStarted(p); } catch (Exception ex) { Fail("dot.DragStarted", ex); } };
        dot.DragMoved += p => { try { OnDotDragMoved(p); } catch (Exception ex) { Fail("dot.DragMoved", ex); } };
        dot.DragEnded += p => { try { OnDotDragEnded(p); } catch (Exception ex) { Fail("dot.DragEnded", ex); } };

        tray.ShowNote += () => Guard("tray.ShowNote", OnTrayShowNote);
        tray.Tuck += () => Guard("tray.Tuck", OnTrayTuck);
        tray.StartDot += () => Guard("tray.StartDot", OnTrayStartDot);
        tray.ToggleLock += () => Guard("tray.ToggleLock", OnTrayToggleLock);
        tray.ToggleStartWithWindows += () => Guard("tray.ToggleStartWithWindows", OnTrayToggleStartWithWindows);
        tray.Quit += () => Guard("tray.Quit", OnTrayQuit);
    }

    // ---- Note mode ----

    void OnNoteDragStarted(Point p)
    {
        // Round 5: a drag that starts mid-switch is refused until the new bird has landed (2.4).
        if (busy || switching || state.Mode != Mode.Note) return;
        noteDragging = true;
        if (!monster.IsOut) ArriveAt(note.PerchPoint);
        monster.SetCarrying(true);
        ArmDotAt(p);
    }

    void OnNoteDragMoved(Point p)
    {
        if (!noteDragging) return;
        MoveMonster(note.PerchPoint);
        if (!Monitors.SameMonitor(dotArmedAt, p)) ArmDotAt(p);
        bool hot = dot.Contains(p);
        if (hot != dotHot) { dotHot = hot; dot.SetHot(hot); }
    }

    void OnNoteDragEnded(Point p)
    {
        if (!noteDragging) return;
        noteDragging = false;
        monster.SetCarrying(false);
        if (dot.Contains(p)) { Dock(armDot: false); return; }
        dot.Disarm();
        dotHot = false;
        StateStore.Save(state);
        // Round 2 (2.6): with BirdStays a locked drag end keeps the bird perched.
        if (state.Locked && !state.BirdStays) monster.Leave(); else MoveMonster(note.PerchPoint);
    }

    void OnNoteLockedChanged(bool locked)
    {
        if (locked) EndChain();   // round 4: a locked note takes no keys, so its typing chain is over
        // During a drag the monster is busy carrying, and during a sequence it is shoving;
        // the drag end or the sequence decides where it goes.
        if (state.Mode == Mode.Note && !busy && !noteDragging)
        {
            // Round 2 (2.6): with BirdStays the bird keeps its perch; the fold's Changed flies it to the new one.
            // Round 3 (2.2): the perch of the unfolded note is known now, so the bird is called at once and
            // flies straight to its final spot instead of waiting a layout pass.
            if (locked) { if (!state.BirdStays) monster.Leave(); }
            else ArriveNow(followLayout: false);
        }
        if (state.Mode == Mode.Note) SyncPerchSpan();   // round 5: the fold's start; its closing Changed sends the end
        Commit();
    }

    void OnNoteChanged()
    {
        StateStore.Save(state);
        // Round 2: the bar's toggles and sliders arrive here too (2.5, 2.6, 2.7).
        bool placed = OnSettingsChanged();
        // Round 5: a bird picked in the bar switches (2.4); a visible switch places the bird itself.
        if (OnBirdPicked() || placed) return;
        if (state.Mode == Mode.Note) SyncPerchSpan();   // round 5: typing, a resize or the fold's end moved the edge
        // The note grows to the left while he types (and resizes on lock and unlock), which carries the
        // perch with it; a big jump is flown (2.1), a small one followed.
        if (state.Mode == Mode.Note && BirdWanted && !busy && !noteDragging && !switching && monster.IsOut)
            SettleOn(note.PerchPoint);
    }

    void OnTrayTuck()
    {
        if (busy || switching || noteDragging || state.Mode != Mode.Note) return;   // round 5: not mid-switch either
        Dock(armDot: true);
    }

    /// <summary>SPEC 3.6: the monster shoves, the note squashes into the dot, the dot catches.</summary>
    void Dock(bool armDot)
    {
        BeginSequence();
        int lead = 0;
        if (armDot)
        {
            dot.Arm(DotSpotOn(Monitors.WorkAreaAt(CentreOf(note.Bounds))));
            lead = ArmLeadMs;
        }
        // A locked note tucked from the tray has no monster yet; let it visibly arrive first.
        if (!monster.IsOut)
        {
            ArriveAt(note.PerchPoint);
            lead = Math.Max(lead, ArriveLeadMs);
        }
        After(lead, "Dock.Shove", ShoveIntoDot);
    }

    void ShoveIntoDot()
    {
        var target = dot.Centre;
        monster.Shove(note.PerchPoint, target, Callback("Dock.MonsterDone", monster.Leave));
        note.SquashInto(target, Callback("Dock.NoteDone", () =>
            dot.Dock(target, Callback("Dock.DotDone", FinishDock))));
    }

    void FinishDock()
    {
        state.Mode = Mode.Dot;
        StoreDotCentre();
        dotHot = false;
        EndSequence();
        Commit();
    }

    // ---- Dot mode ----

    void OnDotDoubleClicked()
    {
        if (busy || dotDragging || state.Mode != Mode.Dot) return;
        PopOut();
    }

    /// <summary>SPEC 3.7: the dot spits the note out beside itself.</summary>
    void PopOut()
    {
        BeginSequence();
        dot.Pop(Callback("Pop", () =>
        {
            var centre = dot.Centre;
            var topLeft = BesideDot(centre);
            state.X = topLeft.X;
            state.Y = topLeft.Y;
            note.PopOut(centre, topLeft);
            // SPEC 3.7: the dot stays until the note is out; hiding it at the peak would cut its settle.
            After(PopOutMs, "Pop.HideDot", () =>
            {
                // A tuck started within these 300 ms has re-armed the dot; hiding it then would break that dock.
                if (state.Mode == Mode.Note && !busy && !noteDragging) dot.HideDot();
                if (state.Mode == Mode.Note) SyncPerchSpan();   // round 5: the paper has grown into place
            });
            state.Mode = Mode.Note;
            EndSequence();
            ArriveNow(followLayout: true);
            Commit();
        }));
    }

    void OnDotDragStarted(Point p)
    {
        if (busy || state.Mode != Mode.Dot) return;
        dotDragging = true;
        ShowCloseTargetAt(p);
    }

    void OnDotDragMoved(Point p)
    {
        if (!dotDragging) return;
        if (!Monitors.SameMonitor(closeShownAt, p)) ShowCloseTargetAt(p);
        bool hot = closeTarget.Contains(p);
        if (hot != closeHot) { closeHot = hot; closeTarget.SetHot(hot); }
    }

    void OnDotDragEnded(Point p)
    {
        if (!dotDragging) return;
        dotDragging = false;
        if (closeTarget.Contains(p))
        {
            BeginSequence();
            dot.Vanish(Callback("Vanish", () =>
            {
                HideCloseTarget();
                state.Mode = Mode.Closed;
                EndSequence();
                Commit();
            }));
            return;
        }
        HideCloseTarget();
        StoreDotCentre();
        Commit();
    }

    /// <summary>
    /// A monitor unplugged (a laptop undocked) or a resolution change while the dot rests: a dot left on a
    /// screen that is gone would be unreachable, so it goes to its spot on the nearest one. Run once the
    /// change has settled, after Windows has moved its own windows about.
    /// </summary>
    void SubscribeDisplayChanges()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
            app.Dispatcher.InvokeAsync(() => Guard("DisplayChanged", OnDisplayChanged), DispatcherPriority.ContextIdle);
    }

    void OnDisplayChanged()
    {
        if (tornDown || busy || dotDragging || state.Mode != Mode.Dot) return;
        var c = dot.Centre;
        if (Monitors.IsOnScreen(c, DotMargin)) return;
        dot.Rest(DotSpotOn(Monitors.WorkAreaAt(c)));
        StoreDotCentre();
        Commit();
    }

    // ---- Closed mode and the tray (SPEC 3.8) ----

    void OnTrayShowNote()
    {
        if (busy) return;
        switch (state.Mode)
        {
            case Mode.Note:
                // A note left on an unplugged monitor, or hidden by a cut-short dock, needs a re-clamp and a show.
                if (NoteVisible) { note.BringToFront(); ArriveNow(followLayout: false); }   // a bird that should be there comes back
                else ShowNoteAtSavedPosition();
                break;
            case Mode.Dot:
                if (!dotDragging) PopOut();
                break;
            case Mode.Closed:
                state.Mode = Mode.Note;
                ShowNoteAtSavedPosition();
                Commit();
                break;
        }
    }

    void OnTrayStartDot()
    {
        if (busy || state.Mode != Mode.Closed) return;
        dot.Rest(HomeCorner());
        state.Mode = Mode.Dot;
        StoreDotCentre();
        Commit();
    }

    void OnTrayToggleLock() => SetLocked(!state.Locked);

    void SetLocked(bool locked)
    {
        if (busy) return;
        // Always through the note, even while it is tucked or closed: it owns the read-only flag, the
        // hit-testing and the no-activate style, and a hidden window skips the bar animation by itself.
        // It raises LockedChanged, which moves the monster (in Note mode) and saves.
        note.SetLocked(locked);
    }

    // Touches no window, so it is honoured even while a sequence runs.
    void OnTrayToggleStartWithWindows()
    {
        StartupRegistry.Set(!StartupRegistry.IsEnabled());
        state.StartWithWindows = StartupRegistry.IsEnabled();
        Commit();
    }

    void OnTrayQuit()
    {
        Teardown();
        app.Shutdown();
    }

    /// <summary>--close: straight to Closed from Note or Dot, no animation (scripts and tests).</summary>
    void CloseNow()
    {
        if (busy || state.Mode == Mode.Closed) return;
        EndChain();   // round 4: a hidden note has no typing chain
        noteDragging = dotDragging = false;
        note.HideNote();
        dot.HideDot();
        HideCloseTarget();
        if (monster.IsOut) monster.Leave();
        state.Mode = Mode.Closed;
        Commit();
    }

    // ---- forwarded command line (SPEC 3.10) ----

    public void OnForwardedArgs(string[] args)
    {
        // The pipe listener may call from a worker thread; every window lives on the UI thread.
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.InvokeAsync(() => OnForwardedArgs(args)); return; }
        if (!ready) { earlyArgs.Add(args); return; }
        HandleArgs(args, forwarded: true);
    }

    void HandleArgs(string[] args, bool forwarded)
    {
        // A bare second launch means "where is my note?".
        if (forwarded && args.Length == 0) { Guard("--show", OnTrayShowNote); return; }
        foreach (var arg in args)
        {
            switch (arg.Trim().ToLowerInvariant())
            {
                case "--show": Guard(arg, OnTrayShowNote); break;
                case "--lock": Guard(arg, () => SetLocked(true)); break;
                case "--unlock": Guard(arg, () => SetLocked(false)); break;
                case "--dot": Guard(arg, OnTrayTuck); break;
                case "--close": Guard(arg, CloseNow); break;
                case "--start": Guard(arg, OnTrayStartDot); break;   // the tray's "Start the dot", for scripts
                case "--quit": Guard(arg, OnTrayQuit); return;
            }
        }
    }

    // ---- helpers ----

    void ShowNoteAtSavedPosition()
    {
        var rect = new Rect(new Point(state.X, state.Y), NoteSize());
        var clamped = Monitors.ClampInto(rect, VisibleWorkArea(rect));
        state.X = clamped.X;
        state.Y = clamped.Y;
        note.ShowNote();
        ArriveNow(followLayout: true);
        SyncPerchSpanSoon();   // round 5: the span once the shown note is laid out, bird or not
    }

    // ArriveNow, ArriveSoon and CallBirdIn (calling the bird in after a show or an unlock) live in Conductor.Round3.cs.

    void ArriveAt(Point perch)
    {
        ApplyBirdOpacity();   // 2.7: before every arrival, so it flies in at the right opacity
        SyncPerchSpan();      // round 5: the walk's room before the bird lands
        monster.ArriveAt(perch);
        lastPerch = perch;
        arriveStarted = Stopwatch.GetTimestamp();
    }

    void MoveMonster(Point perch)
    {
        SyncPerchSpan();   // round 5: every drag move and perch follow; sends only when the span moved
        // Only real moves: MoveTo is instant and would cut short an arrival still in flight.
        if (Math.Abs(perch.X - lastPerch.X) < 0.5 && Math.Abs(perch.Y - lastPerch.Y) < 0.5) return;
        monster.MoveTo(perch);
        lastPerch = perch;
    }

    void ArmDotAt(Point cursor)
    {
        dot.Arm(DotSpotOn(Monitors.WorkAreaAt(cursor)));
        dotArmedAt = cursor;
        dotHot = false;
    }

    void ShowCloseTargetAt(Point cursor)
    {
        closeTarget.ShowAt(Monitors.BottomRightCorner(Monitors.WorkAreaAt(cursor)));
        closeShownAt = cursor;
        closeHot = false;
    }

    void HideCloseTarget() { closeTarget.HideTarget(); closeHot = false; }

    /// <summary>SPEC 3.7: the note's top-right corner 24 left of and 12 below the dot centre, kept on screen.</summary>
    Point BesideDot(Point centre)
    {
        var size = NoteSize();
        var topLeft = new Point(centre.X - BesideDotGapX - size.Width, centre.Y + BesideDotGapY);
        return Monitors.ClampInto(new Rect(topLeft, size), Monitors.WorkAreaAt(centre)).TopLeft;
    }

    // A note never shown yet reports no size; this is about the locked one-liner.
    Size NoteSize()
    {
        var b = note.Bounds;
        return b.Width > 0 && b.Height > 0 ? b.Size : FallbackNoteSize;
    }

    /// <summary>"Its original place": the dot's remembered spot on the monitor the note last lived on.</summary>
    Point HomeCorner() => DotSpotOn(Monitors.WorkAreaAt(new Point(state.X, state.Y)));

    /// <summary>
    /// The dot remembers its spot (his ask after round 5): where he last dropped it, carried from monitor to
    /// monitor as an offset from the work area's top-right corner, so a dot parked top-middle on one screen is
    /// top-middle on the other. No spot yet = the corner of round 1.
    /// </summary>
    Point DotSpotOn(Rect area)
    {
        var corner = Monitors.TopRightCorner(area);
        if (!state.HasDotPosition) return corner;
        var remembered = new Point(state.DotX, state.DotY);
        // A spot on a monitor that is gone (a laptop undocked) has no corner to be measured from: against the
        // nearest screen's corner its offset turns into a jump to the far edge. The plain corner instead.
        if (!Monitors.IsOnScreen(remembered, DotMargin)) return corner;
        var remCorner = Monitors.TopRightCorner(Monitors.WorkAreaAt(remembered));
        var spot = new Point(corner.X + (remembered.X - remCorner.X), corner.Y + (remembered.Y - remCorner.Y));
        var box = new Rect(spot.X - DotMargin, spot.Y - DotMargin, 2 * DotMargin, 2 * DotMargin);
        var clamped = Monitors.ClampInto(box, area);
        return new Point(clamped.X + DotMargin, clamped.Y + DotMargin);
    }

    Point ClampDotCentre(Point c)
    {
        var box = new Rect(c.X - DotMargin, c.Y - DotMargin, 2 * DotMargin, 2 * DotMargin);
        var clamped = Monitors.ClampInto(box, VisibleWorkArea(box));
        return new Point(clamped.X + DotMargin, clamped.Y + DotMargin);
    }

    // The one call into B1's VisibleWorkAreaFor (the work area of the monitor nearest r), so a monitor
    // unplugged since the last run still yields a place on screen. If it is missing at integration,
    // fall back to: Monitors.WorkAreaAt(CentreOf(r)).
    static Rect VisibleWorkArea(Rect r) => Monitors.VisibleWorkAreaFor(r);

    static Point CentreOf(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    void StoreDotCentre()
    {
        var c = dot.Centre;
        state.DotX = c.X;
        state.DotY = c.Y;
    }

    void Commit()
    {
        StateStore.Save(state);
        tray.Update(state.Mode, state.Locked, state.StartWithWindows);
    }

    void BeginSequence()
    {
        busy = true;
        EndChain();   // round 4: a dock (the tuck), pop or vanish ends the typing chain
        int mine = ++sequence;
        After(BusyWatchdogMs, "Watchdog", () =>
        {
            if (!busy || sequence != mine) return;
            Debug.WriteLine("Task Focuser: a sequence never called back; accepting gestures again.");
            busy = false;
        });
    }

    void EndSequence() => busy = false;

    void After(int ms, string what, Action action)
    {
        if (ms <= 0) { Guard(what, action); return; }
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); Guard(what, action); };
        timer.Start();
    }

    Action Callback(string what, Action action) => () => Guard(what, action);

    void Guard(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { Fail(what, ex); }
    }

    // One misbehaving module must not take the note down; the next gesture starts clean.
    void Fail(string what, Exception ex)
    {
        Debug.WriteLine($"Task Focuser: {what} failed: {ex}");
        busy = false;
    }

    public void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // The net under module code we do not call directly (their own timers and storyboards).
        Fail("dispatcher", e.Exception);
        e.Handled = true;
    }

    public void Teardown()
    {
        if (!started || tornDown) return;
        tornDown = true;
        idleTimer?.Stop();
        chainTimer?.Stop();
        switchTimer?.Stop();
        try { StateStore.Flush(state); } catch (Exception ex) { Debug.WriteLine($"Task Focuser: flush failed: {ex}"); }
        try { tray.Dispose(); } catch (Exception ex) { Debug.WriteLine($"Task Focuser: tray dispose failed: {ex}"); }
    }
}
