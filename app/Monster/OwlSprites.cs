namespace TaskFocuser.Monster;

// Meggie the owl (SPEC-round5 2.3): the same grid format as Sprites.cs, so the art can be read by eye
// and parsed by a regex (one Legend entry per line, one quoted row per line). Same size (22 x 16),
// the same CellSize, feet on row 15, facing right (the pupils and the beak hook point right; FlipX
// mirrors her). Her Legend keys are uppercase so they never collide with Lennie's lowercase ones:
// the sprite view merges both legends into one brush map.
public static class OwlSprites
{
    public static readonly Dictionary<char, string> Legend = new()
    {
        ['.'] = "",
        ['O'] = "#4A4A58",
        ['G'] = "#9A9AA6",
        ['F'] = "#D6D6DE",
        ['W'] = "#B8B8C4",
        ['Y'] = "#F5C542",
        ['K'] = "#1A1A2E",
        ['H'] = "#FFFFFF",
        ['B'] = "#D98A2B",
        ['N'] = "#A8641A",
        ['L'] = "#D98A2B",
        ['P'] = "#F4B6C2",
        ['E'] = "#8A5A2B",
        ['V'] = "#3E7D3E",
        ['U'] = "#DDF3C9",
        ['S'] = "#B9D98C",
    };

    // Perched: a round grey body with two ear tufts, a pale heart-shaped face disc and belly, big yellow
    // eyes looking right (a white glint in each), a small hooked beak between them, wings folded at the
    // sides. Feet on row 15; the front toe is cell 14, as Lennie's.
    public static readonly string[] IdleOpen =
    {
        "......................",
        "....O............O....",
        "....OO..........OO....",
        "....OGOOOOOOOOOOGO....",
        "...OGGGGGGGGGGGGGGO...",
        "...OGGFFFFGGFFFFGGO...",
        "...OGFFYYFFFFYYFFGO...",
        "...OGFYYHKFFYYHKFGO...",
        "..OWGFYYKKFFYYKKFGWO..",
        "..OWGFFYYFBBFYYFFGWO..",
        "..OWWPPFFFFNFFFPPWWO..",
        "..OWWGGGGFFFFGGGGWWO..",
        "...OWWGFFFFFFFFGWWO...",
        "....OOOOOOOOOOOOOO....",
        ".........L...L........",
        "........LLL.LLL.......",
    };

    // The blink: eyes closed to a line.
    public static readonly string[] IdleBlink =
    {
        "......................",
        "....O............O....",
        "....OO..........OO....",
        "....OGOOOOOOOOOOGO....",
        "...OGGGGGGGGGGGGGGO...",
        "...OGGFFFFGGFFFFGGO...",
        "...OGFFFFFFFFFFFFGO...",
        "...OGFFFFFFFFFFFFGO...",
        "..OWGFKKKKFFKKKKFGWO..",
        "..OWGFFFFFBBFFFFFGWO..",
        "..OWWPPFFFFNFFFPPWWO..",
        "..OWWGGGGFFFFGGGGWWO..",
        "...OWWGFFFFFFFFGWWO...",
        "....OOOOOOOOOOOOOO....",
        ".........L...L........",
        "........LLL.LLL.......",
    };

    // Wings raised beside the head.
    public static readonly string[] FlapUp =
    {
        "......................",
        "....O............O....",
        "O...OO..........OO...O",
        "OO..OGOOOOOOOOOOGO..OO",
        "OWOOGGGGGGGGGGGGGGOOWO",
        "OWWOGGFFFFGGFFFFGGOWWO",
        ".OWOGFFYYFFFFYYFFGOWO.",
        ".OWWGFYYHKFFYYHKFGWWO.",
        "..OWGFYYKKFFYYKKFGWO..",
        "...OGFFYYFBBFYYFFGO...",
        "...OGPPFFFFNFFFPPGO...",
        "...OGGGGGFFFFGGGGGO...",
        "....OGGFFFFFFFFGGO....",
        ".....OOOOOOOOOOOO.....",
        ".........L...L........",
        "........LLL.LLL.......",
    };

    // Wings level.
    public static readonly string[] FlapMid =
    {
        "......................",
        "....O............O....",
        "....OO..........OO....",
        "....OGOOOOOOOOOOGO....",
        "...OGGGGGGGGGGGGGGO...",
        "...OGGFFFFGGFFFFGGO...",
        "...OGFFYYFFFFYYFFGO...",
        "...OGFYYHKFFYYHKFGO...",
        "...OGFYYKKFFYYKKFGO...",
        "OOOOGFFYYFBBFYYFFGOOOO",
        "OWWWGPPFFFFNFFFPPGWWWO",
        "OOOOGGGGGFFFFGGGGGOOOO",
        "....OGGFFFFFFFFGGO....",
        ".....OOOOOOOOOOOO.....",
        ".........L...L........",
        "........LLL.LLL.......",
    };

