using System.Runtime.InteropServices;

namespace ClassBanner;

/// <summary>Puts text on the Windows clipboard.</summary>
internal static unsafe partial class Clipboard
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    public static bool SetText(string text, nint owner = 0)
    {
        // Another program can hold the clipboard for a moment; try a few times.
        var opened = false;
        for (var attempt = 0; attempt < 10 && !(opened = OpenClipboard(owner)); attempt++) Thread.Sleep(20);
        if (!opened) return false;
        try
        {
            if (!EmptyClipboard()) return false;
            var bytes = (nuint)((text.Length + 1) * sizeof(char));
            var memory = GlobalAlloc(GMEM_MOVEABLE, bytes);
            if (memory == 0) return false;
            var target = (char*)GlobalLock(memory);
            if (target == null)
            {
                GlobalFree(memory);
                return false;
            }
            fixed (char* source = text) Buffer.MemoryCopy(source, target, (long)bytes, text.Length * sizeof(char));
            target[text.Length] = '\0';
            GlobalUnlock(memory);
            if (SetClipboardData(CF_UNICODETEXT, memory) != 0) return true; // the clipboard owns the memory now
            GlobalFree(memory);
            return false;
        }
        finally
        {
            CloseClipboard();
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint owner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    private static partial nint SetClipboardData(uint format, nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    private static partial void* GlobalLock(nint memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalFree(nint memory);
}
