using System.Windows;
using System.Windows.Media.Animation;
using TaskFocuser.Core;

namespace TaskFocuser.Monster;

// Round 6, the ground locomotion (SPEC-round6 2.2): where a bird takes the air, the dog runs along the
// perch line with the run frames, which are the flap cycle (so StartFlap animates them). Everything here
// acts only when the skin's Locomotion is Ground (OnGround); the helpers that say where a path starts and
// ends, and the landing dust at its end, serve both, and for a bird they give exactly the round-1 points.
//
// The paths: the arrival runs in from off-screen at the side of the perch's monitor nearer the perch, feet
// on the perch line, at one pace for most of the 450 ms, then a short skid onto the spot on the Skid frame
// (a cubic slow-down over SkidReach DIP); the leave is a little jump-start and a sprint off to the nearer
// side edge, fading over its second half; FlyTo and the catch run with no lift; the shove is shared.
//
// The chase (StepChase, on the render hook the grip already runs while carrying): she faces the way the
// note moves, with a dead band so a jittery hand does not flip her; her window trails the spot opposite the
// motion, more the faster, and the grip's smoothed speed eases it back as the drag slows; held still, she
// stands and wags instead of running on the spot. MoveTo adds the trail, so the hot path stays two
// assignments, and nothing here allocates per frame.
public partial class MonsterWindow
{
    // The run's beat: 12 fps, 18 above Grip.FastSpeed (a bird flaps at 14 and 20); read through acts.
    private static readonly TimeSpan RunTick = TimeSpan.FromMilliseconds(1000.0 / 12);
    private static readonly TimeSpan FastRunTick = TimeSpan.FromMilliseconds(1000.0 / 18);

    private const double RunShare = 0.75;    // of the arrival's 450 ms at a run (about 340 ms), the rest the skid
    private const double SkidReach = 24;     // DIP: how far a long run slides on the Skid frame ...
    // ... and at most this share of a shorter one (a turnaround): 0.1 is where the skid's cubic starts at
    // exactly the run's pace (3 skid / 0.25 = 0.9 dist / 0.75), so a short run never lurches into its skid.
    private const double SkidMaxShare = 0.1;
    private const double JumpHeight = 6;     // the leave's jump-start, lower than a bird's take-off hop
    private const double JumpShare = 0.3;    // of the leave's 300 ms, like the bird's hop

    private const double ChaseDeadBand = 80;         // DIP/s of horizontal speed before she turns round
    private const double ChaseTrailMax = 24;         // DIP behind the spot at full speed (10 read as glued on, cold read)
    private const double ChaseTrailFullSpeed = 500;  // DIP/s where the trail saturates
    private const double ChaseTrailGrowTime = 0.15;  // seconds for the trail to grow from 0 to the cap at a drag's start
    private const double StillSpeed = 40;            // under this for StillMs, she stops running ...
    private const double StillMs = 150;
    private const double RunAgainSpeed = 80;         // ... and over this she runs again: a gap, so a resting hand does not flicker her
    private const double WagFrameTime = 0.25;        // the standing wag, 4 fps

    private double chaseTrail;                // DIP the window sits off the perch, opposite the motion; 0 for a bird
    private double chaseStillMs, chaseWagClock;
    // True while the carried dog stands and wags: the carry loop (Loop.Flap) with its frame timer resting.
    private bool chaseStanding, chaseWagSecond;

    // The dog's arrival runs on a linear clock and is shaped in GroundProgress (the bird's keeps its BackEase).
    private readonly DoubleAnimation groundArriveAnim = new(0, 1, new Duration(ArriveTime));

    private bool OnGround => Skin.Locomotion == Locomotion.Ground;

    private static double Cube(double x) => x * x * x;

