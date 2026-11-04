namespace TaskFocuser.Monster;

// The bird (a parakeet, after Lenny) as text grids, so the art can be read and edited by eye and
// parsed by tools/make-icon.ps1 (a regex over this file): keep the Legend entries one per line in
// the ['x'] = "#RRGGBB", shape and each frame as one quoted row per line.
// Every bird frame shares one size (Columns x Rows). The art faces right (the beak points right;
// SpriteView.FlipX mirrors it). Row 15, the bottom row, is the feet line of every perched frame,
// so a frame swap never makes the bird sink or float; only Tumble and Hop leave it. The top rows
// are headroom for the raised wings.
public static partial class Sprites
{
    /// <summary>DIPs per cell; drawn aliased so the cells stay hard-edged.</summary>
    public const int CellSize = 3;

    public const int Columns = 22;
    public const int Rows = 16;

    public static readonly Dictionary<char, string> Legend = new()
    {
        ['.'] = "",
        ['o'] = "#1F4FBF",
        ['b'] = "#4F8DFF",
        ['l'] = "#9CC4FF",
        ['w'] = "#FFFFFF",
        ['k'] = "#1A1A2E",
        ['f'] = "#BFE0FF",
        ['p'] = "#FF9EC4",
        ['y'] = "#F2A33A",
        ['n'] = "#C9771F",
        ['r'] = "#FF6FA3",
        ['d'] = "#D9D2C2",
        ['g'] = "#3E7D3E",
        ['c'] = "#DDF3C9",
        ['s'] = "#B9D98C",
        ['e'] = "#8A5A2B",
    };

    // Perched: wings folded at the sides like little arms, tail down behind. The app icon is
    // drawn from this frame.
    public static readonly string[] IdleOpen =
    {
        "......................",
        "......................",
        "......................",
        "........ooooooo.......",
        "......oollbbbbboo.....",
        ".....obllbbbbbbbbo....",
        ".....obbwwwbbwwwbo....",
        ".....obbwkkbbwkkbo....",
        ".....obbwkkbbwkkbyy...",
        "....oobppbbbbbbppyn...",
        "...ofobbblllllbbbofo..",
        "..offobbllllllllboffo.",
        "..oooobbllllllllbooo..",
        ".obooooooooooooooo....",
        "obo......y...y........",
        "oo......yyy.yyy.......",
    };

    // The blink: eyes closed to a line.
    public static readonly string[] IdleBlink =
    {
        "......................",
        "......................",
        "......................",
        "........ooooooo.......",
        "......oollbbbbboo.....",
        ".....obllbbbbbbbbo....",
        ".....obbbbbbbbbbbo....",
        ".....obbkkkbbkkkbo....",
        ".....obbbbbbbbbbbyy...",
        "....oobppbbbbbbppyn...",
        "...ofobbblllllbbbofo..",
        "..offobbllllllllboffo.",
        "..oooobbllllllllbooo..",
        ".obooooooooooooooo....",
        "obo......y...y........",
        "oo......yyy.yyy.......",
    };

    // Wings raised.
    public static readonly string[] FlapUp =
    {
        "......................",
        ".oo.................oo",
        ".ofo...............ofo",
        ".offo...ooooooo...offo",
        ".offfooollbbbbbooofffo",
        "..offobllbbbbbbbboffo.",
        "...ofobbwwwbbwwwbofo..",
        "...ofobbwkkbbwkkbofo..",
        "....oobbwkkbbwkkbyy...",
        ".....obppbbbbbbppyn...",
        ".....obbblllllbbbo....",
        ".....obbllllllllbo....",
        "...ooobbllllllllbo....",
        ".obooooooooooooooo....",
        "obo......y...y........",
        "oo......yyy.yyy.......",
    };

    // Wings level.
    public static readonly string[] FlapMid =
    {
        "......................",
        "......................",
        "......................",
        "........ooooooo.......",
        "......oollbbbbboo.....",
        ".....obllbbbbbbbbo....",
        ".....obbwwwbbwwwbo....",
        ".....obbwkkbbwkkbo....",
        ".....obbwkkbbwkkbyy...",
        ".....obppbbbbbbppyn...",
        "..oooobbblllllbbboooo.",
        ".offfobbllllllllbofffo",
        ".ooooobbllllllllbooooo",
        ".obooooooooooooooo....",
        "obo......y...y........",
        "oo......yyy.yyy.......",
    };

