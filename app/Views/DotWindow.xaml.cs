using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TaskFocuser.Core;

namespace TaskFocuser.Views;

/// <summary>
/// The red dot the note tucks into (SPEC 3.6, 3.7); since SPEC-round3 2.4 a flat badge with almost no glow.
/// A fixed transparent square centred on <see cref="Centre"/>; moving the dot is moving the window.
/// </summary>
public partial class DotWindow : Window, IDot
{
    private enum Phase { Hidden, Armed, Hot, Docking, Resting, Popping, Vanishing, Disarming }

    private const double Half = 48;           // half the window side: Centre to Left/Top
    private const double HitRadius = 44;
    private const double DragThreshold = 3;
    private const double ArmedSize = 26.0 / 18.0;
    private const double HotSize = 34.0 / 18.0;
    private const double DiscSize = 18, HaloRing = 2;   // the XAML's disc, and the halo ring's width in DIP
    // SPEC-round3 2.4: the halo's strength, resting and as a drop target (armed or hot alike).
    private const double RestHalo = 0.12;
    private const double ArmedHalo = 0.20;
    private const double HotHalo = 0.20;
    // The resting breath: the disc's opacity between these, one way in BreathMs, auto-reversed.
    private const double BreathLow = 0.92;
    private const int BreathMs = 3000;

    public event Action? DoubleClicked;
    public event Action<Point>? DragStarted;
    public event Action<Point>? DragMoved;
    public event Action<Point>? DragEnded;

    private Phase _phase = Phase.Hidden;

    // Bumped by every request; a completion handler from an older request sees a stale value and does
    // nothing, so a superseded animation can never hide the dot or fire a callback late.
    private int _gen;

    private bool _pressed;
    private bool _dragging;
    private Point _pressScreen;
    private Point _grabOffset;   // cursor inside the window at the press, kept for the whole drag

