using BetterBanner.Core;

namespace BetterBanner.Core.Tests;

public class ClockTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 13, 30, 5, TimeSpan.FromHours(-5));

    [Fact]
    public void Zulu_converts_to_utc_and_uses_Z()
    {
        Assert.Equal("071830Z OCT 26", BannerText.FormatClock(Now, "ddHHmm%TZI% MMM yy", useZulu: true, uppercase: true));
    }

    [Fact]
    public void Local_keeps_local_time_and_uses_L()
    {
        Assert.Equal("Wed 07-Oct-26 13:30L", BannerText.FormatClock(Now, "ddd dd-MMM-yy HH:mm%TZI%", useZulu: false, uppercase: false));
    }

    [Fact]
    public void Placeholder_is_case_insensitive_and_repeatable()
    {
        Assert.Equal("13L 30L", BannerText.FormatClock(Now, "HH%tzi% mm%TZI%", false, false));
    }

    [Fact]
    public void Single_letter_format_is_custom_not_standard()
    {
        Assert.Equal("7", BannerText.FormatClock(Now, "d", false, false));
    }
}

public class WindowRecoveryTests
{
    private static readonly ScreenRect Work = new(0, 0, 1920, 1040);
    private static readonly ScreenRect TopBanner = new(0, 0, 1920, 24);

    [Fact]
    public void Window_with_title_bar_under_banner_needs_recovery()
    {
        Assert.True(WindowRecovery.NeedsRecovery(new(100, 0, 900, 600), [Work], [TopBanner], 30));
    }

    [Fact]
    public void Normal_window_is_left_alone()
    {
        Assert.False(WindowRecovery.NeedsRecovery(new(100, 100, 900, 600), [Work], [TopBanner], 30));
    }

    [Fact]
    public void Off_screen_window_needs_recovery()
    {
        Assert.True(WindowRecovery.NeedsRecovery(new(5000, 100, 5800, 600), [Work], [TopBanner], 30));
    }

    [Fact]
    public void Cascade_steps_and_wraps_inside_the_work_area()
    {
        Assert.Equal((0, 0), WindowRecovery.Target(Work, 0, 30, true));
        Assert.Equal((60, 60), WindowRecovery.Target(Work, 2, 30, true));
        Assert.Equal((0, 0), WindowRecovery.Target(Work, 5, 30, false));
        var far = WindowRecovery.Target(Work, 1000, 30, true);
        Assert.InRange(far.X, 0, Work.Height / 2);
    }

    [Fact]
    public void Chooses_the_screen_the_window_overlaps_most()
    {
        var second = new ScreenRect(1920, 0, 3840, 1080);
        Assert.Equal(second, WindowRecovery.ChooseWorkArea(new(1800, 500, 2600, 900), [Work, second], Work));
        Assert.Equal(Work, WindowRecovery.ChooseWorkArea(new(9000, 0, 9100, 100), [Work, second], Work));
    }
}

public class CommandLineTests
{
    [Fact]
    public void No_arguments_runs_the_banner()
    {
        Assert.Equal(CommandKind.Run, CommandLine.Parse([]).Kind);
    }

    [Theory]
    [InlineData("/s", CommandKind.ScreenSaverShow)]
    [InlineData("/S", CommandKind.ScreenSaverShow)]
    [InlineData("/p:12345", CommandKind.ScreenSaverPreview)]
    [InlineData("/c", CommandKind.ScreenSaverConfigure)]
    public void Screen_saver_switches(string arg, CommandKind kind)
    {
        Assert.Equal(kind, CommandLine.Parse([arg]).Kind);
    }

    [Fact]
    public void Install_with_friendly_options()
    {
        var cmd = CommandLine.Parse(["install", "--level", "TS/SCI", "--clock", "on", "--zulu", "--logon-title", "Notice", "--set", "FontSize=12", "--no-start"]);

        Assert.Null(cmd.Error);
        Assert.Equal(CommandKind.Install, cmd.Kind);
        Assert.True(cmd.NoStart);

        var s = new BannerSettings();
        SettingKeys.ApplyAll(s, cmd.Settings);
        Assert.Equal(ClassificationLevel.TopSecretSci, s.Level);
        Assert.True(s.ClockEnabled);
        Assert.True(s.ClockUseZulu);
        Assert.Equal(12, s.FontSize);
        Assert.Equal("Notice", s.LogonNoticeTitle);
    }