    // Wings down; the body bobs up a cell on stretched legs, the feet stay on the line.
    public static readonly string[] FlapDown =
    {
        "......................",
        "......................",
        "........ooooooo.......",
        "......oollbbbbboo.....",
        ".....obllbbbbbbbbo....",
        ".....obbwwwbbwwwbo....",
        ".....obbwkkbbwkkbo....",
        ".....obbwkkbbwkkbyy...",
        ".....obppbbbbbbppyn...",
        "....oobbblllllbbboo...",
        "...ofobbllllllllbofo..",
        "..offobbllllllllboffo.",
        ".offooooooooooooooffo.",
        "ooo......y...y.....oo.",
        "oo.......y...y........",
        "........yyy.yyy.......",
    };


    // Faces right: head leaned in, the back wing swept up behind, the near wing tucked, the rear
    // leg braced.
    public static readonly string[] ShoveA =
    {
        "......................",
        "..oo..................",
        "..ofo.................",
        "..offo...ooooooo......",
        "..offfooollbbbbboo....",
        "...offobllbbbbbbbbo...",
        "....ofobbwwwbbwwwbo...",
        "....ofobbwkkbbwkkbo...",
        ".....oobbwkkbbwkkbyy..",
        "......obppbbbbbbppyn..",
        ".....obbblllllbbbbo...",
        ".....obbllllllllbbo...",
        "...ooobbllllllllbo....",
        ".obooooooooooooooo....",
        "obo.....y.....y.......",
        "oo...yyyy.....yyy.....",
    };

    // The bump: squashed a row, eyes squeezed shut.
    public static readonly string[] ShoveB =
    {
        "......................",
        "......................",
        "..oo..................",
        "..ofo.................",
        "..offo...ooooooo......",
        "..offfooollbbbbboo....",
        "...offobbkbbbbbbkbo...",
        "....ofobbbkbbbbkbbo...",
        ".....oobbkbbbbbbkbyy..",
        "......obppbbbbbbppyn..",
        ".....obbblllllbbbbo...",
        ".....obbllllllllbbo...",
        "...ooobbllllllllbo....",
        ".obooooooooooooooo....",
        "obo.....y.....y.......",
        "oo...yyyy.....yyy.....",
    };

    // Lost grip: wings flung up, eyes wide, beak open in a squawk, legs kicking.
    public static readonly string[] Tumble1 =
    {
        ".oo.................oo",
        ".ofo...............ofo",
        ".offo...ooooooo...offo",
        ".offfooollbbbbbooofffo",
        "..offobllbbbbbbbboffo.",
        "...ofobbwwwbbwwwbofo..",
        "...ofobbwkwbbwkwbofo..",
        "....oobbwwwbbwwwbyy...",
        ".....obppbbbbbbppk....",
        ".....obbblllllbbbyy...",
        ".....obbllllllllbo....",
        "...ooobbllllllllbo....",
        ".obooooooooooooooo....",
        "obo...y......y........",
        "oo...y........y.......",
        "....y.y......y.y......",
    };

    // Lost grip, the other beat: wings flung out level, feet kicked up in front.
    public static readonly string[] Tumble2 =
    {
        "......................",
        "......................",
        "........ooooooo.......",
        "......oollbbbbboo.....",
        ".....obllbbbbbbbbo....",
        ".....obbwwwbbwwwbo....",
        ".....obbwkwbbwkwbo....",
        ".....obbwwwbbwwwbyy...",
        "....oobppbbbbbbppk....",
        "....oobbblllllbbbyy...",
        "...ofobbllllllllbofo..",
        "..offobbllllllllboffo.",
        ".offoooooooooooooooffo",
        ".oo.....yy...yy.....oo",
        "......................",
        "......................",
    };

