using ClassBanner.Core;
using static ClassBanner.Native;

namespace ClassBanner;

/// <summary>
/// One banner strip on one edge of one monitor. Registered as a shell "app bar" so maximized windows stop at it
/// instead of sliding underneath.
/// </summary>
internal sealed unsafe class BannerWindow : Win32Window
{
    private const string ClassName = "ClassBanner.Banner";

    private readonly BannerHost _host;
    private readonly BannerSettings _settings;
    private nint _font;
    private nint _brush;
    private bool _appBarRegistered;
    private bool _hovering;

    public BannerWindow(int id, BannerSlot slot, BannerHost host, BannerSettings settings)
    {
        Id = id;
        Slot = slot;
        _host = host;
        _settings = settings;

        var strip = slot.Strip;
        CreateWindow(ClassName, CS_DBLCLKS | CS_HREDRAW | CS_VREDRAW,
            WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED, WS_POPUP,
            strip.Left, strip.Top, strip.Width, strip.Height);

        _font = CreateFontW(-BannerLayout.TextPixels(settings.FontSize, slot.Dpi, slot.Height), 0, 0, 0, FW_BOLD, 0, 0, 0, 1, 0, 0,
            CLEARTYPE_QUALITY, 0, "Segoe UI");
        _brush = CreateSolidBrush(ToColorRef(settings.EffectiveBackColor));
        SetAlpha(255);

        var abd = NewAppBarData();
        _appBarRegistered = SHAppBarMessage(ABM_NEW, ref abd) != 0;
        if (!_appBarRegistered) Log.Warn($"Could not register app bar for {slot.DeviceName} {slot.Edge}; banner will overlay instead.");

        Position();
        ShowWindow(Handle, SW_SHOWNOACTIVATE);
    }

    public int Id { get; }

    public BannerSlot Slot { get; }

    public ScreenRect ActualRect => GetWindowRect(Handle, out var r) ? r.ToScreenRect() : default;