    [Theory]
    [InlineData("install", "--level", "PURPLE")]
    [InlineData("install", "--set", "Bogus=1")]
    [InlineData("install", "--set", "noequals")]
    [InlineData("install", "--level")]
    [InlineData("install", "--frobnicate")]
    [InlineData("explode")]
    public void Bad_input_is_rejected_before_anything_changes(params string[] args)
    {
        var cmd = CommandLine.Parse(args);
        Assert.NotNull(cmd.Error);
        Assert.Equal(CommandKind.Help, cmd.Kind);
    }

    [Theory]
    [InlineData("/settings")]
    [InlineData("/start")]
    [InlineData("/cancel")]
    [InlineData("/p:abc")]
    public void Only_exact_screen_saver_switches_start_the_screen_saver(string arg)
    {
        Assert.False(CommandLine.Parse([arg]).Kind is CommandKind.ScreenSaverShow or CommandKind.ScreenSaverPreview or CommandKind.ScreenSaverConfigure);
    }

    [Fact]
    public void Screen_saver_preview_with_separate_window_handle()
    {
        Assert.Equal(CommandKind.ScreenSaverPreview, CommandLine.Parse(["/p", "123456"]).Kind);
    }

    [Fact]
    public void An_option_is_never_taken_as_the_previous_options_value()
    {
        var cmd = CommandLine.Parse(["config", "--caption", "--quiet"]);
        Assert.NotNull(cmd.Error);
        Assert.Contains("--caption", cmd.Error);
    }

    [Fact]
    public void Empty_value_still_clears_the_caption()
    {
        var cmd = CommandLine.Parse(["config", "--caption", "", "--quiet"]);
        Assert.Null(cmd.Error);
        Assert.True(cmd.Quiet);
    }

    [Fact]
    public void Settings_only_allowed_for_install_and_config()
    {
        Assert.NotNull(CommandLine.Parse(["uninstall", "--level", "S"]).Error);
    }
}

public class ArgumentQuotingTests
{
    /// <summary>Reference splitter following the documented MSVC/CommandLineToArgvW rules.</summary>
    private static List<string> Split(string commandLine)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var hasArg = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var n = 0;
                while (i < commandLine.Length && commandLine[i] == '\\') { n++; i++; }
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', n / 2);
                    if (n % 2 == 1) current.Append('"');
                    else { inQuotes = !inQuotes; }
                }
                else
                {
                    current.Append('\\', n);
                    i--;
                }
                hasArg = true;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                hasArg = true;
            }
            else if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (hasArg) args.Add(current.ToString());
                current.Clear();
                hasArg = false;
            }
            else
            {
                current.Append(c);
                hasArg = true;
            }
        }
        if (hasArg) args.Add(current.ToString());
        return args;
    }

    [Theory]
    [InlineData("simple")]
    [InlineData("")]
    [InlineData("with space")]
    [InlineData("SECRET//NOFORN")]
    [InlineData("say \"hi\"")]
    [InlineData(@"C:\path with space\")]
    [InlineData(@"trailing\\")]
    [InlineData(@"a\\""b")]
    [InlineData("multi\r\nline notice")]
    [InlineData("LogonNoticeText=This system is approved for SECRET. \"Authorized\" use only.")]
    public void Quoted_arguments_round_trip(string value)
    {
        var args = new[] { "config", "--set", value, "--quiet" };
        Assert.Equal(args, Split(CommandLine.Join(args)));
    }

    [Fact]
    public void Settings_command_parses()
    {
        Assert.Equal(CommandKind.Settings, CommandLine.Parse(["settings"]).Kind);
    }
}
