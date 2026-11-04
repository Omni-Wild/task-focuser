namespace TaskFocuser.Views;

/// <summary>
/// One painted cell of a vine, in cells from the paper's edge lines (the bottom and right edges the stems
/// wrap, see <see cref="VinesView.EdgeInset"/>). <see cref="X"/> counts leftwards from the right edge line
/// and <see cref="Y"/> upwards from the bottom one; 0 is the first cell inside the paper and negative cells
/// hang outside it, over the transparent stage. Anchoring to the corner means a resize never moves what is
/// already drawn. <see cref="At"/> is how many cells of growth the vine needs before this cell shows: stem
/// cells get their index along the edge, leaf cells a little more, so a leaf unfurls just behind the tip.
/// </summary>
internal readonly record struct VineCell(int X, int Y, byte Color, int At);

/// <summary>
/// The procedural vines (SPEC-round4 2.2 and 5). Each vine has a fixed plan, made once from a fixed per-vine
/// seed and longer than any note: where its leaves sit, on which side, which shape, where the stem wobbles
/// and where a bud hangs. A vine of a given length is that plan cut to the length with the last 20 % tapered
/// (smaller leaves, wider gaps, a calm stem, a thin curl or a lone leaf at the end). The picture is identical
/// at the same size, and a longer note keeps every leaf it had: the old tip's small leaves simply mature.
/// </summary>
internal static class VinesPattern
{
    public const int Bottom = 0;
    public const int Right = 1;

    public const byte Stem = 1;      // stem and leaf base
    public const byte Leaf = 2;
    public const byte Shine = 3;     // leaf highlight
    public const byte Bud = 4;

    /// <summary>Longest vine planned: 400 cells is 1200 DIP of edge, far past the largest note.</summary>
    private const int Capacity = 400;

    /// <summary>The stem runs one cell outside the edge line, over the stage, so the vine wraps the paper
    /// instead of resting on it (SPEC-round4 5).</summary>
    public const int StemDepth = -1;

    /// <summary>Deepest cell inside the paper: 2 cells (6 DIP), so the vine only reaches into the paper a bit.</summary>
    public const int MaxInside = 1;

    /// <summary>Furthest cell outside the edge line (as a positive count): the stem plus 3 cells of leaf. The
    /// host's overhang is 5 cells, so a cell of air stays between the greenery and the view's own edge.</summary>
    public const int MaxOutside = 4;

    /// <summary>Share of the edge each vine reaches, measured along the edge line.</summary>
    private const double Reach = 0.85;

    /// <summary>Share of each vine, at its tip end, that tapers.</summary>
    private const double TaperShare = 0.2;

    // Leaf shapes in vine-local cells: (along the edge towards the tip, away from the stem, colour). Listed
    // base first so the order is also the unfurl order; they lean towards the tip, the highlight tip-ward.
    private static readonly (int dt, int dd, byte c)[] Round =
        { (0, 1, Stem), (1, 1, Leaf), (0, 2, Leaf), (1, 2, Shine), (2, 2, Leaf), (1, 3, Leaf), (2, 3, Leaf) };
    private static readonly (int dt, int dd, byte c)[] Long =
        { (0, 1, Stem), (1, 1, Leaf), (1, 2, Leaf), (2, 2, Shine), (2, 3, Leaf) };
    private static readonly (int dt, int dd, byte c)[] Small =
        { (0, 1, Stem), (1, 1, Leaf), (1, 2, Shine), (2, 2, Leaf) };
    private static readonly (int dt, int dd, byte c)[] Twin =
        { (0, 1, Stem), (-1, 2, Leaf), (1, 2, Shine) };

    // The taper's leaves, largest first: 3 cells, 2 cells, 1 cell.
    private static readonly (int dt, int dd, byte c)[][] Taper =
    {
        new[] { (0, 1, Stem), (1, 1, Leaf), (1, 2, Shine) },
        new[] { (0, 1, Leaf), (1, 2, Leaf) },
        new[] { (1, 1, Leaf) },
    };

    // Outside leaves carry the look (dd up to 3); inside ones are the smaller shapes (dd up to 2).
    private static readonly (int dt, int dd, byte c)[][] OutsideShapes = { Round, Round, Round, Long };
    private static readonly (int dt, int dd, byte c)[][] InsideShapes = { Small, Small, Twin, Taper[0] };