    /// <summary>Claims the edge with the shell, then puts the window exactly where the shell agreed.</summary>
    public void Position()
    {
        var rect = RECT.From(Slot.Strip);
        if (_appBarRegistered)
        {
            var abd = NewAppBarData();
            abd.rc = rect;
            SHAppBarMessage(ABM_QUERYPOS, ref abd);

            // The shell may move the proposed rect away from other app bars (e.g. a top taskbar); keep our height.
            if (Slot.Edge == BannerEdge.Top) abd.rc.Bottom = abd.rc.Top + Slot.Height;
            else abd.rc.Top = abd.rc.Bottom - Slot.Height;

            SHAppBarMessage(ABM_SETPOS, ref abd);
            rect = abd.rc;
        }

        SetWindowPos(Handle, ZOrderTarget(), rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    /// <summary>
    /// Shows the banner again if something hid or minimized it (another process can call ShowWindow on it, issue #17).
    /// Returns true when it had to.
    /// </summary>
    public bool EnsureVisible()
    {
        if (IsWindowVisible(Handle) && !IsIconic(Handle)) return false;
        ShowWindow(Handle, SW_SHOWNOACTIVATE);
        Position();
        return true;
    }

    public void KeepOnTop()
    {
        // While the banner's own menu or About box is open, raising the banner would put it over them.
        if (_host.OwnUiInFront()) return;
        SetWindowPos(Handle, ZOrderTarget(), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Topmost, unless <see cref="BannerSettings.StayAboveFullScreenApps"/> is off and a full-screen app (full-screen
    /// Remote Desktop, a presentation, video) is frontmost on this monitor: then just behind it, so a remote session
    /// shows its own banner instead of having this one drawn over its edges. Judged per monitor by what is on top
    /// there, not by which window is active, so clicking something on another monitor doesn't bring it back.
    /// </summary>
    private nint ZOrderTarget()
    {
        if (_settings.StayAboveFullScreenApps) return HWND_TOPMOST;
        var verdict = FullScreen.Judge(Slot.MonitorBounds, WindowsFrontToBack());
        LogZOrderChange(verdict);
        return verdict.Behind ?? HWND_TOPMOST;
    }

    private nint? _lastBehind;
    private nint _lastInFront;

    /// <summary>Logs when this banner steps behind a full-screen app or comes back, and which window brought it back.</summary>
    private void LogZOrderChange(FullScreenVerdict verdict)
    {
        var inFront = verdict.InFront?.Handle ?? 0;
        if (verdict.Behind == _lastBehind && inFront == _lastInFront) return;
        var where = $"{Slot.DeviceName} {Slot.Edge}";
        if (verdict.Behind is { } behind)
            Log.Info($"Banner {where}: behind the full-screen window {behind:X} ({GetClassName(behind)}).");
        else if (verdict.InFront is { } window && _lastBehind is not null)
            Log.Info($"Banner {where}: back on top; {window.ClassName} (process {window.ProcessId}) at {window.Rect} is in front on this monitor.");
        else if (_lastBehind is not null)
            Log.Info($"Banner {where}: back on top; no full-screen window on this monitor.");
        _lastBehind = verdict.Behind;
        _lastInFront = inFront;
    }

    /// <summary>Visible top-level windows of other processes, front to back.</summary>
    private static List<TopWindow> WindowsFrontToBack()
    {
        var windows = new List<TopWindow>();
        var ownPid = (uint)Environment.ProcessId;
        for (var hwnd = GetTopWindow(0); hwnd != 0; hwnd = GetWindow(hwnd, GW_HWNDNEXT))
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsCloaked(hwnd)) continue;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == ownPid) continue;
            // Tool windows, no-activate pop-ups (tooltips, notifications) and click-through overlays aren't what the user
            // is looking at.
            var exStyle = (uint)(long)GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
            if ((exStyle & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT)) != 0) continue;
            // The visible frame: GetWindowRect includes invisible resize borders, which make a window maximized on the
            // next monitor reach 8 px across onto this one.
            var rect = GetVisibleBounds(hwnd);
            if (rect.IsEmpty) continue;
            windows.Add(new TopWindow(hwnd, rect, GetClassName(hwnd), pid));
        }
        return windows;
    }

    public void Redraw() => InvalidateRect(Handle, 0, false);

    protected override nint WndProc(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
                Paint();
                return 0;

            case WM_ERASEBKGND:
                return 1;

            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;

            case WM_MOUSEMOVE:
                if (!_hovering)
                {
                    _hovering = true;
                    SetAlpha((byte)(255 * (100 - _settings.HoverTransparency) / 100));
                    var tme = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = TME_LEAVE, hwndTrack = Handle };
                    TrackMouseEvent(ref tme);
                }
                return 0;

            case WM_MOUSELEAVE:
                _hovering = false;
                SetAlpha(255);
                return 0;

            case WM_LBUTTONDBLCLK:
                _host.RecoverWindows();
                return 0;

            case WM_RBUTTONUP:
                GetCursorPos(out var pt);
                _host.ShowMenu(pt.X, pt.Y);
                return 0;

            case WM_WINDOWPOSCHANGED:
                if (_appBarRegistered)
                {
                    var abd = NewAppBarData();
                    SHAppBarMessage(ABM_WINDOWPOSCHANGED, ref abd);
                }
                break;

            case WM_DPICHANGED:
                // Ignore Windows' suggested rect; the host rebuilds banners for the new DPI.
                _host.RequestReconcile();
                return 0;

            case WM_DESTROY:
                RemoveAppBar();
                return 0;
        }

        if (msg == BannerHost.AppBarCallbackMessage)
        {
            switch ((int)wParam)
            {
                case ABN_POSCHANGED:
                    _host.RequestReconcile();
                    break;
                case ABN_FULLSCREENAPP:
                    // lParam != 0: a full-screen app opened on this monitor.
                    KeepOnTop();
                    break;
            }
            return 0;
        }

