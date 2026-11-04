using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TaskFocuser.Views;

/// <summary>
/// Pixel-art vines along the paper's bottom and right edges; never takes the mouse. One vine grows from the
/// bottom-right corner left along the bottom edge, one up the right edge, each to about 85 % of its edge
/// and tapering toward the tip (SPEC-round3 2.5, SPEC-round4 2.2 and 5). The stems wrap the paper one cell
/// outside its edge lines, most leaves hanging out over the stage and a few small ones reaching in: the host
/// stretches the view over the paper plus an overhang and says how far in the edge lines are with
/// <see cref="EdgeInset"/>. The cells come from <see cref="VinesPattern"/>; this element only decides how
/// far each vine has grown and paints that prefix in <see cref="OnRender"/>.
/// Static, it costs nothing: no timers, and every animation is removed the moment it completes.
/// </summary>
public sealed class VinesView : FrameworkElement
{
    /// <summary>DIPs per pixel cell, the same grain as the bird.</summary>
    public const double CellSize = 3;

    private const int GrowMs = 320;
    private const int FoldMs = 260;

    private static readonly Brush[] Brushes = BuildBrushes();

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.Register(
        nameof(Enabled), typeof(bool), typeof(VinesView),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, OnEnabledChanged));

    /// <summary>0 to 1: the share of each vine's current length that is shown. <see cref="Enabled"/> animates it.</summary>
    public static readonly DependencyProperty ExtentProperty = DependencyProperty.Register(
        nameof(Extent), typeof(double), typeof(VinesView),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender), IsUnit);

    /// <summary>DIPs from the view's bottom and right edges to the edge line the stems follow: the paper's
    /// edge when the host lets the view overhang the paper by this much, so the stem and the outside leaves
    /// hang over the stage. 0 (round 3 hosting) puts the stem on the view's edge and drops the outside leaves.</summary>
    public static readonly DependencyProperty EdgeInsetProperty = DependencyProperty.Register(
        nameof(EdgeInset), typeof(double), typeof(VinesView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender), IsInset);

    /// <summary>0 to 1: progress of a resize from the previous tips to the new ones. Private because it
    /// only means something together with <see cref="_from"/> and <see cref="_to"/>.</summary>
    private static readonly DependencyProperty GrowthProperty = DependencyProperty.Register(
        "Growth", typeof(double), typeof(VinesView),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    // Per vine (bottom, right), in growth cells: where the tip was when the current resize began, and
    // where it is heading. The shown tip is Extent * lerp(from, to, Growth).
    private readonly double[] _from = new double[2];
    private readonly double[] _to = new double[2];
    private int _cellsW = -1, _cellsH = -1;

    // Bumped on every BeginAnimation, so the Completed of an animation that was replaced does not remove
    // its successor.
    private int _extentGen, _growthGen;

    // Reused across renders: during an animation OnRender runs every frame.
    private readonly Dictionary<int, byte> _grid = new();
    private readonly List<int> _keys = new();

    public VinesView()
    {
        IsHitTestVisible = false;
        Focusable = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    public bool Enabled
    {
        get => (bool)GetValue(EnabledProperty);
        set => SetValue(EnabledProperty, value);
    }

    public double Extent
    {
        get => (double)GetValue(ExtentProperty);
        set => SetValue(ExtentProperty, value);
    }

    public double EdgeInset
    {
        get => (double)GetValue(EdgeInsetProperty);
        set => SetValue(EdgeInsetProperty, value);
    }

    private static bool IsUnit(object v) => v is double d && d >= 0 && d <= 1;
    private static bool IsInset(object v) => v is double d && d >= 0 && !double.IsInfinity(d);

    // Cells between the view's edges and the edge lines: room for the stem and the outside leaves.
    private int OverhangCells => (int)Math.Floor(EdgeInset / CellSize);

    // The paper's edges in cells, measured along the edge lines (growth reaches about 85 % of each).
    private int EdgeCellsW => (int)Math.Floor(Math.Max(0, ActualWidth - EdgeInset) / CellSize);
    private int EdgeCellsH => (int)Math.Floor(Math.Max(0, ActualHeight - EdgeInset) / CellSize);

    // The vines take no room of their own: the host stretches them over the paper.
    protected override Size MeasureOverride(Size availableSize) => default;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Resize(animate: true);
    }

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var v = (VinesView)d;
        bool on = (bool)e.NewValue;
        double target = on ? 1 : 0;
        if (!v.IsLoaded)
        {
            // Before the note is on screen (the state is applied at start-up) there is nothing to animate from.
            v.Stop(ExtentProperty, ref v._extentGen);
            v.Extent = target;
            return;
        }
        // Grow out from wherever a half-finished fold left the vines, and fold back the same way.
        double now = v.Extent;
        if (on && now <= 0) v.SyncWithoutAnimation(); // the size may have changed while hidden
        v.Animate(ExtentProperty, now, target, on ? GrowMs : FoldMs,
            new CubicEase { EasingMode = on ? EasingMode.EaseOut : EasingMode.EaseIn }, ref v._extentGen);
    }

    /// <summary>Takes the new size in cells and eases each tip from where it is to its new length on its edge.</summary>
    private void Resize(bool animate)
    {
        int w = EdgeCellsW, h = EdgeCellsH;
        if (w == _cellsW && h == _cellsH) return;
        bool first = _cellsW < 0;
        _cellsW = w;
        _cellsH = h;

        double toBottom = VinesPattern.LengthFor(w), toRight = VinesPattern.LengthFor(h);
        // No animation before the note is shown, while the vines are hidden, or when the tips do not move.
        if (!animate || first || !IsLoaded || !Enabled || Extent <= 0
            || (Math.Abs(toBottom - _to[0]) < 0.5 && Math.Abs(toRight - _to[1]) < 0.5))
        {
            SetTips(toBottom, toRight);
            return;
        }

        // Start from the tip actually on screen now, even mid-animation, so a burst of resizes while
        // typing chains smoothly instead of jumping back.
        double g = (double)GetValue(GrowthProperty);
        for (int i = 0; i < 2; i++) _from[i] = _from[i] + (_to[i] - _from[i]) * g;
        _to[0] = toBottom;
        _to[1] = toRight;
        Animate(GrowthProperty, 0, 1, GrowMs, new CubicEase { EasingMode = EasingMode.EaseOut }, ref _growthGen);
    }

    private void SyncWithoutAnimation()
    {
        _cellsW = _cellsH = int.MinValue; // force Resize to recompute
        Resize(animate: false);
    }

    private void SetTips(double bottom, double right)
    {
        Stop(GrowthProperty, ref _growthGen);
        _from[0] = _to[0] = bottom;
        _from[1] = _to[1] = right;
        SetValue(GrowthProperty, 1.0);
    }

    /// <summary>
    /// Runs one animation that leaves no clock behind: the final value is written as the base value first,
    /// the animation uses <see cref="FillBehavior.Stop"/> and is removed in Completed, so once it ends the
    /// property is plain local state and nothing keeps ticking (the app pays for clocks left filling).
    /// </summary>
    private void Animate(DependencyProperty dp, double from, double to, int ms, IEasingFunction ease, ref int gen)
    {
        int mine = ++gen;
        SetValue(dp, to);
        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop,
        };
        bool isExtent = dp == ExtentProperty;
        anim.Completed += (_, _) =>
        {
            int current = isExtent ? _extentGen : _growthGen;
            if (current == mine) BeginAnimation(dp, null);
        };
        BeginAnimation(dp, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private void Stop(DependencyProperty dp, ref int gen)
    {
        gen++;
        BeginAnimation(dp, null);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double extent = Extent;
        if (extent <= 0 || ActualWidth <= 0 || ActualHeight <= 0) return;
        // A layout that skipped OnRenderSizeChanged (an offscreen render, a size set before load) still
        // draws the right length, just without the ease.
        if (EdgeCellsW != _cellsW || EdgeCellsH != _cellsH)
            Resize(animate: false);

        // Outside cells may use the overhang but keep a cell of air before the view's edge, where the
        // window's stage ends. With no room for the stem outside (EdgeInset 0) the drawing shifts in so the
        // stem hugs the view's own edge and the outside leaves are dropped, as in round 3.
        int overhang = OverhangCells;
        int outside = overhang >= 2 ? 1 - overhang : -overhang;
        int shift = Math.Max(0, outside - VinesPattern.StemDepth);
        double g = (double)GetValue(GrowthProperty);
        _grid.Clear();
        for (int v = 0; v < 2; v++)
        {
            // The vine is built at its current length, taper included, and the extent shows a prefix of it:
            // a resize grows or shrinks the taper with the vine, a toggle adds or removes cells at the tip.
            int length = (int)Math.Round(_from[v] + (_to[v] - _from[v]) * g);
            double tip = extent * length;
            if (tip < 1) continue;
            foreach (var cell in VinesPattern.Cells(v, length))
            {
                if (cell.At > tip) break; // the list is ordered root to tip
                int x = cell.X + shift, y = cell.Y + shift;
                if (x >= _cellsW || y >= _cellsH) continue; // a tiny note: keep the vines on the paper
                if (x < outside || y < outside) continue;
                _grid[Key(x, y)] = cell.Color; // later cells win, so a leaf overdraws the stem it grows from
            }
        }
        if (_grid.Count == 0) return;

        _keys.Clear();
        _keys.AddRange(_grid.Keys);
        _keys.Sort(); // by row, then by cell from the right edge line

        // Cells are anchored to the edge lines so the drawing hugs the paper's corner at any size; the
        // inset is used as given so the line lands exactly on the paper's edge.
        double right = ActualWidth - EdgeInset, bottom = ActualHeight - EdgeInset;
        int i = 0;
        while (i < _keys.Count)
        {
            int key = _keys[i], y = (key >> 12) - KeyBias, x0 = (key & 0xFFF) - KeyBias;
            byte color = _grid[key];
            int x1 = x0, j = i + 1;
            // Merge a horizontal run of equal cells into one rectangle: fewer primitives and no seams.
            while (j < _keys.Count && _keys[j] == Key(x1 + 1, y) && _grid[_keys[j]] == color) { x1++; j++; }
            dc.DrawRectangle(Brushes[color], null,
                new Rect(right - (x1 + 1) * CellSize, bottom - (y + 1) * CellSize, (x1 - x0 + 1) * CellSize, CellSize));
            i = j;
        }
    }

    // Outside cells have negative coordinates; the bias keeps the packed key positive and sortable.
    private const int KeyBias = 16;
    private static int Key(int x, int y) => (y + KeyBias) << 12 | (x + KeyBias);

    private static Brush[] BuildBrushes()
    {
        string[] hex = { "#00000000", "#3E7D3E", "#5FA85F", "#8FD48F", "#FF9BC1" };
        var brushes = new Brush[hex.Length];
        for (int i = 0; i < hex.Length; i++)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex[i]));
            b.Freeze();
            brushes[i] = b;
        }
        return brushes;
    }
}
