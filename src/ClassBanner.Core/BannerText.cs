using System.Globalization;
using System.Text;

namespace ClassBanner.Core;

public static class BannerText
{
    public const string ZuluIndicator = "Z";
    public const string LocalIndicator = "L";
    private const string TzPlaceholder = "%TZI%";

    public const string DefaultClockFormat = "ddd dd-MMM-yy HH:mm%TZI%";

    /// <summary>
    /// Formats the banner clock. The format is a .NET custom date/time format; %TZI% (any case) is replaced with
    /// "Z" for Zulu or "L" for local time. An invalid format falls back to <see cref="DefaultClockFormat"/>.
    /// </summary>
    public static string FormatClock(DateTimeOffset now, string format, bool useZulu, bool uppercase, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.InvariantCulture;
        var time = useZulu ? now.ToUniversalTime() : now;
        string result;
        try
        {
            result = Format(time, format, useZulu, culture);
        }
        catch (FormatException)
        {
            result = Format(time, DefaultClockFormat, useZulu, culture);
        }
        return uppercase ? result.ToUpperInvariant() : result;
    }

    /// <summary>True when the format can be used for the clock (checked when the setting is applied).</summary>
    public static bool IsValidClockFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format) || format.Length > 100) return false;
        try
        {
            Format(new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero), format, useZulu: true, CultureInfo.InvariantCulture);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Format(DateTimeOffset time, string format, bool useZulu, CultureInfo culture)
    {
        var indicator = useZulu ? ZuluIndicator : LocalIndicator;
        var sb = new StringBuilder();
        var start = 0;
        while (true)
        {
            var index = format.IndexOf(TzPlaceholder, start, StringComparison.OrdinalIgnoreCase);
            var part = index < 0 ? format[start..] : format[start..index];
            if (part.Length > 0)
                // A lone character is a *standard* format ("d" = short date); force the custom meaning.
                sb.Append(time.ToString(part.Length == 1 ? "%" + part : part, culture));
            if (index < 0) break;
            sb.Append(indicator);
            start = index + TzPlaceholder.Length;
        }
        return sb.ToString();
    }
}
