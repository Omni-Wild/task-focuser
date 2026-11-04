using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TaskFocuser.Monster;

/// <summary>What one physics step decided (SPEC-round2 2.8).</summary>
internal enum GripEvent { None, Lost, Regained }

/// <summary>
/// The grip of 2.8, as plain numbers so the window only has to apply the result: the perch speed
/// estimate, the decision to let go, and the damped spring the tumbling bird hangs on until it can
/// fly back. All positions are the bird window's top-left in screen DIPs. Value fields only,
/// so a step allocates nothing.
/// </summary>
internal sealed class Grip
{
    // Hysteresis: it only lets go above LoseSpeed and only comes back after the note stays under
    // CalmSpeed for CalmMs, so a drag hovering around one threshold does not make it flicker.
    public const double FastSpeed = 900;    // above this the riding bird flaps harder (still holding on)
    public const double LoseSpeed = 1400;
    public const double CalmSpeed = 600;
    private const double CalmMs = 120;

    private const double SmoothTau = 0.060;  // seconds; the velocity estimate's time constant

    // A low natural frequency (about 1.4 Hz) and a bit under critical damping: it trails the note,
    // swings a little when the note stops, and does not oscillate around it like a rubber band.
    private const double Stiffness = 80;
    private const double Damping = 13;
    private const double Sag = 40;           // gravity-ish: it hangs below the perch while it tumbles
    private const double MaxLag = 240;       // never further behind than this, so it cannot get lost off screen
    private const double MomentumKept = 0.5; // it was holding on, so it starts with part of the note's speed

    private Point lastTarget;
    private Vector perchVel;
    private Point pos;
    private Vector vel;
    private double calmMs;

    /// <summary>The smoothed perch speed, DIP per second.</summary>
    public double Speed { get; private set; }

    /// <summary>True while it has let go and the spring drives the window.</summary>
    public bool Tumbling { get; private set; }

    /// <summary>Where the spring has the window now (valid while tumbling).</summary>
    public Point Position => pos;

    /// <summary>Starts a fresh estimate at this target, holding on (drag start, landing, hide).</summary>
    public void Reset(Point target)
    {
        lastTarget = target;
        perchVel = default;
        Speed = 0;
        Tumbling = false;
        calmMs = 0;
        vel = default;
    }

    /// <summary>Takes it out of the tumble without a decision (another motion takes over).</summary>
    public void Release()
    {
        Tumbling = false;
        calmMs = 0;
    }

    /// <summary>
    /// One render frame. <paramref name="target"/> is where the window would sit perched,
    /// <paramref name="window"/> where it sits now; <paramref name="allowLose"/> is false while a
    /// flight runs or the bird itself is the drag handle.
    /// </summary>
    public GripEvent Step(Point target, Point window, double dt, bool allowLose)
    {
        if (dt <= 0) return GripEvent.None;

        // The velocity comes from the target per frame, not per MoveTo: when the mouse stops, no
        // MoveTo arrives, and a per-call estimate would keep the last speed forever.
        double ix = (target.X - lastTarget.X) / dt;
        double iy = (target.Y - lastTarget.Y) / dt;
        lastTarget = target;
        double a = 1 - Math.Exp(-dt / SmoothTau);
        perchVel = new Vector(perchVel.X + (ix - perchVel.X) * a, perchVel.Y + (iy - perchVel.Y) * a);
        Speed = perchVel.Length;

        if (!Tumbling)
        {
            if (!allowLose || Speed <= LoseSpeed) return GripEvent.None;
            Tumbling = true;
            calmMs = 0;
            pos = window;
            vel = perchVel * MomentumKept;
            return GripEvent.Lost;
        }

        // Semi-implicit Euler: velocity first, then position, which stays stable at these stiffnesses
        // for any frame time under the 50 ms the caller caps dt at.
        double gx = target.X, gy = target.Y + Sag;
        vel = new Vector(
            vel.X + (Stiffness * (gx - pos.X) - Damping * vel.X) * dt,
            vel.Y + (Stiffness * (gy - pos.Y) - Damping * vel.Y) * dt);
        double px = pos.X + vel.X * dt, py = pos.Y + vel.Y * dt;
        double dx = px - gx, dy = py - gy;
        double d = Math.Sqrt(dx * dx + dy * dy);
        if (d > MaxLag)
        {
            double k = MaxLag / d;
            px = gx + dx * k;
            py = gy + dy * k;
        }
        pos = new Point(px, py);

        calmMs = Speed < CalmSpeed ? calmMs + dt * 1000 : 0;
        if (calmMs < CalmMs) return GripEvent.None;
        Tumbling = false;
        calmMs = 0;
        return GripEvent.Regained;
    }
}

