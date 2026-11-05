using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TaskFocuser.Core;

namespace TaskFocuser.Monster;

/// <summary>
/// The bird's own window: transparent, never activated, clickable on its painted pixels, larger than
/// the sprite so hearts and dust have room, with the feet line on its bottom edge. Each flight is one
/// 0-to-1 animation of <see cref="ProgressProperty"/> whose callback places the window, so a flight can
/// home in on a perch that moves under it and Left and Top always move together. Two more parts of
/// this class live beside the code they drive: the render hook, the grip, the frame loops and the
/// bird-as-handle drag and the shove in Grip.cs; the act machine of the perched acts in Effects.cs;
/// resting, the click mood, the sulk flight and the public Eat, Flap and Ruffle in MonsterWindow.Mood.cs;
/// the peck, the seed fidget and the fidget draw in MonsterWindow.Peck.cs; round 5: the skin, its step
/// tables and the bird switch in MonsterWindow.Skin.cs, the walk fidget in MonsterWindow.Walk.cs;
/// round 6: where every path starts and ends, the landing dust, the dog's paths along the perch line
/// and the chase in MonsterWindow.Ground.cs, her own acts in MonsterWindow.Dog.cs.
/// The render hook exists only while carrying, tumbling, walking or an effect runs.
/// </summary>
public partial class MonsterWindow : Window, IMonster
{
    // FlyOff is the leave path without the hide (the sulk of SPEC-round3 2.6); Away is the second it
    // then waits out of sight, a motion with no clock so every "only while perched" guard holds.
    private enum Motion { None, Arrive, Leave, Shove, Fly, FlyOff, Away }
    private enum Loop { None, Flap, Bump, Tumble }

    private const double SpriteW = Sprites.Columns * Sprites.CellSize;
    private const double SpriteH = Sprites.Rows * Sprites.CellSize;
    // Room for the hearts above the head and the dust beside the feet (SPEC-round2 2.10).
    private const double WinW = 140;
    private const double WinH = 110;
    private const double SpriteX = (WinW - SpriteW) / 2;
    private const double SpriteY = WinH - SpriteH;
    // The body spans cell rows 4..13 and 14 cells across; the shove aims the body, not the feet.
    private const double BodyCentreY = 9 * Sprites.CellSize;
    private const double BodyHalfWidth = 7 * Sprites.CellSize;
    private const double HitSlop = 6;

    private const double FlyDistance = 320;  // how far above the perch an arrival starts and a leave ends
    private const double HopHeight = 10;
    private const double FlyLift = 10;       // the take-off arc of FlyTo and the catch
    private const double ShoveGap = 10;
    private const double BumpNudge = 3;

