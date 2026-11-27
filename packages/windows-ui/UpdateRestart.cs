using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PiuLink.Desktop;

internal static class UpdateRestart
{
    private const uint NoCrash = 1, NoHang = 2, NoReboot = 8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegisterApplicationRestart(string commandLine, uint flags);

    internal static void Register(string arguments)
    {
        // Restart Manager can relaunch only the prior interactive instance that opted in.
        int result = RegisterApplicationRestart(arguments, NoCrash | NoHang | NoReboot);
        if (result < 0)
            Trace.TraceWarning("PiuLink update restart registration failed (0x{0:X8}).", result);
    }
}
