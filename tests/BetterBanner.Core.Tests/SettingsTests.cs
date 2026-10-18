using BetterBanner.Core;

namespace BetterBanner.Core.Tests;

public class SettingsTests
{
    private sealed class FakePolicy(Dictionary<string, string> values) : IPolicySource
    {
        public IReadOnlyDictionary<string, string> ReadValues() => values;
    }

    [Theory]
    [InlineData(ClassificationLevel.Unclassified, "UNCLASSIFIED", "#007A33")]
    [InlineData(ClassificationLevel.Cui, "CUI", "#502B85")]
    [InlineData(ClassificationLevel.Confidential, "CONFIDENTIAL", "#0033A0")]
    [InlineData(ClassificationLevel.Secret, "SECRET", "#C8102E")]
    [InlineData(ClassificationLevel.TopSecret, "TOP SECRET", "#FF8C00")]
    [InlineData(ClassificationLevel.TopSecretSci, "TOP SECRET//SCI", "#FCE83A")]
    public void Presets_use_standard_markings_and_colors(ClassificationLevel level, string caption, string color)
    {
        var s = new BannerSettings { Level = level };
        Assert.Equal(caption, s.EffectiveCaption);
        Assert.Equal(color, s.EffectiveBackColor.ToString());
    }

    [Fact]
    public void Every_preset_text_color_is_readable()
    {
        foreach (var p in ClassificationPresets.All)
            Assert.True(RgbColor.ContrastRatio(p.Background, p.Foreground) >= 4.5, $"{p.Caption} contrast too low");
    }

    [Theory]
    [InlineData("U", ClassificationLevel.Unclassified)]
    [InlineData("cui", ClassificationLevel.Cui)]
    [InlineData("C", ClassificationLevel.Confidential)]
    [InlineData("Secret", ClassificationLevel.Secret)]
    [InlineData("TS", ClassificationLevel.TopSecret)]
    [InlineData("TOP SECRET", ClassificationLevel.TopSecret)]
    [InlineData("TS/SCI", ClassificationLevel.TopSecretSci)]
    [InlineData("TOP SECRET//SCI", ClassificationLevel.TopSecretSci)]
    [InlineData("TopSecretSci", ClassificationLevel.TopSecretSci)]
    public void Level_accepts_common_spellings(string text, ClassificationLevel expected)
    {
        Assert.True(ClassificationPresets.TryParseLevel(text, out var level));
        Assert.Equal(expected, level);
    }

    [Fact]
    public void Custom_caption_and_colors_override_the_preset()
    {
        var s = new BannerSettings { Level = ClassificationLevel.Secret, Caption = " SECRET//NOFORN ", BackColor = "#112233" };
        Assert.Equal("SECRET//NOFORN", s.EffectiveCaption);
        Assert.Equal("#112233", s.EffectiveBackColor.ToString());
        Assert.Equal(RgbColor.White, s.EffectiveForeColor);
    }

    [Fact]
    public void Policy_overrides_the_settings_file()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            SettingsLoader.Save(new BannerSettings { Level = ClassificationLevel.Unclassified, ClockEnabled = true }, path);

            var policy = new FakePolicy(new() { ["Level"] = "SECRET", ["Position"] = "1" });
            var loaded = SettingsLoader.Load(path, policy);

            Assert.Equal(ClassificationLevel.Secret, loaded.Settings.Level);
            Assert.Equal(BannerPosition.Top, loaded.Settings.Position);
            Assert.True(loaded.Settings.ClockEnabled); // from the file, not overridden
            Assert.True(loaded.FromPolicy);
            Assert.Empty(loaded.Warnings);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Bad_policy_values_are_reported_and_ignored()
    {
        var loaded = SettingsLoader.Load(null, new FakePolicy(new() { ["Level"] = "PURPLE", ["FontSize"] = "huge", ["Nope"] = "1" }));

        Assert.Equal(2, loaded.Warnings.Count(w => w.StartsWith("Ignoring invalid policy value")));
        Assert.Contains(loaded.Warnings, w => w.Contains("invalid Level 'PURPLE'"));
        Assert.True(loaded.Settings.LevelNotConfigured);
    }

