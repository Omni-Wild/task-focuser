namespace TaskFocuser.Core;

/// <summary>Where the note is: on screen, tucked into the dot, or closed (tray only).</summary>
public enum Mode { Note, Dot, Closed }

/// <summary>Round 3: which idle sounds the bird makes in the background.</summary>
public enum IdleSounds { Chirp, Ruffle, Both }

/// <summary>Round 3: how often, as a range (Often 1.5 to 3 min, Mid 3 to 6, Rarely 8 to 15).</summary>
public enum IdleRate { Often, Mid, Rarely }

/// <summary>
/// Everything that survives a restart. Persisted by <see cref="StateStore"/> to
/// %APPDATA%\Task Focuser\state.json. The first six fields keep the old app's names and meaning
/// so the note text written by the Python version carries over.
/// </summary>
public sealed class NoteState
{
    public int Version { get; set; } = 2;

    public string Text { get; set; } = "";

    /// <summary>Top-left of the note window in screen DIPs.</summary>
    public double X { get; set; } = 100;
    public double Y { get; set; } = 100;

    /// <summary>One of <see cref="Palette.Colors"/> as "#RRGGBB". An unknown value falls back to the first swatch.</summary>
    public string Color { get; set; } = Palette.Colors[0].Hex;

    /// <summary>Whole-window opacity in percent, <see cref="Palette.MinOpacity"/> to <see cref="Palette.MaxOpacity"/>.</summary>
    public int Opacity { get; set; } = 100;

    public bool Locked { get; set; } = false;

    /// <summary>One of <see cref="Palette.Fonts"/>. An unknown value falls back to the first.</summary>
    public string Font { get; set; } = Palette.Fonts[0];

    public int FontSize { get; set; } = Palette.DefaultFontSize;

    public Mode Mode { get; set; } = Mode.Note;

    /// <summary>The dot's centre in screen DIPs; NaN until the dot has been placed once.</summary>
    public double DotX { get; set; } = double.NaN;
    public double DotY { get; set; } = double.NaN;

    public bool StartWithWindows { get; set; } = false;

    // Round 2 (docs/SPEC-round2.md).
    /// <summary>The bird keeps its perch while the note is locked.</summary>
    public bool BirdStays { get; set; } = false;
    /// <summary>True: the bird is fully opaque; false: it follows the note's opacity.</summary>
    public bool BirdOpaque { get; set; } = true;
    public bool SoundOn { get; set; } = true;
    /// <summary>0 to 1; the keep-alive chirp and every other sound play at this volume.</summary>
    public double Volume { get; set; } = 0.10;

    // Round 3 (docs/SPEC-round3.md).
    /// <summary>Which background idle sounds play: the chirp, the feather ruffle, or both.</summary>
    public IdleSounds IdleSounds { get; set; } = IdleSounds.Both;
    /// <summary>How often the idle sounds come: fuzzy ranges, never an exact period.</summary>
    public IdleRate IdleRate { get; set; } = IdleRate.Mid;
    /// <summary>The pixel-art vines in the note's bottom-right corner.</summary>
    public bool PlantsOn { get; set; } = true;

    // Round 4 (docs/SPEC-round4.md).
    /// <summary>The size he set with the corner grip, as a minimum for the auto-size; NaN = auto.</summary>
    public double ManualWidth { get; set; } = double.NaN;
    public double ManualHeight { get; set; } = double.NaN;

    // Round 5 (docs/SPEC-round5.md).
    /// <summary>The active bird's id ("lennie", "meggie"); unknown = Lennie.</summary>
    public string Bird { get; set; } = "lennie";
    /// <summary>His names for the birds by id; absent = the bird's default name.</summary>
    public Dictionary<string, string> BirdNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pulls every field into its valid range; called after load and before save.</summary>
    public void Normalize()
    {
        Text ??= "";
        if (!Palette.IsKnownColor(Color)) Color = Palette.Colors[0].Hex;
        Opacity = Math.Clamp(Opacity, Palette.MinOpacity, Palette.MaxOpacity);
        if (Array.IndexOf(Palette.Fonts, Font) < 0) Font = Palette.Fonts[0];
        FontSize = Math.Clamp(FontSize, Palette.MinFontSize, Palette.MaxFontSize);
        if (double.IsNaN(X) || double.IsInfinity(X)) X = 100;
        if (double.IsNaN(Y) || double.IsInfinity(Y)) Y = 100;
        if (double.IsInfinity(DotX)) DotX = double.NaN;
        if (double.IsInfinity(DotY)) DotY = double.NaN;
        if (double.IsNaN(Volume) || double.IsInfinity(Volume)) Volume = 0.10;
        Volume = Math.Clamp(Volume, 0, 1);
        if (double.IsInfinity(ManualWidth) || ManualWidth <= 0) ManualWidth = double.NaN;
        if (double.IsInfinity(ManualHeight) || ManualHeight <= 0) ManualHeight = double.NaN;
        if (string.IsNullOrWhiteSpace(Bird)) Bird = "lennie";
        Bird = Bird.Trim().ToLowerInvariant();
        BirdNames ??= new(StringComparer.OrdinalIgnoreCase);
        Version = 2;
    }

    public bool HasDotPosition => !double.IsNaN(DotX) && !double.IsNaN(DotY);
}
