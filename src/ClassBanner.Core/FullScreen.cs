namespace ClassBanner.Core;

/// <summary>Whether the window in front is a full-screen app the banner should step behind.</summary>
public static class FullScreen
{
    // The desktop and the taskbar cover a monitor without being an app.
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    /// <summary>True when <paramref name="window"/> covers the whole of <paramref name="monitor"/> and isn't the shell.</summary>
    public static bool Covers(ScreenRect window, ScreenRect monitor, string className) =>
        !ShellClasses.Contains(className) &&
        window.Left <= monitor.Left && window.Top <= monitor.Top &&
        window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;
}
