using System.Text.Json;

namespace ClassBanner.Core;

/// <summary>An install command that sets up another computer the way this one is set up.</summary>
public sealed record ExportedCommand(string Command, IReadOnlyList<string> Arguments, IReadOnlyList<string> Notes);

public static class ExportCommand
{
    // Settings that have their own option, written first and by name rather than as --set.
    private static readonly HashSet<string> OwnOption = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(BannerSettings.Level), nameof(BannerSettings.Caption),
        nameof(BannerSettings.LogonNoticeTitle), nameof(BannerSettings.LogonNoticeText),
    };

    // cmd.exe treats these specially outside quotes, so any argument containing one is quoted.
    private static readonly char[] CmdSpecial = [' ', '\t', '&', '|', '<', '>', '^', '(', ')', '%', '!', '"'];

    /// <summary>
    /// <c>ClassBanner.exe install --level ... --reset --quiet</c> plus every setting that differs from the built-in
    /// default. <c>--reset</c> makes the result exact on a computer that already has other settings. Returns null when
    /// no classification level is set.
    /// </summary>
    public static ExportedCommand? Build(BannerSettings settings)
    {
        if (settings.Level is not { } level) return null;
        var notes = new List<string>();
        var args = new List<string> { "install", "--level", level.ToString() };
        if (!string.IsNullOrWhiteSpace(settings.Caption)) args.AddRange(["--caption", settings.Caption.Trim()]);
        args.AddRange(["--reset", "--quiet"]);

        using var current = JsonDocument.Parse(SettingsSerializer.Serialize(settings));
        using var defaults = JsonDocument.Parse(SettingsSerializer.Serialize(new BannerSettings()));
        foreach (var property in current.RootElement.EnumerateObject())
        {
            if (OwnOption.Contains(property.Name)) continue;
            if (defaults.RootElement.TryGetProperty(property.Name, out var standard) &&
                standard.GetRawText() == property.Value.GetRawText()) continue;
            if (Value(property.Value) is { } value) args.AddRange(["--set", $"{property.Name}={value}"]);
        }

        AddNotice(args, notes, "--logon-title", settings.LogonNoticeTitle);
        AddNotice(args, notes, "--logon-text", settings.LogonNoticeText);

        var command = "start /wait ClassBanner.exe " + string.Join(' ', args.Select(CmdQuote));
        if (command.Contains('%'))
            notes.Add("In a .bat or .cmd file, write each % as %% (cmd expands %...% there).");
        if (args.Any(a => a.Contains('"')))
            notes.Add("A value contains a double quote, which cmd may not pass on as typed; check that setting after running it.");
        return new ExportedCommand(command, args, notes);
    }

    private static void AddNotice(List<string> args, List<string> notes, string option, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var oneLine = string.Join(' ', text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        if (oneLine != text.Trim())
            notes.Add($"The sign-in notice has line breaks, which a command line can't carry; {option} puts it on one line.");
        args.AddRange([option, oneLine]);
    }

    private static string? Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => "1",
        JsonValueKind.False => "0",
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null,
    };

    /// <summary>Quotes an argument for both cmd.exe and the program's own argument parsing.</summary>
    public static string CmdQuote(string arg) => CommandLine.Quote(arg, force: arg.Length == 0 || arg.IndexOfAny(CmdSpecial) >= 0);
}
