using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PiuLink.Desktop;

internal enum CopyResult { Copied, Unavailable, Canceled }

internal static class DesktopClipboard
{
    private static long latestRequest;

    // All callers run on the WPF dispatcher. Release it between attempts so another click,
    // closing the window, or disabling an invitation can supersede a pending write.
    internal static async Task<CopyResult> CopyAsync(Window owner, string text, CancellationToken token = default)
    {
        owner.Dispatcher.VerifyAccess();
        var request = ++latestRequest;
        var hwnd = new WindowInteropHelper(owner).Handle;
        var elapsed = Stopwatch.StartNew();
        while (!token.IsCancellationRequested && request == latestRequest && owner.IsVisible && hwnd != IntPtr.Zero)
        {
            if (OpenClipboard(hwnd))
            {
                try { return WriteText(text) ? CopyResult.Copied : CopyResult.Unavailable; }
                finally { CloseClipboard(); }
            }
            if (elapsed.ElapsedMilliseconds >= 2000) return CopyResult.Unavailable;
            await Task.Delay(60);
        }
        return CopyResult.Canceled;
    }

    private static bool WriteText(string text)
    {
        const uint movable = 0x0002;
        const uint unicodeText = 13;
        var memory = GlobalAlloc(movable, ((nuint)text.Length + 1) * sizeof(char));
        if (memory == IntPtr.Zero) return false;
        try
        {
            var pointer = GlobalLock(memory);
            if (pointer == IntPtr.Zero) return false;
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
                Marshal.WriteInt16(pointer, checked(text.Length * sizeof(char)), 0);
            }
            finally { GlobalUnlock(memory); }

            if (!EmptyClipboard() || SetClipboardData(unicodeText, memory) == IntPtr.Zero) return false;
            memory = IntPtr.Zero; // Windows now owns the Unicode text, including after Host exit.
            return true;
        }
        finally { if (memory != IntPtr.Zero) GlobalFree(memory); }
    }

    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}
