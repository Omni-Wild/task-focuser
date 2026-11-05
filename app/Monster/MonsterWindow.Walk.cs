using System.Windows;

namespace TaskFocuser.Monster;

// The walk (SPEC-round5 2.2): an idle fidget in which the bird walks along the note's top edge about two
// sprite widths, pauses, and walks back to its perch. It runs on the render hook (smooth at any refresh
// rate, nothing allocated per frame) and only moves the window's Left; the perch itself never changes.
//
// What may interrupt it: everything. A flight (StartMotion, CancelActivity) starts from where the bird
// stands and decides where it ends. MoveTo and the grip (CutWalkHome) put it straight on the perch. An
// act or a press (StopWalk) leaves it standing where it is and marks it stray, and the rest that follows
// the act (EnterRest, ReturnIfStray) brings it home with a short glide first. A hide stops it like
// everything else (StopEverything goes through CancelActivity).
//
// Round 6, the dog's zoomies (SPEC-round6 2.3, started in Dog.cs) are this same walk with zoom set: the
// legs at ZoomSpeed with the run frames, a skid with a puff of dust instead of the pause at each end, and
// one more Out leg to the other end (zoomOtherEnd) before the way home. So they are cut exactly like a walk.
public partial class MonsterWindow
{
    private enum WalkPhase { None, Out, Pause, Back, Return }

    private const double WalkSpeed = 60;                  // DIP per second
    private const double WalkFrameTime = 1.0 / 8;         // Walk1 and Walk2 alternate at 8 fps
    private const double WalkMaxDistance = 2 * SpriteW;   // about 132 DIP
    private const double WalkMinRoom = SpriteW;           // less room than one sprite width: not that way
    private const double WalkEdgeGap = 6;                 // it stops this short of the span's end
    private const double ReturnTime = 0.12;               // the glide home after a cut walk, seconds

    private WalkPhase walkPhase;
    // True when a walk was cut by an act or a press: the bird stands off its perch and goes home when
    // that ends. Cleared whenever something puts the window on the perch or takes it into a flight.
    private bool stray;
    private double walkX, walkTarget, walkSpeed, walkPause, walkFrameClock;
    private bool walkSecondFrame;
    // The zoomies (round 6): the second end still to run to, NaN once only the way home is left.
    private bool zoom;
    private double zoomOtherEnd = double.NaN;
    private int zoomFrame;
    // The x range the feet (the window's centre line) may occupy; NaN until the conductor says.
    private double spanLeft = double.NaN, spanRight = double.NaN;

    public void SetPerchSpan(double left, double right)
    {
        spanLeft = Math.Min(left, right);
        spanRight = Math.Max(left, right);
        // The note shrank under a bird that is out on its walk: it turns home now rather than walk off the edge.
        if (walkPhase is WalkPhase.Out or WalkPhase.Pause
            && (OutsideSpan(walkTarget + WinW / 2) || OutsideSpan(walkX + WinW / 2)
                || (!double.IsNaN(zoomOtherEnd) && OutsideSpan(zoomOtherEnd + WinW / 2))))
            TurnHome();
    }

    /// <summary>The walk fidget; false when there is no span or no room on either side (the fidget is skipped).</summary>
    private bool TryStartWalk()
    {
        if (double.IsNaN(spanLeft)) return false;
        double roomRight = spanRight - perch.X, roomLeft = perch.X - spanLeft;
        int dir = Random.Shared.Next(2) == 0 ? -1 : 1;
        double room = dir > 0 ? roomRight : roomLeft;
        if (room < WalkMinRoom)
        {
            dir = -dir;
            room = dir > 0 ? roomRight : roomLeft;
            if (room < WalkMinRoom) return false;
        }
        double distance = Math.Min(WalkMaxDistance, room - WalkEdgeGap);
        BeginWalk(TopLeftFor(perch).X + dir * distance, WalkSpeed, zoomies: false);
        return true;
    }

    /// <summary>Sets off from the perch toward <paramref name="target"/> (a window left): the walk, or the dog's zoomies.</summary>
    private void BeginWalk(double target, double speed, bool zoomies)
    {
        var home = TopLeftFor(perch);
        Left = home.X;
        Top = home.Y;
        walkX = home.X;
        walkTarget = target;
        zoom = zoomies;
        // The walk owns the frames until it ends: no blink (the fidget timer has just fired).
        blinkTimer.Stop();
        blinking = false;
        idleTimer.Stop();
        BeginLeg(WalkPhase.Out, speed);
        UpdateRenderHook();
    }

    private void BeginLeg(WalkPhase phase, double speed)
    {
        walkPhase = phase;
        walkSpeed = speed;
        walkFrameClock = 0;
        walkSecondFrame = false;
        zoomFrame = 0;
        // Turned to face the way it goes; the glide home keeps the sitting pose, facing right.
        bool glide = phase == WalkPhase.Return;
        Sprite.FlipX = !glide && walkTarget < walkX;
        Sprite.Frame = glide ? Skin.IdleOpen : zoom ? acts.FlapCycle[0] : Skin.Walk1;
    }

    private void TurnHome()
    {
        walkTarget = TopLeftFor(perch).X;
        zoomOtherEnd = double.NaN; // the zoomies too: home is the last leg
        BeginLeg(WalkPhase.Back, zoom ? ZoomSpeed : WalkSpeed);
    }

