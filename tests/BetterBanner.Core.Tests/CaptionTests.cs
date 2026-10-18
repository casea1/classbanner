using BetterBanner.Core;

namespace BetterBanner.Core.Tests;

/// <summary>Issue #18: the banner text can never contradict the level.</summary>
public class CaptionTests
{
    private sealed class FakePolicy(Dictionary<string, string> values) : IPolicySource
    {
        public IReadOnlyDictionary<string, string> ReadValues() => values;
    }

    [Theory]
    [InlineData(ClassificationLevel.Unclassified, "UNCLASSIFIED")]
    [InlineData(ClassificationLevel.Unclassified, "UNCLASSIFIED//FOUO")]
    [InlineData(ClassificationLevel.Unclassified, "  unclassified  ")]
    [InlineData(ClassificationLevel.Cui, "CUI")]
    [InlineData(ClassificationLevel.Cui, "CUI//SP-PRVCY")]
    [InlineData(ClassificationLevel.Confidential, "CONFIDENTIAL//REL TO USA, FVEY")]
    [InlineData(ClassificationLevel.Secret, "SECRET")]
    [InlineData(ClassificationLevel.Secret, "SECRET//NOFORN")]
    [InlineData(ClassificationLevel.Secret, "secret//noforn")]
    [InlineData(ClassificationLevel.Secret, "SECRET NOFORN")]
    [InlineData(ClassificationLevel.TopSecret, "TOP SECRET")]
    [InlineData(ClassificationLevel.TopSecret, "TOP SECRET//NOFORN")]
    [InlineData(ClassificationLevel.TopSecret, "TOP SECRET//SCIENCE PROGRAM")]
    [InlineData(ClassificationLevel.TopSecretSci, "TOP SECRET//SCI")]
    [InlineData(ClassificationLevel.TopSecretSci, "TOP SECRET//SCI//NOFORN")]
    [InlineData(ClassificationLevel.TopSecretSci, "TOP SECRET//SCI NOFORN")]
    [InlineData(ClassificationLevel.Secret, "")]
    [InlineData(ClassificationLevel.Secret, null)]
    public void Valid_captions_are_accepted(ClassificationLevel level, string? caption)
    {
        Assert.Null(ClassificationPresets.CaptionProblem(level, caption));
    }

    [Theory]
    [InlineData(ClassificationLevel.Secret, "UNCLASSIFIED")]
    [InlineData(ClassificationLevel.Confidential, "SECRET//NOFORN")]
    [InlineData(ClassificationLevel.Unclassified, "SECRET")]
    [InlineData(ClassificationLevel.Unclassified, "CUI")]
    [InlineData(ClassificationLevel.Cui, "UNCLASSIFIED")]
    [InlineData(ClassificationLevel.Secret, "SECRETS")]
    [InlineData(ClassificationLevel.Secret, "SECRET/NOFORN")]
    [InlineData(ClassificationLevel.Secret, "TOP SECRET")]
    [InlineData(ClassificationLevel.Secret, "NOFORN")]
    [InlineData(ClassificationLevel.TopSecret, "TOP SECRET//SCI")]
    [InlineData(ClassificationLevel.TopSecret, "TOP SECRET//SCI//NOFORN")]
    [InlineData(ClassificationLevel.TopSecret, "SECRET")]
    [InlineData(ClassificationLevel.TopSecretSci, "TOP SECRET")]
    [InlineData(ClassificationLevel.TopSecretSci, "TOP SECRET//NOFORN")]
    [InlineData(ClassificationLevel.TopSecretSci, "TOP SECRET//SCIENCE")]
    public void Contradicting_captions_are_refused_naming_the_expected_prefix(ClassificationLevel level, string caption)
    {
        var problem = ClassificationPresets.CaptionProblem(level, caption);
        Assert.NotNull(problem);
        Assert.Contains($"\"{ClassificationPresets.Get(level).Caption}//\"", problem);
    }

    [Fact]
    public void Bad_caption_in_the_settings_file_falls_back_to_the_standard_marking()
    {
        var loaded = Load("""{ "Level": "Secret", "Caption": "UNCLASSIFIED" }""");
        Assert.Equal("SECRET", loaded.Settings.EffectiveCaption);
        Assert.Contains(loaded.Warnings, w => w.Contains("Caption from the settings file") && w.Contains("SECRET"));
    }

    [Fact]
    public void Bad_caption_from_policy_falls_back_to_the_standard_marking()
    {
        var loaded = Load(null, new() { ["Level"] = "S", ["Caption"] = "UNCLASSIFIED" });
        Assert.Equal("SECRET", loaded.Settings.EffectiveCaption);
        Assert.Contains(loaded.Warnings, w => w.Contains("Caption from Group Policy"));
    }

    [Fact]
    public void File_caption_is_checked_against_the_policy_level()
    {
        var loaded = Load("""{ "Level": "Secret", "Caption": "SECRET//NOFORN" }""", new() { ["Level"] = "C" });
        Assert.Equal("CONFIDENTIAL", loaded.Settings.EffectiveCaption);
        Assert.Contains(loaded.Warnings, w => w.Contains("Caption from the settings file"));
    }

    [Fact]
    public void Matching_caption_is_kept_without_warnings()
    {
        var loaded = Load(null, new() { ["Level"] = "TS/SCI", ["Caption"] = "TOP SECRET//SCI//NOFORN" });
        Assert.Equal("TOP SECRET//SCI//NOFORN", loaded.Settings.EffectiveCaption);
        Assert.Empty(loaded.Warnings);
    }

    [Fact]
    public void Command_line_values_are_checked_the_same_way()
    {
        // install/config apply --caption through SettingKeys, then refuse with CaptionProblem (exit 2).
        var settings = new BannerSettings();
        SettingKeys.ApplyAll(settings, [new("Level", "S"), new("Caption", "UNCLASSIFIED")]);
        Assert.NotNull(ClassificationPresets.CaptionProblem(settings.Level!.Value, settings.Caption));
    }

    [Fact]
    public void Non_standard_colors_are_warned_about_but_kept()
    {
        var loaded = Load(null, new() { ["Level"] = "S", ["BackColor"] = "#007A33", ["ForeColor"] = "#000000" });
        Assert.Equal(RgbColor.Parse("#007A33"), loaded.Settings.EffectiveBackColor);
        Assert.Contains(loaded.Warnings, w => w.StartsWith("BackColor"));
        Assert.Contains(loaded.Warnings, w => w.StartsWith("ForeColor"));
    }

    [Fact]
    public void Standard_colors_written_out_are_not_warned_about()
    {
        var loaded = Load(null, new() { ["Level"] = "S", ["BackColor"] = "#C8102E", ["ForeColor"] = "#FFFFFF" });
        Assert.Empty(loaded.Warnings);
    }

    private static LoadedSettings Load(string? json, Dictionary<string, string>? policy = null)
    {
        var path = json is null ? null : Path.GetTempFileName();
        try
        {
            if (path is not null) File.WriteAllText(path, json);
            return SettingsLoader.Load(path, policy is null ? null : new FakePolicy(policy));
        }
        finally
        {
            if (path is not null) File.Delete(path);
        }
    }
}
