using System.Windows;
using System.Windows.Input;
using TaskFocuser.Monster;

namespace TaskFocuser.Views;

// The treat drag (SPEC-round2 2.9; the treat follows the companion since SPEC-round6 2.6). The bar's treat is
// only a handle: the note runs the drag because it owns the screen coordinates, the capture lives on the handle
// (part of the bar) and the carried treat is its own small window. The bar's glyph never moves: the supply is endless.
public partial class NoteWindow
{
    public event Action<Point>? CucumberDropped;
    public event Action? TreatDragStarted;
    public event Action? TreatDragCancelled;

    private CucumberWindow? _slice;
    private bool _slicePressed, _sliceDragging;
    // Set by the button's release just before it lets the capture go: only that loss of the capture is a drop.
    private bool _sliceReleased;
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
            _sliceReleased = true;
            h.ReleaseMouseCapture();
            // The release normally ends the drag through LostMouseCapture above; without a capture to lose
            // (it was refused at the press) nothing would, and the treat would hang under the cursor with the
            // dog begging. EndSlice is a no-op once it has run.
            EndSlice();
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
        _sliceReleased = false;
        _slicePress = ScreenPoint(e.GetPosition(this));
        // A refused capture (the handle not hit-testable this instant) is no drag: the moves would come only while
        // over the handle and a release elsewhere would never reach it.
        if (!Bar.CucumberHandle.CaptureMouse()) _slicePressed = false;
    }

    private void OnSliceMove(object sender, MouseEventArgs e)
    {
        if (!_slicePressed) return;
        var p = ScreenPoint(e.GetPosition(this));
        bool first = false;
        if (!_sliceDragging)
        {
            if (Math.Abs(p.X - _slicePress.X) <= DragThreshold && Math.Abs(p.Y - _slicePress.Y) <= DragThreshold) return;
            _sliceDragging = true;
            first = true;
            // Created on the first drag: an owner must have been shown, and most sessions never drag one.
            _slice ??= new CucumberWindow { Owner = this };
            // Read at every pick-up rather than pushed on every pick: the carried treat exists only during a drag.
            _slice.SetTreat(Birds.ById(State.Bird).Treat);
        }
        _slice?.ShowAt(p);
        // After the show, so a dog that sits up begging already sees the treat in his hand.
        if (first) TreatDragStarted?.Invoke();
    }

    private void EndSlice()
    {
        if (!_slicePressed) return;
        bool wasDragging = _sliceDragging, released = _sliceReleased;
        _slicePressed = false;
        _sliceDragging = false;
        _sliceReleased = false;
        if (!wasDragging) return;

        // The fall always plays: a drop lets it go, and so does a capture taken away mid-drag.
        _slice?.FallAway();
        if (!released)
        {
            // Alt-Tab or another window took the mouse while he held the treat: he never let go over anything.
            TreatDragCancelled?.Invoke();
            return;
        }
        // The same window-to-screen arithmetic as the note's own drag events, so the conductor compares
        // like with like when it asks the companion whether the point is on it.
        var p = ScreenPoint(Mouse.GetPosition(this));
        // If the companion eats it, the chomp runs on it at the same time as the fall.
        CucumberDropped?.Invoke(p);
    }

    /// <summary>Drops a treat drag without a drop (lock, hide, squash): the treat simply vanishes.</summary>
    private void CancelCucumber()
    {
        bool captured = _slicePressed && Bar.CucumberHandle.IsMouseCaptured;
        bool wasDragging = _slicePressed && _sliceDragging;
        // Flags first, so the LostMouseCapture the release raises finds nothing to end.
        _slicePressed = false;
        _sliceDragging = false;
        _sliceReleased = false;
        if (captured) Bar.CucumberHandle.ReleaseMouseCapture();
        _slice?.HideNow();
        if (wasDragging) TreatDragCancelled?.Invoke();
    }
}
