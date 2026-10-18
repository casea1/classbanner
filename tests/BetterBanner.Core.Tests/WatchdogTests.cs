using BetterBanner.Core;

namespace BetterBanner.Core.Tests;

/// <summary>Issue #17: which banner exits the service restarts, and which it doesn't.</summary>
public class WatchdogTests
{
    [Theory]
    [InlineData(1)]                    // killed by Task Manager / taskkill (TerminateProcess exit code 1)
    [InlineData(0)]                    // a clean exit while the user is still signed in is still unexpected
    [InlineData(-1073741819)]          // 0xC0000005 access violation (crash)
    [InlineData(-1073741510)]          // 0xC000013A Ctrl+C / console close
    public void Unexpected_exits_restart_and_are_reported(int exitCode)
    {
        var decision = RestartPolicy.Decide(exitCode, userSignedIn: true, serviceStopping: false, allowUserExit: true);
        Assert.Equal(RestartAction.Restart, decision.Action);
        Assert.True(decision.Unexpected);
    }

    [Fact]
    public void Crash_codes_are_shown_in_hex()
    {
        var decision = RestartPolicy.Decide(-1073741819, true, false, false);
        Assert.Contains("0xC0000005", decision.Reason);
    }

    [Fact]
    public void User_exit_is_respected_only_when_allowed()
    {
        Assert.Equal(RestartAction.StayStopped, RestartPolicy.Decide(BannerExitCodes.UserExit, true, false, allowUserExit: true).Action);

        var notAllowed = RestartPolicy.Decide(BannerExitCodes.UserExit, true, false, allowUserExit: false);
        Assert.Equal(RestartAction.Restart, notAllowed.Action);
        Assert.True(notAllowed.Unexpected);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(BannerExitCodes.UserExit)]
    [InlineData(BannerExitCodes.Upgrade)]
    public void Nothing_restarts_while_the_service_stops_for_uninstall_or_upgrade(int exitCode)
    {
        Assert.Equal(RestartAction.None, RestartPolicy.Decide(exitCode, true, serviceStopping: true, allowUserExit: false).Action);
    }

    [Fact]
    public void Nothing_restarts_after_sign_out()
    {
        Assert.Equal(RestartAction.None, RestartPolicy.Decide(1, userSignedIn: false, serviceStopping: false, allowUserExit: false).Action);
    }

    [Fact]
    public void Upgrade_restarts_quietly()
    {
        var decision = RestartPolicy.Decide(BannerExitCodes.Upgrade, true, false, false);
        Assert.Equal(RestartAction.Restart, decision.Action);
        Assert.False(decision.Unexpected);
    }

    [Fact]
    public void Another_banner_in_the_session_is_retried_quietly()
    {
        var decision = RestartPolicy.Decide(BannerExitCodes.AlreadyRunning, true, false, false);
        Assert.Equal(RestartAction.RetryLater, decision.Action);
        Assert.False(decision.Unexpected);
    }

    [Fact]
    public void Restarts_are_quick_until_a_crash_loop_then_back_off()
    {
        var throttle = new RestartThrottle();
        var t = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < RestartThrottle.MaxQuickRestarts; i++)
            Assert.Equal(RestartThrottle.Delay, throttle.NextDelay(t.AddSeconds(i)));
        Assert.Equal(RestartThrottle.BackoffDelay, throttle.NextDelay(t.AddSeconds(10)));

        // Once things calm down, restarts are quick again.
        Assert.Equal(RestartThrottle.Delay, throttle.NextDelay(t.AddMinutes(5)));
    }

    [Fact]
    public void Service_and_managed_banner_commands_parse()
    {
        Assert.Equal(CommandKind.Service, CommandLine.Parse(["service"]).Kind);
        var managed = CommandLine.Parse(["banner", "--managed"]);
        Assert.Equal(CommandKind.Run, managed.Kind);
        Assert.True(managed.Managed);
        Assert.NotNull(CommandLine.Parse(["install", "--managed"]).Error);
    }
}
