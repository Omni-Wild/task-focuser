using System.Windows;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TaskFocuser.Core;

namespace TaskFocuser.Views;

// Sizing and placement. The note is measured by hand, not by SizeToContent: explicit sizes keep the
// growth anchored exactly (right edge fixed) and give the lock/unlock a single animatable number.
public partial class NoteWindow
{
    private const double Pad = 12;           // transparent margin for the shadow, left and top
    // Right and below the stage also holds the vines' overhang (SPEC-round4 2.2: 15 DIP of leaves past the
    // paper's edge), so 16 there; the shadow fits inside either way. Only the window grows: the paper rect
    // (Bounds, PerchPoint, State.X/Y, the drags) is the visible note as before.
    private const double PadRight = 16, PadBottom = 16;
    private const double StripHeight = 6;
    private const double BarHeight = 34;     // one row again (SPEC-round3 2.1)
    // The bird's feet, from the paper's left: 4 DIP right of the middle of the bar's perch slot, as before;
    // the slot is 56 DIP since round 5 (the name under the bird), so the middle moved from 20 to 28.
    private const double PerchOffset = 32;
    private const double PadX = 14, PadY = 10;
    private const double MinNoteWidth = 140, MaxNoteWidth = 520;
    private const double UnfoldHeadroom = 1.12; // BackEase(0.4) peaks near 1.09; reserve a little more

    /// <summary>0 = locked (no bar), 1 = unlocked (bar open). Animated; may overshoot past 1.</summary>
    private static readonly DependencyProperty UnfoldProperty = DependencyProperty.Register(
        "Unfold", typeof(double), typeof(NoteWindow),
        new PropertyMetadata(0.0, (d, _) => ((NoteWindow)d).OnUnfoldFrame()));

    private double Unfold => (double)GetValue(UnfoldProperty);

    private readonly record struct NoteSize(double Width, double Height, double BarHeight, double TextWidth);

    private Rect _paper;                             // the visible note on screen (what Bounds reports)
    private double _winLeft = double.NaN, _winTop = double.NaN, _winW, _winH;
    // Set while the code itself moves the window, so OnLocationChanged tells those moves from the system's.
    private bool _placing;
    private double _barNatural;
    private double _editorChrome = 4;
    private Typeface? _typeface;

    private bool _animating;
    private int _unfoldGen;
    private Rect _animAnchor;                        // the note's rect when the running fold started
    private Rect _animWorkArea;
    private double _animTargetUnfold;
    private Rect _animTarget;                        // where the running fold ends (what PerchPoint reports)

    private NoteSize MeasureNote(double unfold)
    {
        double open = Math.Clamp(unfold, 0, 1);
        double minW = MinNoteWidth + (BarNaturalWidth() - MinNoteWidth) * open;

        // +1 so layout rounding can never make the box a hair narrower than the line and wrap it.
        double natural = Math.Ceiling(NaturalTextWidth() + _editorChrome) + 1;
        double w = Math.Round(Math.Min(MaxNoteWidth, Math.Max(minW, natural + 2 * PadX)));
        // A manual size (SPEC-round4 2.4) is a floor over the auto size: never smaller than he made it,
        // still bigger when the text needs it. The text wraps at the paper's real width. Since SPEC-round5 5
        // the floor holds only while unlocked: locked, the note compacts to its auto size. It comes and goes
        // with the fold (scaled by the same open share as the bar), so the lock shrinks to the compact size in
        // one motion and the unlock grows back to the floor; the fold's target (open 0 or 1) is therefore the
        // final rect, which is what PerchPoint reports during the fold.
        double mw = ManualWidthNow, mh = ManualHeightNow;
        if (!double.IsNaN(mw)) w = Math.Round(Floor(w, mw, open));
        double textW = w - 2 * PadX;
        if (Editor.Width != textW) Editor.Width = textW;
        double textH = Math.Ceiling(WrappedTextHeight(textW - _editorChrome));

        double barH = BarHeight * Math.Max(0, unfold);
        double h = Math.Round(StripHeight + barH + 2 * PadY + textH);
        if (!double.IsNaN(mh)) h = Math.Round(Floor(h, mh, open));
        return new NoteSize(w, h, barH, textW);
    }

    /// <summary>The auto value raised toward the manual floor by the open share: all of it unlocked, none locked.</summary>
    private static double Floor(double auto, double manual, double open) =>
        manual > auto ? auto + (Math.Round(manual) - auto) * open : auto;

