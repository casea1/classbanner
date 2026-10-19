using System.Globalization;
using ClassBanner.Core;
using Microsoft.Win32;

namespace ClassBanner;

/// <summary>Reads Group Policy values from HKLM\SOFTWARE\Policies\ClassBanner (written by the ADMX template).</summary>
internal sealed class RegistryPolicySource : IPolicySource
{
    public const string KeyPath = @"SOFTWARE\Policies\ClassBanner";

    /// <summary>Better Banner's policy key: still honored when the ClassBanner one doesn't exist, so existing GPOs keep
    /// working until the new templates are deployed (the value names are the same).</summary>
    public const string OldKeyPath = @"SOFTWARE\Policies\BetterBanner";

    public IReadOnlyDictionary<string, string> ReadValues()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath) ?? Registry.LocalMachine.OpenSubKey(OldKeyPath);
        if (key is null) return values;

        foreach (var name in key.GetValueNames())
        {
            var value = key.GetValue(name, null, RegistryValueOptions.None);
            var text = value switch
            {
                string s => s,
                int i => i.ToString(CultureInfo.InvariantCulture),
                long l => l.ToString(CultureInfo.InvariantCulture),
                _ => null,
            };
            if (text is not null && name.Length > 0) values[name] = text;
        }
        return values;
    }

    public static LoadedSettings LoadEffective() =>
        SettingsLoader.Load(SettingsLoader.DefaultSettingsPath, new RegistryPolicySource());
}