    /// <summary>One render frame of the walk; dt is capped by the caller.</summary>
    private void StepWalk(double dt)
    {
        if (walkPhase == WalkPhase.Pause)
        {
            walkPause -= dt;
            if (walkPause > 0) return;
            if (!double.IsNaN(zoomOtherEnd))
            {
                // The zoomies' second dash: back past the perch to the other end.
                walkTarget = zoomOtherEnd;
                zoomOtherEnd = double.NaN;
                BeginLeg(WalkPhase.Out, ZoomSpeed);
            }
            else TurnHome();
            return;
        }
        double step = walkSpeed * dt, d = walkTarget - walkX;
        bool reached = Math.Abs(d) <= step;
        walkX = reached ? walkTarget : walkX + Math.Sign(d) * step;
        Left = walkX;
        if (reached)
        {
            if (walkPhase == WalkPhase.Out && zoom)
            {
                // A zoomies turn: she brakes with a puff of dust, still facing the way she ran.
                walkPhase = WalkPhase.Pause;
                walkPause = ZoomSkidTime;
                Sprite.Frame = acts.Skid;
                effects.BurstDust(ZoomSkidDust, new Point(WinW / 2, WinH));
            }
            else if (walkPhase == WalkPhase.Out)
            {
                // The pause at the far end: standing, still facing out, 300 to 800 ms.
                walkPhase = WalkPhase.Pause;
                walkPause = 0.3 + Random.Shared.NextDouble() * 0.5;
                Sprite.Frame = Skin.IdleOpen;
            }
            else
            {
                FinishWalk();
            }
            return;
        }
        if (walkPhase == WalkPhase.Return) return;
        walkFrameClock += dt;
        double frameTime = zoom ? ZoomFrameTime : WalkFrameTime;
        if (walkFrameClock < frameTime) return;
        walkFrameClock -= frameTime;
        if (zoom)
        {
            var run = acts.FlapCycle;
            zoomFrame = (zoomFrame + 1) % run.Length;
            Sprite.Frame = run[zoomFrame];
            return;
        }
        walkSecondFrame = !walkSecondFrame;
        Sprite.Frame = walkSecondFrame ? Skin.Walk2 : Skin.Walk1;
    }

    /// <summary>Home again: exactly on the perch, facing right, resting (blink and fidgets restart).</summary>
    private void FinishWalk()
    {
        walkPhase = WalkPhase.None;
        zoom = false;
        stray = false;
        var home = TopLeftFor(perch);
        Left = home.X;
        Top = home.Y;
        UpdateRenderHook();
        EnterRest();
    }

    /// <summary>Ends a walk on the spot (an act or a press takes over there); true when one was running.</summary>
    private bool StopWalk()
    {
        if (walkPhase == WalkPhase.None) return false;
        walkPhase = WalkPhase.None;
        zoom = false;
        zoomOtherEnd = double.NaN;
        stray = true;
        Sprite.FlipX = false;
        Sprite.Frame = Skin.IdleOpen;
        UpdateRenderHook();
        return true;
    }

    /// <summary>MoveTo and the grip: the walk ends and the bird sits on the perch at once.</summary>
    private void CutWalkHome()
    {
        bool walked = StopWalk();
        stray = false;
        if (motion == Motion.None && !grip.Tumbling)
        {
            var home = TopLeftFor(perch);
            Left = home.X;
            Top = home.Y;
        }
        // The walk had stopped the blink and the fidgets; a plain rest restarts them (a carried bird
        // keeps flapping instead, and a running act rests when it ends).
        if (walked && IsVisible && motion == Motion.None && act == Act.None && loop == Loop.None && !carrying) EnterRest();
    }

    /// <summary>The glide home after a cut walk (EnterRest calls it); false when it is already home.</summary>
    private bool StartReturn()
    {
        stray = false;
        if (!IsVisible) return false;
        walkX = Left;
        walkTarget = TopLeftFor(perch).X;
        Top = perch.Y - WinH;
        if (Math.Abs(walkTarget - walkX) < 0.5)
        {
            Left = walkTarget;
            return false;
        }
        blinkTimer.Stop();
        blinking = false;
        idleTimer.Stop();
        if (OnGround && Math.Abs(walkTarget - walkX) > WalkMaxDistance)
        {
            // Round 6: zoomies cut at the far end of a wide note leave her further out than any walk can;
            // the 120 ms sitting glide would cross that in a blink, so she runs home at the zoomies' pace.
            zoom = true;
            BeginLeg(WalkPhase.Back, ZoomSpeed);
        }
        else BeginLeg(WalkPhase.Return, Math.Abs(walkTarget - walkX) / ReturnTime);
        UpdateRenderHook();
        return true;
    }

    /// <summary>After a press that started nothing: a bird left off its perch goes home.</summary>
    private void ReturnIfStray()
    {
        if (stray && IsVisible && IsOut && motion == Motion.None && loop == Loop.None && act == Act.None
            && !carrying && !grip.Tumbling && !pressed)
            EnterRest();
    }

    private bool OutsideSpan(double feetX) =>
        !double.IsNaN(spanLeft) && (feetX < spanLeft + WalkEdgeGap - 0.5 || feetX > spanRight - WalkEdgeGap + 0.5);
}
