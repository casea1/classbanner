using System.ComponentModel;
using System.Runtime.InteropServices;
using static ClassBanner.Native;

namespace ClassBanner;

/// <summary>Minimal Win32 window wrapper. One shared window procedure routes messages to instances.</summary>
internal abstract unsafe class Win32Window
{
    private const uint WM_NCDESTROY = 0x0082;

    private static readonly Dictionary<nint, Win32Window> Windows = [];
    private static readonly HashSet<string> RegisteredClasses = [];
    private static Win32Window? _creating;

    public nint Handle { get; private set; }

    public bool IsAlive => Handle != 0;

    protected void CreateWindow(string className, uint classStyle, uint exStyle, uint style, int x, int y, int width, int height, nint background = 0)
    {
        RegisterClass(className, classStyle, background);
        _creating = this;
        try
        {
            var hwnd = CreateWindowExW(exStyle, className, "ClassBanner", style, x, y, width, height, 0, 0, GetModuleHandleW(null), 0);
            if (hwnd == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"CreateWindowEx({className}) failed");
            Handle = hwnd;
            Windows[hwnd] = this;
        }
        finally
        {
            _creating = null;
        }
    }

    public void Destroy()
    {
        if (Handle != 0) DestroyWindow(Handle);
    }

    protected virtual nint WndProc(uint msg, nint wParam, nint lParam) => DefWindowProcW(Handle, msg, wParam, lParam);

    /// <summary>Called after WM_DESTROY, once the handle is gone.</summary>
    protected virtual void OnDestroyed()
    {
    }

    private static void RegisterClass(string className, uint classStyle, nint background)
    {
        if (RegisteredClasses.Contains(className)) return;
        fixed (char* name = className)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = classStyle,
                lpfnWndProc = &StaticWndProc,
                hInstance = GetModuleHandleW(null),
                hCursor = LoadCursorW(0, IDC_ARROW),
                hIcon = LoadAppIcon(32),
                hIconSm = LoadAppIcon(16),
                hbrBackground = background,
                lpszClassName = name,
            };
            if (RegisterClassExW(wc) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"RegisterClassEx({className}) failed");
        }
        RegisteredClasses.Add(className);
    }

    [UnmanagedCallersOnly]
    private static nint StaticWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (!Windows.TryGetValue(hwnd, out var window))
            {
                // Messages sent during CreateWindowEx arrive before it returns the handle.
                if (_creating is null) return DefWindowProcW(hwnd, msg, wParam, lParam);
                window = _creating;
                window.Handle = hwnd;
                Windows[hwnd] = window;
            }

            var result = window.WndProc(msg, wParam, lParam);

            if (msg == WM_NCDESTROY)
            {
                Windows.Remove(hwnd);
                window.Handle = 0;
                window.OnDestroyed();
            }
            return result;
        }
        catch (Exception ex)
        {
            // Never let an exception unwind into Windows; that would kill the banner.
            Log.Error($"Unhandled error in window message 0x{msg:X4}", ex);
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    /// <summary>Runs the message loop until PostQuitMessage. Pass a window to get Tab/Enter/Esc dialog navigation.</summary>
    public static int RunMessageLoop(nint dialog = 0)
    {
        while (true)
        {
            var result = GetMessageW(out var msg, 0, 0, 0);
            if (result == 0) return (int)msg.wParam;
            if (result == -1) return 1;
            if (dialog != 0 && IsDialogMessageW(dialog, ref msg)) continue;
            TranslateMessage(msg);
            DispatchMessageW(msg);
        }
    }
}
