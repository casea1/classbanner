namespace ClassBanner.Core;

/// <summary>Keeps a window inside a monitor's work area, which is what the banners leave free.</summary>
public static class WindowFit
{
    /// <summary>The smallest a window is shrunk to fit a short work area.</summary>
    public const double MinScale = 0.5;

    /// <summary>
    /// How much to shrink a window laid out at <paramref name="height"/> pixels so it fits a work area
    /// <paramref name="workHeight"/> tall: 1 when it fits, never below <see cref="MinScale"/>.
    /// </summary>
    public static double ScaleToFit(int height, int workHeight) =>
        height <= workHeight || height <= 0 ? 1 : Math.Max(MinScale, workHeight / (double)height);

    /// <summary>
    /// Moves <paramref name="window"/> (size unchanged) so it lies inside <paramref name="work"/>; a window larger than
    /// the work area is aligned to its top-left so the title bar stays reachable.
    /// </summary>
    public static ScreenRect MoveInside(ScreenRect window, ScreenRect work)
    {
        var left = Math.Max(work.Left, Math.Min(window.Left, work.Right - window.Width));
        var top = Math.Max(work.Top, Math.Min(window.Top, work.Bottom - window.Height));
        return new ScreenRect(left, top, left + window.Width, top + window.Height);
    }
}