// The window's side of the grip, the bird as a drag handle for the note (SPEC-round2 2.8, 2.10),
// the frame loops, and the shove with its bump.
public partial class MonsterWindow
{
    private const double ClickSlop = 3;

    private bool pressed, dragging, selfDrag;
    private Point pressScreen, lastScreen;

    /// <summary>One render hook for the grip, the effects and the walk, present only while one of them needs frames.</summary>
    private void UpdateRenderHook()
    {
        bool need = IsVisible && (carrying || grip.Tumbling || effects.Active || walkPhase != WalkPhase.None);
        if (need == renderHooked) return;
        renderHooked = need;
        if (need)
        {
            renderPrimed = false;
            CompositionTarget.Rendering += renderHandler;
        }
        else
        {
            CompositionTarget.Rendering -= renderHandler;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (!renderPrimed)
        {
            renderPrimed = true;
            lastRender = now;
            return;
        }
        double dt = (now - lastRender).TotalSeconds;
        if (dt <= 0) return; // Rendering can fire more than once for the same frame
        lastRender = now;
        // A stall (a busy UI thread) must not fling the spring or teleport the dust.
        if (dt > 0.05) dt = 0.05;
        if (carrying || grip.Tumbling) StepGrip(dt);
        if (effects.Active) effects.Tick(dt);
        if (walkPhase != WalkPhase.None) StepWalk(dt);
        UpdateRenderHook();
    }

    private void StepGrip(double dt)
    {
        // It only lets go while plainly riding: not mid-flight, and not while he holds the bird itself
        // (the bird is the handle then, and a handle that falls off his hand would be nonsense).
        bool allowLose = carrying && IsOut && motion == Motion.None && !selfDrag;
        switch (grip.Step(TopLeftFor(perch), new Point(Left, Top), dt, allowLose))
        {
            case GripEvent.Lost:
                StartTumble();
                break;
            case GripEvent.Regained:
                StartCatch();
                return;
        }
        if (grip.Tumbling)
        {
            var p = grip.Position;
            Left = p.X;
            Top = p.Y;
        }
        else if (loop == Loop.Flap && motion == Motion.None)
        {
            // Between the calm and the losing speed it still holds on, flapping harder for balance.
            bool fast = grip.Speed > Grip.FastSpeed;
            if (fast != fastFlap)
            {
                fastFlap = fast;
                frameTimer.Interval = fast ? FastFlapTick : FlapTick;
            }
        }
    }

    private void StartTumble()
    {
        StopAct();
        idleTimer.Stop();
        blinkTimer.Stop();
        blinking = false;
        Sprite.FlipX = false;
        loop = Loop.Tumble;
        flapIndex = 0;
        Sprite.Frame = Skin.Tumble1;
        frameTimer.Interval = TumbleTick;
        frameTimer.Start();
    }

    /// <summary>Flies back to the perch from wherever the tumble left it; the landing carries on or sits.</summary>
    private void StartCatch()
    {
        grip.Release();
        if (!IsVisible) return;
        StartMotion(Motion.Fly, catchAnim, new Point(Left, Top), default);
        StartFlap();
    }

    // ---- the bird as a handle -----------------------------------------------------------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (pressed || !IsVisible || !IsOut || motion != Motion.None || grip.Tumbling) return;
        var w = e.GetPosition(this);
        // Only the bird itself: a heart or a dust speck is not a handle.
        if (w.X < SpriteX || w.X > SpriteX + SpriteW || w.Y < SpriteY || w.Y > SpriteY + SpriteH) return;
        pressScreen = new Point(Left + w.X, Top + w.Y);
        lastScreen = pressScreen;
        pressed = true;
        dragging = false;
        if (!CaptureMouse())
        {
            pressed = false;
            return;
        }
        // A press cuts a walk: the click or the drag acts from where the bird stands.
        StopWalk();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!pressed) return;
        var w = e.GetPosition(this);
        // Screen DIPs: the window moves under the cursor during the drag (the conductor's MoveTo).
        var p = new Point(Left + w.X, Top + w.Y);
        lastScreen = p;
        if (!dragging)
        {
            double dx = p.X - pressScreen.X, dy = p.Y - pressScreen.Y;
            if (dx * dx + dy * dy <= ClickSlop * ClickSlop) return;
            dragging = true;
            selfDrag = true;
            StopAct(); // a drag that starts mid-eat ends the meal
            // The press point first, then the cursor: the note keeps the offset he grabbed it at.
            DragStarted?.Invoke(pressScreen);
            if (!pressed) return; // the handler ended it (a hide)
            // The conductor refused the drag (a dock lead, the end of a switch): nothing carries the bird,
            // so it sits again instead of keeping a flap burst running or freezing in a cut act's frame.
            if (!carrying && motion == Motion.None && !grip.Tumbling) EnterRest();
        }
        DragMoved?.Invoke(p);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!pressed) return;
        var w = e.GetPosition(this);
        var p = new Point(Left + w.X, Top + w.Y);
        bool wasDrag = dragging;
        // Flags first: releasing capture raises LostMouseCapture, which must find nothing to end.
        ClearPress();
        if (IsMouseCaptured) ReleaseMouseCapture();
        e.Handled = true;
        if (wasDrag) DragEnded?.Invoke(p);
        else OnClick();
        // A press that cut a walk and started nothing (a drag the conductor did not take, a click
        // with no act) still walks the bird home.
        ReturnIfStray();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        AbortPress();
    }

    /// <summary>The press ends without a release (capture lost, the bird hidden): a drag still gets its end.</summary>
    private void AbortPress()
    {
        if (!pressed) return;
        bool wasDrag = dragging;
        ClearPress();
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (wasDrag) DragEnded?.Invoke(lastScreen);
        ReturnIfStray();
    }

    private void ClearPress()
    {
        pressed = false;
        dragging = false;
        selfDrag = false;
    }

    // ---- frame loops --------------------------------------------------------------------------

    private void StartFlap()
    {
        if (!IsVisible) return;
        blinkTimer.Stop();
        blinking = false;
        Sprite.FlipX = false;
        if (loop == Loop.Flap) return;
        loop = Loop.Flap;
        flapIndex = 0;
        fastFlap = false;
        Sprite.Frame = acts.FlapCycle[0];
        frameTimer.Interval = FlapTick;
        frameTimer.Start();
    }

    // ---- the shove (its public entry beside the bump loop it ends in) --------------------------

    public void Shove(Point from, Point target, Action onDone)
    {
        IsOut = true;
        CancelActivity();
        Opacity = baseOpacity;

        Point start;
        if (IsVisible && motion is not (Motion.FlyOff or Motion.Away))
        {
            start = new Point(Left, Top);
        }
        else
        {
            // Docked from the tray while locked, or docked while it sulks off screen: it pops up at
            // the perch and shoves from there.
            start = TopLeftFor(from);
            Left = start.X;
            Top = start.Y;
            if (!IsVisible) Show();
        }

        var body = new Point(start.X + WinW / 2, start.Y + SpriteY + BodyCentreY);
        Vector toTarget = target - body;
        double dist = toTarget.Length;
        shoveDir = dist > 0.5 ? toTarget / dist : new Vector(1, 0);
        double reach = Math.Max(0, dist - (BodyHalfWidth + ShoveGap));
        var end = start + shoveDir * reach;

        Sprite.FlipX = target.X < body.X;
        Sprite.Frame = Skin.ShoveA;
        shoveDone = onDone;
        StartMotion(Motion.Shove, shoveAnim, start, end);
        UpdateRenderHook();
    }

    private void StartBump()
    {
        loop = Loop.Bump;
        bumpTicks = 0;
        Sprite.Frame = Skin.ShoveB;
        Left += shoveDir.X * BumpNudge;
        Top += shoveDir.Y * BumpNudge;
        frameTimer.Interval = BumpTick;
        frameTimer.Start();
    }

    private void StopLoop()
    {
        frameTimer.Stop();
        loop = Loop.None;
    }

    private void OnFrameTick(object? sender, EventArgs e)
    {
        switch (loop)
        {
            case Loop.Flap:
            {
                var cycle = acts.FlapCycle;
                flapIndex = (flapIndex + 1) % cycle.Length;
                Sprite.Frame = cycle[flapIndex];
                return;
            }
            case Loop.Tumble:
                flapIndex ^= 1;
                Sprite.Frame = flapIndex == 0 ? Skin.Tumble1 : Skin.Tumble2;
                return;
            case Loop.Bump:
                if (++bumpTicks == 1)
                {
                    Left -= shoveDir.X * BumpNudge;
                    Top -= shoveDir.Y * BumpNudge;
                    return;
                }
                StopLoop();
                var done = shoveDone;
                shoveDone = null;
                done?.Invoke();
                return;
        }
        frameTimer.Stop();
    }
}
