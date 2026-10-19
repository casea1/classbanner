using ClassBanner.Core;

namespace ClassBanner.Core.Tests;

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
    public void Invalid_format_falls_back_to_the_default_instead_of_showing_the_format()
    {
        var text = BannerText.FormatClock(Now, "HH:mm \\", useZulu: true, uppercase: false);
        Assert.Equal(BannerText.FormatClock(Now, BannerText.DefaultClockFormat, true, false), text);
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
    [InlineData("banner")]
    [InlineData("run")]
    public void Banner_command_runs_the_banner_explicitly(string arg)
    {
        Assert.Equal(CommandKind.Run, CommandLine.Parse([arg]).Kind);
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

    /// <summary>Issue #11: values may start with "--" when they aren't one of our options.</summary>
    [Theory]
    [InlineData("--logon-text", "-- Authorized use only --")]
    [InlineData("--caption", "--")]
    public void Values_starting_with_dashes_are_accepted(string option, string value)
    {
        var cmd = CommandLine.Parse(["config", option, value, "--quiet"]);
        Assert.Null(cmd.Error);
        Assert.Equal(value, Assert.Single(cmd.Settings).Value);
        Assert.True(cmd.Quiet);
    }

    [Fact]
    public void Option_equals_value_form()
    {
        var cmd = CommandLine.Parse(["config", "--caption=--quiet", "--level=S", "--logon-text=a=b"]);
        Assert.Null(cmd.Error);
        Assert.False(cmd.Quiet);
        Assert.Equal(["--quiet", "S", "a=b"], cmd.Settings.Select(s => s.Value));
    }

    [Fact]
    public void Flags_do_not_take_values()
    {
        Assert.NotNull(CommandLine.Parse(["uninstall", "--quiet=1"]).Error);
    }

    [Theory]
    [InlineData("FontSize=999")]
    [InlineData("ClockFormat=HH:mm \\")]
    public void Command_line_rejects_out_of_range_and_invalid_values(string pair)
    {
        Assert.NotNull(CommandLine.Parse(["config", "--set", pair]).Error);
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

/// <summary>Issue #10: the marking is never cut off; the clock gives way.</summary>
public class CaptionLayoutTests
{
    [Fact]
    public void Centered_when_the_clock_fits_beside_it()
    {
        var p = CaptionLayout.Place(1920, 200, 0, 250, 8);
        Assert.Equal(new CaptionPlacement(0, 1920, false, true, 1), p);
    }

    [Fact]
    public void Moved_left_when_centering_would_hit_the_clock()
    {
        var p = CaptionLayout.Place(1024, 600, 0, 300, 8);
        Assert.True(p.ShowClock);
        Assert.Equal(1, p.Scale);
        Assert.Equal(8, p.Left);
        Assert.True(p.Right - p.Left >= 600);
        Assert.True(p.Right <= 1024 - 8 - 300);
    }

    [Fact]
    public void Clock_is_hidden_before_the_marking_is_shortened()
    {
        var p = CaptionLayout.Place(1024, 800, 0, 300, 8);
        Assert.False(p.ShowClock);
        Assert.Equal(1, p.Scale);
        Assert.Equal(new CaptionPlacement(0, 1024, false, false, 1), p);
    }

    [Fact]
    public void User_name_and_clock_both_shown_when_they_fit()
    {
        var p = CaptionLayout.Place(1920, 200, 150, 250, 8);
        Assert.Equal(new CaptionPlacement(0, 1920, true, true, 1), p);
    }

    [Fact]
    public void Marking_moves_right_of_a_long_user_name()
    {
        var p = CaptionLayout.Place(1024, 400, 350, 0, 8);
        Assert.True(p.ShowUser);
        Assert.Equal(8 + 350 + 8, p.Left);
        Assert.True(p.Right - p.Left >= 400);
    }

    [Fact]
    public void Clock_gives_way_before_the_user_name()
    {
        var p = CaptionLayout.Place(1024, 500, 200, 300, 8);
        Assert.True(p.ShowUser);
        Assert.False(p.ShowClock);
        Assert.Equal(1, p.Scale);
        Assert.Equal(new CaptionPlacement(0, 1024, true, false, 1), p); // still centered, clear of the name
    }

    [Fact]
    public void Both_side_items_hidden_before_the_marking_is_shortened()
    {
        var p = CaptionLayout.Place(1024, 900, 200, 300, 8);
        Assert.Equal(new CaptionPlacement(0, 1024, false, false, 1), p);
    }

    [Fact]
    public void Show_user_name_setting_round_trips()
    {
        var s = new BannerSettings();
        Assert.False(s.ShowUserName);
        Assert.True(SettingKeys.TryApply(s, "ShowUserName", "1"));
        Assert.True(s.ShowUserName);
    }

    [Fact]
    public void Marking_wider_than_the_screen_is_shrunk_not_truncated()
    {
        var p = CaptionLayout.Place(800, 1600, 0, 0, 8);
        Assert.False(p.ShowClock);
        Assert.InRange(p.Scale, 0.48, 0.5);
    }
}