    private static LoadedSettings LoadJson(string json, IPolicySource? policy = null)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            return SettingsLoader.Load(path, policy);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Tester's bug: "Level": "S" (documented as accepted) plus one bad value reset the whole file to UNCLASSIFIED.</summary>
    [Fact]
    public void One_bad_value_does_not_discard_the_rest_of_the_file()
    {
        var loaded = LoadJson("""{ "Level": "S", "Caption": "SECRET//NOFORN", "FontSize": "huge", "ClockEnabled": true }""");

        Assert.Equal(ClassificationLevel.Secret, loaded.Settings.Level);
        Assert.Equal("SECRET//NOFORN", loaded.Settings.EffectiveCaption);
        Assert.True(loaded.Settings.ClockEnabled);
        Assert.Equal(10, loaded.Settings.FontSize);
        Assert.Contains(loaded.Warnings, w => w.Contains("FontSize"));
        Assert.False(loaded.Settings.LevelNotConfigured);
    }

    [Theory]
    [InlineData("""{ "Level": "S" }""")]
    [InlineData("""{ "level": "secret" }""")]
    [InlineData("""{ "Level": "Secret", }""")]
    public void Settings_file_accepts_the_same_spellings_as_the_command_line(string json)
    {
        Assert.Equal(ClassificationLevel.Secret, LoadJson(json).Settings.Level);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    [InlineData("")]
    public void Unreadable_settings_file_never_shows_a_marking_nobody_chose(string json)
    {
        var loaded = LoadJson(json);

        Assert.True(loaded.Settings.LevelNotConfigured);
        Assert.Equal(BannerSettings.NotConfiguredCaption, loaded.Settings.EffectiveCaption);
        Assert.NotEqual(ClassificationPresets.Get(ClassificationLevel.Unclassified).Background, loaded.Settings.EffectiveBackColor);
    }

    [Fact]
    public void Numeric_level_is_rejected_as_ambiguous()
    {
        Assert.True(LoadJson("""{ "Level": 3 }""").Settings.LevelNotConfigured);
    }

    [Fact]
    public void Invalid_level_in_the_file_shows_not_configured_instead_of_unclassified()
    {
        var loaded = LoadJson("""{ "Level": "PURPLE", "Caption": "SECRET" }""");
        Assert.True(loaded.Settings.LevelNotConfigured);
        Assert.Equal(BannerSettings.NotConfiguredCaption, loaded.Settings.EffectiveCaption);
    }

    [Fact]
    public void No_settings_at_all_is_not_configured()
    {
        Assert.True(SettingsLoader.Load(null, null).Settings.LevelNotConfigured);
    }

    [Fact]
    public void Policy_level_alone_is_enough()
    {
        var loaded = SettingsLoader.Load(null, new FakePolicy(new() { ["Level"] = "TS" }));
        Assert.False(loaded.Settings.LevelNotConfigured);
        Assert.Equal("TOP SECRET", loaded.Settings.EffectiveCaption);
    }

    [Fact]
    public void Invalid_policy_level_shows_not_set_instead_of_the_file_level()
    {
        // Issue #19: a broken GPO level is not hidden behind the local level.
        var loaded = LoadJson("""{ "Level": "Secret" }""", new FakePolicy(new() { ["Level"] = "PURPLE" }));
        Assert.True(loaded.Settings.LevelNotConfigured);
        Assert.Equal(BannerSettings.NotConfiguredCaption, loaded.Settings.EffectiveCaption);
        Assert.Contains(loaded.Warnings, w => w.Contains("Group Policy sets an invalid Level"));
    }

    [Fact]
    public void Load_file_reports_unreadable_so_config_can_refuse_to_overwrite_it()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{ broken");
            var file = SettingsLoader.LoadFile(path);
            Assert.True(file.Exists);
            Assert.True(file.Unreadable);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("FontSize", "999")]
    [InlineData("FontSize", "-5")]
    [InlineData("FontSize", "7")]
    [InlineData("HoverTransparency", "91")]
    [InlineData("ScreenSaverTimeoutSeconds", "59")]
    [InlineData("ScreenSaverTimeoutSeconds", "7201")]
    public void Out_of_range_numbers_are_rejected_not_clamped(string name, string value)
    {
        var s = new BannerSettings();
        Assert.False(SettingKeys.TryApply(s, name, value));
    }

    [Theory]
    [InlineData("FontSize", "8")]
    [InlineData("FontSize", "36")]
    [InlineData("HoverTransparency", "0")]
    [InlineData("ScreenSaverTimeoutSeconds", "7200")]
    public void Range_limits_are_inclusive(string name, string value)
    {
        Assert.True(SettingKeys.TryApply(new BannerSettings(), name, value));
    }

    [Fact]
    public void Out_of_range_value_in_the_file_is_reported_and_the_default_kept()
    {
        var loaded = LoadJson("""{ "Level": "S", "FontSize": 999 }""");
        Assert.Equal(10, loaded.Settings.FontSize);
        Assert.Contains(loaded.Warnings, w => w.Contains("FontSize"));
    }

    /// <summary>Issue #6: saving settings that have no level must never write a level.</summary>
    [Fact]
    public void Saving_without_a_level_writes_no_level()
    {
        var path = Path.GetTempFileName();
        try
        {
            var s = SettingsSerializer.Parse("""{ "Level": "Sekret", "Caption": "SECRET//NOFORN" }""");
            Assert.Null(s.Level);
            SettingKeys.TryApply(s, "FontSize", "12");
            SettingsLoader.Save(s, path);

            Assert.DoesNotContain("\"Level\"", File.ReadAllText(path));
            Assert.True(SettingsLoader.Load(path, null).Settings.LevelNotConfigured);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Issue #8: the settings window previews a clone; it must stay NOT SET.</summary>
    [Fact]
    public void Clone_keeps_not_configured()
    {
        var s = SettingsLoader.Load(null, null).Settings;
        Assert.True(s.Clone().LevelNotConfigured);
        Assert.Equal(BannerSettings.NotConfiguredCaption, s.Clone().EffectiveCaption);
    }

    /// <summary>Issue #7: the running banner reloads when its serialized settings change.</summary>
    [Fact]
    public void Not_set_and_unclassified_serialize_differently()
    {
        var notSet = new BannerSettings();
        var unclassified = new BannerSettings { Level = ClassificationLevel.Unclassified };
        Assert.NotEqual(SettingsSerializer.Serialize(notSet), SettingsSerializer.Serialize(unclassified));
    }

    /// <summary>Issue #12.4: the file stays readable and hand-editable.</summary>
    [Fact]
    public void Settings_file_keeps_ampersands_angle_brackets_and_accents()
    {
        var s = new BannerSettings { Level = ClassificationLevel.Confidential, Caption = "C&ONFIDENTIAL Ünï <b>" };
        var json = SettingsSerializer.Serialize(s);
        Assert.Contains("C&ONFIDENTIAL Ünï <b>", json);
        Assert.Equal("C&ONFIDENTIAL Ünï <b>", SettingsSerializer.Parse(json).Caption);
    }

    [Theory]
    [InlineData("HH:mm \\")]
    [InlineData("")]
    [InlineData("   ")]
    public void Invalid_clock_format_is_rejected(string format)
    {
        Assert.False(SettingKeys.TryApply(new BannerSettings(), "ClockFormat", format));
    }

    [Fact]
    public void Json_round_trips_with_readable_enums_and_comments()
    {
        var s = new BannerSettings { Level = ClassificationLevel.TopSecretSci, WallpaperMode = WallpaperMode.Generated };
        var json = SettingsSerializer.Serialize(s);
        Assert.Contains("\"TopSecretSci\"", json);

        var back = SettingsSerializer.Parse("// machine settings\n" + json);
        Assert.Equal(ClassificationLevel.TopSecretSci, back.Level);
        Assert.Equal(WallpaperMode.Generated, back.WallpaperMode);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("Off", false)]
    public void Registry_style_booleans(string value, bool expected)
    {
        var s = new BannerSettings();
        Assert.True(SettingKeys.TryApply(s, "ClockEnabled", value));
        Assert.Equal(expected, s.ClockEnabled);
    }

    [Fact]
    public void Numeric_enum_values_must_be_defined()
    {
        var s = new BannerSettings();
        Assert.False(SettingKeys.TryApply(s, "Position", "7"));
        Assert.True(SettingKeys.TryApply(s, "Position", "2"));
        Assert.Equal(BannerPosition.Bottom, s.Position);
    }

    [Fact]
    public void Logon_notice_is_off_until_text_is_set()
    {
        var s = new BannerSettings();
        Assert.False(s.HasLogonNotice);
        SettingKeys.TryApply(s, "LogonNoticeText", "Authorized use only.");
        Assert.True(s.HasLogonNotice);
    }
}
