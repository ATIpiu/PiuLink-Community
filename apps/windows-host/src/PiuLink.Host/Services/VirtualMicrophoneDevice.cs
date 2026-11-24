using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PiuLink.Host.Services;

// The instance belongs to PiuLink. Its signed transport driver stays in the Windows driver store.
internal static class VirtualMicrophoneDevice
{
    internal const string InstanceId = @"ROOT\PIULINKVIRTUALMICROPHONE\0000";
    private const string HardwareId = @"ROOT\PiuLinkMic";
    private const string LegacyHardwareId = @"ROOT\SteamStreamingMicrophone";
    private const string Name = "PiuLink 虚拟麦克风";
    private static readonly Guid MediaClass = new("4d36e96c-e325-11ce-bfc1-08002be10318");
    private const string SetupKey = @"SOFTWARE\ATIPiu Labs\PiuLink Host\Installer";
    private const string PreviousBinding = "MicrophonePreviousBinding";
    private static string TransactionKey(Guid product) => SetupKey + @"\" + product.ToString("N");

    internal static void BeginSetup(Guid product)
    {
        using var key = Registry.LocalMachine.CreateSubKey(TransactionKey(product));
        if (key.GetValue(PreviousBinding) != null) return;
        key.SetValue(PreviousBinding, HasInstance ? (HasLegacyHardwareId ? "legacy" : "existing") : "absent");
    }

    internal static void FinishSetup(Guid product)
    {
        using var key = Registry.LocalMachine.OpenSubKey(SetupKey, writable: true);
        key?.DeleteSubKey(product.ToString("N"), false);
    }

    internal static void RollbackSetup(Guid product)
    {
        using var key = Registry.LocalMachine.OpenSubKey(TransactionKey(product));
        var previous = key?.GetValue(PreviousBinding) as string;
        if (previous is "absent" or "legacy" && HasInstance) {
            var set = SetupDiCreateDeviceInfoList(in MediaClass, IntPtr.Zero);
            if (set == new IntPtr(-1)) throw new Win32Exception();
            try {
                var device = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
                Check(SetupDiOpenDeviceInfoW(set, InstanceId, IntPtr.Zero, 0, ref device));
                if (previous == "absent") Check(SetupDiCallClassInstaller(5, set, ref device));
                else {
                    SetProperty(set, ref device, 1, LegacyHardwareId + "\0\0");
                    Check(DiInstallDevice(IntPtr.Zero, set, ref device, IntPtr.Zero, 0, out _));
                }
            } finally { SetupDiDestroyDeviceInfoList(set); }
        }
        key?.Close();
        FinishSetup(product);
    }