    /// <summary>
    /// The text laid out the way the text box lays it out. FormattedText rather than measuring the TextBox
    /// itself: a TextBox re-formats its lines lazily, so inside TextChanged it still reports the old size.
    /// </summary>
    private FormattedText Format(double wrapWidth)
    {
        string text = State.Text;
        // The box shows an empty line after a trailing newline (the caret sits there); FormattedText does not.
        if (text.Length == 0 || text.EndsWith('\n')) text += " ";
        _typeface ??= new Typeface(Editor.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface,
            State.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        // FormattedText trims by default (WordEllipsis): a word wider than the line (a long URL, an e-mail
        // address at a big size) stayed one line here while the text box breaks it over several, so the text
        // ran out below the paper. Without trimming it breaks the word the way the box does.
        ft.Trimming = TextTrimming.None;
        if (!double.IsNaN(wrapWidth)) ft.MaxTextWidth = Math.Max(1, wrapWidth);
        return ft;
    }

    // The fold (and the snap) call MeasureNote on every frame with the same text, font and size, so the two
    // text measurements are memoised on their inputs: a frame then builds no FormattedText at all, and the
    // garbage per fold drops from about seventy line layouts to none. Any change of text, font, size or DPI
    // invalidates both (SPEC 3.11: no per-frame allocations).
    private string _measText = "", _measFont = "";
    private int _measSize = -1;
    private double _measDip = double.NaN;
    private double _measNatural = double.NaN, _measWrapW = double.NaN, _measWrapH;

    private void SyncMeasureKey()
    {
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_measText == State.Text && _measFont == State.Font && _measSize == State.FontSize && _measDip == dip) return;
        _measText = State.Text;
        _measFont = State.Font;
        _measSize = State.FontSize;
        _measDip = dip;
        _measNatural = double.NaN;
        _measWrapW = double.NaN;
    }

    /// <summary>The text's unwrapped width, memoised; the same number Format(NaN) gives.</summary>
    private double NaturalTextWidth()
    {
        SyncMeasureKey();
        if (double.IsNaN(_measNatural)) _measNatural = Format(double.NaN).WidthIncludingTrailingWhitespace;
        return _measNatural;
    }

    /// <summary>The text's height wrapped at <paramref name="wrapWidth"/>, memoised on that width too.</summary>
    private double WrappedTextHeight(double wrapWidth)
    {
        SyncMeasureKey();
        if (_measWrapW != wrapWidth)
        {
            _measWrapW = wrapWidth;
            _measWrapH = Format(wrapWidth).Height;
        }
        return _measWrapH;
    }

    /// <summary>
    /// What the text box adds around its text horizontally (room for the caret). Measured once, on a fresh
    /// box whose first measure is always current, against FormattedText of the same string.
    /// </summary>
    private void CalibrateEditor()
    {
        const string probe = "MMMMMMMM";
        Editor.Text = probe;
        Editor.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double box = Editor.DesiredSize.Width - Editor.Margin.Left - Editor.Margin.Right;
        var saved = State.Text;
        State.Text = probe;
        double bare = Format(double.NaN).WidthIncludingTrailingWhitespace;
        State.Text = saved;
        _editorChrome = Math.Clamp(box - bare, 0, 12);
    }

    private double BarNaturalWidth()
    {
        if (_barNatural <= 0)
        {
            Bar.Measure(new Size(double.PositiveInfinity, BarHeight));
            _barNatural = Math.Ceiling(Bar.DesiredSize.Width);
        }
        return Math.Clamp(_barNatural, MinNoteWidth, MaxNoteWidth);
    }

    /// <summary>
    /// Where a note of <paramref name="size"/> goes when it was at <paramref name="anchor"/>: the right edge
    /// stays, unless the left edge would cross the work area's left edge (then the left edge pins there,
    /// or where it already was if he had pushed it further out). Same idea downward for the bottom edge.
    /// </summary>
    private static Rect Place(Rect anchor, NoteSize size, Rect wa)
    {
        double left = Math.Max(anchor.Right - size.Width, Math.Min(anchor.Left, wa.Left));
        double top = anchor.Top;
        double allowedBottom = Math.Max(anchor.Bottom, wa.Bottom);
        if (top + size.Height > allowedBottom)
            top = Math.Max(allowedBottom - size.Height, Math.Min(anchor.Top, wa.Top));
        return new Rect(left, top, size.Width, size.Height);
    }

