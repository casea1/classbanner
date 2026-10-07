using System.Globalization;
using System.Text;

namespace BetterBanner.Core;

public static class BannerText
{
    public const string ZuluIndicator = "Z";
    public const string LocalIndicator = "L";
    private const string TzPlaceholder = "%TZI%";

    /// <summary>
    /// Formats the banner clock. The format is a .NET custom date/time format; %TZI% (any case) is replaced with
    /// "Z" for Zulu or "L" for local time. A bad format falls back to the raw format text instead of throwing.
    /// </summary>
    public static string FormatClock(DateTimeOffset now, string format, bool useZulu, bool uppercase, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.InvariantCulture;
        var time = useZulu ? now.ToUniversalTime() : now;
        var indicator = useZulu ? ZuluIndicator : LocalIndicator;

        var sb = new StringBuilder();
        var start = 0;
        while (true)
        {
            var index = format.IndexOf(TzPlaceholder, start, StringComparison.OrdinalIgnoreCase);
            var part = index < 0 ? format[start..] : format[start..index];
            sb.Append(FormatPart(time, part, culture));
            if (index < 0) break;
            sb.Append(indicator);
            start = index + TzPlaceholder.Length;
        }

        var result = sb.ToString();
        return uppercase ? result.ToUpperInvariant() : result;
    }

    private static string FormatPart(DateTimeOffset time, string format, CultureInfo culture)
    {
        if (format.Length == 0) return "";
        // A lone character is a *standard* format ("d" = short date); force the custom meaning.
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
}
