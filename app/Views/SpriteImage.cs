using System.Windows;
using System.Windows.Media;
using TaskFocuser.Monster;

namespace TaskFocuser.Views;

/// <summary>
/// A sprite frame or glyph as a small frozen image, for the places that show one outside the companion's own
/// window: the picker's portraits, the bar's treat and the carried treat (SPEC-round5 2.3, SPEC-round6 2.6).
/// </summary>
internal static class SpriteImage
{
    // One brush per legend key, shared by every image: the legends of all companions plus the glyphs, merged,
    // so a treat drawn in one companion's colours renders whichever skin is active.
    private static readonly Dictionary<char, Brush> CellBrushes = BuildBrushes();

    /// <summary>The rows at <paramref name="cellDip"/> DIP per cell, one rectangle per run of equal cells, as the sprite view draws it.</summary>
    internal static ImageSource Render(string[] rows, double cellDip)
    {
        int cols = 0;
        foreach (var r in rows) cols = Math.Max(cols, r.Length);
        var group = new DrawingGroup();
        RenderOptions.SetEdgeMode(group, EdgeMode.Aliased);
        using (var dc = group.Open())
        {
            // The transparent backdrop pins the bounds to the whole grid, so the feet sit where the grid says.
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, cols * cellDip, rows.Length * cellDip));
            for (int y = 0; y < rows.Length; y++)
            {
                string row = rows[y];
                int x = 0;
                while (x < row.Length)
                {
                    char ch = row[x];
                    int start = x;
                    while (x < row.Length && row[x] == ch) x++;
                    if (CellBrushes.TryGetValue(ch, out var b))
                        dc.DrawRectangle(b, null, new Rect(start * cellDip, y * cellDip, (x - start) * cellDip, cellDip));
                }
            }
        }
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// <summary>The size <see cref="Render"/> gives the rows, for the Image that shows it.</summary>
    internal static Size SizeOf(string[] rows, double cellDip)
    {
        int cols = 0;
        foreach (var r in rows) cols = Math.Max(cols, r.Length);
        return new Size(cols * cellDip, rows.Length * cellDip);
    }

    private static Dictionary<char, Brush> BuildBrushes()
    {
        var map = new Dictionary<char, Brush>();
        foreach (var (key, hex) in Birds.MergedLegend)
        {
            if (string.IsNullOrEmpty(hex)) continue; // transparent cells are not drawn
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            map[key] = b;
        }
        return map;
    }
}