    private static Rect WorkAreaFor(Rect r)
    {
        try { return Monitors.WorkAreaAt(new Point(r.X + r.Width / 2, r.Y + r.Height / 2)); }
        catch { return SystemParameters.WorkArea; }
    }

    /// <summary>Re-fits after the text or the look changed, keeping the anchor rules.</summary>
    private void Relayout()
    {
        // A grip drag keeps the top-left where it is; everything else keeps the top-right (Place).
        if (_resizing)
        {
            ApplyResize();
            return;
        }
        // Typing during the snap back to auto ends it where it is; Place then finishes the job, and since
        // the snap keeps the right edge and the top, the result is the same rect.
        if (_snapping) StopSnap();
        if (_animating)
        {
            // Typing or a bar change during the fold moves the end too; the frame measure comes last, so
            // the text box is left at the width of the frame on screen.
            UpdateAnimTarget();
            ApplyFrame();
            return;
        }
        var size = MeasureNote(Unfold);
        Apply(Place(_paper, size, WorkAreaFor(_paper)), size, exact: true);
        State.X = _paper.X;
        State.Y = _paper.Y;
    }

    /// <summary>Puts the note's top-left at <paramref name="topLeft"/> with its current size.</summary>
    private void LayoutAt(Point topLeft)
    {
        var size = MeasureNote(Unfold);
        Apply(new Rect(topLeft.X, topLeft.Y, size.Width, size.Height), size, exact: true);
    }

    /// <summary>
    /// Places the paper at <paramref name="paper"/> on screen. The window keeps a fixed generous width and
    /// moves only when the paper would leave it; vertically it fits exactly at rest, and during a fold it
    /// only grows, so the window is not resized on every animation frame.
    /// </summary>
    private void Apply(Rect paper, NoteSize size, bool exact, Rect? reserve = null)
    {
        var need = paper;
        if (reserve is Rect r) need.Union(r);
        double nl = need.Left - Pad, nt = need.Top - Pad, nr = need.Right + PadRight, nb = need.Bottom + PadBottom;

        double winW = Math.Max(MaxNoteWidth + Pad + PadRight, nr - nl);
        double left = double.IsNaN(_winLeft) ? nr - winW : _winLeft;
        if (nl < left) left = nl;
        if (nr > left + winW) left = nr - winW;

        double top, height;
        if (exact || double.IsNaN(_winTop)) { top = nt; height = nb - nt; }
        else { top = Math.Min(_winTop, nt); height = Math.Max(_winTop + _winH, nb) - top; }

        // Grow before moving and move before shrinking, so the frame in between never crops the paper.
        bool growing = winW >= _winW && height >= _winH;
        _placing = true;
        try
        {
            if (growing) SetWindowSize(winW, height);
            if (left != _winLeft) Left = left;
            if (top != _winTop) Top = top;
            _winLeft = left;
            _winTop = top;
            if (!growing) SetWindowSize(winW, height);
        }
        finally { _placing = false; }

        BarClip.Height = size.BarHeight;
        NoteRoot.Width = paper.Width;
        NoteRoot.Height = paper.Height;
        Canvas.SetLeft(NoteRoot, paper.Left - left);
        Canvas.SetTop(NoteRoot, paper.Top - top);
        _paper = paper;
    }

    private void SetWindowSize(double w, double h)
    {
        if (w != _winW) Width = w;
        if (h != _winH) Height = h;
        _winW = w;
        _winH = h;
    }

    /// <summary>The drag path: one move of the whole window, the paper rides along. No allocations.</summary>
    private void MoveWindowTo(double left, double top)
    {
        var delta = new Vector(left - _winLeft, top - _winTop);
        // A running fold re-places the paper from its anchor on every frame; the anchor and the fold's end
        // move along (a bar drag or a bird drag during the unfold), or the fold would pull the note back to
        // where the drag started and the perch would point at the old spot.
        if (_animating)
        {
            _animAnchor.Offset(delta);
            _animTarget.Offset(delta);
        }
        if (_snapping)
        {
            _snapFrom.Offset(delta);
            _snapTo.Offset(delta);
        }
        _paper.Offset(delta);
        _placing = true;
        try
        {
            Left = left;
            Top = top;
            _winLeft = left;
            _winTop = top;
        }
        finally { _placing = false; }
    }

