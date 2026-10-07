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

        Assert.Equal(ClassificationLevel.Unclassified, loaded.Settings.Level);
        Assert.Equal(3, loaded.Warnings.Count);
    }

    [Fact]
    public void Corrupt_settings_file_falls_back_to_defaults()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{ not json");
            var loaded = SettingsLoader.Load(path, null);
            Assert.Equal(ClassificationLevel.Unclassified, loaded.Settings.Level);
            Assert.Single(loaded.Warnings);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        var s = SettingsSerializer.Parse("""{ "FontSize": 200, "HoverTransparency": -5, "Position": 9, "ScreenSaverTimeoutSeconds": 1 }""");
        Assert.Equal(36, s.FontSize);
        Assert.Equal(0, s.HoverTransparency);
        Assert.Equal(BannerPosition.Both, s.Position);
        Assert.Equal(60, s.ScreenSaverTimeoutSeconds);
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
