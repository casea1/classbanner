namespace BetterBanner.Core;

public enum ClassificationLevel
{
    Unclassified,
    Cui,
    Confidential,
    Secret,
    TopSecret,
    TopSecretSci,
}

/// <summary>Default caption and colors for a classification level.</summary>
public sealed record ClassificationPreset(ClassificationLevel Level, string Caption, RgbColor Background, RgbColor Foreground);

public static class ClassificationPresets
{
    // Standard US classification banner colors (DoDM 5200.01 / CUI marking guidance).
    private static readonly IReadOnlyDictionary<ClassificationLevel, ClassificationPreset> Presets =
        new Dictionary<ClassificationLevel, ClassificationPreset>
        {
            [ClassificationLevel.Unclassified] = new(ClassificationLevel.Unclassified, "UNCLASSIFIED", RgbColor.Parse("#007A33"), RgbColor.White),
            [ClassificationLevel.Cui] = new(ClassificationLevel.Cui, "CUI", RgbColor.Parse("#502B85"), RgbColor.White),
            [ClassificationLevel.Confidential] = new(ClassificationLevel.Confidential, "CONFIDENTIAL", RgbColor.Parse("#0033A0"), RgbColor.White),
            [ClassificationLevel.Secret] = new(ClassificationLevel.Secret, "SECRET", RgbColor.Parse("#C8102E"), RgbColor.White),
            [ClassificationLevel.TopSecret] = new(ClassificationLevel.TopSecret, "TOP SECRET", RgbColor.Parse("#FF8C00"), RgbColor.Black),
            [ClassificationLevel.TopSecretSci] = new(ClassificationLevel.TopSecretSci, "TOP SECRET//SCI", RgbColor.Parse("#FCE83A"), RgbColor.Black),
        };

    public static ClassificationPreset Get(ClassificationLevel level) => Presets[level];

    /// <summary>
    /// The level a caption claims: the level whose standard marking the caption (trimmed, upper-cased) equals or starts
    /// with, followed by "//" or a space. The longest marking wins, so "TOP SECRET//SCI//NOFORN" is TS/SCI, not TS.
    /// Null when it starts with no standard marking (e.g. "SEKRET").
    /// </summary>
    public static ClassificationLevel? LevelOfCaption(string caption)
    {
        var text = caption.Trim().ToUpperInvariant();
        ClassificationPreset? best = null;
        foreach (var preset in Presets.Values)
        {
            if (StartsWithMarking(text, preset.Caption) && (best is null || preset.Caption.Length > best.Caption.Length))
                best = preset;
        }
        return best?.Level;
    }

    private static bool StartsWithMarking(string text, string marking) =>
        text == marking || text.StartsWith(marking + "//", StringComparison.Ordinal) || text.StartsWith(marking + " ", StringComparison.Ordinal);

    /// <summary>
    /// Why <paramref name="caption"/> can't be shown for <paramref name="level"/>, or null when it can. A custom caption
    /// must keep the level's marking first (e.g. SECRET//NOFORN for SECRET) and must not read as another level.
    /// </summary>
    public static string? CaptionProblem(ClassificationLevel level, string? caption)
    {
        if (string.IsNullOrWhiteSpace(caption)) return null;
        var marking = Get(level).Caption;
        var claimed = LevelOfCaption(caption);
        if (claimed == level) return null;
        var reason = claimed is { } other
            ? $"it reads as {Get(other).Caption}"
            : "it doesn't start with the classification marking";
        return $"Banner text '{caption.Trim()}' doesn't match the classification {marking}: {reason}. " +
               $"It must be {marking} or start with \"{marking}//\" or \"{marking} \" (e.g. {marking}//NOFORN).";
    }

    public static IEnumerable<ClassificationPreset> All => Presets.Values;

    /// <summary>
    /// Accepts enum names plus the spellings people actually type: "TS", "TS/SCI", "TOP SECRET SCI", "U", "S", "C".
    /// </summary>
    public static bool TryParseLevel(string? text, out ClassificationLevel level)
    {
        level = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var normalized = new string(text.Where(char.IsLetter).ToArray()).ToUpperInvariant();
        switch (normalized)
        {
            case "U":
            case "UNCLASSIFIED":
                level = ClassificationLevel.Unclassified; return true;
            case "CUI":
                level = ClassificationLevel.Cui; return true;
            case "C":
            case "CONFIDENTIAL":
                level = ClassificationLevel.Confidential; return true;
            case "S":
            case "SECRET":
                level = ClassificationLevel.Secret; return true;
            case "TS":
            case "TOPSECRET":
                level = ClassificationLevel.TopSecret; return true;
            case "TSSCI":
            case "TOPSECRETSCI":
                level = ClassificationLevel.TopSecretSci; return true;
            default:
                return false;
        }
    }
}