    /// <summary>The ground twin of OnProgress's flights; false for the shove, which is shared.</summary>
    private bool GroundProgress(double p)
    {
        switch (motion)
        {
            case Motion.Arrive:
            {
                // The perch may move under her; she homes in on it like a bird, on the line it is on. Her
                // clock is linear (groundArriveAnim): the run keeps one pace for RunShare of the 450 ms, then
                // she brakes on the Skid frame and slides the last SkidReach DIP to a stop on the spot. (The
                // birds' BackEase crossed a monitor in about 150 ms and slid back 300 ms: it read as a
                // teleport and a moonwalk, review R1.)
                var target = TopLeftFor(perch);
                double dx = target.X - motionFrom.X, dist = Math.Abs(dx);
                double skid = Math.Min(SkidReach, dist * SkidMaxShare);
                bool braking = p >= RunShare;
                double done = braking
                    ? dist - skid * Cube(1 - Math.Min(1, (p - RunShare) / (1 - RunShare)))
                    : (dist - skid) * p / RunShare;
                double f = dist >= 1 ? done / dist : p;
                Left = motionFrom.X + dx * f;
                Top = motionFrom.Y + (target.Y - motionFrom.Y) * f;
                // Braking: the run stops on the Skid frame until the landing (a carry that restarts the run
                // meanwhile is stopped again on the next tick).
                if (braking && loop == Loop.Flap && dist >= 1)
                {
                    StopLoop();
                    Sprite.Frame = acts.Skid;
                }
                return true;
            }
            case Motion.Fly:
            {
                // FlyTo and the catch: the run toward wherever the perch is now, with no lift.
                var target = TopLeftFor(perch);
                Left = motionFrom.X + (target.X - motionFrom.X) * p;
                Top = motionFrom.Y + (target.Y - motionFrom.Y) * p;
                return true;
            }
            case Motion.Leave:
            case Motion.FlyOff:
            {
                double x = motionFrom.X, y = motionFrom.Y;
                if (p < JumpShare)
                {
                    // The jump-start: a small arc up and back onto the perch line.
                    double q = p / JumpShare;
                    y -= JumpHeight * 4 * q * (1 - q);
                }
                else
                {
                    // Then the sprint, accelerating off the side of the screen.
                    double q = (p - JumpShare) / (1 - JumpShare);
                    x += (motionTo.X - motionFrom.X) * q * q;
                }
                Left = x;
                Top = y;
                Opacity = baseOpacity * (p < 0.5 ? 1 : Math.Max(0, 1 - (p - 0.5) / 0.5));
                return true;
            }
        }
        return false;
    }

    /// <summary>The leave path from where it stands (the leave, the sulk, the switch): a bird climbs away, the dog sprints off to the side.</summary>
    private void StartLeavePath(Motion kind)
    {
        var to = OnGround ? OffscreenSide() : OffscreenAbove();
        StartMotion(kind, leaveAnim, new Point(Left, Top), to);
        StartFlap();
        FaceTravel(Left, to.X);
    }

    /// <summary>A dog faces the way she is about to run; a bird keeps facing right in the air.</summary>
    private void FaceTravel(double fromX, double toX)
    {
        if (OnGround && Math.Abs(toX - fromX) >= 0.5) Sprite.FlipX = toX < fromX;
    }

    /// <summary>
    /// Where an arrival from out of sight starts: a bird a full flight above the perch and above the monitor's
    /// top (round 1), the dog just off the side of the perch's monitor nearer the perch, at the perch's height.
    /// </summary>
    private Point ArrivalStart(Point perch)
    {
        var target = TopLeftFor(perch);
        var screen = Monitors.BoundsAt(perch);
        if (!OnGround) return new Point(target.X, Math.Min(target.Y - FlyDistance, screen.Top - WinH));
        bool fromLeft = perch.X - screen.Left <= screen.Right - perch.X;
        return new Point(fromLeft ? screen.Left - WinW : screen.Right, target.Y);
    }

    /// <summary>Where a climb from here ends: out of sight above the monitor's top, at least a full flight up.</summary>
    private Point OffscreenAbove()
    {
        double monitorTop = Monitors.BoundsAt(new Point(Left + WinW / 2, Top + WinH)).Top;
        return new Point(Left, Math.Min(Top - HopHeight - FlyDistance, monitorTop - WinH));
    }

    /// <summary>Where the dog's sprint ends: just past the nearer side edge of her monitor, on the line she runs on.</summary>
    private Point OffscreenSide()
    {
        double feetX = Left + WinW / 2;
        var screen = Monitors.BoundsAt(new Point(feetX, Top + WinH));
        bool toLeft = feetX - screen.Left < screen.Right - feetX;
        return new Point(toLeft ? screen.Left - WinW : screen.Right, Top);
    }

