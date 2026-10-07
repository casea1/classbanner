namespace BetterBanner.Core;

public enum CommandKind
{
    /// <summary>No arguments: show the banner.</summary>
    Run,
    Install,
    Uninstall,
    Config,
    Show,
    Help,
    Version,
    ScreenSaverShow,
    ScreenSaverPreview,
    ScreenSaverConfigure,
}

public sealed record ParsedCommand(
    CommandKind Kind,
    IReadOnlyList<KeyValuePair<string, string>> Settings,
    bool Quiet = false,
    bool NoStart = false,
    bool KeepSettings = false,
    string? Error = null);

/// <summary>Parses the single-exe command line. Friendly options map onto <see cref="SettingKeys"/> names.</summary>
public static class CommandLine
{
    public const string Usage = """
        Better Banner - classification banner for Windows

        Usage:
          BetterBanner.exe                        Show the banner (normally started automatically at sign-in)
          BetterBanner.exe install [options]      Install for all users (run as administrator)
          BetterBanner.exe config [options]       Change machine settings (run as administrator)
          BetterBanner.exe show                   Print the effective settings
          BetterBanner.exe uninstall [--keep-settings] [--quiet]
          BetterBanner.exe version

        Options for install and config:
          --level <U|CUI|C|S|TS|TS/SCI>   Classification level
          --caption <text>                Custom marking, e.g. "SECRET//NOFORN" ("" for the standard marking)
          --position <top|bottom|both>
          --clock <on|off>                Show a clock (add --zulu for UTC)
          --zulu
          --logon-title <text>            Notice users must acknowledge before signing in
          --logon-text <text>             ("" for both removes the notice)
          --set <Name>=<Value>            Any setting, e.g. --set FontSize=12 (see README)
          --no-start                      Don't start the banner after installing
          --quiet                         No output except errors

        Group Policy settings (HKLM\SOFTWARE\Policies\BetterBanner) override these.
        """;

    public static ParsedCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return new ParsedCommand(CommandKind.Run, []);

        // Windows calls screen savers with /s, /p <hwnd>, /p:<hwnd>, /c or /c:<hwnd>, in any case.
        var first = args[0].Trim();
        if (first.Length >= 2 && first[0] == '/' )
        {
            switch (char.ToLowerInvariant(first[1]))
            {
                case 's': return new ParsedCommand(CommandKind.ScreenSaverShow, []);
                case 'p': return new ParsedCommand(CommandKind.ScreenSaverPreview, []);
                case 'c': return new ParsedCommand(CommandKind.ScreenSaverConfigure, []);
                case '?': return new ParsedCommand(CommandKind.Help, []);
            }
        }

        var kind = first.ToLowerInvariant() switch
        {
            "install" => CommandKind.Install,
            "uninstall" => CommandKind.Uninstall,
            "config" or "configure" => CommandKind.Config,
            "show" or "status" => CommandKind.Show,
            "help" or "-h" or "--help" or "-?" => CommandKind.Help,
            "version" or "--version" => CommandKind.Version,
            _ => (CommandKind?)null,
        };
        if (kind is null) return Fail($"Unknown command '{first}'.");

        var settings = new List<KeyValuePair<string, string>>();
        bool quiet = false, noStart = false, keepSettings = false;

        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            string? Next()
            {
                if (i + 1 >= args.Count) return null;
                return args[++i];
            }

            void Add(string key, string? value)
            {
                if (value is null) throw new ArgumentException($"{arg} needs a value.");
                settings.Add(new(key, value));
            }

            try
            {
                switch (arg.ToLowerInvariant())
                {
                    case "--level": Add(nameof(BannerSettings.Level), Next()); break;
                    case "--caption": Add(nameof(BannerSettings.Caption), Next()); break;
                    case "--position": Add(nameof(BannerSettings.Position), Next()); break;
                    case "--clock": Add(nameof(BannerSettings.ClockEnabled), Next()); break;
                    case "--zulu": Add(nameof(BannerSettings.ClockUseZulu), "1"); break;
                    case "--logon-title": Add(nameof(BannerSettings.LogonNoticeTitle), Next()); break;
                    case "--logon-text": Add(nameof(BannerSettings.LogonNoticeText), Next()); break;
                    case "--set":
                        var pair = Next() ?? throw new ArgumentException("--set needs Name=Value.");
                        var eq = pair.IndexOf('=');
                        if (eq <= 0) throw new ArgumentException($"--set expects Name=Value, got '{pair}'.");
                        settings.Add(new(pair[..eq].Trim(), pair[(eq + 1)..]));
                        break;
                    case "--quiet": case "-q": quiet = true; break;
                    case "--no-start": noStart = true; break;
                    case "--keep-settings": keepSettings = true; break;
                    default: return Fail($"Unknown option '{arg}'.");
                }
            }
            catch (ArgumentException ex)
            {
                return Fail(ex.Message);
            }
        }

        if (settings.Count > 0 && kind is not (CommandKind.Install or CommandKind.Config))
            return Fail($"Settings options only apply to 'install' and 'config'.");

        // Validate values up front so a typo never half-applies.
        var probe = new BannerSettings();
        foreach (var (name, value) in settings)
        {
            if (!SettingKeys.TryApply(probe, name, value))
                return Fail(SettingKeys.Names.Contains(name, StringComparer.OrdinalIgnoreCase)
                    ? $"Invalid value '{value}' for {name}."
                    : $"Unknown setting '{name}'.");
        }

        return new ParsedCommand(kind.Value, settings, quiet, noStart, keepSettings);
    }

    private static ParsedCommand Fail(string error) => new(CommandKind.Help, [], Error: error);
}