    // Beak open for a bite.
    public static readonly string[] Eat1 =
    {
        "......................",
        "......................",
        "......................",
        "........ooooooo.......",
        "......oollbbbbboo.....",
        ".....obllbbbbbbbbo....",
        ".....obbwwwbbwwwbo....",
        ".....obbwkkbbwkkbo....",
        ".....obbwkkbbwkkbyyy..",
        "....oobppbbbbbbppkk...",
        "...ofobbblllllbbbyy...",
        "..offobbllllllllboffo.",
        "..oooobbllllllllbooo..",
        ".obooooooooooooooo....",
        "obo......y...y........",
        "oo......yyy.yyy.......",
    };

    // Beak closed on the slice, eyes shut with the taste.
    public static readonly string[] Eat2 =
    {
        "......................",
        "......................",
        "......................",
        "........ooooooo.......",
        "......oollbbbbboo.....",
        ".....obllbbbbbbbbo....",
        ".....obbbbbbbbbbbo....",
        ".....obbkkkbbkkkbo.gg.",
        ".....obbbbbbbbbbbyyccg",
        "....oobppbbbbbbppynscg",
        "...ofobbblllllbbbofgg.",
        "..offobbllllllllboffo.",
        "..oooobbllllllllbooo..",
        ".obooooooooooooooo....",
        "obo......y...y........",
        "oo......yyy.yyy.......",
    };

    // Pleased: eyes as happy arcs, bigger cheeks, a little lean forward.
    public static readonly string[] Happy =
    {
        "......................",
        "......................",
        "......................",
        ".........ooooooo......",
        ".......oollbbbbboo....",
        "......obllbbbbbbbbo...",
        "......obbkkkbbkkkbo...",
        "......obbkbkbbkbkbo...",
        "......obpppbbbpppbyy..",
        "....ooobpppbbbpppbyn..",
        "...ofobbblllllbbbbofo.",
        "..offobbllllllllbboffo",
        "..oooobbllllllllbbooo.",
        ".oboooooooooooooooo...",
        "obo......y...y........",
        "oo......yyy.yyy.......",
    };

    // A chirp: beak open, chest out, eyes half-lidded.
    public static readonly string[] Chirp =
    {
        "......................",
        "......................",
        "........ooooooo.......",
        "......oollbbbbboo.....",
        ".....obllbbbbbbbbo....",
        ".....obbbbbbbbbbbo.yy.",
        ".....obbwkkbbwkkbyy...",
        ".....obbwkkbbwkkbkk...",
        "....oobppbbbbbbppyy...",
        "...ofobbbllllllbbbo...",
        "..offobblllllllllbo...",
        "..offobblllllllllbo...",
        "..oooobbbllllllllo....",
        ".obooooooooooooooo....",
        "obo......y...y........",
        "oo......yyy.yyy.......",
    };

    // Head tilted a cell to the left, the left eye lower.
    public static readonly string[] TiltL =
    {
        "......................",
        "......................",
        "......................",
        ".......ooooooo........",
        ".....oollbbbbboo......",
        "....obllbbbbwwwbo.....",
        "....obbwwwbbwkkbo.....",
        "....obbwkkbbwkkbo.....",
        "....obbwkkbbbppbyy....",
        "...oobppbbbbbbbbyn....",
        "...ofobbblllllbbbofo..",
        "..offobbllllllllboffo.",
        "..oooobbllllllllbooo..",
        ".obooooooooooooooo....",
        "obo......y...y........",
        "oo......yyy.yyy.......",
    };

    // Head tilted a cell to the right, the right eye lower.
    public static readonly string[] TiltR =
    {
        "......................",
        "......................",
        "......................",
        ".........ooooooo......",
        ".......oollbbbbboo....",
        "......obbwwwbbbbbbo...",
        "......obbwkkbbwwwbo...",
        "......obbwkkbbwkkbo...",
        "......obppbbbbwkkbyy..",
        ".....oobbbbbbbbppbyn..",
        "...ofobbblllllbbbofo..",
        "..offobbllllllllboffo.",
        "..oooobbllllllllbooo..",
        ".obooooooooooooooo....",
        "obo......y...y........",
        "oo......yyy.yyy.......",
    };