    private static readonly TimeSpan ArriveTime = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan LeaveTime = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ShoveTime = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan FlyTime = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan CatchTime = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan FlapTick = TimeSpan.FromMilliseconds(1000.0 / 14);
    private static readonly TimeSpan FastFlapTick = TimeSpan.FromMilliseconds(1000.0 / 20);
    private static readonly TimeSpan TumbleTick = TimeSpan.FromMilliseconds(1000.0 / 10);
    private static readonly TimeSpan BumpTick = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan BlinkHold = TimeSpan.FromMilliseconds(120);

    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        "Progress", typeof(double), typeof(MonsterWindow),
        new PropertyMetadata(0.0, (d, e) => ((MonsterWindow)d).OnProgress((double)e.NewValue)));

    private readonly DoubleAnimation arriveAnim, leaveAnim, shoveAnim, flyAnim, catchAnim;
    private readonly DispatcherTimer frameTimer;
    private readonly DispatcherTimer blinkTimer;
    private readonly Grip grip = new();
    private readonly Effects effects;
    private readonly EventHandler renderHandler;

    private Motion motion;
    private AnimationClock? clock;
    private Loop loop;
    private bool carrying;
    private bool blinking;
    private bool fastFlap;
    private int flapIndex;
    private int bumpTicks;
    private Point perch;
    private Point motionFrom;   // window top-left at the start of the running flight
    private Point motionTo;     // window top-left at its end (leave, shove)
    private Vector shoveDir;
    private Action? shoveDone;
    private double baseOpacity = 1;
    private long lastLanding = Environment.TickCount64;
    private bool renderHooked, renderPrimed;
    private TimeSpan lastRender;

    public MonsterWindow()
    {
        InitializeComponent();
        Width = WinW;
        Height = WinH;
        Sprite.Margin = new Thickness(SpriteX, SpriteY, 0, 0);
        Sprite.Frame = Skin.IdleOpen;
        Sprite.RenderTransform = spriteShift;
        effects = new Effects(FxLayer);
        renderHandler = OnRendering;

        arriveAnim = new DoubleAnimation(0, 1, new Duration(ArriveTime))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 },
        };
        // The leave path is shaped in OnProgress (a hop, then an accelerating climb), so it runs linear.
        leaveAnim = new DoubleAnimation(0, 1, new Duration(LeaveTime));
        shoveAnim = new DoubleAnimation(0, 1, new Duration(ShoveTime))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        flyAnim = new DoubleAnimation(0, 1, new Duration(FlyTime))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };
        catchAnim = new DoubleAnimation(0, 1, new Duration(CatchTime))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };

        frameTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = FlapTick };
        frameTimer.Tick += OnFrameTick;
        blinkTimer = new DispatcherTimer(DispatcherPriority.Background);
        blinkTimer.Tick += OnBlinkTick;
        InitActs();

        SourceInitialized += (_, _) =>
        {
            // No longer click-through (round 2 clicks): the layered window still lets clicks on its
            // transparent pixels fall through to whatever is below.
            Win32.SetToolWindow(this);
            Win32.SetNoActivate(this, true);
        };
        // Whoever hides the window, a hidden bird must cost nothing.
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) { UpdateRenderHook(); return; }
            StopEverything();
            IsOut = false;
        };
    }

    public bool IsOut { get; private set; }

    public event Action? Clicked;
    public event Action<ChirpKind>? ChirpRequested;
    public event Action<Point>? DragStarted;
    public event Action<Point>? DragMoved;
    public event Action<Point>? DragEnded;

    public void ArriveAt(Point perch)
    {
        this.perch = perch;
        // Sulking or switching birds: it is out and already coming back, so a new perch only retargets the return.
        if (motion is Motion.FlyOff or Motion.Away) return;
        // Called back while it left with a bird switch pending: the bird that turns around is the new one.
        var wasMoving = Skin.Locomotion;
        ApplyPendingSkin();
        IsOut = true;
        shoveDone = null;
        CancelActivity();
        Touch();
        Opacity = baseOpacity;
        Sprite.FlipX = false;

        Point from;
        // Cancelling a leave, a shove or a tumble: turn around from wherever it is now. Unless the one that
        // turns around gets about differently (a dog put on mid-climb): then it comes in the way it enters.
        if (IsVisible && Skin.Locomotion == wasMoving)
        {
            from = new Point(Left, Top);
        }
        else
        {
            from = ArrivalStart(perch);
            Left = from.X;
            Top = from.Y;
            if (!IsVisible) Show();
        }
        StartMotion(Motion.Arrive, OnGround ? groundArriveAnim : arriveAnim, from, default);
        StartFlap();
        FaceTravel(from.X, TopLeftFor(perch).X);
        UpdateRenderHook();
    }

    public void MoveTo(Point perch)
    {
        // Hot path (every drag move at the monitor's refresh rate): no allocations, no animation calls.
        this.perch = perch;
        // A walk (or a bird still off its perch after one) ends: the new perch is taken directly.
        if (walkPhase != WalkPhase.None || stray)
        {
            CutWalkHome();
            return;
        }
        if (motion == Motion.None && !grip.Tumbling)
        {
            // chaseTrail is the carried dog running behind the spot (Ground.cs); always 0 for a bird.
            Left = perch.X - WinW / 2 + chaseTrail;
            Top = perch.Y - WinH;
        }
        // A flight homes in on the new perch on its next tick, a tumble's spring follows it on the next
        // frame; a leave or a shove keeps its course.
    }

    public void FlyTo(Point perch)
    {
        this.perch = perch;
        // Hidden or leaving: nothing to fly to. Arriving or flying: already homing in on the new perch.
        // Shoving: the dock sequence decides. Tumbling: the spring and then the catch follow the perch.
        if (!IsVisible || !IsOut || motion != Motion.None || grip.Tumbling) return;
        var from = new Point(Left, Top);
        var to = TopLeftFor(perch);
        if (Math.Abs(to.X - from.X) < 1 && Math.Abs(to.Y - from.Y) < 1) { MoveTo(perch); return; }
        CancelActivity();
        StartMotion(Motion.Fly, flyAnim, from, default);
        StartFlap();
        FaceTravel(from.X, to.X);
    }

    public void SetCarrying(bool carrying)
    {
        this.carrying = carrying;
        if (carrying)
        {
            // A drag beats a walk too: the bird is back on its perch at once, where the grip expects it.
            if (walkPhase != WalkPhase.None || stray) CutWalkHome();
            // A drag beats any perched sequence (a drag that starts mid-eat ends the meal).
            StopAct();
            idleTimer.Stop();
            Touch();
            grip.Reset(TopLeftFor(perch));
            ResetChase();
            if (motion is Motion.None or Motion.Arrive or Motion.Fly) StartFlap();
        }
        else
        {
            // A dog that ran a few DIP behind the spot is left stray, so the rest below trots her home.
            EndChase();
            // The drag ended while it had let go: it flies back to the perch, then sits (or the
            // conductor sends it away, which takes over the catch).
            if (grip.Tumbling) StartCatch();
            else if (motion == Motion.None && IsVisible && loop != Loop.Bump) EnterRest();
        }
        UpdateRenderHook();
    }

    public void Leave()
    {
        IsOut = false;
        shoveDone = null;
        if (!IsVisible)
        {
            StopEverything();
            return;
        }
        if (motion == Motion.Away)
        {
            // Already out of sight: just go (the hide stops the return timer).
            Hide();
            Opacity = baseOpacity;
            return;
        }
        if (motion == Motion.FlyOff)
        {
            // Already climbing away on the same path: it simply does not come back.
            motion = Motion.Leave;
            return;
        }
        CancelActivity();
        StartLeavePath(Motion.Leave);
    }

    public void SetOpacity(double opacity)
    {
        baseOpacity = Math.Clamp(opacity, 0, 1);
        // A leave or a sulk owns the opacity while it fades out or waits unseen; it ends at the new base.
        if (motion is not (Motion.Leave or Motion.FlyOff or Motion.Away)) Opacity = baseOpacity;
    }

    public bool Contains(Point screenDip) =>
        IsVisible && new Rect(Left + SpriteX - HitSlop, Top + SpriteY - HitSlop,
            SpriteW + 2 * HitSlop, SpriteH + 2 * HitSlop).Contains(screenDip);

    // ---- flights ------------------------------------------------------------------------------

    private void StartMotion(Motion kind, DoubleAnimation anim, Point from, Point to)
    {
        // Every flight starts from where the bird stands and decides where it ends: no walk home after it.
        StopWalk();
        stray = false;
        StopMotion();
        motionFrom = from;
        motionTo = to;
        motion = kind;
        var c = anim.CreateClock();
        c.Completed += OnMotionCompleted;
        clock = c;
        ApplyAnimationClock(ProgressProperty, c, HandoffBehavior.SnapshotAndReplace);
    }

    private void StopMotion()
    {
        awayTimer.Stop();
        if (clock is null)
        {
            // Away runs without a clock; nothing else does.
            motion = Motion.None;
            return;
        }
        // Motion goes to None first: removing the clock resets Progress to 0, and that callback must not move the window.
        motion = Motion.None;
        clock.Completed -= OnMotionCompleted;
        clock = null;
        ApplyAnimationClock(ProgressProperty, null);
    }

    private void OnProgress(double p)
    {
        // Round 6: a dog's arrival, run and leave keep to the perch line (Ground.cs); the shove is shared.
        if (OnGround && GroundProgress(p)) return;
        switch (motion)
        {
            case Motion.Arrive:
            {
                // BackEase takes p past 1 for the little overshoot below the perch before it settles.
                var target = TopLeftFor(perch);
                Left = motionFrom.X + (target.X - motionFrom.X) * p;
                Top = motionFrom.Y + (target.Y - motionFrom.Y) * p;
                break;
            }
            case Motion.Fly:
            {
                // A glide toward wherever the perch is now, lifted on an arc: the take-off hop and the landing.
                var target = TopLeftFor(perch);
                double lift = FlyLift * Math.Sin(Math.PI * Math.Clamp(p, 0, 1));
                Left = motionFrom.X + (target.X - motionFrom.X) * p;
                Top = motionFrom.Y + (target.Y - motionFrom.Y) * p - lift;
                break;
            }
            case Motion.Shove:
                Left = motionFrom.X + (motionTo.X - motionFrom.X) * p;
                Top = motionFrom.Y + (motionTo.Y - motionFrom.Y) * p;
                break;
            case Motion.Leave:
            case Motion.FlyOff:
            {
                const double hopShare = 0.3;
                double y;
                if (p < hopShare)
                {
                    double q = p / hopShare;
                    y = motionFrom.Y - HopHeight * (1 - (1 - q) * (1 - q));
                }
                else
                {
                    double q = (p - hopShare) / (1 - hopShare);
                    double hopTop = motionFrom.Y - HopHeight;
                    y = hopTop + (motionTo.Y - hopTop) * q * q;
                }
                Top = y;
                Opacity = baseOpacity * (p < 0.4 ? 1 : Math.Max(0, 1 - (p - 0.4) / 0.6));
                break;
            }
        }
    }

    private void OnMotionCompleted(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, clock)) return;
        var finished = motion;
        StopMotion();
        switch (finished)
        {
            case Motion.Arrive:
            case Motion.Fly:
                MoveTo(perch);
                Land();
                // A bird that has just flown in (or back from a sulk) starts with a calm temper.
                if (finished == Motion.Arrive) ResetMood();
                EnterRest();
                break;
            case Motion.FlyOff:
                BeginAway();
                break;
            case Motion.Leave:
                Hide();
                Opacity = baseOpacity;
                break;
            case Motion.Shove:
                Left = motionTo.X;
                Top = motionTo.Y;
                StartBump();
                break;
        }
        UpdateRenderHook();
    }

    /// <summary>Whatever the bird is doing on the spot ends (a new motion takes over).</summary>
    private void CancelActivity()
    {
        StopAct();
        StopLoop();
        blinkTimer.Stop();
        idleTimer.Stop();
        blinking = false;
        grip.Release();
        ResetChase(); // a dog's trail behind the carried spot means nothing once a motion or a hide takes over
        // A new motion takes over from where the bird stands, so neither the walk nor its return go on.
        StopWalk();
        stray = false;
    }

    private void StopEverything()
    {
        // A bird switch cut short (a hide, a Leave) still swaps the skin: the next arrival is the new bird.
        ApplyPendingSkin();
        StopMotion();
        CancelActivity();
        // A hide ends the carrying too: a drag cut short without its end (--close mid-drag) must not
        // leave the next arrival flapping on the perch with the render hook running every frame.
        carrying = false;
        shoveDone = null;
        effects.Clear();
        AbortPress();
        ResetMood();
        UpdateRenderHook();
    }

    // Where the paths start and end (OffscreenAbove, OffscreenSide, ArrivalStart, TopLeftFor) live in Ground.cs.
}
