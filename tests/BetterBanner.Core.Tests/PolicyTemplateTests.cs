using System.Xml.Linq;
using BetterBanner.Core;

namespace BetterBanner.Core.Tests;

/// <summary>Keeps policy/BetterBanner.admx/.adml in sync with the setting names the app understands.</summary>
public class PolicyTemplateTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/GroupPolicy/2006/07/PolicyDefinitions";

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BetterBanner.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    private static XDocument Admx => XDocument.Load(RepoFile("policy", "BetterBanner.admx"));
    private static XDocument Adml => XDocument.Load(RepoFile("policy", "en-US", "BetterBanner.adml"));

    [Fact]
    public void Every_policy_value_name_is_a_known_setting()
    {
        var valueNames = Admx.Descendants().Attributes("valueName").Select(a => a.Value).Distinct().ToList();
        Assert.NotEmpty(valueNames);
        foreach (var name in valueNames)
            Assert.Contains(name, SettingKeys.Names, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_policy_writes_under_the_policy_key()
    {
        foreach (var policy in Admx.Descendants(Ns + "policy"))
            Assert.Equal(@"SOFTWARE\Policies\BetterBanner", policy.Attribute("key")?.Value);
    }

    [Fact]
    public void Every_string_and_presentation_reference_exists_in_the_adml()
    {
        var strings = Adml.Descendants(Ns + "string").Select(s => s.Attribute("id")!.Value).ToHashSet();
        var presentations = Adml.Descendants(Ns + "presentation").Select(p => p.Attribute("id")!.Value).ToHashSet();

        foreach (var attr in Admx.Descendants().Attributes())
        {
            if (attr.Value.StartsWith("$(string.")) Assert.Contains(attr.Value[9..^1], strings);
            if (attr.Value.StartsWith("$(presentation.")) Assert.Contains(attr.Value[15..^1], presentations);
        }
    }

    [Fact]
    public void Every_presentation_control_refers_to_an_element_of_its_policy()
    {
        var admx = Admx;
        foreach (var presentation in Adml.Descendants(Ns + "presentation"))
        {
            var id = presentation.Attribute("id")!.Value;
            var policy = admx.Descendants(Ns + "policy").Single(p => p.Attribute("presentation")?.Value == $"$(presentation.{id})");
            var elementIds = policy.Descendants().Attributes("id").Select(a => a.Value).ToHashSet();
            foreach (var refId in presentation.Descendants().Attributes("refId"))
                Assert.Contains(refId.Value, elementIds);
        }
    }

    [Fact]
    public void Level_enum_values_parse_as_levels()
    {
        var level = Admx.Descendants(Ns + "enum").Single(e => e.Attribute("valueName")?.Value == "Level");
        var values = level.Descendants(Ns + "string").Select(s => s.Value).ToList();
        Assert.Equal(Enum.GetValues<ClassificationLevel>().Length, values.Count);
        foreach (var value in values) Assert.True(ClassificationPresets.TryParseLevel(value, out _), value);
    }
}
