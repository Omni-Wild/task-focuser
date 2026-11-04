using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace TaskFocuser.Views;

// The resize corner (SPEC-round4 2.4). A drag on the grip sets a manual size, which MeasureNote treats as a
// floor over the auto size; a click on it (no drag) drops the manual size and snaps back to the natural size.
// The grip runs its own press like the cucumber handle: handled at once, so it never arms a bar drag, half a
// double-click lock or a text selection, and its capture keeps the keyboard focus in the text box.
public partial class NoteWindow
{
    private const int SnapMs = 220;

    /// <summary>0 to 1: progress of the snap back to the natural size. Only meaningful with _snapFrom/_snapTo.</summary>
    private static readonly DependencyProperty SnapProperty = DependencyProperty.Register(
        "Snap", typeof(double), typeof(NoteWindow),
        new PropertyMetadata(0.0, (d, _) => ((NoteWindow)d).OnSnapFrame()));

    private bool _gripPressed, _gripReleased, _resizing;
    private Point _gripPress;
    private Size _gripStart;
    // The size the cursor asks for while a grip drag runs; it stands in for State's manual size until the
    // release commits the paper's real size, so a drag cut short by a lock never leaves a half value behind.
    private double _gripW = double.NaN, _gripH = double.NaN;

    private bool _snapping;
    private int _snapGen;
    private Rect _snapFrom, _snapTo;                 // the snap's ends; _snapTo is what PerchPoint reports
    private NoteSize _snapSize;

    private double ManualWidthNow => _resizing ? _gripW : State.ManualWidth;
    private double ManualHeightNow => _resizing ? _gripH : State.ManualHeight;

    private void WireGrip()
    {
        Grip.MouseLeftButtonDown += OnGripPress;
        Grip.MouseMove += OnGripMove;
        Grip.MouseLeftButtonUp += (_, e) =>
        {
            if (!_gripPressed) return;
            e.Handled = true;
            // Marks a real release, so a capture lost to Alt-Tab without a drag does not count as a click.
            _gripReleased = true;
            Grip.ReleaseMouseCapture();
        };
        // Release and a lost capture both end here.
        Grip.LostMouseCapture += (_, _) => EndGrip();
    }

    private void OnGripPress(object sender, MouseButtonEventArgs e)
    {
        // Handled in every case: the paper's own press handler (drag, lock, focus) must never see it.
        e.Handled = true;
        if (State.Locked || _gripPressed) return;

        // A fold or a snap still running ends now, so the drag starts from the rect on screen at rest.
        if (_animating || _snapping)
        {
            if (_animating) StopUnfold(_animTargetUnfold);
            if (_snapping) StopSnap();
            Relayout();
        }

        _gripPressed = true;
        _gripReleased = false;
        _resizing = false;
        _gripPress = ScreenPoint(e.GetPosition(this));
        _gripStart = _paper.Size;
        Grip.CaptureMouse();
    }

    private void OnGripMove(object sender, MouseEventArgs e)
    {
        if (!_gripPressed) return;
        var p = ScreenPoint(e.GetPosition(this));
        double dx = p.X - _gripPress.X, dy = p.Y - _gripPress.Y;

        if (!_resizing)
        {
            // The same 3 DIP as the bar drag: below it, the press is a click (back to auto).
            if (Math.Abs(dx) <= DragThreshold && Math.Abs(dy) <= DragThreshold) return;
            _resizing = true;
            Bar.ClosePopups();
        }

        // The bottom-right corner follows the cursor but stays inside the work area; the lower bound is the
        // text's natural size, which MeasureNote applies by itself (the manual size is only a floor).
        var wa = WorkAreaFor(_paper);
        _gripW = Math.Max(1, Math.Min(_gripStart.Width + dx, wa.Right - _paper.Left));
        _gripH = Math.Max(1, Math.Min(_gripStart.Height + dy, wa.Bottom - _paper.Top));
        ApplyResize();
    }

    /// <summary>Lays the note out for the running grip drag: the top-left stays, the size is measured.</summary>
    private void ApplyResize()
    {
        var size = MeasureNote(Unfold);
        Apply(new Rect(_paper.Left, _paper.Top, size.Width, size.Height), size, exact: true);
    }

    private void EndGrip()
    {
        if (!_gripPressed) return;
        bool wasResizing = _resizing, released = _gripReleased;
        _gripPressed = false;
        _gripReleased = false;

        if (wasResizing)
        {
            // The paper's real size, not the cursor's: where the text forced it bigger, that is what he sees.
            _resizing = false;
            _gripW = _gripH = double.NaN;
            State.ManualWidth = _paper.Width;
            State.ManualHeight = _paper.Height;
            State.X = _paper.X;
            State.Y = _paper.Y;
            Changed?.Invoke();
        }
        else if (released)
        {
            ResetToAutoSize();
        }
    }

    /// <summary>Ends a grip press (lock, hide, squash): a running drag keeps the size reached, a press is dropped.</summary>
    private void CancelGrip()
    {
        if (!_gripPressed) return;
        _gripReleased = false;
        if (Grip.IsMouseCaptured) Grip.ReleaseMouseCapture(); // LostMouseCapture runs EndGrip
        else EndGrip();
    }

    // ---- the snap back to the natural size -------------------------------------------------------------

    /// <summary>Drops the manual size and slides to the natural size over 220 ms, right edge fixed.</summary>
    private void ResetToAutoSize()
    {
        State.ManualWidth = double.NaN;
        State.ManualHeight = double.NaN;

        if (!IsVisible || _animating || double.IsNaN(_winLeft))
        {
            // A fold re-measures every frame and picks the auto size up by itself; hidden, nothing to show.
            Relayout();
            Changed?.Invoke();
            return;
        }

        var from = _paper;
        _snapSize = MeasureNote(Unfold);
        var to = Place(from, _snapSize, WorkAreaFor(from));
        State.X = to.X;
        State.Y = to.Y;
        if (to == from)
        {
            Changed?.Invoke();
            return;
        }

        int gen = ++_snapGen;
        _snapping = true;
        _snapFrom = from;
        _snapTo = to;
        var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(SnapMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) =>
        {
            if (gen != _snapGen) return;
            StopSnap();
            Relayout();
        };
        BeginAnimation(SnapProperty, anim, HandoffBehavior.SnapshotAndReplace);
        // Raised now, with PerchPoint already at the end: the bird settles straight on the final perch.
        Changed?.Invoke();
    }

    private void OnSnapFrame()
    {
        if (!_snapping) return;
        double t = (double)GetValue(SnapProperty);
        var r = new Rect(
            Lerp(_snapFrom.Left, _snapTo.Left, t), Lerp(_snapFrom.Top, _snapTo.Top, t),
            Math.Max(0, Lerp(_snapFrom.Width, _snapTo.Width, t)), Math.Max(0, Lerp(_snapFrom.Height, _snapTo.Height, t)));
        // Not exact: the window only grows during the slide and fits exactly again at the end.
        Apply(r, _snapSize, exact: false);
    }

    /// <summary>Stops a snap where it is; the caller lays the note out after (Relayout lands on the end).</summary>
    private void StopSnap()
    {
        _snapping = false;
        _snapGen++;
        BeginAnimation(SnapProperty, null);
    }

    /// <summary>Hide and squash: no grip press and no snap may outlive the note on screen.</summary>
    private void EndGripAndSnap()
    {
        CancelGrip();
        if (_snapping)
        {
            StopSnap();
            Relayout();
        }
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
