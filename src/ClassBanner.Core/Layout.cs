namespace ClassBanner.Core;

/// <summary>A screen rectangle in physical pixels (right/bottom exclusive), matching Win32 RECT.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public ScreenRect Intersect(ScreenRect other)
    {
        var r = new ScreenRect(Math.Max(Left, other.Left), Math.Max(Top, other.Top), Math.Min(Right, other.Right), Math.Min(Bottom, other.Bottom));
        return r.IsEmpty ? default : r;
    }

    public bool IntersectsWith(ScreenRect other) => !Intersect(other).IsEmpty;

    public long Area => IsEmpty ? 0 : (long)Width * Height;

    public override string ToString() => $"({Left},{Top})-({Right},{Bottom})";
}

/// <summary>A display. <see cref="DeviceName"/> (e.g. \\.\DISPLAY2) identifies it across reconfigurations.</summary>
public sealed record MonitorInfo(string DeviceName, ScreenRect Bounds, ScreenRect WorkArea, int Dpi, bool IsPrimary);

public enum BannerEdge
{
    Top,
    Bottom,
}

/// <summary>Where one banner window belongs: a specific edge of a specific monitor.</summary>
public sealed record BannerSlot(string DeviceName, BannerEdge Edge, ScreenRect MonitorBounds, int Dpi, int Height)
{
    /// <summary>The full-width strip at the slot's edge (before the shell's app-bar negotiation).</summary>
    public ScreenRect Strip => Edge == BannerEdge.Top
        ? MonitorBounds with { Bottom = MonitorBounds.Top + Height }
        : MonitorBounds with { Top = MonitorBounds.Bottom - Height };
}

/// <summary>A banner window that currently exists, and where Windows actually has it.</summary>
public sealed record ExistingBanner(int Id, BannerSlot Slot, ScreenRect ActualRect);

/// <summary>What to do to bring the banner windows in line with the monitors.</summary>
public sealed record ReconcilePlan(bool RebuildAll, IReadOnlyList<int> Reposition, string Reason)
{
    public static readonly ReconcilePlan Nothing = new(false, [], "up to date");
    public bool IsNoOp => !RebuildAll && Reposition.Count == 0;
}

public static class BannerLayout
{
    public const int BaseDpi = 96;

    /// <summary>Text height in pixels for a point size at a DPI (96 DPI = 100% scaling).</summary>
    public static int FontPixels(int points, int dpi) => (int)Math.Round(points * dpi / 72.0);

    public const int MinThickness = 12;
    public const int MaxThickness = 120;

    /// <summary>
    /// Banner thickness, scaled for the monitor's DPI so it looks the same at any zoom. <paramref name="thickness"/> is
    /// pixels at 100% scaling; 0 means automatic: the text plus padding.
    /// </summary>
    public static int BannerHeight(int fontPoints, int dpi, int thickness = 0)
    {
        dpi = dpi <= 0 ? BaseDpi : dpi;
        if (thickness > 0) return (int)Math.Round(Math.Clamp(thickness, MinThickness, MaxThickness) * dpi / (double)BaseDpi);
        var text = FontPixels(fontPoints, dpi);
        var padding = (int)Math.Round(4 * dpi / (double)BaseDpi);
        return text + 2 * padding + (text / 4);
    }

    /// <summary>Text height for a banner of <paramref name="bannerHeight"/> pixels: the chosen size, shrunk to fit a thin banner.</summary>
    public static int TextPixels(int fontPoints, int dpi, int bannerHeight) =>
        Math.Max(6, Math.Min(FontPixels(fontPoints, dpi <= 0 ? BaseDpi : dpi), (int)(bannerHeight * 0.75)));

    /// <summary>One slot per requested edge per monitor, in a stable order.</summary>
    public static IReadOnlyList<BannerSlot> ComputeSlots(IReadOnlyList<MonitorInfo> monitors, BannerPosition position, int fontPoints, int thickness = 0)
    {
        var slots = new List<BannerSlot>();
        foreach (var m in monitors.OrderBy(m => m.DeviceName, StringComparer.OrdinalIgnoreCase))
        {
            var height = BannerHeight(fontPoints, m.Dpi, thickness);
            if (position is BannerPosition.Top or BannerPosition.Both)
                slots.Add(new BannerSlot(m.DeviceName, BannerEdge.Top, m.Bounds, m.Dpi, height));
            if (position is BannerPosition.Bottom or BannerPosition.Both)
                slots.Add(new BannerSlot(m.DeviceName, BannerEdge.Bottom, m.Bounds, m.Dpi, height));
        }
        return slots;
    }

