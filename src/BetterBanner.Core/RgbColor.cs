using System.Globalization;

namespace BetterBanner.Core;

/// <summary>An opaque 24-bit color. Kept independent of System.Drawing so the core stays cross-platform.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor Black => new(0, 0, 0);
    public static RgbColor White => new(255, 255, 255);

    /// <summary>Parses "#RRGGBB", "RRGGBB" or "R G B" (the format Windows uses under Control Panel\Colors).</summary>
    public static bool TryParse(string? text, out RgbColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();

        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3)
        {
            if (byte.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var r) &&
                byte.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var g) &&
                byte.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var b))
            {
                color = new RgbColor(r, g, b);
                return true;
            }
            return false;
        }

        if (s.StartsWith('#')) s = s[1..];
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return false;
        color = new RgbColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static RgbColor Parse(string text) =>
        TryParse(text, out var c) ? c : throw new FormatException($"'{text}' is not a valid color. Use #RRGGBB.");

    /// <summary>
    /// Converts a Visual Basic / Win32 COLORREF (0x00BBGGRR stored as a decimal number), as used by Banner 3.1.
    /// </summary>
    public static RgbColor FromColorRef(int colorRef) =>
        new((byte)(colorRef & 0xFF), (byte)((colorRef >> 8) & 0xFF), (byte)((colorRef >> 16) & 0xFF));

    /// <summary>Win32 COLORREF (0x00BBGGRR), for SetSysColors and friends.</summary>
    public int ToColorRef() => R | (G << 8) | (B << 16);

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>WCAG relative luminance, used to pick readable text colors.</summary>
    public double RelativeLuminance()
    {
        static double Channel(byte c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(R) + 0.7152 * Channel(G) + 0.0722 * Channel(B);
    }

    public static double ContrastRatio(RgbColor a, RgbColor b)
    {
        var la = a.RelativeLuminance();
        var lb = b.RelativeLuminance();
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
