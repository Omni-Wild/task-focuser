using System.Windows.Media;

namespace TaskFocuser.Core;

/// <summary>The seven colours (White since round 6), the five fonts and the limits. The only place these live.</summary>
public static class Palette
{
    public sealed record Swatch(string Name, string Hex);

    public static readonly Swatch[] Colors =
    {
        new("Yellow", "#FFF59E"),
        new("Peach",  "#FFD3A8"),
        new("Pink",   "#FFC6DD"),
        new("Mint",   "#C4F0C9"),
        new("Sky",    "#BFE3FF"),
        new("Grey",   "#E3E1DC"),
        new("White",  "#FAFAF8"),
    };

    /// <summary>All ship with Windows 11.</summary>
    public static readonly string[] Fonts =
    {
        "Segoe UI",
        "Segoe Print",
        "Comic Sans MS",
        "Cascadia Code",
        "Bahnschrift",
    };

    public const int MinFontSize = 11;
    public const int MaxFontSize = 28;
    public const int DefaultFontSize = 15;
    public const int MinOpacity = 20;
    public const int MaxOpacity = 100;

    /// <summary>Text colour on every swatch.</summary>
    public static readonly Color Ink = (Color)ColorConverter.ConvertFromString("#2B2A25");

    /// <summary>The glue strip and the settings bar: the note colour darkened by this factor.</summary>
    public const double StripDarken = 0.12;

    public static bool IsKnownColor(string? hex) =>
        hex is not null && Array.Exists(Colors, c => string.Equals(c.Hex, hex, StringComparison.OrdinalIgnoreCase));

    public static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>Darkens by <paramref name="factor"/> (0 = unchanged, 1 = black), alpha untouched.</summary>
    public static Color Darken(Color c, double factor)
    {
        double k = 1 - Math.Clamp(factor, 0, 1);
        return Color.FromArgb(c.A, (byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
    }

    public static Color StripOf(string hex) => Darken(Parse(hex), StripDarken);
}