    // Wings down; the body bobs up a cell on stretched legs, the feet stay on the line.
    public static readonly string[] FlapDown =
    {
        "....O............O....",
        "....OO..........OO....",
        "....OGOOOOOOOOOOGO....",
        "...OGGGGGGGGGGGGGGO...",
        "...OGGFFFFGGFFFFGGO...",
        "...OGFFYYFFFFYYFFGO...",
        "...OGFYYHKFFYYHKFGO...",
        "..OWGFYYKKFFYYKKFGWO..",
        "..OWGFFYYFBBFYYFFGWO..",
        ".OWWWPPFFFFNFFFPPWWWO.",
        ".OWWGGGGGFFFFGGGGGWWO.",
        "OWWOOGGFFFFFFFFGGOOWWO",
        "OWO..OOOOOOOOOOOO..OWO",
        "OO.......L...L......OO",
        ".........L...L........",
        "........LLL.LLL.......",
    };

    // Faces right: leaned in a cell, the back wing swept up behind, the rear leg braced.
    public static readonly string[] ShoveA =
    {
        "......................",
        "OO...O............O...",
        "OWO..OO..........OO...",
        "OWWO.OGOOOOOOOOOOGO...",
        ".OWWOGGGGGGGGGGGGGGO..",
        "..OWOGGFFFFGGFFFFGGO..",
        "..OWWGFFYYFFFFYYFFGO..",
        "...OWGFYYHKFFYYHKFGO..",
        "...OWGFYYKKFFYYKKFGO..",
        "....OGFFYYFBBFYYFFGO..",
        "....OGPPFFFFNFFFPPGO..",
        "....OGGGGGFFFFGGGGGO..",
        ".....OGGFFFFFFFFGGO...",
        "......OOOOOOOOOOOO....",
        "........L.....L.......",
        ".....LLLL.....LLL.....",
    };

    // The bump: squashed a row, eyes squeezed shut (> <).
    public static readonly string[] ShoveB =
    {
        "......................",
        "......................",
        "OO...O............O...",
        "OWO..OO..........OO...",
        "OWWO.OGOOOOOOOOOOGO...",
        ".OWWOGGFFFFGGFFFFGGO..",
        "..OWWGFFFFFFFFFFFFGO..",
        "...OWGFKKFFFFFFKKFGO..",
        "...OWGFFFKKFFKKFFFGO..",
        "....OGFKKFFBBFFKKFGO..",
        "....OGPPFFFFNFFFPPGO..",
        "....OGGGGGFFFFGGGGGO..",
        ".....OGGFFFFFFFFGGO...",
        "......OOOOOOOOOOOO....",
        "........L.....L.......",
        ".....LLLL.....LLL.....",
    };

    // Lost grip: wings flung up, eyes wide with tiny pupils, beak open, legs kicking.
    public static readonly string[] Tumble1 =
    {
        "O...OO..........OO...O",
        "OO..OGOOOOOOOOOOGO..OO",
        "OWOOGGGGGGGGGGGGGGOOWO",
        "OWWOGGFFFFGGFFFFGGOWWO",
        ".OWOGFFYYFFFFYYFFGOWO.",
        ".OWWGFYYYYFFYYYYFGWWO.",
        "..OWGFYYKYFFYYKYFGWO..",
        "...OGFFYYFFFFYYFFGO...",
        "...OGFFFFFBBFFFFFGO...",
        "...OGPPFFFKKFFFPPGO...",
        "...OGGGGGFBBFGGGGGO...",
        "....OGGFFFFFFFFGGO....",
        ".....OOOOOOOOOOOO.....",
        "........L.....L.......",
        ".......L.......L......",
        "......L.L.....L.L.....",
    };