    /// <summary>
    /// Compares the banners that exist with the banners that should exist.
    /// <list type="bullet">
    /// <item>Different monitors, resolution, DPI, or a duplicated/missing slot: rebuild everything from scratch.</item>
    /// <item>Same layout, but Windows moved a banner off its monitor (e.g. the display dropped out while the PC was
    /// locked and every window was moved to the primary screen): put just that banner back.</item>
    /// </list>
    /// This is the fix for Banner 3.1's "four banners on the main screen, none on the second after unlock".
    /// </summary>
    public static ReconcilePlan Reconcile(IReadOnlyList<BannerSlot> desired, IReadOnlyList<ExistingBanner> existing)
    {
        if (existing.Count != desired.Count)
            return new ReconcilePlan(true, [], $"expected {desired.Count} banners, found {existing.Count}");

        var desiredSet = new HashSet<BannerSlot>(desired);
        var seen = new HashSet<BannerSlot>();
        foreach (var banner in existing)
        {
            if (!desiredSet.Contains(banner.Slot))
                return new ReconcilePlan(true, [], $"display layout changed ({banner.Slot.DeviceName} {banner.Slot.Edge})");
            if (!seen.Add(banner.Slot))
                return new ReconcilePlan(true, [], $"duplicate banner for {banner.Slot.DeviceName} {banner.Slot.Edge}");
        }

        var misplaced = existing.Where(b => !IsInPlace(b)).Select(b => b.Id).ToArray();
        return misplaced.Length == 0
            ? ReconcilePlan.Nothing
            : new ReconcilePlan(false, misplaced, $"{misplaced.Length} banner(s) moved off their monitor");
    }

    /// <summary>
    /// A banner is in place when it is inside its own monitor, spans most of its width, and hugs its edge. The shell's
    /// app-bar negotiation may push it inward (a bottom banner sits above a bottom taskbar; a taskbar docked left or
    /// right narrows it), so allow that.
    /// </summary>
    public static bool IsInPlace(ExistingBanner banner)
    {
        var actual = banner.ActualRect;
        var monitor = banner.Slot.MonitorBounds;
        if (actual.IsEmpty) return false;
        if (actual.Left < monitor.Left || actual.Right > monitor.Right) return false;
        if (actual.Top < monitor.Top || actual.Bottom > monitor.Bottom) return false;
        if (actual.Width * 2 < monitor.Width) return false;
        if (actual.Height != banner.Slot.Height) return false;

        var slack = monitor.Height / 3;
        return banner.Slot.Edge == BannerEdge.Top
            ? actual.Top - monitor.Top <= slack
            : monitor.Bottom - actual.Bottom <= slack;
    }
}

/// <summary>Where to draw the marking in a banner, whether the clock fits, and how much to shrink the text.</summary>
public readonly record struct CaptionPlacement(int Left, int Right, bool ShowClock, double Scale);

public static class CaptionLayout
{
    /// <summary>
    /// The marking is never cut off: the clock gives way first. Centered when the clock fits beside it; otherwise moved
    /// left into the free space; otherwise the clock is hidden; if the marking alone is still too wide, it is shrunk.
    /// Widths are in pixels; <paramref name="clockWidth"/> is 0 when there is no clock.
    /// </summary>
    public static CaptionPlacement Place(int bannerWidth, int captionWidth, int clockWidth, int pad)
    {
        if (clockWidth > 0)
        {
            var clockLeft = bannerWidth - pad - clockWidth;
            var centeredRight = (bannerWidth + captionWidth) / 2;
            if (centeredRight + pad <= clockLeft)
                return new CaptionPlacement(0, bannerWidth, ShowClock: true, Scale: 1);
            if (pad + captionWidth + pad <= clockLeft)
                return new CaptionPlacement(pad, clockLeft - pad, ShowClock: true, Scale: 1);
        }

        var available = Math.Max(1, bannerWidth - 2 * pad);
        var scale = captionWidth <= available ? 1 : available / (double)captionWidth;
        return new CaptionPlacement(0, bannerWidth, ShowClock: false, Scale: scale);
    }
}
