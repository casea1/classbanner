namespace BetterBanner.Core;

/// <summary>Geometry for "Recover windows": find windows that are off-screen or whose title bar is under a banner.</summary>
public static class WindowRecovery
{
    /// <summary>
    /// True when the window's title bar can't be grabbed: it doesn't touch any work area, or it is covered by a banner.
    /// </summary>
    public static bool NeedsRecovery(ScreenRect window, IReadOnlyList<ScreenRect> workAreas, IReadOnlyList<ScreenRect> banners, int titleBarHeight)
    {
        if (window.IsEmpty) return false;

        var titleBar = window with { Bottom = window.Top + Math.Max(1, titleBarHeight) };

        // Need a usable chunk of title bar on some screen: at least 40 px wide (enough to grab).
        var visibleOnScreen = workAreas.Any(w => titleBar.Intersect(w).Width >= Math.Min(40, titleBar.Width));
        if (!visibleOnScreen) return true;

        return banners.Any(b => b.Intersect(titleBar).Area * 2 >= titleBar.Area);
    }

    /// <summary>Where the n-th recovered window goes: the work area's top-left, cascading down-right.</summary>
    public static (int X, int Y) Target(ScreenRect workArea, int index, int cascadeStep, bool cascade)
    {
        var step = cascade ? index * cascadeStep : 0;
        // Wrap so a long cascade never walks off the screen.
        var room = Math.Max(1, Math.Min(workArea.Width, workArea.Height) / 2);
        step %= room;
        return (workArea.Left + step, workArea.Top + step);
    }

    /// <summary>The work area a window should be recovered to: the one it overlaps most, else the primary.</summary>
    public static ScreenRect ChooseWorkArea(ScreenRect window, IReadOnlyList<ScreenRect> workAreas, ScreenRect primaryWorkArea)
    {
        ScreenRect best = primaryWorkArea;
        long bestArea = 0;
        foreach (var w in workAreas)
        {
            var area = window.Intersect(w).Area;
            if (area > bestArea)
            {
                bestArea = area;
                best = w;
            }
        }
        return best;
    }
}
