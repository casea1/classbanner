namespace ClassBanner.Core;

/// <summary>A top-level window, as seen when walking the screen's windows from front to back.</summary>
public readonly record struct TopWindow(nint Handle, ScreenRect Rect, string ClassName, uint ProcessId);

/// <summary>Whether a full-screen app is in front on a monitor, so the banner should step behind it.</summary>
public static class FullScreen
{
    // The desktop and the taskbar cover or sit on a monitor without being an app.
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    /// <summary>True when <paramref name="window"/> covers the whole of <paramref name="monitor"/> and isn't the shell.</summary>
    public static bool Covers(ScreenRect window, ScreenRect monitor, string className) =>
        !ShellClasses.Contains(className) &&
        window.Left <= monitor.Left && window.Top <= monitor.Top &&
        window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;

    /// <summary>
    /// The full-screen window that is frontmost on <paramref name="monitor"/>, or null. Walks
    /// <paramref name="frontToBack"/> (already without hidden windows and the banner's own) down to the first window
    /// that covers the monitor. The banner goes behind it unless a window of another app is in front of it there (a
    /// local window dragged over it, the Start menu), which brings the banner back. The app's own smaller windows in
    /// front of it, such as the Remote Desktop connection bar, don't count. What is active on another monitor doesn't
    /// matter, so clicking a window there leaves a full-screen session here covering its banner.
    /// </summary>
    public static nint? FrontmostOn(ScreenRect monitor, IEnumerable<TopWindow> frontToBack)
    {
        var inFront = new HashSet<uint>();
        foreach (var window in frontToBack)
        {
            if (ShellClasses.Contains(window.ClassName) || !window.Rect.IntersectsWith(monitor)) continue;
            if (Covers(window.Rect, monitor, window.ClassName))
                return inFront.All(pid => pid == window.ProcessId) ? window.Handle : null;
            inFront.Add(window.ProcessId);
        }
        return null;
    }
}
