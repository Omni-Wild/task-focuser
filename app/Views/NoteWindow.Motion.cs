using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TaskFocuser.Core;

namespace TaskFocuser.Views;

// Appearing and disappearing: the fade-in, the pop out of the dot, the squash into it. All of them run on
// the paper's ScaleTransform and opacity (render-only), plus the window position for the squash.
public partial class NoteWindow
{
    private int _motionGen;
    // The shadow's DropShadowEffect, off while the paper scales: an effect re-rendered under an animated
    // ScaleTransform makes WPF realise intermediate surfaces at every size it passes through (measured: about
    // 55 MB of private bytes after the first squash and pop, against 3 MB without the effect). StopMotion puts it back.
    private System.Windows.Media.Effects.Effect? _shadowFx;

    private void ShadowOff()
    {
        _shadowFx ??= Shadow.Effect;
        Shadow.Effect = null;
    }

    public void ShowNote()
    {
        if (IsVisible)
        {
            BringToFront();
            return;
        }
        StopMotion();
        PlaceClamped(new Point(State.X, State.Y));

        int gen = _motionGen;
        Fade(0, 1, 120, gen);
        ShowNow();
    }

    public void HideNote()
    {
        Bar.ClosePopups();
        StopMotion();
        CancelPress();
        CancelCucumber();
        EndGripAndSnap();
        Hide();
    }

    public void BringToFront()
    {
        if (!IsVisible) return;
        // Re-asserting Topmost lifts the window above other topmost windows without activating it.
        Topmost = false;
        Topmost = true;
        if (!State.Locked)
        {
            Activate();
            FocusEditorAtEnd();
        }
    }

    public void PopOut(Point from, Point topLeft)
    {
        StopMotion();
        PlaceClamped(topLeft);

        // Grow out of the corner that faces the dot, so it reads as coming from there.
        var r = _paper;
        double ox = Math.Abs(from.X - r.Left) < Math.Abs(from.X - r.Right) ? 0 : 1;
        double oy = Math.Abs(from.Y - r.Top) < Math.Abs(from.Y - r.Bottom) ? 0 : 1;
        NoteRoot.RenderTransformOrigin = new Point(ox, oy);
        ShadowOff();

        int gen = _motionGen;
        var dur = TimeSpan.FromMilliseconds(280);
        var grow = new DoubleAnimation(0.2, 1.0, dur)
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 },
        };
        grow.Completed += (_, _) => { if (gen == _motionGen) StopMotion(); };
        NoteScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        NoteScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        Fade(0, 1, 140, gen);
        ShowNow();
    }

    public void SquashInto(Point target, Action onDone)
    {
        StopMotion();
        CancelPress();
        CancelCucumber();
        // Before the corner is measured: a snap still sliding would move the paper under the squash.
        EndGripAndSnap();
        Bar.ClosePopups();
        if (!IsVisible)
        {
            onDone();
            return;
        }

        // Shrinking about the paper's top-right keeps that corner still inside the window,
        // so moving the window by the corner's distance to the target lands the corner on it.
        NoteRoot.RenderTransformOrigin = new Point(1, 0);
        ShadowOff();
        double cornerX = _paper.Right - _winLeft, cornerY = _paper.Top - _winTop;
        double homeLeft = _winLeft, homeTop = _winTop;

        int gen = _motionGen;
        var dur = TimeSpan.FromMilliseconds(200);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        var shrink = new DoubleAnimation(1.0, 0.08, dur) { EasingFunction = ease };
        var toLeft = new DoubleAnimation(homeLeft, target.X - cornerX, dur) { EasingFunction = ease };
        var toTop = new DoubleAnimation(homeTop, target.Y - cornerY, dur) { EasingFunction = ease };
        shrink.Completed += (_, _) =>
        {
            if (gen != _motionGen) return;
            Hide();
            StopMotion();
            onDone();
        };
        NoteScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        NoteScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        BeginAnimation(LeftProperty, toLeft);
        BeginAnimation(TopProperty, toTop);
    }

    /// <summary>
    /// Cancels any running appear/disappear and puts the paper back to rest: full size, fully opaque,
    /// the window where the layout says. A running squash's callback is dropped with it.
    /// </summary>
    private void StopMotion()
    {
        _motionGen++;
        NoteScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NoteScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        NoteScale.ScaleX = 1;
        NoteScale.ScaleY = 1;
        NoteRoot.BeginAnimation(OpacityProperty, null);
        NoteRoot.Opacity = 1;
        if (_shadowFx is not null && Shadow.Effect is null) Shadow.Effect = _shadowFx;
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        if (!double.IsNaN(_winLeft))
        {
            _placing = true;
            try { Left = _winLeft; Top = _winTop; }
            finally { _placing = false; }
        }
        StopShake();
    }

    // ---- the shake (SPEC-round2 2.1, softened in SPEC-round5 2.1) --------------------------------------

    // Two cycles in 140 ms at 1 DIP (round 2 had three in 180 at 2; he found it too dramatic): a peak every
    // half cycle at the odd quarters (17.5 ms, then every 35 ms), sine-eased between, the second cycle easing
    // off so it settles instead of stopping dead. Same curve shape, same trigger. Built once and frozen: a
    // lock is a frequent action and the curve never changes.
    private const double ShakeMs = 140, ShakeQuarterMs = ShakeMs / 8;
    private static readonly DoubleAnimationUsingKeyFrames ShakeCurve = BuildShake();

    private static DoubleAnimationUsingKeyFrames BuildShake()
    {
        double[] peaks = { 1, -1, 0.7, -0.35 };
        var a = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(ShakeMs),
            FillBehavior = FillBehavior.Stop,
        };
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        for (int i = 0; i < peaks.Length; i++)
            a.KeyFrames.Add(new EasingDoubleKeyFrame(peaks[i],
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ShakeQuarterMs * (1 + 2 * i))), ease));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ShakeMs)), ease));
        a.Freeze();
        return a;
    }

    /// <summary>
    /// The tiny horizontal shake on lock and unlock: render-only on the note root, so the window, the
    /// layout and the perch stay put. A new shake replaces a running one.
    /// </summary>
    public void Shake()
    {
        if (!IsVisible) return;
        NoteShift.BeginAnimation(TranslateTransform.XProperty, ShakeCurve, HandoffBehavior.SnapshotAndReplace);
    }

    private void StopShake()
    {
        NoteShift.BeginAnimation(TranslateTransform.XProperty, null);
        NoteShift.X = 0;
    }

    private void Fade(double from, double to, int ms, int gen)
    {
        var fade = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms));
        fade.Completed += (_, _) =>
        {
            if (gen != _motionGen) return;
            NoteRoot.BeginAnimation(OpacityProperty, null);
            NoteRoot.Opacity = to;
        };
        NoteRoot.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Lays the note out at <paramref name="topLeft"/>, kept inside a visible work area.</summary>
    private void PlaceClamped(Point topLeft)
    {
        LayoutAt(topLeft);
        var r = _paper;
        try { r = Monitors.ClampInto(r, WorkAreaFor(r)); }
        catch { /* the note simply stays where it was asked to be */ }
        if (r.TopLeft != _paper.TopLeft) LayoutAt(r.TopLeft);

        if (State.X != _paper.X || State.Y != _paper.Y)
        {
            State.X = _paper.X;
            State.Y = _paper.Y;
            Changed?.Invoke();
        }
    }

    private void ShowNow()
    {
        // A locked note must appear without taking the keyboard from whatever he is typing in.
        ShowActivated = !State.Locked;
        Show();
        if (!State.Locked)
        {
            Activate();
            FocusEditorAtEnd();
        }
    }
}
