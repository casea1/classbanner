using System.Runtime.InteropServices;
using ClassBanner.Core;
using static ClassBanner.Native;

namespace ClassBanner;

/// <summary>
/// Hidden top-level window that owns every banner. It listens for anything that can move or invalidate banners
/// (display changes, DPI changes, unlock, resume, Explorer restart) and reconciles the banners against the monitors
/// that actually exist. A periodic check catches anything Windows doesn't announce.
/// </summary>
internal sealed unsafe class BannerHost : Win32Window
{
    public static readonly uint AppBarCallbackMessage = WM_APP + 1;

    private const string ClassName = "ClassBanner.Host";
    private const nuint TimerReconcile = 1;
    private const nuint TimerDebounce = 2;
    private const nuint TimerClock = 3;
    private const nuint TimerSettings = 4;
    private const nuint TimerSettled = 5;

    private const uint ReconcileIntervalMs = 2000;
    private const uint DebounceMs = 500;
    private const uint SettingsIntervalMs = 30_000;
    private const uint SettleMs = 1500;
    private const long MaxSettleMs = 10_000;

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

        // Re-judge which banners a full-screen app should cover the moment the active window changes or a window is
        // restored, instead of on the next 2-second check.
        _current = this;
        _focusHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, &OnWindowEvent, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        _restoreHook = SetWinEventHook(EVENT_SYSTEM_MINIMIZEEND, EVENT_SYSTEM_MINIMIZEEND, 0, &OnWindowEvent, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        ApplySettings(firstRun: true);
    }

    private static BannerHost? _current;
    private readonly nint _focusHook, _restoreHook;

    [UnmanagedCallersOnly]
    private static void OnWindowEvent(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_current is not { } host) return;
        foreach (var banner in host._banners) banner.KeepOnTop();
    }

    public BannerSettings Settings => _loaded.Settings;

    public string ClockText { get; private set; } = "";

    /// <summary>The left and right text (computer name, user name, clock) for a banner on <paramref name="edge"/>.</summary>
    public (string Left, string Right) SideTexts(BannerEdge edge) =>
        BannerCorners.For(Settings, edge, ClockText, Environment.UserName, Environment.MachineName);

    /// <summary>True from an unlock or resume until the displays have been quiet for <see cref="SettleMs"/>.</summary>
    private bool _settling;
    private long _settleStarted;

    public void RequestReconcile()
    {
        // While the displays are still coming back, wait for them to go quiet instead of reacting to each step.
        // Never longer than MaxSettleMs, so a display that keeps flickering can't hold off the check.
        if (_settling)
        {
            if (Environment.TickCount64 - _settleStarted < MaxSettleMs) SetTimer(Handle, TimerSettled, SettleMs, 0);
            return;
        }
        SetTimer(Handle, TimerDebounce, DebounceMs, 0);
    }

    /// <summary>
    /// After an unlock or resume, monitors can drop out and come back one by one and Windows may move windows to the
    /// primary screen. Reacting to each step rebuilt the banners once or twice, and rebuilding unconditionally after
    /// every unlock made them flash even when nothing had moved. Wait until the displays are quiet, then check once:
    /// only a banner that is actually wrong is put back, and a rebuild happens only if the layout really changed.
    /// </summary>
    private void SettleThenCheck()
    {
        _settling = true;
        _settleStarted = Environment.TickCount64;
        KillTimer(Handle, TimerDebounce);
        SetTimer(Handle, TimerSettled, SettleMs, 0);
    }

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
                    // "Policy" is broadcast after a Group Policy refresh; "ClassBanner" by our own config command.
                    if (area is "Policy" or "ClassBanner") ReloadSettings();
                }
                return 0;

            case WM_WTSSESSION_CHANGE:
                if ((int)wParam is WTS_SESSION_UNLOCK or WTS_CONSOLE_CONNECT or WTS_REMOTE_CONNECT)
                {
                    Log.Info($"Session change {(int)wParam}; checking banners once the displays settle.");
                    SettleThenCheck();
                }
                return 0;

            case WM_POWERBROADCAST:
                if ((int)wParam is PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND)
                {
                    Log.Info("Resumed; checking banners once the displays settle.");
                    SettleThenCheck();
                }
                return 1;

            case WM_QUERYENDSESSION:
                return 1;

            case WM_DESTROY:
                if (_focusHook != 0) UnhookWinEvent(_focusHook);
                if (_restoreHook != 0) UnhookWinEvent(_restoreHook);
                _current = null;
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
                if (!_settling) Reconcile();
                foreach (var banner in _banners) banner.KeepOnTop();
                break;
            case TimerDebounce:
                KillTimer(Handle, TimerDebounce);
                Reconcile();
                break;
            case TimerSettled:
                KillTimer(Handle, TimerSettled);
                _settling = false;
                Reconcile();
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
        foreach (var banner in _banners)
        {
            if (banner.EnsureVisible()) Log.Warn($"A banner ({banner.Slot.DeviceName} {banner.Slot.Edge}) had been hidden or minimized; shown again.");
        }

        var desired = BannerLayout.ComputeSlots(Monitors.GetAll(), Settings.Position, Settings.FontSize, Settings.BannerThickness);
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
        Rebuild(BannerLayout.ComputeSlots(Monitors.GetAll(), Settings.Position, Settings.FontSize, Settings.BannerThickness));
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

    /// <summary>True while the banner's right-click menu, About box or another of its own windows is in front.</summary>
    public bool OwnUiInFront()
    {
        if (_menuOpen) return true;
        var front = GetForegroundWindow();
        if (front == 0) return false;
        GetWindowThreadProcessId(front, out var pid);
        return pid == (uint)Environment.ProcessId && _banners.All(b => b.Handle != front);
    }

    private bool _menuOpen;

    public void ShowMenu(int x, int y)
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenuW(menu, MF_STRING, MenuRecover, "&Recover hidden windows");
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, MenuSettings, "&Settings...");
            AppendMenuW(menu, MF_STRING, MenuAbout, "&About ClassBanner");
            if (Settings.AllowUserExit)
            {
                AppendMenuW(menu, MF_SEPARATOR, 0, null);
                AppendMenuW(menu, MF_STRING, MenuExit, "E&xit");
            }

            // Required so the menu closes when the user clicks elsewhere.
            SetForegroundWindow(Handle);
            uint command;
            _menuOpen = true;
            try { command = (uint)TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, x, y, 0, Handle, 0); }
            finally { _menuOpen = false; }
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
            $"ClassBanner {AppInfo.Version}\n\nMarking: {Settings.EffectiveCaption}\nSettings: {source}\n\n" +
            "Double-click a banner to bring back windows hidden under it or off-screen.",
            "About ClassBanner", MB_OK | MB_ICONINFORMATION | MB_TOPMOST);
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