    // Lost grip, the other beat: wings flung out level, feet kicked up.
    public static readonly string[] Tumble2 =
    {
        "......................",
        "....OO..........OO....",
        "....OGOOOOOOOOOOGO....",
        "...OGGGGGGGGGGGGGGO...",
        "...OGGFFFFGGFFFFGGO...",
        "...OGFFYYFFFFYYFFGO...",
        "...OGFYYYYFFYYYYFGO...",
        "...OGFYYKYFFYYKYFGO...",
        "OOOOGFFYYFBBFYYFFGOOOO",
        "OWWWGPPFFFKKFFFPPGWWWO",
        "OOOOGGGGGFBBFGGGGGOOOO",
        "....OGGFFFFFFFFGGO....",
        ".....OOOOOOOOOOOO.....",
        "..........LL..LL......",
        "......................",
        "......................",
    };

    // Beak open for a bite.
    public static readonly string[] Eat1 =
    {
        "......................",
        "....O............O....",
        "....OO..........OO....",
        "....OGOOOOOOOOOOGO....",
        "...OGGGGGGGGGGGGGGO...",
        "...OGGFFFFGGFFFFGGO...",
        "...OGFFYYFFFFYYFFGO...",
        "...OGFYYHKFFYYHKFGO...",
        "..OWGFYYKKFFYYKKFGWO..",
        "..OWGFFYYFBBFYYFFGWO..",
        "..OWWPPFFFKKFFFPPWWO..",
        "..OWWGGGGFBBFGGGGWWO..",
        "...OWWGFFFFFFFFGWWO...",
        "....OOOOOOOOOOOOOO....",
        ".........L...L........",
        "........LLL.LLL.......",
    };

    // Beak closed on the slice (held against her chest), eyes shut with the taste.
    public static readonly string[] Eat2 =
    {
        "......................",
        "....O............O....",
        "....OO..........OO....",
        "....OGOOOOOOOOOOGO....",
        "...OGGGGGGGGGGGGGGO...",
        "...OGGFFFFGGFFFFGGO...",
        "...OGFFFFFFFFFFFFGO...",
        "...OGFKFFKFFKFFKFGO...",
        "..OWGFFKKFFFFKKFFGWO..",
        "..OWGFFFFFBBFVVVFGWO..",
        "..OWWPPFFFFNVUUUVWWO..",
        "..OWWGGGGFFFVUSUVWWO..",
        "...OWWGFFFFFVUUUVWO...",
        "....OOOOOOOOOVVVOO....",
        ".........L...L........",
        "........LLL.LLL.......",
    };

    // Pleased: eyes as happy arcs, bigger cheeks, a little lean forward.
    public static readonly string[] Happy =
    {
        "......................",
        ".....O............O...",
        ".....OO..........OO...",
        ".....OGOOOOOOOOOOGO...",
        "....OGGGGGGGGGGGGGGO..",
        "....OGGFFFFGGFFFFGGO..",
        "....OGFFFFFFFFFFFFGO..",
        "....OGFFKKFFFFKKFFGO..",
        "...OWGFKFFKFFKFFKFGWO.",
        "...OWGPPFFFBBFFFPPGWO.",
        "...OWWPPFFFFNFFFPPWWO.",
        "...OWWGGGGFFFFGGGGWWO.",
        "....OWWGFFFFFFFFGWWO..",
        ".....OOOOOOOOOOOOOO...",
        ".........L...L........",
        "........LLL.LLL.......",
    };

    // A soft hoot: eyes shut, beak open, the body up a cell on stretched legs.
    public static readonly string[] Chirp =
    {
        "....O............O....",
        "....OO..........OO....",
        "....OGOOOOOOOOOOGO....",
        "...OGGGGGGGGGGGGGGO...",
        "...OGGFFFFGGFFFFGGO...",
        "...OGFFFFFFFFFFFFGO...",
        "...OGFKFFKFFKFFKFGO...",
        "..OWGFFKKFFFFKKFFGWO..",
        "..OWGFFFFFBBFFFFFGWO..",
        "..OWWPPFFFKKFFFPPWWO..",
        "..OWWGGGFFBBFFGGGWWO..",
        "...OWWFFFFFFFFFFWWO...",
        "....OOOOOOOOOOOOOO....",
        ".........L...L........",
        ".........L...L........",
        "........LLL.LLL.......",
    };

    // Head tilted: the face a cell to the left, the left eye lower, the right tuft raised.
    public static readonly string[] TiltL =
    {
        ".................O....",
        "....O...........OO....",
        "....OO.........OGO....",
        "....OGOOOOOOOOOOGO....",
        "...OGGGGGGGGGGGGGGO...",
        "...OGFFFFGGFFFFFGGO...",
        "...OFFFFFFFFYYFFGGO...",
        "...OGFYYFFFYYHKFGGO...",
        "..OWGYYHKFFYYKKFGGWO..",
        "..OWGYYKKBBFYYFFGGWO..",
        "..OWWFYYFFNFFFPPFWWO..",
        "..OWWGGGFFFFGGGGGWWO..",
        "...OWWGFFFFFFFFGWWO...",
        "....OOOOOOOOOOOOOO....",
        ".........L...L........",
        "........LLL.LLL.......",
    };

