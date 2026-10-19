namespace ClassBanner.Core;

public enum CommandKind
{
    /// <summary>No arguments: show the banner.</summary>
    Run,
    Install,
    Uninstall,
    Config,
    Show,
    Settings,
    Help,
    Version,
    ScreenSaverShow,
    ScreenSaverPreview,
    ScreenSaverConfigure,

    /// <summary>Run as the Windows service that keeps a banner in every signed-in session (started by Windows).</summary>
    Service,
}

public sealed record ParsedCommand(
    CommandKind Kind,
    IReadOnlyList<KeyValuePair<string, string>> Settings,
    bool Quiet = false,
    bool NoStart = false,
    bool KeepSettings = false,
    string? Error = null,
    bool Managed = false,
    bool KeepUserSettings = false);

/// <summary>Parses the single-exe command line. Friendly options map onto <see cref="SettingKeys"/> names.</summary>
public static partial class CommandLine
{
    [System.Text.RegularExpressions.GeneratedRegex(@"^/([scp])(:\d*)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ScreenSaverSwitchRegex();

    private static readonly System.Text.RegularExpressions.Regex ScreenSaverSwitch = ScreenSaverSwitchRegex();

    public const string Usage = """
        ClassBanner - classification banner for Windows

        Usage:
          ClassBanner.exe                        Setup window (installed copy: show the banner)
          ClassBanner.exe banner                 Show the banner without installing
          ClassBanner.exe install [options]      Install for all users (run as administrator)
          ClassBanner.exe config [options]       Change machine settings (run as administrator)
          ClassBanner.exe settings               Open the settings window (also in Control Panel)
          ClassBanner.exe show                   Print the effective settings
          ClassBanner.exe uninstall [--keep-settings] [--keep-user-settings] [--quiet]
          ClassBanner.exe version

        Options for install and config:
          --level <U|CUI|C|S|TS|TS/SCI>   Classification level
          --caption <text>                Custom marking, e.g. "SECRET//NOFORN" ("" for the standard marking)
          --position <top|bottom|both>
          --clock <on|off>                Show a clock (add --zulu for UTC)
          --zulu
          --logon-title <text>            Notice users must acknowledge before signing in
          --logon-text <text>             ("" for both removes the notice)
          --set <Name>=<Value>            Any setting, e.g. --set FontSize=12 (see README)
          --no-start                      Don't start the banner service now (it starts at the next restart)
          --quiet                         No output except errors

        Values can also be written --option=value (e.g. --logon-text="-- Authorized use only --").

        Group Policy settings (HKLM\SOFTWARE\Policies\ClassBanner) override these.
        """;

    /// <summary>The same help laid out for a message box (proportional font, no column alignment).</summary>
    public const string DialogUsage = """
        ClassBanner - classification banner for Windows

        ClassBanner.exe install --level <level> [options]
            Install for all users. Levels: U, CUI, C, S, TS, TS/SCI.
        ClassBanner.exe config [options]
            Change this computer's settings.
        ClassBanner.exe settings
            Open the settings window (also in Control Panel).
        ClassBanner.exe show
            Print the settings in effect.
        ClassBanner.exe uninstall [--keep-settings]

        Options: --level, --caption <text>, --position top|bottom|both, --clock on|off, --zulu, --logon-title <text>, --logon-text <text>, --set Name=Value, --no-start, --quiet.

        Run from a command prompt to see full help. Group Policy settings override these.
        """;

    public static ParsedCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return new ParsedCommand(CommandKind.Run, []);

        // Windows calls screen savers with exactly /s, /p <hwnd>, /p:<hwnd>, /c or /c:<hwnd>, in any case.
        var first = args[0].Trim();
        var screenSaver = ScreenSaverSwitch.Match(first);
        if (screenSaver.Success)
        {
            return char.ToLowerInvariant(screenSaver.Groups[1].Value[0]) switch
            {
                's' => new ParsedCommand(CommandKind.ScreenSaverShow, []),
                'p' => new ParsedCommand(CommandKind.ScreenSaverPreview, []),
                _ => new ParsedCommand(CommandKind.ScreenSaverConfigure, []),
            };
        }
        if (first == "/?") return new ParsedCommand(CommandKind.Help, []);

        var kind = first.ToLowerInvariant() switch
        {
            "install" => CommandKind.Install,
            "uninstall" => CommandKind.Uninstall,
            "config" or "configure" => CommandKind.Config,
            "show" or "status" => CommandKind.Show,
            "banner" or "run" => CommandKind.Run,
            "service" => CommandKind.Service,
            "settings" => CommandKind.Settings,
            "help" or "-h" or "--help" or "-?" => CommandKind.Help,
            "version" or "--version" => CommandKind.Version,
            _ => (CommandKind?)null,
        };
        if (kind is null) return Fail($"Unknown command '{first}'.");

        var settings = new List<KeyValuePair<string, string>>();
        bool quiet = false, noStart = false, keepSettings = false, managed = false, keepUserSettings = false;

        for (var i = 1; i < args.Count; i++)
        {
            // "--option=value" is always unambiguous; "--option value" also works.
            var arg = args[i];
            string? inlineValue = null;
            var eqIndex = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && eqIndex > 2 && IsOption(arg[..eqIndex]))
            {
                inlineValue = arg[(eqIndex + 1)..];
                arg = arg[..eqIndex];
            }

            // The next argument is a missing value only if it is one of our own options, so "--caption --quiet" is an
            // error but "--logon-text \"-- Authorized use only --\"" is fine.
            string? Next()
            {
                if (inlineValue is not null) return inlineValue;
                if (i + 1 >= args.Count || IsOption(args[i + 1])) return null;
                return args[++i];
            }

            void Add(string key, string? value)
            {
                if (value is null) throw new ArgumentException($"{arg} needs a value.");
                settings.Add(new(key, value));
            }

            void Flag(ref bool flag)
            {
                if (inlineValue is not null) throw new ArgumentException($"{arg} doesn't take a value.");
                flag = true;
            }

            try
            {
                switch (arg.ToLowerInvariant())
                {
                    case "--level": Add(nameof(BannerSettings.Level), Next()); break;
                    case "--caption": Add(nameof(BannerSettings.Caption), Next()); break;
                    case "--position": Add(nameof(BannerSettings.Position), Next()); break;
                    case "--clock": Add(nameof(BannerSettings.ClockEnabled), Next()); break;
                    case "--zulu": Add(nameof(BannerSettings.ClockUseZulu), inlineValue ?? "1"); break;
                    case "--logon-title": Add(nameof(BannerSettings.LogonNoticeTitle), Next()); break;
                    case "--logon-text": Add(nameof(BannerSettings.LogonNoticeText), Next()); break;
                    case "--set":
                        var pair = Next() ?? throw new ArgumentException("--set needs Name=Value.");
                        var eq = pair.IndexOf('=');
                        if (eq <= 0) throw new ArgumentException($"--set expects Name=Value, got '{pair}'.");
                        settings.Add(new(pair[..eq].Trim(), pair[(eq + 1)..]));
                        break;
                    case "--quiet": case "-q": Flag(ref quiet); break;
                    case "--no-start": Flag(ref noStart); break;
                    case "--keep-settings": Flag(ref keepSettings); break;
                    case "--managed" when kind == CommandKind.Run: Flag(ref managed); break;
                    case "--keep-user-settings" when kind == CommandKind.Uninstall: Flag(ref keepUserSettings); break;
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

        return new ParsedCommand(kind.Value, settings, quiet, noStart, keepSettings, Managed: managed, KeepUserSettings: keepUserSettings);
    }

    private static ParsedCommand Fail(string error) => new(CommandKind.Help, [], Error: error);

    private static readonly HashSet<string> Options = new(StringComparer.OrdinalIgnoreCase)
    {
        "--level", "--caption", "--position", "--clock", "--zulu", "--logon-title", "--logon-text", "--set",
        "--quiet", "-q", "--no-start", "--keep-settings", "--managed", "--keep-user-settings",
    };

    /// <summary>True for our own options, alone or in "--option=value" form.</summary>
    private static bool IsOption(string arg)
    {
        var eq = arg.IndexOf('=');
        return Options.Contains(eq > 0 ? arg[..eq] : arg);
    }

    /// <summary>Builds a Windows command line that CommandLineToArgvW / the C runtime split back into exactly <paramref name="args"/>.</summary>
    public static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    /// <summary>Quotes one argument using the Windows rules (backslashes only matter before a quote).</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\r', '"']) < 0) return arg;

        var sb = new System.Text.StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
            }
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}