    /// <summary>One leaf of the plan: where, on which side, which shape, and whether a bud hangs in the gap after it.</summary>
    private readonly record struct LeafPlan(int At, bool Inside, int Shape, bool BudAfter);

    /// <summary>A stretch where the stem droops a cell further out, from <see cref="From"/> to <see cref="To"/>.</summary>
    private readonly record struct Droop(int From, int To);

    private static readonly (LeafPlan[] Leaves, Droop[] Droops)[] Plans = { Plan(0x5EED_B077), Plan(0x0BAD_F00D) };

    // Built vines by (vine, length); a resize animation walks through the lengths between two sizes.
    private static readonly Dictionary<int, VineCell[]> Built = new();

    /// <summary>How many cells of growth reach about 85 % of an edge of <paramref name="edgeCells"/>
    /// cells, measured along the edge line.</summary>
    public static int LengthFor(int edgeCells) =>
        Math.Clamp((int)Math.Round(edgeCells * Reach), 0, Capacity);

    /// <summary>The cells of one vine grown to <paramref name="length"/>, sorted by <see cref="VineCell.At"/>
    /// (root to tip, every At within the length), the taper last.</summary>
    public static VineCell[] Cells(int vine, int length)
    {
        length = Math.Clamp(length, 0, Capacity);
        int key = vine << 16 | length;
        if (Built.TryGetValue(key, out var cells)) return cells;
        if (Built.Count > 128) Built.Clear(); // a few sizes are live at once; drop the rest
        return Built[key] = Build(vine, length);
    }

    /// <summary>The fixed plan: leaves 4 to 7 cells apart, about one in three inside the paper (never two
    /// inside in a row, never more than two outside in a row), a bud in about one gap in four, and now and
    /// then a stretch of 5 to 9 cells where the stem droops a cell further out (the wobble).</summary>
    private static (LeafPlan[], Droop[]) Plan(uint seed)
    {
        var rng = new Rng(seed);
        var leaves = new List<LeafPlan>();
        int outsideRun = 0;
        bool lastInside = false;
        for (int t = 3 + rng.Next(0, 2), n = 0; t < Capacity; t += rng.Next(4, 8), n++)
        {
            // The first leaf of each vine is a round one outside: with the knot it marks where the vines start.
            bool inside = n > 0 && !lastInside && (outsideRun >= 2 || rng.Next(0, 100) < 25);
            outsideRun = inside ? 0 : outsideRun + 1;
            lastInside = inside;
            int shape = n == 0 ? 0 : rng.Next(0, 4);
            leaves.Add(new LeafPlan(t, inside, shape, rng.Next(0, 100) < 25));
        }
        var droops = new List<Droop>();
        for (int t = 10 + rng.Next(0, 8); t < Capacity; )
        {
            int len = rng.Next(5, 10);
            droops.Add(new Droop(t, t + len - 1));
            t += len + rng.Next(10, 26);
        }
        return (leaves.ToArray(), droops.ToArray());
    }