    // Head tilted the other way: the face a cell to the right, the right eye lower, the left tuft raised.
    public static readonly string[] TiltR =
    {
        "....O.................",
        "....OO...........O....",
        "....OGO.........OO....",
        "....OGOOOOOOOOOOGO....",
        "...OGGGGGGGGGGGGGGO...",
        "...OGGFFFFFGGFFFFGO...",
        "...OGFFFYYFFFFFFFFO...",
        "...OGFFYYHKFFFYYFGO...",
        "..OWGFFYYKKFFYYHKGWO..",
        "..OWGFFFYYFBBYYKKGWO..",
        "..OWWFPPFFFFNFYYFWWO..",
        "..OWWGGGGGFFFFGGGWWO..",
        "...OWWGFFFFFFFFGWWO...",
        "....OOOOOOOOOOOOOO....",
        ".........L...L........",
        "........LLL.LLL.......",
    };

    // Preening: head dipped, eyes shut, the beak buried in her ruffled chest feathers.
    public static readonly string[] Preen =
    {
        "......................",
        "......................",
        "....O............O....",
        "....OO..........OO....",
        "....OGOOOOOOOOOOGO....",
        "...OGGGGGGGGGGGGGGO...",
        "...OGGFFFFGGFFFFGGO...",
        "...OGFFFFFFFFFFFFGO...",
        "..OWGFKKKFFFFKKKFGWO..",
        "..OWWPPFFFFFFFFPPWWO..",
        "..OWWGGFFFBBFFFGGWWO..",
        "..OWWGGWFFBNFWFGGWWO..",
        "...OWWGFFWFFWFFGWWO...",
        "....OOOOOOOOOOOOOO....",
        ".........L...L........",
        "........LLL.LLL.......",
    };

    // A hop: up two cells, feet tucked under, wings out for balance.
    public static readonly string[] Hop =
    {
        "....OO..........OO....",
        "....OGOOOOOOOOOOGO....",
        "...OGGGGGGGGGGGGGGO...",
        "...OGGFFFFGGFFFFGGO...",
        "...OGFFYYFFFFYYFFGO...",
        "...OGFYYHKFFYYHKFGO...",
        "..OOGFYYKKFFYYKKFGOO..",
        ".OWWGFFYYFBBFYYFFGWWO.",
        "OWWWWPPFFFFNFFFPPWWWWO",
        ".OOWWGGGGFFFFGGGGWWOO.",
        "....OGGFFFFFFFFGGO....",
        ".....OOOOOOOOOOOO.....",
        ".........LL..LL.......",
        "......................",
        "......................",
        "......................",
    };

    // The feather shake: puffed up (spiky crest, the outline notched where feathers stand out), the
    // wings held away from the body. Face, beak and feet stay where IdleOpen has them, so alternating
    // the two poses reads as shaking, not jumping. Here the left wing is up, the right one down.
    public static readonly string[] Ruffle1 =
    {
        "....O..O..O..O...O....",
        "....OOOOOOOOOOOOOO....",
        "...OGGGGGGGGGGGGGGOO..",
        ".OOGGGGGGGGGGGGGGGGO..",
        "..OGGGGGGGGGGGGGGGGGO.",
        ".OOGGGFFFFGGFFFFGGGO..",
        "OWOGGFFYYFFFFYYFFGGOO.",
        "OWWOGFYYHKFFYYHKFGGO..",
        ".OWOGFYYKKFFYYKKFGGOO.",
        "..OOGFFYYFBBFYYFFGGO..",
        ".OOGGPPFFFFNFFFPPGOWWO",
        "..OGGGGGGFFFFGGGGGOWWO",
        ".OOGGGGFFFFFFFFGGGOWO.",
        "..OOOOOOOOOOOOOOOOOO..",
        ".........L...L........",
        "........LLL.LLL.......",
    };

