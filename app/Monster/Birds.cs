namespace TaskFocuser.Monster;

/// <summary>The companions he can pick (SPEC-round5 2.3, SPEC-round6 2): Lennie the budgie, Meggie the owl and Rori the Japanese spitz.</summary>
public static class Birds
{
    public static readonly BirdSkin Lennie = new("lennie", "Lennie", Sprites.Legend, new BirdSkin.Frames(
        Sprites.IdleOpen, Sprites.IdleBlink, Sprites.FlapUp, Sprites.FlapMid, Sprites.FlapDown,
        Sprites.ShoveA, Sprites.ShoveB, Sprites.Tumble1, Sprites.Tumble2, Sprites.Eat1, Sprites.Eat2,
        Sprites.Happy, Sprites.Chirp, Sprites.TiltL, Sprites.TiltR, Sprites.Preen, Sprites.Hop,
        Sprites.Ruffle1, Sprites.Ruffle2, Sprites.Peck1, Sprites.Peck2, Sprites.Walk1, Sprites.Walk2));

    public static readonly BirdSkin Meggie = new("meggie", "Meggie", OwlSprites.Legend, new BirdSkin.Frames(
        OwlSprites.IdleOpen, OwlSprites.IdleBlink, OwlSprites.FlapUp, OwlSprites.FlapMid, OwlSprites.FlapDown,
        OwlSprites.ShoveA, OwlSprites.ShoveB, OwlSprites.Tumble1, OwlSprites.Tumble2, OwlSprites.Eat1, OwlSprites.Eat2,
        OwlSprites.Happy, OwlSprites.Chirp, OwlSprites.TiltL, OwlSprites.TiltR, OwlSprites.Preen, OwlSprites.Hop,
        OwlSprites.Ruffle1, OwlSprites.Ruffle2, OwlSprites.Peck1, OwlSprites.Peck2, OwlSprites.Walk1, OwlSprites.Walk2));

    /// <summary>Rori (round 6): on the ground, eats the bone, and has the dog frames.</summary>
    public static readonly BirdSkin Rori = new("rori", "Rori", DogSprites.Legend, new BirdSkin.Frames(
        DogSprites.IdleOpen, DogSprites.IdleBlink, DogSprites.FlapUp, DogSprites.FlapMid, DogSprites.FlapDown,
        DogSprites.ShoveA, DogSprites.ShoveB, DogSprites.Tumble1, DogSprites.Tumble2, DogSprites.Eat1, DogSprites.Eat2,
        DogSprites.Happy, DogSprites.Chirp, DogSprites.TiltL, DogSprites.TiltR, DogSprites.Preen, DogSprites.Hop,
        DogSprites.Ruffle1, DogSprites.Ruffle2, DogSprites.Peck1, DogSprites.Peck2, DogSprites.Walk1, DogSprites.Walk2))
    {
        Locomotion = Locomotion.Ground,
        Treat = DogSprites.Bone,
        Dog = new BirdSkin.DogExtras(
            DogSprites.Wag1, DogSprites.Wag2, DogSprites.Bow, DogSprites.Beg, DogSprites.Skid,
            DogSprites.Lie, DogSprites.Sleep1, DogSprites.Sleep2, DogSprites.Stretch),
    };

    public static readonly BirdSkin[] All = { Lennie, Meggie, Rori };

    /// <summary>An unknown or empty id is Lennie, so an old state file still works.</summary>
    public static BirdSkin ById(string? id) =>
        Array.Find(All, b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Lennie;

    /// <summary>Every legend entry of every companion plus the glyph colours; the keys must not collide (the owl uses uppercase, the dog digits and symbols).</summary>
    public static Dictionary<char, string> MergedLegend
    {
        get
        {
            var map = new Dictionary<char, string>(Sprites.Legend);
            foreach (var b in All)
                foreach (var (k, v) in b.Legend) map.TryAdd(k, v);
            return map;
        }
    }
}