    /// <summary>
    /// Windows moved the window itself: a monitor unplugged or plugged back in (Windows 11 puts windows back
    /// where they were on that monitor), Win+Shift+arrow. The cached window and paper rects would still point
    /// at the old spot, so the bird would perch there and the next drag (which starts from the cache) would
    /// throw the note back onto the missing monitor. The paper rides along instead and the spot is saved.
    /// </summary>
    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (_placing || double.IsNaN(_winLeft) || !IsVisible) return;
        // The squash animates Left and Top on purpose.
        if (DependencyPropertyHelper.GetValueSource(this, LeftProperty).IsAnimated
            || DependencyPropertyHelper.GetValueSource(this, TopProperty).IsAnimated) return;
        double l = Left, t = Top;
        // Our own moves come back within a pixel's rounding; the system's are whole monitors.
        if (Math.Abs(l - _winLeft) < 2 && Math.Abs(t - _winTop) < 2) return;
        MoveWindowTo(l, t);
        State.X = _paper.X;
        State.Y = _paper.Y;
        Changed?.Invoke();
    }

    /// <summary>
    /// The bird drags the note (SPEC-round2 2.10): the conductor hands over the cursor's delta. The same
    /// window move as the note's own drag; State follows, nothing is raised (the conductor is the caller).
    /// </summary>
    public void NudgeBy(Vector delta)
    {
        if (double.IsNaN(_winLeft) || (delta.X == 0 && delta.Y == 0)) return;
        // The popups are their own windows and stay where they opened while the bird carries the note away.
        Bar.ClosePopups();
        MoveWindowTo(_winLeft + delta.X, _winTop + delta.Y);
        State.X = _paper.X;
        State.Y = _paper.Y;
    }

    // ---- the fold --------------------------------------------------------------------------------

    private void AnimateUnfold(double target)
    {
        int gen = ++_unfoldGen;
        // The fold measures from the paper as it is now; a running snap back to auto stops here (its end
        // keeps the same right edge and top, which is all Place needs).
        if (_snapping) StopSnap();

        if (!IsVisible)
        {
            StopUnfold(target);
            Relayout();
            return;
        }

        // A fold reversed halfway starts from where the paper is now, so the right edge never wanders.
        _animAnchor = _paper;
        _animWorkArea = WorkAreaFor(_paper);
        _animating = true;
        _animTargetUnfold = target;
        // The end is known before the first frame: PerchPoint reports it from here on (SPEC-round3 2.2).
        UpdateAnimTarget();

        var end = MeasureNote(target > 0 ? UnfoldHeadroom : 0);
        Apply(_paper, MeasureNote(Unfold), exact: false, reserve: Place(_animAnchor, end, _animWorkArea));

        bool opening = target > 0;
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(opening ? 220 : 180))
        {
            EasingFunction = opening
                ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 }
                : new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        anim.Completed += (_, _) =>
        {
            if (gen != _unfoldGen) return;
            StopUnfold(target);
            Relayout();
            Changed?.Invoke();
        };
        BeginAnimation(UnfoldProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>The paper rect the running fold settles on: the same Place the last frame will use.</summary>
    private void UpdateAnimTarget()
    {
        _animTarget = Place(_animAnchor, MeasureNote(_animTargetUnfold), _animWorkArea);
    }

    /// <summary>Ends any fold and pins the value, so the next layout is exact.</summary>
    private void StopUnfold(double value)
    {
        _animating = false;
        SetValue(UnfoldProperty, value);
        BeginAnimation(UnfoldProperty, null);
    }

    private void OnUnfoldFrame()
    {
        if (_animating) ApplyFrame();
    }

    private void ApplyFrame()
    {
        var size = MeasureNote(Unfold);
        Apply(Place(_animAnchor, size, _animWorkArea), size, exact: false);
    }
}

/// <summary>
/// The paper and its content grid never clip (SPEC-round5 5). WPF adds a layout clip to an element whose
/// content asks for a hair more than it was given, and the note's sizes are measured by hand, so on a compact
/// locked note with a long line (the text box a fraction of a DIP wider than the rounded paper) the grid
/// clipped the vines' overhang at the paper's edge. Nothing inside the paper may overflow anyway (the sizes
/// are measured from the text), so dropping the clip only lets the vines hang out as they do unlocked.
/// </summary>
public sealed class UnclippedBorder : Border
{
    protected override Geometry? GetLayoutClip(Size layoutSlotSize) => ClipToBounds ? base.GetLayoutClip(layoutSlotSize) : null;
}

/// <inheritdoc cref="UnclippedBorder"/>
public sealed class UnclippedGrid : Grid
{
    protected override Geometry? GetLayoutClip(Size layoutSlotSize) => ClipToBounds ? base.GetLayoutClip(layoutSlotSize) : null;
}