    // The other beat of the shake: wings swapped, the notches on the other side.
    public static readonly string[] Ruffle2 =
    {
        "....O...O..O..O..O....",
        "....OOOOOOOOOOOOOO....",
        "..OOGGGGGGGGGGGGGGO...",
        "..OGGGGGGGGGGGGGGGGOO.",
        ".OGGGGGGGGGGGGGGGGGO..",
        "..OGGGFFFFGGFFFFGGGOO.",
        ".OOGGFFYYFFFFYYFFGGOWO",
        "..OGGFYYHKFFYYHKFGOWWO",
        ".OOGGFYYKKFFYYKKFGOWO.",
        "..OGGFFYYFBBFYYFFGGOO.",
        "OWWOGPPFFFFNFFFPPGGOO.",
        "OWWOGGGGGFFFFGGGGGGO..",
        ".OWOGGGFFFFFFFFGGGGOO.",
        "..OOOOOOOOOOOOOOOOOO..",
        ".........L...L........",
        "........LLL.LLL.......",
    };

    // The peck: leaned forward two cells and down one, the feet stay on the line.
    public static readonly string[] Peck1 =
    {
        "......................",
        "......................",
        "......O............O..",
        "......OO..........OO..",
        "......OGOOOOOOOOOOGO..",
        ".....OGGFFFFGGFFFFGGO.",
        ".....OGFFYYFFFFYYFFGO.",
        ".....OGFYYHKFFYYHKFGO.",
        "....OWGFYYKKFFYYKKFGWO",
        "....OWGFFYYFBBFYYFFGWO",
        "....OWWPPFFFFNFFFPPWWO",
        "....OWWGGGGFFFFGGGGWWO",
        ".....OWWGFFFFFFFFGWWO.",
        "......OOOOOOOOOOOOOO..",
        ".........L...L........",
        "........LLL.LLL.......",
    };

    // The bottom of the peck, turned to profile: tipped forward with the head down over the toes, the
    // hooked beak running down the right edge to the seed (the Seed glyph's two cells, cols 20-21 of
    // row 15, exactly where Lennie's Peck2 holds it), so the seed shows exactly for this frame.
    public static readonly string[] Peck2 =
    {
        "......................",
        "......................",
        "......................",
        "......................",
        "..............O.......",
        "......OOOOO..OGO......",
        "....OOGGGGGOOOGGOOO...",
        "...OWWGGGGGGGGGGGGGO..",
        "..OGWWWGGGGGGGGGFFFFO.",
        "..OGGWWWWGGGGGGFFYYFFO",
        "..OGGGWWWWWGGGGFYYHKFO",
        "..OGGGGWWWWWGGGFYYKKBO",
        "..OOGGGGFFFFFGGPFYYFBB",
        "...OOGGFFFFFFGGFFFOOOB",
        ".....OOOOOOOOOOOOO...N",
        "........LLL.LLL.....EE",
    };

    // The walk (SPEC-round5 2.2): the body bobbed up a cell on stretched legs, mid-stride (one leg
    // forward, the other back), both feet on the line.
    public static readonly string[] Walk1 =
    {
        "....O............O....",
        "....OO..........OO....",
        "....OGOOOOOOOOOOGO....",
        "...OGGGGGGGGGGGGGGO...",
        "...OGGFFFFGGFFFFGGO...",
        "...OGFFYYFFFFYYFFGO...",
        "...OGFYYHKFFYYHKFGO...",
        "..OWGFYYKKFFYYKKFGWO..",
        "..OWGFFYYFBBFYYFFGWO..",
        "..OWWPPFFFFNFFFPPWWO..",
        "..OWWGGGGFFFFGGGGWWO..",
        "...OWWGFFFFFFFFGWWO...",
        "....OOOOOOOOOOOOOO....",
        ".........L...L........",
        "........L.....L.......",
        ".......LLL....LLL.....",
    };

    // The other step: the body back down, the back foot planted, the front foot lifted as it passes.
    public static readonly string[] Walk2 =
    {
        "......................",
        "....O............O....",
        "....OO..........OO....",
        "....OGOOOOOOOOOOGO....",
        "...OGGGGGGGGGGGGGGO...",
        "...OGGFFFFGGFFFFGGO...",
        "...OGFFYYFFFFYYFFGO...",
        "...OGFYYHKFFYYHKFGO...",
        "..OWGFYYKKFFYYKKFGWO..",
        "..OWGFFYYFBBFYYFFGWO..",
        "..OWWPPFFFFNFFFPPWWO..",
        "..OWWGGGGFFFFGGGGWWO..",
        "...OWWGFFFFFFFFGWWO...",
        "....OOOOOOOOOOOOOO....",
        "..........L..LL.......",
        ".........LLL..........",
    };
}
