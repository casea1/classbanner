using System.Runtime.InteropServices;
using BetterBanner.Core;
using static BetterBanner.Native;

namespace BetterBanner;

/// <summary>
/// Hidden top-level window that owns every banner. It listens for anything that can move or invalidate banners
/// (display changes, DPI changes, unlock, resume, Explorer restart) and reconciles the banners against the monitors
/// that actually exist. A periodic check catches anything Windows doesn't announce.
/// </summary>
internal sealed unsafe class BannerHost : Win32Window
{
    public static readonly uint AppBarCallbackMessage = WM_APP + 1;

    private const string ClassName = "BetterBanner.Host";
    private const nuint TimerReconcile = 1;
    private const nuint TimerDebounce = 2;
    private const nuint TimerClock = 3;
    private const nuint TimerSettings = 4;
    private const nuint TimerRebuild = 5;

    private const uint ReconcileIntervalMs = 2000;
    private const uint DebounceMs = 500;
    private const uint SettingsIntervalMs = 30_000;
    private const uint AfterUnlockRebuildMs = 1500;

    private const uint MenuRecover = 1;
    private const uint MenuAbout = 2;
    private const uint MenuExit = 3;
    private const uint MenuSettings = 4;

    private static List<nint>? _enumerated;

    private readonly uint _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
    private readonly List<BannerWindow> _banners = [];
    private LoadedSettings _loaded;
    private string _settingsSnapshot;
    private int _nextId;
    private int _repositionStreak;
    private string _lastReconcileReason = "";
    private string _warningsSnapshot = "";
    private readonly string? _installId = ReadInstallId();

    /// <summary>Set when a newer version was installed; Program restarts the banner after the message loop ends.</summary>
    public static bool RestartRequested { get; private set; }

    /// <summary>Set when the user picked Exit on the menu (only offered when AllowUserExit is on).</summary>
    public static bool UserChoseExit { get; private set; }

    public BannerHost()
    {
        _loaded = RegistryPolicySource.LoadEffective();
        _settingsSnapshot = SettingsSerializer.Serialize(_loaded.Settings);
        _warningsSnapshot = string.Join('\n', _loaded.Warnings);
        foreach (var warning in _loaded.Warnings) Log.Warn(warning);

        // A hidden top-level (not message-only) window: only top-level windows receive display and setting broadcasts.
        CreateWindow(ClassName, 0, WS_EX_TOOLWINDOW, WS_POPUP, 0, 0, 0, 0);
        WTSRegisterSessionNotification(Handle, NOTIFY_FOR_THIS_SESSION);

        SetTimer(Handle, TimerReconcile, ReconcileIntervalMs, 0);
        SetTimer(Handle, TimerSettings, SettingsIntervalMs, 0);
        ApplySettings(firstRun: true);
    }

    public BannerSettings Settings => _loaded.Settings;

    public string ClockText { get; private set; } = "";

    public void RequestReconcile() => SetTimer(Handle, TimerDebounce, DebounceMs, 0);

    protected override nint WndProc(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_TIMER:
                OnTimer((nuint)wParam);
                return 0;

            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
                RequestReconcile();
                return 0;

            case WM_SETTINGCHANGE:
                if ((int)wParam == SPI_SETWORKAREA) RequestReconcile();
                if (lParam != 0)
                {
                    var area = Marshal.PtrToStringUni(lParam);
                    // "Policy" is broadcast after a Group Policy refresh; "BetterBanner" by our own config command.
                    if (area is "Policy" or "BetterBanner") ReloadSettings();
                }
                return 0;

            case WM_WTSSESSION_CHANGE:
                if ((int)wParam is WTS_SESSION_UNLOCK or WTS_CONSOLE_CONNECT or WTS_REMOTE_CONNECT)
                {
                    // Displays can drop out while locked and Windows moves windows to the primary screen. Rebuild once
                    // they have settled; the periodic reconcile keeps checking after that.
                    Log.Info($"Session change {(int)wParam}; rebuilding banners.");
                    SetTimer(Handle, TimerRebuild, AfterUnlockRebuildMs, 0);
                }
                return 0;

            case WM_POWERBROADCAST:
                if ((int)wParam is PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND)
                    SetTimer(Handle, TimerRebuild, AfterUnlockRebuildMs, 0);
                return 1;

            case WM_QUERYENDSESSION:
                return 1;

            case WM_DESTROY:
                WTSUnRegisterSessionNotification(Handle);
                DestroyBanners();
                PostQuitMessage(0);
                return 0;
        }