        return base.WndProc(msg, wParam, lParam);
    }

    protected override void OnDestroyed()
    {
        if (_font != 0) DeleteObject(_font);
        if (_brush != 0) DeleteObject(_brush);
        _font = _brush = 0;
    }

    private void Paint()
    {
        var hdc = BeginPaint(Handle, out var ps);
        try
        {
            GetClientRect(Handle, out var client);
            FillRect(hdc, client, _brush);

            var oldFont = SelectObject(hdc, _font);
            SetBkMode(hdc, TRANSPARENT);
            SetTextColor(hdc, ToColorRef(_settings.EffectiveForeColor));

            // The marking is never cut off; the right text, then the left text, give way first (see CaptionLayout).
            const uint flags = DT_SINGLELINE | DT_NOPREFIX;
            var caption = _settings.EffectiveCaption;
            var (left, right) = _host.SideTexts(Slot.Edge);
            var pad = (int)Math.Round(8 * Slot.Dpi / (double)BannerLayout.BaseDpi);
            var captionWidth = MeasureWidth(hdc, caption, flags);
            var leftWidth = left.Length > 0 ? MeasureWidth(hdc, left, flags) : 0;
            var rightWidth = right.Length > 0 ? MeasureWidth(hdc, right, flags) : 0;
            var placement = CaptionLayout.Place(client.Right - client.Left, captionWidth, leftWidth, rightWidth, pad);

            if (placement.ShowLeft && leftWidth > 0)
            {
                var leftRect = client;
                leftRect.Left += pad;
                DrawTextW(hdc, left, -1, ref leftRect, DT_LEFT | DT_VCENTER | flags);
            }

            if (placement.ShowRight && rightWidth > 0)
            {
                var rightRect = client;
                rightRect.Right -= pad;
                DrawTextW(hdc, right, -1, ref rightRect, DT_RIGHT | DT_VCENTER | flags);
            }

            var captionRect = new RECT(client.Left + placement.Left, client.Top, client.Left + placement.Right, client.Bottom);
            if (placement.Scale < 1)
            {
                // Too wide even without the clock (very long marking on a narrow screen): shrink rather than truncate.
                var pixels = Math.Max(6, (int)(BannerLayout.TextPixels(_settings.FontSize, Slot.Dpi, Slot.Height) * placement.Scale));
                var small = CreateFontW(-pixels, 0, 0, 0, FW_BOLD, 0, 0, 0, 1, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");
                var previous = SelectObject(hdc, small);
                DrawTextW(hdc, caption, -1, ref captionRect, DT_CENTER | DT_VCENTER | flags);
                SelectObject(hdc, previous);
                DeleteObject(small);
            }
            else
            {
                DrawTextW(hdc, caption, -1, ref captionRect, DT_CENTER | DT_VCENTER | flags);
            }

            SelectObject(hdc, oldFont);
        }
        finally
        {
            EndPaint(Handle, ps);
        }
    }

    private static int MeasureWidth(nint hdc, string text, uint flags)
    {
        var rect = new RECT(0, 0, 0, 0);
        DrawTextW(hdc, text, -1, ref rect, DT_CALCRECT | flags);
        return rect.Right - rect.Left;
    }

    private void SetAlpha(byte alpha) => SetLayeredWindowAttributes(Handle, 0, alpha, LWA_ALPHA);

    private void RemoveAppBar()
    {
        if (!_appBarRegistered) return;
        var abd = NewAppBarData();
        SHAppBarMessage(ABM_REMOVE, ref abd);
        _appBarRegistered = false;
    }

    private APPBARDATA NewAppBarData() => new()
    {
        cbSize = (uint)sizeof(APPBARDATA),
        hWnd = Handle,
        uCallbackMessage = BannerHost.AppBarCallbackMessage,
        uEdge = Slot.Edge == BannerEdge.Top ? ABE_TOP : ABE_BOTTOM,
    };
}
