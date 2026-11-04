namespace TaskFocuser.Monster;

/// <summary>
/// One bird: its id, its default name, its legend (cell character to colour) and the 23 frames every
/// behaviour uses. The frame semantics are the contract: feet on the bottom row of the legs, facing right.
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

    /// <summary>Every frame, for pre-rendering.</summary>
    public string[][] All => new[]
    {
        IdleOpen, IdleBlink, FlapUp, FlapMid, FlapDown, ShoveA, ShoveB, Tumble1, Tumble2, Eat1, Eat2,
        Happy, Chirp, TiltL, TiltR, Preen, Hop, Ruffle1, Ruffle2, Peck1, Peck2, Walk1, Walk2,
    };

    /// <summary>The 23 frames of one bird, named after the round-2 to round-5 frame contract.</summary>
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
}
