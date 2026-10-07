using System.Globalization;
using System.Text;

namespace BetterBanner.Core;

public static class BannerText
{
    /// <summary>Spreads a caption out: ("SECRET", " ") becomes "S E C R E T". Existing spaces become wider gaps.</summary>
    public static string Expand(string caption, string? delimiter)
    {
        if (string.IsNullOrEmpty(delimiter) || caption.Length < 2) return caption;

        var sb = new StringBuilder(caption.Length * (delimiter.Length + 1));
        var elements = StringInfo.GetTextElementEnumerator(caption);
        var first = true;
        while (elements.MoveNext())
        {
            if (!first) sb.Append(delimiter);
            sb.Append(elements.GetTextElement());
            first = false;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Formats the banner clock. <paramref name="settings"/>.Format is a .NET custom date/time format where
    /// %TZI% (any case) is replaced with the local or Zulu indicator.
    /// </summary>
    public static string FormatClock(DateTimeOffset now, ClockSettings settings, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var time = settings.UseZulu ? now.ToUniversalTime() : now;
        var indicator = settings.UseZulu ? settings.ZuluIndicator : settings.LocalIndicator;

        var parts = SplitIgnoreCase(settings.Format, "%TZI%");

        var sb = new StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) sb.Append(indicator);
            sb.Append(FormatPart(time, parts[i], culture));
        }

        var result = sb.ToString();
        return settings.Uppercase ? result.ToUpper(culture) : result;
    }

    private static string FormatPart(DateTimeOffset time, string format, CultureInfo culture)
    {
        if (format.Length == 0) return "";
        // A single character would be treated as a standard format ("d" = short date); force custom.
        if (format.Length == 1) format = "%" + format;
        try
        {
            return time.ToString(format, culture);
        }
        catch (FormatException)
        {
            return format;
        }
    }

    private static string[] SplitIgnoreCase(string text, string separator)
    {
        var result = new List<string>();
        var start = 0;
        int index;
        while ((index = text.IndexOf(separator, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            result.Add(text[start..index]);
            start = index + separator.Length;
        }
        result.Add(text[start..]);
        return result.ToArray();
    }
}