    private static VineCell[] Build(int vine, int length)
    {
        var cells = new List<VineCell>(length * 3 + 16);
        var stem = new HashSet<int>();
        void Put(int t, int d, byte c, int at)
        {
            if (d < -MaxOutside || d > MaxInside || t < 0) return;
            // Leaves and buds never paint over the stem, so it stays one unbroken line.
            if (c == Stem ? !stem.Add(t << 8 | (d + 128)) : stem.Contains(t << 8 | (d + 128))) return;
            at = Math.Min(at, length);
            // Bottom vine: t runs leftwards along the bottom edge; right vine: t runs up the right edge.
            cells.Add(vine == Bottom ? new VineCell(t, d, c, at) : new VineCell(d, t, c, at));
        }

        if (vine == Bottom) AddKnot(cells);
        if (length < 2) return Sorted(cells);

        // The taper is the last 20 %; the stem's last cell is length - 1, the end piece sits on length.
        int taper = length - Math.Max(2, (int)Math.Ceiling(length * TaperShare));
        var (plan, droops) = Plans[vine];

        // Stem depth per cell: one cell out, drooping one more in the planned stretches that end before
        // the taper, so the stem calms toward the tip.
        var depth = new int[length];
        Array.Fill(depth, StemDepth);
        foreach (var dr in droops)
        {
            if (dr.To + 1 >= taper) break;
            for (int i = dr.From; i <= dr.To; i++) depth[i] = StemDepth - 1;
        }

        // Stem cells from the corner (t = 0 is the stem beside the knot) to the last cell before the end.
        // Stems go in first, so the leaves and buds below know where not to paint.
        for (int i = 0; i < length; i++)
        {
            // A one-cell line joined only at a diagonal reads as a break at this size; a short riser keeps
            // the stem visibly continuous where it steps.
            if (i > 0 && depth[i] != depth[i - 1]) Put(i, depth[i - 1], Stem, i);
            Put(i, depth[i], Stem, i);
        }

        // Leaves: the body as planned, the taper thinned out and shrinking toward the tip.
        int kept = int.MinValue / 2;
        bool lastSide = false;
        for (int n = 0; n < plan.Length; n++)
        {
            var leaf = plan[n];
            int at = leaf.At;
            if (at + 2 > length - 2) break; // keep the end piece clear
            int s = depth[at], sign = leaf.Inside ? 1 : -1;
            (int dt, int dd, byte c)[] shape;
            if (at < taper)
            {
                shape = leaf.Inside ? InsideShapes[leaf.Shape] : OutsideShapes[leaf.Shape];
                // On a drooping stretch an outside leaf has a cell less of room: take the smaller leaf.
                int room = leaf.Inside ? MaxInside - s : MaxOutside + s;
                if (shape.Max(p => p.dd) > room) shape = room >= 2 ? Small : Taper[2];
            }
            else
            {
                double p = (at - taper) / (double)Math.Max(1, length - taper);
                // The gaps open up from about 5 cells to about 9 toward the tip.
                if (at - kept < 5 + (int)Math.Round(4 * p)) continue;
                shape = Taper[Math.Min(2, (int)(p * 3))];
            }
            foreach (var (dt, dd, c) in shape)
                Put(at + dt, s + sign * dd, c, at + Math.Max(Math.Abs(dt), Math.Abs(dd)));
            kept = at;
            lastSide = leaf.Inside;

            // A bud on a one-cell stalk, midway to the next leaf on the side this leaf is not on.
            if (leaf.BudAfter && n + 1 < plan.Length && plan[n + 1].At < taper)
            {
                int budT = (at + plan[n + 1].At + 1) / 2, bs = depth[budT];
                int bsign = leaf.Inside ? -1 : 1;
                Put(budT, bs + bsign, Stem, budT + 1);
                Put(budT, bs + 2 * bsign, Bud, budT + 2);
            }
        }

        AddEnd(Put, vine, length, depth[length - 1], kept, lastSide);
        return Sorted(cells);
    }

    /// <summary>The tip: a thin two-cell curl hooking back on the bottom vine, a lone small leaf on the
    /// right one. The curl turns away from the paper unless the last leaf already hangs out right beside it.</summary>
    private static void AddEnd(Action<int, int, byte, int> put, int vine, int length, int s, int lastLeaf, bool lastInside)
    {
        int t = length;
        if (vine == Bottom)
        {
            int sign = !lastInside && t - lastLeaf < 5 ? 1 : -1;
            put(t, s + sign, Stem, t);
            put(t - 1, s + 2 * sign, Stem, t);
        }
        else
        {
            put(t, s - 1, Leaf, t);
        }
    }

    /// <summary>The knot on the corner: the two stems meet in the corner cell outside the paper, and a small
    /// leaf tucks into the paper's corner. Nothing points out along the diagonal.</summary>
    private static void AddKnot(List<VineCell> cells)
    {
        cells.Add(new VineCell(-1, -1, Stem, 0));
        cells.Add(new VineCell(0, 0, Stem, 1));
        cells.Add(new VineCell(1, 0, Leaf, 2));
        cells.Add(new VineCell(0, 1, Leaf, 2));
        cells.Add(new VineCell(1, 1, Shine, 2));
    }

    // Stable sort by growth step: drawing walks the list and stops at the first cell past the tip.
    private static VineCell[] Sorted(List<VineCell> cells) =>
        cells.Select((c, i) => (c, i)).OrderBy(p => p.c.At).ThenBy(p => p.i).Select(p => p.c).ToArray();

    /// <summary>xorshift32: tiny and identical on every runtime, unlike a seeded System.Random whose
    /// sequence is only promised for the legacy algorithm.</summary>
    private struct Rng
    {
        private uint _s;
        public Rng(uint seed) => _s = seed == 0 ? 0x9E3779B9u : seed;

        /// <summary>A value in [min, max).</summary>
        public int Next(int min, int max)
        {
            _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5;
            return max <= min ? min : min + (int)(_s % (uint)(max - min));
        }
    }
}
