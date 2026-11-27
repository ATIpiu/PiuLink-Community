using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace PiuLink.Host.Services;

// The capture endpoint is installed independently of a stream. Only its default roles are leased.
internal sealed record SessionMicrophoneRoute(string Endpoint, Dictionary<Role,string> Original)
{
    private static readonly Role[] Roles = [Role.Console, Role.Multimedia, Role.Communications];
    // Windows may promote both new endpoints on first installation. Snapshot render and capture
    // in the interactive user's session and restore only roles promoted to our new device.
    internal static Dictionary<(DataFlow Flow, Role Role), string> CaptureInstallationDefaults()
    {
        using var devices = new MMDeviceEnumerator();
        var defaults = new Dictionary<(DataFlow, Role), string>();
        foreach (var flow in new[] { DataFlow.Capture, DataFlow.Render })
            foreach (var role in Roles) {
                try { using var d = devices.GetDefaultAudioEndpoint(flow, role); defaults.Add((flow, role), d.ID); }
                catch (COMException e) when (e.HResult == unchecked((int)0x80070490)) { /* No default yet. */ }
            }
        return defaults;
    }

    internal static void RestoreInstallationDefaults(Dictionary<(DataFlow Flow, Role Role), string> original)
    {
        using var devices = new MMDeviceEnumerator();
        var current = CaptureInstallationDefaults();
        var policy = CreatePolicy();
        try {
            foreach (var (role, old) in original) {
                if (!current.TryGetValue(role, out var now) || old == now || !VirtualMicrophoneDevice.OwnsEndpoint(now)) continue;
                try {
                    using var device = devices.GetDevice(old);
                    if (device.State == DeviceState.Active) Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(old, role.Role));
                } catch (COMException e) when (e.HResult == unchecked((int)0x80070490)) { /* Unplugged during installation. */ }
            }
        } finally { Marshal.ReleaseComObject(policy); }
    }

    private static IPolicyConfig CreatePolicy() => (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(
        new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"), true)!)!;
    internal static SessionMicrophoneRoute? Capture()
    {
        using var devices = new MMDeviceEnumerator();
        if (RemoteMicrophoneOutput.FindEndpoints(devices) is not { } pair) return null;
        var original = new Dictionary<Role,string>();
        foreach (var role in Roles) {
            try {
                using var endpoint = devices.GetDefaultAudioEndpoint(DataFlow.Capture, role);
                original.Add(role, endpoint.ID);
            } catch (COMException e) when (e.HResult == unchecked((int)0x80070490)) { /* No default yet. */ }
        }
        return new(pair.Capture, original);
    }

    internal void Switch(bool restore)
    {
        using var devices = new MMDeviceEnumerator();
        var policy = CreatePolicy();
        try {
            // Snapshot the roles before writing. Windows may couple Console and Multimedia.
            var eligible = new List<Role>();
            foreach (var (role, old) in Original) {
                if (old == Endpoint) continue;
                try {
                    using var current = devices.GetDefaultAudioEndpoint(DataFlow.Capture, role);
                    if (current.ID == (restore ? Endpoint : old)) eligible.Add(role);
                } catch (COMException e) when (e.HResult == unchecked((int)0x80070490)) {
                    // If the virtual mic disappeared before restore, still attempt to restore the original role.
                    if (restore) eligible.Add(role);
                }
            }
            foreach (var role in eligible) {
                string target = restore ? Original[role] : Endpoint;
                if (restore) {
                    try {
                        using var previous = devices.GetDevice(target);
                        if (previous.State != DeviceState.Active) continue;
                    } catch (COMException) { continue; } // Original hardware was unplugged.
                    try { Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(target, role)); }
                    catch (COMException) { /* Continue restoring remaining roles. */ }
                } else {
                    Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(target, role));
                }
            }
        } finally { Marshal.ReleaseComObject(policy); }
    }

    // Same PolicyConfig interface used by the bundled Sunshine Windows audio backend.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        void GetMixFormat(); void GetDeviceFormat(); void ResetDeviceFormat(); void SetDeviceFormat();
        void GetProcessingPeriod(); void SetProcessingPeriod(); void GetShareMode(); void SetShareMode();
        void GetPropertyValue(); void SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, Role role);
    }
}