    // Preening: head turned back into the left wing, eye shut.
    public static readonly string[] Preen =
    {
        "......................",
        "......................",
        "......................",
        "........ooooooo.......",
        "......oobbbbblloo.....",
        ".....obbbbbbbbllbo....",
        ".....obbbbbbbbbbbo....",
        ".....obkkkbbbbbbbo....",
        ".....obbbbbbbbbbbo....",
        "..ooyybppbbbbbbbbo....",
        ".offnobbblllllbbbofo..",
        ".offfobbllllllllboffo.",
        "..oooobbllllllllbooo..",
        ".obooooooooooooooo....",
        "obo......y...y........",
        "oo......yyy.yyy.......",
    };

    // A hop: up two cells, feet tucked under, wings out for balance.
    public static readonly string[] Hop =
    {
        "......................",
        "........ooooooo.......",
        "......oollbbbbboo.....",
        ".....obllbbbbbbbbo....",
        ".....obbwwwbbwwwbo....",
        ".....obbwkkbbwkkbo....",
        ".....obbwkkbbwkkbyy...",
        "....oobppbbbbbbppyn...",
        "..oofobbblllllbbbofoo.",
        ".offfobbllllllllbofffo",
        "..oooobbllllllllboooo.",
        ".obooooooooooooooo....",
        "obo.....yy..yy........",
        "oo....................",
        "......................",
        "......................",
    };

    // Round 3: the feather shake (the "brrrrp"): puffed up a cell taller (a ruffled crest on top)
    // and a cell wider toward the tail, the outline notched where feathers stand out, the wings held
    // a little away from the body. The face, the beak and the feet stay where IdleOpen has them, so
    // alternating the two poses reads as the feathers shaking, not the bird jumping. Here the left
    // wing is up and the right one down, with the notches on one side.
    public static readonly string[] Ruffle1 =
    {
        "......................",
        "........o..o..o.......",
        ".......oooooooo.......",
        ".....oollbbbbbboo.....",
        "...oobllbbbbbbbbbo....",
        "....obbbbbbbbbbbboo...",
        "....obbbwwwbbwwwbo....",
        "...oobbbwkkbbwkkbo....",
        "....obbbwkkbbwkkbyy...",
        ".oooobbppbbbbbbppyn...",
        "offfobbbblllllbbbo....",
        ".oooobblllllllllbofo..",
        "....obblllllllllboffo.",
        ".oboooooooooooooooooo.",
        "obo......y...y........",
        "oo......yyy.yyy.......",
    };

    // The other beat of the shake: the wings swapped (left down, right up), the crest and the
    // notches on the other side.
    public static readonly string[] Ruffle2 =
    {
        "......................",
        ".......o..o..o........",
        ".......oooooooo.......",
        ".....oollbbbbbboo.....",
        "....obllbbbbbbbbboo...",
        "...oobbbbbbbbbbbbo....",
        "....obbbwwwbbwwwboo...",
        "....obbbwkkbbwkkbo....",
        "...oobbbwkkbbwkkbyy...",
        "....obbppbbbbbbppynooo",
        "....obbbblllllbbbofffo",
        ".offobblllllllllbooooo",
        "offfobblllllllllbo....",
        ".obooooooooooooooo....",
        "obo......y...y........",
        "oo......yyy.yyy.......",
    };

    // Round 4: the peck at a seed. The head dips forward and down a little, the tail rises a cell
    // to balance it; the feet stay on the line.
    public static readonly string[] Peck1 =
    {
        "......................",
        "......................",
        "......................",
        "......................",
        "......................",
        "..........ooooooo.....",
        "........oollbbbbboo...",
        "....oooobllbbbbbbbbo..",
        "...obbbbbbwwwbbwwwbo..",
        "..ofobbbbbwkkbbwkkbo..",
        ".offobbbbbwkkbbwkkbyy.",
        ".oooobllbppbbbbbbppyn.",
        ".oboolllllllllllbo....",
        "obo.oooooooooooooo....",
        "oo.......y...y........",
        "........yyy.yyy.......",
    };

