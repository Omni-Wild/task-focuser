using System.Windows;
using System.Windows.Media;

namespace TaskFocuser.Monster;

/// <summary>
/// Draws one sprite frame at <see cref="Sprites.CellSize"/> DIPs per cell, hard-edged.
/// Every frame is built once into a frozen drawing, so changing <see cref="Frame"/> at flap speed
/// only swaps which cached drawing the next render uses.
/// </summary>
public sealed class SpriteView : FrameworkElement
{
    private sealed record Baked(Drawing Drawing, Transform Flip, Size Size);

    // Keyed by array identity: the frames are the static arrays in Sprites, and hashing the rows
    // on every frame switch would cost more than the drawing itself.
    private static readonly Dictionary<string[], Baked> Cache = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<char, Brush> Brushes = BuildBrushes();

    public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(
        nameof(Frame), typeof(string[]), typeof(SpriteView),
        new FrameworkPropertyMetadata(Sprites.IdleOpen,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty FlipXProperty = DependencyProperty.Register(
        nameof(FlipX), typeof(bool), typeof(SpriteView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    static SpriteView()
    {
        foreach (var f in Sprites.All) Bake(f);
    }

    public SpriteView()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    /// <summary>The grid to show; one of the arrays in <see cref="Sprites"/>.</summary>
    public string[] Frame
    {
        get => (string[])GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    /// <summary>Mirrors the frame so the monster faces left (the art faces right).</summary>
    public bool FlipX
    {
        get => (bool)GetValue(FlipXProperty);
        set => SetValue(FlipXProperty, value);
    }

    /// <summary>Bakes every frame of a bird up front (round 5), so its first flight never builds a drawing mid-animation.</summary>
    public static void Prebake(string[][] frames)
    {
        foreach (var f in frames) Bake(f);
    }

    protected override Size MeasureOverride(Size availableSize) => Bake(Frame).Size;

    protected override void OnRender(DrawingContext dc)
    {
        var baked = Bake(Frame);
        if (FlipX) dc.PushTransform(baked.Flip);
        dc.DrawDrawing(baked.Drawing);
        if (FlipX) dc.Pop();
    }

    private static Baked Bake(string[] rows)
    {
        if (Cache.TryGetValue(rows, out var hit)) return hit;

        const double c = Sprites.CellSize;
        int cols = 0;
        foreach (var r in rows) cols = Math.Max(cols, r.Length);

        var group = new DrawingGroup();
        RenderOptions.SetEdgeMode(group, EdgeMode.Aliased);
        using (var dc = group.Open())
        {
            // A transparent backdrop pins the drawing's bounds to the full grid, so every frame
            // lines up on the same origin whatever its outermost painted cell is.
            dc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0, 0, cols * c, rows.Length * c));
            for (int y = 0; y < rows.Length; y++)
            {
                string row = rows[y];
                int x = 0;
                while (x < row.Length)
                {
                    char ch = row[x];
                    int start = x;
                    while (x < row.Length && row[x] == ch) x++;
                    // One rectangle per run of equal cells: fewer primitives, and no hairline seams
                    // between neighbouring cells of the same colour.
                    if (Brushes.TryGetValue(ch, out var brush))
                        dc.DrawRectangle(brush, null, new Rect(start * c, y * c, (x - start) * c, c));
                }
            }
        }
        group.Freeze();

        var size = new Size(cols * c, rows.Length * c);
        var flip = new ScaleTransform(-1, 1, size.Width / 2, 0);
        flip.Freeze();
        var baked = new Baked(group, flip, size);
        Cache[rows] = baked;
        return baked;
    }

    private static Dictionary<char, Brush> BuildBrushes()
    {
        var map = new Dictionary<char, Brush>();
        // Every bird's legend plus the glyphs (round 5): the keys never collide, so one brush map serves all.
        foreach (var (key, hex) in Birds.MergedLegend)
        {
            if (string.IsNullOrEmpty(hex)) continue; // transparent cells are simply not drawn
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            map[key] = brush;
        }
        return map;
    }
}
