namespace ClassBanner.Core;

/// <summary>Exit codes the banner uses to tell the service why it stopped. Anything else is an unexpected exit.</summary>
public static class BannerExitCodes
{
    /// <summary>The user chose Exit on the banner menu (only offered when AllowUserExit is on).</summary>
    public const int UserExit = 20;

    /// <summary>Another banner already runs in this session.</summary>
    public const int AlreadyRunning = 21;

    /// <summary>A new version was installed; start the new exe.</summary>
    public const int Upgrade = 22;
}

public enum RestartAction
{
    /// <summary>Start the banner again (after <see cref="RestartThrottle.NextDelay"/>).</summary>
    Restart,

    /// <summary>Another banner holds the session; try again later, quietly.</summary>
    RetryLater,

    /// <summary>Leave this session without a banner until the user next signs in.</summary>
    StayStopped,

    /// <summary>Nothing to do: the session ended or the service is stopping (uninstall, upgrade, shutdown).</summary>
    None,
}

/// <param name="Unexpected">True when the exit should be logged as a warning (the banner was ended or crashed).</param>
public sealed record RestartDecision(RestartAction Action, bool Unexpected, string Reason);

/// <summary>What the service does when a banner it started has exited (issue #17).</summary>
public static class RestartPolicy
{
    public static RestartDecision Decide(int exitCode, bool userSignedIn, bool serviceStopping, bool allowUserExit)
    {
        if (serviceStopping) return new(RestartAction.None, false, "the service is stopping");
        if (!userSignedIn) return new(RestartAction.None, false, "the user signed out");

        return exitCode switch
        {
            // Exit is only on the menu when allowed; an exit code claiming it when it isn't allowed is not believed.
            BannerExitCodes.UserExit when allowUserExit => new(RestartAction.StayStopped, false, "the user chose Exit"),
            BannerExitCodes.UserExit => new(RestartAction.Restart, true, "it reported a user exit, but AllowUserExit is off"),
            BannerExitCodes.AlreadyRunning => new(RestartAction.RetryLater, false, "another banner is already running in the session"),
            BannerExitCodes.Upgrade => new(RestartAction.Restart, false, "a new version was installed"),
            _ => new(RestartAction.Restart, true, $"it exited unexpectedly (exit code {Describe(exitCode)})"),
        };
    }

    private static string Describe(int exitCode) => exitCode < 0 || exitCode > 0xFFFF ? $"0x{exitCode:X8}" : exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Restarts quickly, but backs off when a banner keeps dying (so a crash loop doesn't spin the CPU).</summary>
public sealed class RestartThrottle
{
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan BackoffDelay = TimeSpan.FromSeconds(30);
    public const int MaxQuickRestarts = 5;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private readonly Queue<DateTime> _restarts = new();

    /// <summary>Records a restart at <paramref name="now"/> and returns how long to wait before starting the banner.</summary>
    public TimeSpan NextDelay(DateTime now)
    {
        while (_restarts.Count > 0 && now - _restarts.Peek() > Window) _restarts.Dequeue();
        _restarts.Enqueue(now);
        return _restarts.Count > MaxQuickRestarts ? BackoffDelay : Delay;
    }

    public void Reset() => _restarts.Clear();
}
