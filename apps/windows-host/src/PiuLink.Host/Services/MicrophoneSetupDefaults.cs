using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using NAudio.CoreAudioApi;

namespace PiuLink.Host.Services;

// MSI runs in Session 0. Audio defaults must be read and restored as each logged-in user.
internal static class MicrophoneSetupDefaults
{
    private sealed record SavedRole(DataFlow Flow, Role Role, string Id);

    internal static void InUserSession(bool restore, Guid id)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PiuLink", "AudioSetup");
        var path = Path.Combine(directory, $"{id:N}-{Process.GetCurrentProcess().SessionId}.json");
        if (restore) {
            var roles = JsonSerializer.Deserialize<SavedRole[]>(File.ReadAllText(path)) ?? throw new IOException("Missing audio defaults.");
            SessionMicrophoneRoute.RestoreInstallationDefaults(roles.ToDictionary(r => (r.Flow, r.Role), r => r.Id));
            File.Delete(path);
        } else {
            Directory.CreateDirectory(directory);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, SessionMicrophoneRoute.CaptureInstallationDefaults()
                .Select(r => new SavedRole(r.Key.Flow, r.Key.Role, r.Value)));
        }
    }

    internal static async Task InstallAsync(Func<Task> install)
    {
        var id = Guid.NewGuid();
        if (Process.GetCurrentProcess().SessionId != 0) {
            InUserSession(false, id);
            try { await install(); } finally { InUserSession(true, id); }
            return;
        }
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var data, out var count)) throw new Win32Exception();
        var users = new List<SafeAccessTokenHandle>();
        try {
            for (int i = 0; i < count; i++) {
                var session = Marshal.PtrToStructure<WtsSession>(data + i * Marshal.SizeOf<WtsSession>());
                if (session.Id == 0 || session.State is not (0 or 1 or 4)) continue;
                if (!WTSQueryUserToken(session.Id, out var token)) {
                    if (Marshal.GetLastWin32Error() == 1008) continue; // No logged-on user.
                    throw new Win32Exception();
                }
                try { RunAsUser(token, false, id); users.Add(token); }
                catch { token.Dispose(); throw; }
            }
            await install();
        } finally {
            WTSFreeMemory(data);
            var failures = new List<Exception>();
            foreach (var token in users) {
                try { RunAsUser(token, true, id); } catch (Exception error) { failures.Add(error); }
                finally { token.Dispose(); }
            }
            if (failures.Count != 0) throw new AggregateException("Could not restore the user's audio defaults.", failures);
        }
    }

    private static void RunAsUser(SafeAccessTokenHandle token, bool restore, Guid id)
    {
        var executable = Environment.ProcessPath!;
        var argument = restore ? "--restore-setup-audio" : "--save-setup-audio";
        var command = new System.Text.StringBuilder($"\"{executable}\" {argument} {id:D}");
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        if (!CreateEnvironmentBlock(out var environment, token, false)) throw new Win32Exception();
        try {
            if (!CreateProcessAsUserW(token, executable, command, IntPtr.Zero, IntPtr.Zero, false,
                0x08000400, environment, AppContext.BaseDirectory, ref startup, out var process)) throw new Win32Exception();
            try {
                if (WaitForSingleObject(process.Process, 30000) != 0 ||
                    !GetExitCodeProcess(process.Process, out var exit) || exit != 0)
                    throw new IOException("Audio defaults helper did not complete.");
            } finally { CloseHandle(process.Thread); CloseHandle(process.Process); }
        } finally { DestroyEnvironmentBlock(environment); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct WtsSession { public uint Id; public IntPtr Name; public int State; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo {
        public int Size; public IntPtr Reserved, Desktop, Title;
        public uint X,Y,Width,Height,Cols,Rows,Fill,Flags; public ushort Show,Reserved2Size;
        public IntPtr Reserved2,Input,Output,Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcInfo { public IntPtr Process, Thread; public uint Pid, Tid; }
    [DllImport("wtsapi32.dll", SetLastError=true)] private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);
    [DllImport("wtsapi32.dll", SetLastError=true)] private static extern bool WTSQueryUserToken(uint session, out SafeAccessTokenHandle token);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr data);
    [DllImport("userenv.dll", SetLastError=true)] private static extern bool CreateEnvironmentBlock(out IntPtr env, SafeAccessTokenHandle token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool CreateProcessAsUserW(SafeAccessTokenHandle token, string app, System.Text.StringBuilder cmd, IntPtr processAttributes, IntPtr threadAttributes,
        bool inherit, uint flags, IntPtr environment, string dir, ref StartupInfo startup, out ProcInfo process);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr handle, out uint status);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