    public DotWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            Win32.SetToolWindow(this);
            Win32.SetNoActivate(this, true);
        };
    }

    public Point Centre { get; private set; }

    public bool Contains(Point p) => (p - Centre).Length <= HitRadius;

    // ---- the contract ---------------------------------------------------------------------------

    public void Arm(Point centre)
    {
        bool fresh = !IsVisible || _phase is Phase.Hidden or Phase.Disarming or Phase.Vanishing
                     || (centre - Centre).Length > 0.5;
        int gen = ++_gen;
        CancelDrag();
        StopPulse();
        _phase = Phase.Armed;

        if (fresh)
        {
            // A new place (first arm, or the cursor crossed to another monitor): appear there from nothing.
            SetFx(0);
            MoveTo(centre);
            ShowQuietly();
        }

        AnimateSize(ArmedSize, 120);
        Animate(Disc, OpacityProperty, 1.0, 120, Cubic());
        Animate(Halo, OpacityProperty, ArmedHalo, 120, Cubic());
        AnimateFx(1.0, 180, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 }, gen, null);
    }

    public void SetHot(bool hot)
    {
        if (_phase is not (Phase.Armed or Phase.Hot)) return;
        if ((_phase == Phase.Hot) == hot) return;
        _phase = hot ? Phase.Hot : Phase.Armed;
        AnimateSize(hot ? HotSize : ArmedSize, 120);
        Animate(Halo, OpacityProperty, hot ? HotHalo : ArmedHalo, 120, Cubic());
    }

    public void Disarm()
    {
        if (_phase is Phase.Hidden or Phase.Disarming) return;
        int gen = ++_gen;
        CancelDrag();
        _phase = Phase.Disarming;
        AnimateFx(0, 150, Quad(EasingMode.EaseIn), gen, HideNow);
    }

    public void Dock(Point centre, Action onDone)
    {
        int gen = ++_gen;
        CancelDrag();
        StopPulse();
        _phase = Phase.Docking;
        MoveTo(centre);
        if (!IsVisible) { SetFx(0); ShowQuietly(); }

        // Back to the resting size fast, so the 1.4 swell happens around the small dot, not the hot one
        // (and everything stays inside the 96 DIP window).
        AnimateSize(1.0, 130);
        Animate(Halo, OpacityProperty, RestHalo, 260, Cubic());
        Animate(Disc, OpacityProperty, 1.0, 120, Cubic());

        var catchAnim = new DoubleAnimationUsingKeyFrames { Duration = Ms(260) };
        catchAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.4, KeyTime.FromTimeSpan(Ms(80)), Cubic()));
        catchAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(Ms(170)), Sine()));
        catchAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(Ms(260)), Sine()));
        RunFx(catchAnim, gen, () =>
        {
            _phase = Phase.Resting;
            StartPulse();
            onDone();
        });
    }

    public void Rest(Point centre)
    {
        ++_gen;
        CancelDrag();
        StopAll();
        SetFx(1);
        SizeScale.ScaleX = SizeScale.ScaleY = 1;
        HaloFit.ScaleX = HaloFit.ScaleY = 1;
        Halo.Opacity = RestHalo;
        Disc.Opacity = 1;
        MoveTo(centre);
        _phase = Phase.Resting;
        ShowQuietly();
        StartPulse();
    }

    public void Pop(Action onDone)
    {
        if (!IsVisible) { onDone(); return; }
        int gen = ++_gen;
        CancelDrag();
        _phase = Phase.Popping;

        var squashStretch = new DoubleAnimationUsingKeyFrames { Duration = Ms(140) };
        squashStretch.KeyFrames.Add(new EasingDoubleKeyFrame(0.7, KeyTime.FromTimeSpan(Ms(70)), Quad(EasingMode.EaseOut)));
        squashStretch.KeyFrames.Add(new EasingDoubleKeyFrame(1.3, KeyTime.FromTimeSpan(Ms(140)), Quad(EasingMode.EaseOut)));
        RunFx(squashStretch, gen, () =>
        {
            // Settle first, then hand over: the note pops out while the dot is still big, and if the
            // conductor hides the dot inside onDone the settle is simply cut.
            AnimateFx(1.0, 80, Sine(), gen, () => _phase = Phase.Resting);
            onDone();
        });
    }

    public void Vanish(Action onDone)
    {
        if (!IsVisible) { HideNow(); onDone(); return; }
        int gen = ++_gen;
        CancelDrag();
        _phase = Phase.Vanishing;
        AnimateFx(0, 160, Quad(EasingMode.EaseIn), gen, () =>
        {
            HideNow();
            onDone();
        });
    }

    public void HideDot()
    {
        ++_gen;
        CancelDrag();
        HideNow();
    }

    // ---- mouse: double-click and drag with capture ------------------------------------------------

    // Only a settled dot takes the mouse: during the catch the conductor is still in Note mode and
    // would not know what a dot double-click or drag means.
    private bool AcceptsInput => _phase == Phase.Resting;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;
        if (!AcceptsInput) return;

        if (e.ClickCount >= 2)
        {
            CancelDrag();
            DoubleClicked?.Invoke();
            return;
        }

        var p = e.GetPosition(this);
        _grabOffset = p;
        _pressScreen = new Point(Left + p.X, Top + p.Y);
        _pressed = true;
        _dragging = false;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed) return;

        var p = e.GetPosition(this);
        var screen = new Point(Left + p.X, Top + p.Y);

        if (!_dragging)
        {
            // The threshold keeps a double-click (which always jitters a pixel or two) from becoming a drag.
            if ((screen - _pressScreen).Length <= DragThreshold) return;
            _dragging = true;
            DragStarted?.Invoke(_pressScreen);
            // The close target is shown by that handler, so it would now stack above the dot; re-entering
            // the topmost band puts the dot back on top without activating it (SWP_NOACTIVATE).
            Topmost = false;
            Topmost = true;
        }

        // In his hand: the grab point stays under the cursor, no easing.
        Left = screen.X - _grabOffset.X;
        Top = screen.Y - _grabOffset.Y;
        Centre = new Point(Left + Half, Top + Half);
        DragMoved?.Invoke(screen);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_pressed) return;
        e.Handled = true;
        var p = e.GetPosition(this);
        FinishPress(new Point(Left + p.X, Top + p.Y));
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!_pressed) return;
        var p = Mouse.GetPosition(this);
        FinishPress(new Point(Left + p.X, Top + p.Y));
    }

    private void FinishPress(Point screen)
    {
        // Clear the flags before releasing capture: ReleaseMouseCapture raises LostMouseCapture again.
        bool wasDragging = _dragging;
        _pressed = false;
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (wasDragging) DragEnded?.Invoke(screen);
    }

    /// <summary>Drops a press or drag silently: the conductor has moved on, so no DragEnded.</summary>
    private void CancelDrag()
    {
        if (!_pressed) return;
        _pressed = false;
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    // ---- animation plumbing ---------------------------------------------------------------------

    private void StartPulse()
    {
        // SPEC-round3 2.4: a barely visible breath of the disc's own brightness instead of a pulsing glow.
        // It starts at full and breathes out first, so the rest never begins with a jump.
        var pulse = new DoubleAnimation(1.0, BreathLow, Ms(BreathMs))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = Sine(),
        };
        // The one thing that runs while the dot sits there; 30 fps keeps it off the CPU meter.
        Timeline.SetDesiredFrameRate(pulse, 30);
        Disc.BeginAnimation(OpacityProperty, pulse, HandoffBehavior.SnapshotAndReplace);
    }

    private void StopPulse()
    {
        double now = Disc.Opacity;
        Disc.BeginAnimation(OpacityProperty, null);
        Disc.Opacity = now;
    }

    /// <summary>Removes every running animation, keeping the values where they were.</summary>
    private void StopAll()
    {
        StopPulse();
        double halo = Halo.Opacity, size = SizeScale.ScaleX, fit = HaloFit.ScaleX, fx = FxScale.ScaleX;
        Halo.BeginAnimation(OpacityProperty, null);
        SizeScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        SizeScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        HaloFit.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        HaloFit.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        FxScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        FxScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Halo.Opacity = halo;
        SizeScale.ScaleX = SizeScale.ScaleY = size;
        HaloFit.ScaleX = HaloFit.ScaleY = fit;
        FxScale.ScaleX = FxScale.ScaleY = fx;
    }

    private void HideNow()
    {
        StopAll();
        _phase = Phase.Hidden;
        Hide();
    }

    private void SetFx(double v)
    {
        FxScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        FxScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        FxScale.ScaleX = FxScale.ScaleY = v;
    }

    private void MoveTo(Point centre)
    {
        Centre = centre;
        Left = centre.X - Half;
        Top = centre.Y - Half;
    }

    private void ShowQuietly()
    {
        if (!IsVisible) Show();
    }

    private void AnimateFx(double to, int ms, IEasingFunction ease, int gen, Action? then) =>
        RunFx(new DoubleAnimation(to, Ms(ms)) { EasingFunction = ease }, gen, then);

    /// <summary>Runs one uniform-scale animation on FxScale; <paramref name="then"/> runs once, and only
    /// if no newer request has come in meanwhile.</summary>
    private void RunFx(AnimationTimeline anim, int gen, Action? then)
    {
        if (then is not null)
        {
            bool fired = false;   // the same timeline drives ScaleX and ScaleY, so Completed comes twice
            anim.Completed += (_, _) =>
            {
                if (fired || gen != _gen) return;
                fired = true;
                then();
            };
        }
        FxScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim, HandoffBehavior.SnapshotAndReplace);
        FxScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Animates the dot's size and, with it, the halo's counter-scale that keeps its ring at
    /// <see cref="HaloRing"/> DIP whatever the size (SPEC-round3 2.4).</summary>
    private void AnimateSize(double size, int ms)
    {
        Animate(SizeScale, size, ms, Cubic());
        Animate(HaloFit, HaloFitFor(size), ms, Cubic());
    }

    // Inside SizeScale the halo's outer diameter is (DiscSize + 2 * HaloRing) * fit * size; it should be
    // DiscSize * size + 2 * HaloRing.
    private static double HaloFitFor(double size) => (DiscSize * size + 2 * HaloRing) / ((DiscSize + 2 * HaloRing) * size);

    private static void Animate(ScaleTransform t, double to, int ms, IEasingFunction ease)
    {
        var a = new DoubleAnimation(to, Ms(ms)) { EasingFunction = ease };
        t.BeginAnimation(ScaleTransform.ScaleXProperty, a, HandoffBehavior.SnapshotAndReplace);
        t.BeginAnimation(ScaleTransform.ScaleYProperty, a, HandoffBehavior.SnapshotAndReplace);
    }

    private static void Animate(UIElement el, DependencyProperty dp, double to, int ms, IEasingFunction ease) =>
        el.BeginAnimation(dp, new DoubleAnimation(to, Ms(ms)) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);
    private static CubicEase Cubic() => new() { EasingMode = EasingMode.EaseOut };
    private static SineEase Sine() => new() { EasingMode = EasingMode.EaseInOut };
    private static QuadraticEase Quad(EasingMode mode) => new() { EasingMode = mode };
}