    // The bottom of the peck: the whole bird tipped forward, the tail up high behind, the head down
    // over the toes with the hooked beak on the ground and the seed (the Seed glyph's two cells) at
    // its tip, so the seed shows exactly for this frame and is gone with the next one.
    public static readonly string[] Peck2 =
    {
        "......................",
        "......................",
        "......................",
        "......................",
        "oo....................",
        "obo..ooooo............",
        ".oboollbbbo...........",
        "..obbbbbbbbooooooo....",
        "..obbbbbbbbllbbbbboo..",
        "..obbbbbbbllbbbbbbbbo.",
        "..ofobbbbbbwwwbbwwwbo.",
        "..offobbbbbwkkbbwkkbo.",
        "..oooolllbbwkkbbwkkbyy",
        "..ooooooobppbbbbbbppyn",
        "........oooooooooooo.n",
        "........yyy.yyy.....ee",
    };

    // Round 5: the walk (SPEC-round5 2.2), two frames alternated at 8 fps. Walk1: the body bobbed up a
    // cell on stretched legs, mid-stride (one leg forward, the other back), both feet
    // on the line. Walk2: the body back down, the legs passing: the back foot planted, the other lifted.
    public static readonly string[] Walk1 =
    {
        "......................",
        "......................",
        "........ooooooo.......",
        "......oollbbbbboo.....",
        ".....obllbbbbbbbbo....",
        ".....obbwwwbbwwwbo....",
        ".....obbwkkbbwkkbo....",
        ".....obbwkkbbwkkbyy...",
        "....oobppbbbbbbppyn...",
        "...ofobbblllllbbbofo..",
        "..offobbllllllllboffo.",
        "..oooobbllllllllbooo..",
        ".obooooooooooooooo....",
        "obo......y...y........",
        "oo......y.....y.......",
        ".......yyy....yyy.....",
    };

    // The other step: the body back down on the perch line, the back foot planted and the front one
    // lifted off the line as it passes under.
    public static readonly string[] Walk2 =
    {
        "......................",
        "......................",
        "......................",
        "........ooooooo.......",
        "......oollbbbbboo.....",
        ".....obllbbbbbbbbo....",
        ".....obbwwwbbwwwbo....",
        ".....obbwkkbbwkkbo....",
        ".....obbwkkbbwkkbyy...",
        "....oobppbbbbbbppyn...",
        "...ofobbblllllbbbofo..",
        "..offobbllllllllboffo.",
        "..oooobbllllllllbooo..",
        ".obooooooooooooooo....",
        "obo.......y..yy.......",
        "oo.......yyy..........",
    };

    /// <summary>Every bird frame, so the view can pre-render them all once.</summary>
    public static readonly string[][] All =
    {
        IdleOpen, IdleBlink, FlapUp, FlapMid, FlapDown, ShoveA, ShoveB,
        Tumble1, Tumble2, Eat1, Eat2, Happy, Chirp, TiltL, TiltR, Preen, Hop, Ruffle1, Ruffle2,
        Peck1, Peck2, Walk1, Walk2,
    };

    // --- Effect glyphs: the same grid format and Legend, their own sizes, not in All. ---

    // A pixel heart, 5 by 4, for the happy burst.
    public static readonly string[] Heart =
    {
        "rr.rr",
        "rrrrr",
        ".rrr.",
        "..r..",
    };

    // A dust mote, 2 by 2, for landings (drawn at about 70 % opacity by the effect).
    public static readonly string[] Dust =
    {
        "dd",
        "dd",
    };

    // A cucumber slice, 7 by 7: dark green rim, pale flesh, three seeds.
    public static readonly string[] Cucumber =
    {
        "..ggg..",
        ".gcccg.",
        "gcscscg",
        "gcccccg",
        "gccsccg",
        ".gcccg.",
        "..ggg..",
    };

    // A seed, 2 by 1, lying on the ground line in front of the bird (the "eats a seed" fidget).
    public static readonly string[] Seed =
    {
        "ee",
    };

    /// <summary>The effect glyphs, so a view can pre-render them too.</summary>
    public static readonly string[][] Glyphs = { Heart, Dust, Cucumber, Seed };
}
