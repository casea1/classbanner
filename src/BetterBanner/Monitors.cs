using System.Runtime.InteropServices;
using BetterBanner.Core;
using static BetterBanner.Native;

namespace BetterBanner;

internal static unsafe class Monitors
{
    private static List<nint>? _handles;

    /// <summary>All active displays with physical-pixel bounds and their effective DPI.</summary>
    public static List<MonitorInfo> GetAll()
    {
        _handles = [];
        try
        {
            EnumDisplayMonitors(0, 0, &Collect, 0);
            var result = new List<MonitorInfo>(_handles.Count);
            foreach (var handle in _handles)
            {
                var info = new MONITORINFOEXW { cbSize = (uint)sizeof(MONITORINFOEXW) };
                if (!GetMonitorInfoW(handle, ref info)) continue;

                var dpi = GetDpiForMonitor(handle, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0 ? (int)dpiX : BannerLayout.BaseDpi;
                var name = new string(info.szDevice);
                var nul = name.IndexOf('\0');
                if (nul >= 0) name = name[..nul];

                result.Add(new MonitorInfo(
                    name,
                    info.rcMonitor.ToScreenRect(),
                    info.rcWork.ToScreenRect(),
                    dpi,
                    (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            }
            return result;
        }
        finally
        {
            _handles = null;
        }
    }

    [UnmanagedCallersOnly]
    private static int Collect(nint monitor, nint hdc, RECT* rect, nint data)
    {
        _handles?.Add(monitor);
        return 1;
    }
}
