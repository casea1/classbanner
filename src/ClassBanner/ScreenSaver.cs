using ClassBanner.Core;
using static ClassBanner.Native;

namespace ClassBanner;

/// <summary>"ClassBanner.scr /s": the classification marking scrolling across every monitor.</summary>
internal sealed class ScreenSaver : Win32Window
{
    private const string ClassName = "ClassBanner.ScreenSaver";
    private const nuint TimerAnimate = 1;
    private const int MouseMoveThreshold = 10;

    private static readonly List<ScreenSaver> Instances = [];
    private static POINT? _startCursor;
    private static bool _closing;

    private readonly BannerSettings _settings;
    private readonly int _width;
    private readonly int _height;
    private nint _font;
    private nint _brush;
    private int _x;
    private int _textWidth;

    private ScreenSaver(MonitorInfo monitor, BannerSettings settings)
    {
        _settings = settings;
        _width = monitor.Bounds.Width;
        _height = monitor.Bounds.Height;
        _x = _width;

        CreateWindow(ClassName, CS_HREDRAW | CS_VREDRAW, WS_EX_TOPMOST | WS_EX_TOOLWINDOW, WS_POPUP | WS_VISIBLE,
            monitor.Bounds.Left, monitor.Bounds.Top, _width, _height);
        _font = CreateFontW(-(_height / 6), 0, 0, 0, FW_BOLD, 0, 0, 0, 1, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");
        _brush = CreateSolidBrush(ToColorRef(settings.EffectiveBackColor));
        SetTimer(Handle, TimerAnimate, 16, 0);
    }

    public static int Run()
    {
        var settings = RegistryPolicySource.LoadEffective().Settings;
        foreach (var monitor in Monitors.GetAll()) Instances.Add(new ScreenSaver(monitor, settings));
        ShowCursor(false);
        return RunMessageLoop();
    }

    protected override nint WndProc(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case WM_PAINT:
                Paint();
                return 0;
            case WM_ERASEBKGND:
                return 1;
            case WM_TIMER:
                // About one screen width every 6 seconds at 60 fps, whatever the resolution.
                _x -= Math.Max(2, _width / 360);
                if (_textWidth > 0 && _x < -_textWidth) _x = _width;
                InvalidateRect(Handle, 0, false);
                return 0;
            case WM_MOUSEMOVE:
                GetCursorPos(out var pt);
                _startCursor ??= pt;
                if (Math.Abs(pt.X - _startCursor.Value.X) > MouseMoveThreshold || Math.Abs(pt.Y - _startCursor.Value.Y) > MouseMoveThreshold)
                    Close();
                return 0;
            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
            case WM_LBUTTONDOWN:
            case WM_RBUTTONDOWN:
            case WM_MBUTTONDOWN:
            case WM_MOUSEWHEEL:
            case WM_ACTIVATEAPP when wParam == 0:
                Close();
                return 0;
            case WM_SETCURSOR:
                return 1; // keep the cursor hidden
            case WM_DESTROY:
                KillTimer(Handle, TimerAnimate);
                return 0;
        }
        return base.WndProc(msg, wParam, lParam);
    }

    protected override void OnDestroyed()
    {
        DeleteObject(_font);
        DeleteObject(_brush);
        _font = _brush = 0;
    }

    private static void Close()
    {
        if (_closing) return;
        _closing = true;
        ShowCursor(true);
        foreach (var instance in Instances) instance.Destroy();
        PostQuitMessage(0);
    }

    private void Paint()
    {
        var hdc = BeginPaint(Handle, out var ps);
        try
        {
            var client = new RECT(0, 0, _width, _height);
            FillRect(hdc, client, _brush);
            var oldFont = SelectObject(hdc, _font);
            SetBkMode(hdc, TRANSPARENT);
            SetTextColor(hdc, ToColorRef(_settings.EffectiveForeColor));

            if (_textWidth == 0)
            {
                var measure = client;
                DrawTextW(hdc, _settings.EffectiveCaption, -1, ref measure, DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX);
                _textWidth = measure.Right - measure.Left;
            }

            var text = new RECT(_x, 0, _x + _textWidth + 1, _height);
            DrawTextW(hdc, _settings.EffectiveCaption, -1, ref text, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
            SelectObject(hdc, oldFont);
        }
        finally
        {
            EndPaint(Handle, ps);
        }
    }
}