    private static Point TopLeftFor(Point perch) => new(perch.X - WinW / 2, perch.Y - WinH);

    /// <summary>The landing dust of 2.10: more the longer nothing landed on the note, none right after a landing.</summary>
    private void Land()
    {
        long now = Environment.TickCount64;
        int n = DustFor(now - lastLanding);
        lastLanding = now;
        if (n > 0) effects.BurstDust(n, new Point(WinW / 2, WinH));
    }

    private static int DustFor(long sinceMs)
    {
        if (sinceMs < 2000) return 0;
        double s = sinceMs / 1000.0;
        if (s <= 60) return 3;
        // Linear in log time: 3 at a minute, 10 at an hour and beyond.
        double t = Math.Min(1, Math.Log(s / 60) / Math.Log(60));
        return 3 + (int)Math.Round(7 * t);
    }

    // ---- the chase --------------------------------------------------------------------------------

    /// <summary>A carry starts (or the spring takes over): no trail yet and no stillness counted.</summary>
    private void ResetChase()
    {
        chaseTrail = 0;
        chaseStillMs = 0;
    }

    /// <summary>The carry ended: a dog the trail left off the perch is stray, so the rest that follows trots her home.</summary>
    private void EndChase()
    {
        if (Math.Abs(chaseTrail) >= 0.5) stray = true;
        ResetChase();
    }

    /// <summary>One render frame of the carried dog running after the spot; true while she stands (no beat to speed up).</summary>
    private bool StepChase(double dt)
    {
        double vx = grip.VelocityX;
        // Faces the way the note goes; inside the dead band she keeps the way she last ran. Set only on a
        // change: the property setter boxes the bool, and this runs every frame of a drag.
        bool flip = Sprite.FlipX;
        if (vx > ChaseDeadBand && flip) Sprite.FlipX = false;
        else if (vx < -ChaseDeadBand && !flip) Sprite.FlipX = true;

        // Behind the spot, opposite the motion, up to the cap. Not while he holds her by the scruff (the
        // bird-as-handle drag): the handle stays under his hand.
        double target = selfDrag ? 0 : -Math.Clamp(vx / ChaseTrailFullSpeed, -1, 1) * ChaseTrailMax;
        // Falling behind is rate-limited (the full cap takes ChaseTrailGrowTime), so the first pull visibly
        // leaves her behind; catching up (the same side, less trail) follows the speed at once, so she is
        // back on the spot as quickly as the grip's smoothed speed falls. A reversal crosses at the grow rate.
        bool catchingUp = target * chaseTrail >= 0 && Math.Abs(target) <= Math.Abs(chaseTrail);
        double grow = ChaseTrailMax / ChaseTrailGrowTime * dt;
        double trail = catchingUp ? target : chaseTrail + Math.Clamp(target - chaseTrail, -grow, grow);
        if (Math.Abs(trail - chaseTrail) >= 0.05)
        {
            chaseTrail = trail;
            Left = perch.X - WinW / 2 + chaseTrail;
        }

        double speed = grip.Speed;
        if (!chaseStanding)
        {
            chaseStillMs = speed < StillSpeed ? chaseStillMs + dt * 1000 : 0;
            if (chaseStillMs < StillMs) return false;
            // Held still: she stops and wags. The frame timer rests; this hook, which runs anyway while
            // carrying, swaps the two wag frames.
            chaseStanding = true;
            frameTimer.Stop();
            chaseWagClock = 0;
            chaseWagSecond = false;
            Sprite.Frame = Skin.Dog?.Wag1 ?? Skin.IdleOpen;
            return true;
        }
        if (speed > RunAgainSpeed)
        {
            chaseStillMs = 0;
            StartFlap(); // clears chaseStanding and runs again from the first frame
            return false;
        }
        chaseWagClock += dt;
        if (chaseWagClock >= WagFrameTime)
        {
            chaseWagClock -= WagFrameTime;
            chaseWagSecond = !chaseWagSecond;
            if (Skin.Dog is { } d) Sprite.Frame = chaseWagSecond ? d.Wag2 : d.Wag1;
        }
        return true;
    }
}