    internal static bool WaitForEndpoints()
    {
        var capture = new Guid("2eef81be-33fa-4800-9670-1cd474972c3f");
        var render = new Guid("e6327cad-dcec-4949-ae8a-991e976a79d2");
        const uint presentOnly = 0x100;
        const uint enumerator = 1;
        for (int attempt = 0; attempt < 60; attempt++) {
            const string filter = "SWD";
            if (CM_Get_Device_ID_List_SizeW(out var size, filter, enumerator | presentOnly) == 0) {
                var ids = new char[size];
                if (CM_Get_Device_ID_ListW(filter, ids, size, enumerator | presentOnly) == 0) {
                    const string prefix = @"SWD\MMDEVAPI\";
                    var owned = new string(ids).Split('\0', StringSplitOptions.RemoveEmptyEntries)
                        .Where(id => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        .Select(id => id[prefix.Length..]).Where(OwnsEndpoint).ToArray();
                    bool HasInterface(Guid type) => owned.Any(id =>
                        CM_Get_Device_Interface_List_SizeW(out var length, in type, prefix + id, 0) == 0 && length > 1);
                    if (HasInterface(capture) && HasInterface(render)) return true;
                }
            }
            Thread.Sleep(250);
        }
        return false;
    }

    internal static bool OwnsEndpoint(string endpointId)
    {
        var id = @"SWD\MMDEVAPI\" + endpointId;
        if (CM_Locate_DevNodeW(out var node, id, 0) != 0 || CM_Get_Parent(out var parent, node, 0) != 0) return false;
        var name = new StringBuilder(200);
        return CM_Get_Device_IDW(parent, name, name.Capacity, 0) == 0 &&
            string.Equals(name.ToString(), InstanceId, StringComparison.OrdinalIgnoreCase);
    }

    private static string PackageInf => Path.Combine(AppContext.BaseDirectory, "drivers", "microphone", "PiuLinkMic.inf");
    internal static bool HasPackage => File.Exists(PackageInf);
    internal static bool HasInstance => CM_Locate_DevNodeW(out _, InstanceId, 0) == 0;
    internal static bool HasNativeDriver => DevicePropertyMatches(5, "PiuLinkMic"); // CM_DRP_SERVICE
    private static bool HasLegacyHardwareId => DevicePropertyMatches(2, LegacyHardwareId); // CM_DRP_HARDWAREID

    private static bool DevicePropertyMatches(uint property, string expected)
    {
        if (CM_Locate_DevNodeW(out var node, InstanceId, 0) != 0) return false;
        var value = new StringBuilder(256);
        uint bytes = (uint)value.Capacity * 2;
        return CM_Get_DevNode_Registry_PropertyW(node, property, out _, value, ref bytes, 0) == 0 &&
            value.ToString().Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool Install()
    {
        if (!HasPackage) return false;
        Check(SetupCopyOEMInfW(PackageInf, null, 1, 0, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero));
        var set = SetupDiCreateDeviceInfoList(in MediaClass, IntPtr.Zero);
        if (set == new IntPtr(-1)) throw new Win32Exception();
        try {
            var device = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
            bool existed = SetupDiOpenDeviceInfoW(set, InstanceId, IntPtr.Zero, 0, ref device);
            if (!existed) {
                if (Marshal.GetLastWin32Error() != unchecked((int)0xe000020b)) throw new Win32Exception();
                Check(SetupDiCreateDeviceInfoW(set, InstanceId, in MediaClass, Name, IntPtr.Zero, 0, ref device));
                SetProperty(set, ref device, 1, HardwareId + "\0\0"); // SPDRP_HARDWAREID (REG_MULTI_SZ)
                SetProperty(set, ref device, 12, Name + "\0"); // SPDRP_FRIENDLYNAME
                Check(SetupDiCallClassInstaller(0x19, set, ref device)); // DIF_REGISTERDEVICE
            }
            // 0.2.100 registered the same PiuLink instance with a Valve hardware ID.
            // Rebind only our devnode. A class-wide driver update would also touch Steam.
            var migrating = existed && HasLegacyHardwareId;
            try {
                SetProperty(set, ref device, 1, HardwareId + "\0\0");
                Check(DiInstallDevice(IntPtr.Zero, set, ref device, IntPtr.Zero, 0, out var reboot));
                if (reboot || !HasNativeDriver) throw new IOException("PiuLink microphone driver binding is not active.");
                SetProperty(set, ref device, 12, Name + "\0");
            } catch {
                if (!existed) Check(SetupDiCallClassInstaller(5, set, ref device)); // DIF_REMOVE
                else if (migrating) {
                    SetProperty(set, ref device, 1, LegacyHardwareId + "\0\0");
                    Check(DiInstallDevice(IntPtr.Zero, set, ref device, IntPtr.Zero, 0, out _));
                }
                throw;
            }
            return true;
        } finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private static void SetProperty(IntPtr set, ref DeviceInfo device, int property, string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value);
        Check(SetupDiSetDeviceRegistryPropertyW(set, ref device, property, bytes, bytes.Length));
    }

    private static void Check(bool ok)
    {
        if (!ok) throw new Win32Exception();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public int Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupCopyOEMInfW(string source, string? media, int type, uint flags, IntPtr destination,
        uint length, IntPtr required, IntPtr filename);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(in Guid deviceClass, IntPtr parent);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string id, IntPtr parent, uint flags, ref DeviceInfo device);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiCreateDeviceInfoW(IntPtr set, string id, in Guid deviceClass, string desc,
        IntPtr parent, uint flags, ref DeviceInfo device);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref DeviceInfo device, int property, byte[] value, int length);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(uint action, IntPtr set, ref DeviceInfo device);
    [DllImport("newdev.dll", SetLastError = true)]
    private static extern bool DiInstallDevice(IntPtr parent, IntPtr set, ref DeviceInfo device, IntPtr driver, uint flags, out bool reboot);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out uint size, string filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string filter, [Out] char[] ids, uint size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint length, in Guid type, string instanceId, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern int CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint node, StringBuilder id, int length, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_Registry_PropertyW(uint node, uint key, out uint type, StringBuilder value, ref uint size, uint flags);
}