        if (msg == _taskbarCreatedMessage && msg != 0)
        {
            // Explorer restarted and forgot every app bar.
            Log.Info("Explorer restarted; re-registering banners.");
            Rebuild("explorer restarted");
            return 0;
        }

        return base.WndProc(msg, wParam, lParam);
    }

    private void OnTimer(nuint id)
    {
        switch (id)
        {
            case TimerReconcile:
                if (CheckForUpgrade()) break;
                Reconcile();
                foreach (var banner in _banners) banner.KeepOnTop();
                break;
            case TimerDebounce:
                KillTimer(Handle, TimerDebounce);
                Reconcile();
                break;
            case TimerRebuild:
                KillTimer(Handle, TimerRebuild);
                Rebuild("session unlocked or resumed");
                break;
            case TimerClock:
                UpdateClock();
                break;
            case TimerSettings:
                ReloadSettings();
                break;
        }
    }

    /// <summary>After "install" replaces the exe, restart into the new version in this user's own session.</summary>
    private bool CheckForUpgrade()
    {
        var current = ReadInstallId();
        if (current is null || current == _installId) return false;
        Log.Info("A new version was installed; restarting.");
        RestartRequested = true;
        Destroy();
        return true;
    }

    private static string? ReadInstallId()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(Installer.StateKey);
            return key?.GetValue(Installer.InstallIdValue) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private void Reconcile()
    {
        var desired = BannerLayout.ComputeSlots(Monitors.GetAll(), Settings.Position, Settings.FontSize);
        var existing = _banners.Select(b => new ExistingBanner(b.Id, b.Slot, b.ActualRect)).ToList();
        var plan = BannerLayout.Reconcile(desired, existing);

        if (plan.IsNoOp)
        {
            _repositionStreak = 0;
            _lastReconcileReason = "";
            return;
        }

        if (plan.Reason != _lastReconcileReason) Log.Info($"Reconcile: {plan.Reason}.");
        _lastReconcileReason = plan.Reason;

        // If putting banners back doesn't stick, start over rather than fighting the shell forever.
        if (plan.RebuildAll || ++_repositionStreak > 3)
        {
            _repositionStreak = 0;
            Rebuild(desired);
            return;
        }

        foreach (var banner in _banners.Where(b => plan.Reposition.Contains(b.Id)))
            banner.Position();
    }

    private void Rebuild(string reason)
    {
        Log.Info($"Rebuilding banners: {reason}.");
        Rebuild(BannerLayout.ComputeSlots(Monitors.GetAll(), Settings.Position, Settings.FontSize));
    }

    private void Rebuild(IReadOnlyList<BannerSlot> slots)
    {
        DestroyBanners();
        foreach (var slot in slots)
        {
            try
            {
                _banners.Add(new BannerWindow(++_nextId, slot, this, Settings));
            }
            catch (Exception ex)
            {
                Log.Error($"Could not create banner for {slot.DeviceName} {slot.Edge}", ex);
            }
        }
    }

    private void DestroyBanners()
    {
        foreach (var banner in _banners) banner.Destroy();
        _banners.Clear();
    }

    private void ReloadSettings()
    {
        var loaded = RegistryPolicySource.LoadEffective();

        // Report new problems (e.g. the file just became unreadable) even when the effective settings didn't change.
        var warnings = string.Join('\n', loaded.Warnings);
        if (warnings != _warningsSnapshot)
        {
            _warningsSnapshot = warnings;
            foreach (var warning in loaded.Warnings) Log.Warn(warning);
        }

        // "Not set" is part of the serialized settings (Level is absent), so NOT SET <-> UNCLASSIFIED is a change too.
        var snapshot = SettingsSerializer.Serialize(loaded.Settings);
        if (snapshot == _settingsSnapshot) return;

        Log.Info("Settings changed; applying.");
        _loaded = loaded;
        _settingsSnapshot = snapshot;
        ApplySettings(firstRun: false);
    }

    private void ApplySettings(bool firstRun)
    {
        Policies.Apply(Settings);

        KillTimer(Handle, TimerClock);
        ClockText = "";
        if (Settings.ClockEnabled)
        {
            UpdateClock();
            SetTimer(Handle, TimerClock, 1000, 0);
        }

        Rebuild(firstRun ? "startup" : "settings changed");
    }

    private void UpdateClock()
    {
        var text = BannerText.FormatClock(DateTimeOffset.Now, Settings.ClockFormat, Settings.ClockUseZulu, Settings.ClockUppercase);
        if (text == ClockText) return;
        ClockText = text;
        foreach (var banner in _banners) banner.Redraw();
    }

    public void ShowMenu(int x, int y)
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenuW(menu, MF_STRING, MenuRecover, "&Recover hidden windows");
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, MenuSettings, "&Settings...");
            AppendMenuW(menu, MF_STRING, MenuAbout, "&About Better Banner");
            if (Settings.AllowUserExit)
            {
                AppendMenuW(menu, MF_SEPARATOR, 0, null);
                AppendMenuW(menu, MF_STRING, MenuExit, "E&xit");
            }

            // Required so the menu closes when the user clicks elsewhere.
            SetForegroundWindow(Handle);
            var command = (uint)TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, x, y, 0, Handle, 0);
            PostMessageW(Handle, 0 /* WM_NULL */, 0, 0);

            switch (command)
            {
                case MenuRecover: RecoverWindows(); break;
                case MenuAbout: ShowAbout(); break;
                case MenuSettings: OpenSettings(); break;
                case MenuExit:
                    UserChoseExit = true;
                    Destroy();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static void OpenSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "settings") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open settings", ex);
        }
    }

    private void ShowAbout()
    {
        var source = _loaded.FromPolicy ? "Group Policy" : File.Exists(SettingsLoader.DefaultSettingsPath) ? "machine settings file" : "defaults";
        MessageBoxW(Handle,
            $"Better Banner {AppInfo.Version}\n\nMarking: {Settings.EffectiveCaption}\nSettings: {source}\n\n" +
            "Double-click a banner to bring back windows hidden under it or off-screen.",
            "About Better Banner", MB_OK | MB_ICONINFORMATION | MB_TOPMOST);
    }

    /// <summary>Moves windows whose title bar is off-screen or under a banner back into view.</summary>
    public void RecoverWindows()
    {
        var monitors = Monitors.GetAll();
        if (monitors.Count == 0) return;
        var workAreas = monitors.Select(m => m.WorkArea).ToList();
        var primaryWork = (monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0]).WorkArea;
        var bannerRects = _banners.Select(b => b.ActualRect).ToList();
        var ownPid = (uint)Environment.ProcessId;

        var recovered = 0;
        foreach (var hwnd in EnumerateTopLevelWindows())
        {
            if (!IsRecoverable(hwnd, ownPid)) continue;

            var bounds = GetVisibleBounds(hwnd);
            var titleBar = (int)Math.Round(32 * GetDpiForWindow(hwnd) / (double)BannerLayout.BaseDpi);
            if (!WindowRecovery.NeedsRecovery(bounds, workAreas, bannerRects, titleBar)) continue;

            var target = WindowRecovery.ChooseWorkArea(bounds, workAreas, primaryWork);
            var (x, y) = WindowRecovery.Target(target, recovered, titleBar, cascade: true);
            // The window rect includes invisible resize borders; offset so the visible frame lands on the target.
            GetWindowRect(hwnd, out var full);
            x -= bounds.Left - full.Left;
            SetWindowPos(hwnd, 0, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            Log.Info($"Recovered window '{GetClassName(hwnd)}' from {bounds}.");
            recovered++;
        }
        Log.Info($"Recover windows: moved {recovered}.");
    }

    private static List<nint> EnumerateTopLevelWindows()
    {
        _enumerated = [];
        try
        {
            EnumWindows(&CollectWindow, 0);
            return _enumerated;
        }
        finally
        {
            _enumerated = null;
        }
    }

    private static readonly HashSet<string> NeverMove = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "Windows.UI.Core.CoreWindow",
    };

    private static bool IsRecoverable(nint hwnd, uint ownPid)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsZoomed(hwnd) || IsCloaked(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == ownPid) return false;
        var style = (uint)(long)GetWindowLongPtrW(hwnd, GWL_STYLE);
        var exStyle = (uint)(long)GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        if ((style & WS_CAPTION) != WS_CAPTION || (exStyle & WS_EX_TOOLWINDOW) != 0) return false;
        return !NeverMove.Contains(GetClassName(hwnd));
    }

    [UnmanagedCallersOnly]
    private static int CollectWindow(nint hwnd, nint data)
    {
        _enumerated?.Add(hwnd);
        return 1;
    }
}
