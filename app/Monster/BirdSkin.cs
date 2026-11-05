namespace TaskFocuser.Monster;

/// <summary>How a companion gets about (SPEC-round6 2.2): a bird flies in from above, a dog runs in along the perch line.</summary>
public enum Locomotion { Air, Ground }

/// <summary>
/// One companion: its id, its default name, its legend (cell character to colour) and the 23 frames every
/// behaviour uses. The frame semantics are the contract: feet on the bottom row of the legs, facing right.
/// Round 6 adds traits: how it moves, which treat it eats, and the dog's extra frames (SPEC-round6 3).
/// </summary>
public sealed class BirdSkin
{
    public BirdSkin(string id, string defaultName, Dictionary<char, string> legend, Frames frames)
    {
        Id = id;
        DefaultName = defaultName;
        Legend = legend;
        F = frames;
    }

    public string Id { get; }
    public string DefaultName { get; }
    public Dictionary<char, string> Legend { get; }
    public Frames F { get; }

    /// <summary>Air (the birds) or Ground (Rori): the arrival, the leave, the carrying and the catch pick their path by it.</summary>
    public Locomotion Locomotion { get; init; } = Locomotion.Air;

    /// <summary>The treat glyph shown on the bar and carried to the companion; null means the cucumber drawing of Theme.xaml.</summary>
    public string[]? Treat { get; init; }

    /// <summary>The dog-only frames (SPEC-round6 2.1); null for a bird, and the dog acts are then never drawn.</summary>
    public DogExtras? Dog { get; init; }

    public string[] IdleOpen => F.IdleOpen;
    public string[] IdleBlink => F.IdleBlink;
    public string[] FlapUp => F.FlapUp;
    public string[] FlapMid => F.FlapMid;
    public string[] FlapDown => F.FlapDown;
    public string[] ShoveA => F.ShoveA;
    public string[] ShoveB => F.ShoveB;
    public string[] Tumble1 => F.Tumble1;
    public string[] Tumble2 => F.Tumble2;
    public string[] Eat1 => F.Eat1;
    public string[] Eat2 => F.Eat2;
    public string[] Happy => F.Happy;
    public string[] Chirp => F.Chirp;
    public string[] TiltL => F.TiltL;
    public string[] TiltR => F.TiltR;
    public string[] Preen => F.Preen;
    public string[] Hop => F.Hop;
    public string[] Ruffle1 => F.Ruffle1;
    public string[] Ruffle2 => F.Ruffle2;
    public string[] Peck1 => F.Peck1;
    public string[] Peck2 => F.Peck2;
    public string[] Walk1 => F.Walk1;
    public string[] Walk2 => F.Walk2;

    /// <summary>Every frame, the extras included, for pre-rendering.</summary>
    public string[][] All
    {
        get
        {
            var list = new List<string[]>
            {
                IdleOpen, IdleBlink, FlapUp, FlapMid, FlapDown, ShoveA, ShoveB, Tumble1, Tumble2, Eat1, Eat2,
                Happy, Chirp, TiltL, TiltR, Preen, Hop, Ruffle1, Ruffle2, Peck1, Peck2, Walk1, Walk2,
            };
            if (Dog is { } d)
                list.AddRange(new[] { d.Wag1, d.Wag2, d.Bow, d.Beg, d.Skid, d.Lie, d.Sleep1, d.Sleep2, d.Stretch });
            return list.ToArray();
        }
    }

    /// <summary>The 23 frames of one companion, named after the round-2 to round-5 frame contract.</summary>
    public sealed record Frames(
        string[] IdleOpen, string[] IdleBlink,
        string[] FlapUp, string[] FlapMid, string[] FlapDown,
        string[] ShoveA, string[] ShoveB,
        string[] Tumble1, string[] Tumble2,
        string[] Eat1, string[] Eat2,
        string[] Happy, string[] Chirp,
        string[] TiltL, string[] TiltR, string[] Preen, string[] Hop,
        string[] Ruffle1, string[] Ruffle2,
        string[] Peck1, string[] Peck2,
        string[] Walk1, string[] Walk2);

    /// <summary>The nine dog-only frames (SPEC-round6 2.1): the wag, the play bow, begging, the skid, the nap and the stretch.</summary>
    public sealed record DogExtras(
        string[] Wag1, string[] Wag2,
        string[] Bow, string[] Beg, string[] Skid,
        string[] Lie, string[] Sleep1, string[] Sleep2, string[] Stretch);
}
