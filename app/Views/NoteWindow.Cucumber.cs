using System.Windows;
using System.Windows.Input;

namespace TaskFocuser.Views;

// The cucumber drag (SPEC-round2 2.9). The bar's slice is only a handle: the note runs the drag because
// it owns the screen coordinates, the capture lives on the handle (part of the bar) and the carried slice
// is its own small window. The bar's glyph never moves: the supply is endless.
public partial class NoteWindow
{
    public event Action<Point>? CucumberDropped;

    private CucumberWindow? _slice;
    private bool _slicePressed, _sliceDragging;
    private Point _slicePress;

    private void WireCucumber()
    {
        var h = Bar.CucumberHandle;
        h.MouseLeftButtonDown += OnSlicePress;
        h.MouseMove += OnSliceMove;
        h.MouseLeftButtonUp += (_, e) =>
        {
            if (!_slicePressed) return;
            e.Handled = true;
            h.ReleaseMouseCapture();
        };
        // Release, a lost capture (Alt-Tab, another window grabbing the mouse) both end here.
        h.LostMouseCapture += (_, _) => EndSlice();
    }

    private void OnSlicePress(object sender, MouseButtonEventArgs e)
    {
        // Handled in every case, so a press on the slice never arms a note drag or half a double-click.
        e.Handled = true;
        if (State.Locked || _slicePressed) return;
        _slicePressed = true;
        _sliceDragging = false;
        _slicePress = ScreenPoint(e.GetPosition(this));
        Bar.CucumberHandle.CaptureMouse();
    }

    private void OnSliceMove(object sender, MouseEventArgs e)
    {
        if (!_slicePressed) return;
        var p = ScreenPoint(e.GetPosition(this));
        if (!_sliceDragging)
        {
            if (Math.Abs(p.X - _slicePress.X) <= DragThreshold && Math.Abs(p.Y - _slicePress.Y) <= DragThreshold) return;
            _sliceDragging = true;
            // Created on the first drag: an owner must have been shown, and most sessions never drag one.
            _slice ??= new CucumberWindow { Owner = this };
        }
        _slice?.ShowAt(p);
    }

    private void EndSlice()
    {
        if (!_slicePressed) return;
        bool wasDragging = _sliceDragging;
        _slicePressed = false;
        _sliceDragging = false;
        if (!wasDragging) return;

        // The same window-to-screen arithmetic as the note's own drag events, so the conductor compares
        // like with like when it asks the bird whether the point is on it.
        var p = ScreenPoint(Mouse.GetPosition(this));
        // The fall always plays; if the bird eats it, the chomp runs on the bird at the same time.
        _slice?.FallAway();
        CucumberDropped?.Invoke(p);
    }

    /// <summary>Drops a slice drag without a drop (lock, hide, squash): the slice simply vanishes.</summary>
    private void CancelCucumber()
    {
        bool captured = _slicePressed && Bar.CucumberHandle.IsMouseCaptured;
        // Flags first, so the LostMouseCapture the release raises finds nothing to end.
        _slicePressed = false;
        _sliceDragging = false;
        if (captured) Bar.CucumberHandle.ReleaseMouseCapture();
        _slice?.HideNow();
    }
}
