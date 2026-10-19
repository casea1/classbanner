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

        SetWindowPos(Handle, HWND_TOPMOST, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
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
        if (_settings.StayAboveFullScreenApps)
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
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
                    if (_settings.StayAboveFullScreenApps) KeepOnTop();
                    else SetWindowPos(Handle, lParam != 0 ? -2 /* HWND_NOTOPMOST */ : HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
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

            // The marking is never cut off; the clock gives way first (see CaptionLayout).
            const uint flags = DT_SINGLELINE | DT_NOPREFIX;
            var caption = _settings.EffectiveCaption;
            var clock = _host.ClockText;
            var pad = (int)Math.Round(8 * Slot.Dpi / (double)BannerLayout.BaseDpi);
            var captionWidth = MeasureWidth(hdc, caption, flags);
            var clockWidth = clock.Length > 0 ? MeasureWidth(hdc, clock, flags) : 0;
            var placement = CaptionLayout.Place(client.Right - client.Left, captionWidth, clockWidth, pad);

            if (placement.ShowClock && clockWidth > 0)
            {
                var clockRect = client;
                clockRect.Right -= pad;
                DrawTextW(hdc, clock, -1, ref clockRect, DT_RIGHT | DT_VCENTER | flags);
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
