using System.Globalization;
using ClassBanner.Core;
using Microsoft.Win32;
using static ClassBanner.Native;

namespace ClassBanner;

/// <summary>Per-user look-and-feel enforcement applied when the banner starts and when settings change.</summary>
internal static unsafe class Policies
{
    public static void Apply(BannerSettings settings)
    {
        Try("colors", () => { if (settings.EnforceColors) ApplyColors(settings); });
        Try("wallpaper", () => ApplyWallpaper(settings));
        Try("screen saver", () => { if (settings.EnforceScreenSaver) ApplyScreenSaver(settings); });
    }

    private static void Try(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not apply {what} policy", ex);
        }
    }

    /// <summary>Title bars, accent and selection highlight in the classification color.</summary>
    private static void ApplyColors(BannerSettings settings)
    {
        // Keep this user's own values the first time, so uninstall can put them back (issue #19).
        UserSettingsBackup.Remember(new RegistryUserStore(Registry.CurrentUser), UserSettingsBackup.Colors,
            UserSettingsBackup.BannerAccentColors([settings.EffectiveBackColor]));
        var back = settings.EffectiveBackColor;
        var fore = settings.EffectiveForeColor;

        // Classic system colors (selection highlight, menus, classic title bars).
        var elements = stackalloc int[] { COLOR_ACTIVECAPTION, COLOR_GRADIENTACTIVECAPTION, COLOR_CAPTIONTEXT, COLOR_HIGHLIGHT, COLOR_HIGHLIGHTTEXT, COLOR_MENUHILIGHT };
        var colors = stackalloc uint[] { ToColorRef(back), ToColorRef(back), ToColorRef(fore), ToColorRef(back), ToColorRef(fore), ToColorRef(back) };
        SetSysColors(6, elements, colors);

        // Windows 10/11 title bars use the accent color when "show accent color on title bars" is on.
        using (var dwm = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\DWM"))
        {
            dwm.SetValue("ColorPrevalence", 1, RegistryValueKind.DWord);
            dwm.SetValue("AccentColor", unchecked((int)(0xFF000000 | (uint)back.ToColorRef())), RegistryValueKind.DWord);
            dwm.SetValue("ColorizationColor", unchecked((int)(0xC4000000 | ((uint)back.R << 16) | ((uint)back.G << 8) | back.B)), RegistryValueKind.DWord);
        }
        using (var accent = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
        {
            accent.SetValue("AccentColorMenu", unchecked((int)(0xFF000000 | (uint)back.ToColorRef())), RegistryValueKind.DWord);
        }

        SendMessageTimeoutW(HWND_BROADCAST, WM_SETTINGCHANGE, 0, "ImmersiveColorSet", SMTO_ABORTIFHUNG, 1000, out _);
    }

    private static void ApplyWallpaper(BannerSettings settings)
    {
        string path;
        switch (settings.WallpaperMode)
        {
            case WallpaperMode.Off:
                return;
            case WallpaperMode.Image:
                path = Environment.ExpandEnvironmentVariables(settings.WallpaperImagePath ?? "");
                if (!File.Exists(path))
                {
                    Log.Warn($"Wallpaper image '{path}' not found.");
                    return;
                }
                break;
            default:
                path = GenerateWallpaper(settings);
                break;
        }

        UserSettingsBackup.Remember(new RegistryUserStore(Registry.CurrentUser), UserSettingsBackup.Wallpaper,
            UserSettingsBackup.BannerAccentColors([settings.EffectiveBackColor]));
        using (var desktop = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop"))
        {
            desktop.SetValue("WallpaperStyle", "10"); // Fill
            desktop.SetValue("TileWallpaper", "0");
        }
        SystemParametersInfoString(SPI_SETDESKWALLPAPER, 0, path, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
    }

    /// <summary>Renders a solid classification-colored image with the marking centered, sized for the primary monitor.</summary>
    private static string GenerateWallpaper(BannerSettings settings)
    {
        var monitors = Monitors.GetAll();
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        var width = Math.Max(640, primary?.Bounds.Width ?? 1920);
        var height = Math.Max(480, primary?.Bounds.Height ?? 1080);

        var path = Path.Combine(Path.GetDirectoryName(Log.FilePath)!, "wallpaper.bmp");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER),
            biWidth = width,
            biHeight = height, // bottom-up, the classic BMP row order
            biPlanes = 1,
            biBitCount = 32,
        };

        var dc = CreateCompatibleDC(0);
        var bitmap = CreateDIBSection(dc, header, DIB_RGB_COLORS, out var bits, 0, 0);
        if (bitmap == 0)
        {
            DeleteDC(dc);
            throw new InvalidOperationException("CreateDIBSection failed.");
        }

        var font = CreateFontW(-(height / 8), 0, 0, 0, FW_BOLD, 0, 0, 0, 1, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");
        var brush = CreateSolidBrush(ToColorRef(settings.EffectiveBackColor));
        var oldBitmap = SelectObject(dc, bitmap);
        var oldFont = SelectObject(dc, font);
        try
        {
            var rect = new RECT(0, 0, width, height);
            FillRect(dc, rect, brush);
            SetBkMode(dc, TRANSPARENT);
            SetTextColor(dc, ToColorRef(settings.EffectiveForeColor));
            DrawTextW(dc, settings.EffectiveCaption, -1, ref rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
            GdiFlush();

            var pixelBytes = width * height * 4;
            using var file = new BinaryWriter(File.Create(path));
            file.Write((ushort)0x4D42); // "BM"
            file.Write(14 + sizeof(BITMAPINFOHEADER) + pixelBytes);
            file.Write(0);
            file.Write(14 + sizeof(BITMAPINFOHEADER));
            file.Write(new ReadOnlySpan<byte>(&header, sizeof(BITMAPINFOHEADER)));
            file.Write(new ReadOnlySpan<byte>(bits, pixelBytes));
        }
        finally
        {
            SelectObject(dc, oldFont);
            SelectObject(dc, oldBitmap);
            DeleteObject(font);
            DeleteObject(brush);
            DeleteObject(bitmap);
            DeleteDC(dc);
        }
        return path;
    }

    /// <summary>Points the user's screen saver at ClassBanner.scr (installed next to the exe).</summary>
    private static void ApplyScreenSaver(BannerSettings settings)
    {
        var scr = Path.ChangeExtension(Environment.ProcessPath ?? "", ".scr");
        if (!File.Exists(scr))
        {
            Log.Warn($"Screen saver '{scr}' not found; run 'ClassBanner.exe install' to set it up.");
            return;
        }

        UserSettingsBackup.Remember(new RegistryUserStore(Registry.CurrentUser), UserSettingsBackup.ScreenSaver,
            UserSettingsBackup.BannerAccentColors([settings.EffectiveBackColor]));
        var seconds = settings.ScreenSaverTimeoutSeconds;
        using (var desktop = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop"))
        {
            desktop.SetValue("SCRNSAVE.EXE", scr);
            desktop.SetValue("ScreenSaveActive", "1");
            desktop.SetValue("ScreenSaveTimeOut", seconds.ToString(CultureInfo.InvariantCulture));
            desktop.SetValue("ScreenSaverIsSecure", settings.ScreenSaverRequirePassword ? "1" : "0");
        }
        SystemParametersInfoInt(SPI_SETSCREENSAVETIMEOUT, (uint)seconds, 0, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        SystemParametersInfoInt(SPI_SETSCREENSAVEACTIVE, 1, 0, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        SystemParametersInfoInt(SPI_SETSCREENSAVESECURE, settings.ScreenSaverRequirePassword ? 1u : 0u, 0, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
    }
}
