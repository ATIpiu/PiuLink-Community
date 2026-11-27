using PiuLink.Client.Core.Models;
using PiuLink.Client.Core.Networking;
using PiuLink.Client.Core.Streaming;

if (args.Length > 0 && args[0] == "discover") {
    foreach (var host in await new LanHostDiscoveryService().DiscoverAsync()) Console.WriteLine($"{host.Name}\t{host.Address}");
    return 0;
}
if (args.Length > 1 && args[0] == "mac") {
    Console.WriteLine(await LanMacAddressResolver.ResolveAsync(args[1]) ?? "MAC_UNAVAILABLE");
    return 0;
}
if (args.Length > 1 && args[0] == "wake") {
    var mac = args.Length > 2 ? args[2] : await LanMacAddressResolver.ResolveAsync(args[1]);
    if (mac is null) throw new InvalidOperationException("Host MAC address is unavailable.");
    await new WakeOnLanService().SendAsync(HostEndpoint.Parse(args[1]), mac);
    Console.WriteLine($"WOL_SENT {mac}");
    return 0;
}
if (args.Length > 1 && args[0] == "sshinfo") {
    var info = await new HostSshInfoClient().GetAsync(HostEndpoint.Parse(args[1]));
    Console.WriteLine($"SSH={info.Username}@{args[1]}:{info.Port} PLATFORM={info.Platform}");
    return 0;
}
if (args.Length < 2) throw new ArgumentException("Usage: discover | pair|apps|stream|quit|dllrun HOST [APP] [--relative] [--hdr]");
var endpoint = HostEndpoint.Parse(args[1]);
var identity = GameStreamIdentity.LoadOrCreate();
var client = new GameStreamHttpClient(endpoint, identity);
if (args[0] == "inventory") {
    using var companion = new HostCompanionClient();
    var inventory = await companion.GetInventoryAsync(endpoint, CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(inventory,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
} else if (args[0] == "pair") {
    var pin = args.Length > 2 ? args[2] : System.Security.Cryptography.RandomNumberGenerator.GetInt32(10000).ToString("D4");
    Console.WriteLine("PAIRING_STARTED");
    await client.PairAsync(pin, token => {
        Console.WriteLine($"请在 Host 的配对界面输入 {pin}");
        return Task.CompletedTask;
    }, CancellationToken.None);
    Console.WriteLine("PAIRING_OK");
} else if (args[0] == "apps") {
    foreach (var app in await client.GetAppsAsync(CancellationToken.None)) Console.WriteLine($"{app.Id}\t{app.Name}");
} else if (args[0] == "rawapps") {
    Console.WriteLine(await client.GetRawAppListAsync(CancellationToken.None));
} else if (args[0] == "stream") {
    var launch = await client.LaunchDesktopAsync(1920, 1080, 60, CancellationToken.None);
    Console.WriteLine(string.Join("|", launch.Address, launch.AppVersion, launch.GfeVersion, launch.SessionUrl, launch.CodecSupport,
        Convert.ToHexString(launch.RemoteInputKey), Convert.ToHexString(launch.RemoteInputIv)));
} else if (args[0] == "quit") {
    Console.WriteLine(await client.QuitAppAsync(CancellationToken.None) ? "QUIT_OK" : "QUIT_REJECTED");
} else if (args[0] == "dllrun") {
    var appName = args.Length > 2 ? args[2] : "Desktop";
    var hdr = args.Contains("--hdr", StringComparer.OrdinalIgnoreCase);
    var streamWidth = args.Contains("--720p", StringComparer.OrdinalIgnoreCase) ? 1280 : 1920;
    var streamHeight = args.Contains("--720p", StringComparer.OrdinalIgnoreCase) ? 720 : 1080;
    var launch = await client.LaunchAppAsync(appName, streamWidth, streamHeight, 60, hdr, CancellationToken.None);
    var dll = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "windows-engine", "PiuLink-Native-0.1.0-x64", "PiuLink.Native.dll"));
    var hwnd = NativeWindow.CreateWindowEx(0, "STATIC", "PiuLink DLL E2E", 0x10CF0000, 100, 100, 1280, 720, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Unable to create native test window.");
    using var bridge = new PiuLinkNativeBridge(dll);
    var absoluteMouse = !args.Contains("--relative", StringComparer.OrdinalIgnoreCase);
    var formats = args.Contains("--h264", StringComparer.OrdinalIgnoreCase) ? 0x1 : hdr ? 0x3301 : 0x1101;
    var rc = bridge.Start(hwnd, launch, formats, streamWidth, streamHeight, 60, 20000, absoluteMouse, true, !args.Contains("--no-adaptive", StringComparer.OrdinalIgnoreCase));
    if (rc != 0) throw new InvalidOperationException($"DLL start failed: {rc}");
    bridge.SetManagedInput(true);
    var waitFailure = args.Contains("--wait-failure", StringComparer.OrdinalIgnoreCase);
    var benchmark = args.Contains("--benchmark", StringComparer.OrdinalIgnoreCase);
    var deadline = DateTime.UtcNow.AddSeconds(waitFailure ? 55 : 25); var injected = false; var readyReported = false; DateTime? injectedAt = null;
    ulong benchmarkStartFrames=0, benchmarkStartSubmitted=0, benchmarkStartDecoded=0; var benchmarkStartCpu=TimeSpan.Zero; DateTime? benchmarkAt=null; var lastVisualPulse=-1; var testProcess=System.Diagnostics.Process.GetCurrentProcess();
    while (DateTime.UtcNow < deadline && bridge.State is not (3 or 4)) {
        while (NativeWindow.PeekMessage(out var message, IntPtr.Zero, 0, 0, 1)) { NativeWindow.TranslateMessage(ref message); NativeWindow.DispatchMessage(ref message); }
        if (!injected && bridge.RenderedFrames >= 30) {
            if (absoluteMouse) bridge.MousePosition(640,360,1280,720); else bridge.MouseMove(12,-7);
            bridge.MouseButton(7,1); bridge.MouseButton(8,1);
            NativeWindow.PostMessage(hwnd,0x0201,IntPtr.Zero,new IntPtr((360<<16)|640)); NativeWindow.PostMessage(hwnd,0x0202,IntPtr.Zero,new IntPtr((360<<16)|640));
            bridge.Scroll(120); bridge.HScroll(120);
            bridge.Keyboard(0x41,3,0); bridge.Keyboard(0x41,4,0);
            bridge.Controller(0x1000, 0, 0, 12000, 0, 0, 0);
            injected = true; injectedAt = DateTime.UtcNow;
            if(benchmark){benchmarkStartFrames=bridge.RenderedFrames;benchmarkStartSubmitted=bridge.SubmittedVideoUnits;benchmarkStartDecoded=bridge.DecodedFrames;benchmarkStartCpu=testProcess.TotalProcessorTime;benchmarkAt=DateTime.UtcNow;}
        }
        if(benchmark && benchmarkAt is not null){var elapsed=(DateTime.UtcNow-benchmarkAt.Value).TotalSeconds;bridge.MousePosition((short)(640+Math.Sin(elapsed*20)*300),(short)(360+Math.Cos(elapsed*20)*180),1280,720);var visualPulse=(int)(elapsed*8);if(args.Contains("--visual-pulse",StringComparer.OrdinalIgnoreCase)&&visualPulse!=lastVisualPulse){lastVisualPulse=visualPulse;bridge.Keyboard(0x5B,3,0);bridge.Keyboard(0x5B,4,0);}if(elapsed>=15)break;}
        if (injected && bridge.ForwardedInputEvents >= 10 && !readyReported) {
            Console.WriteLine("STREAM_READY"); Console.Out.Flush(); readyReported = true;
        }
        if (!benchmark && !waitFailure && injected && bridge.ForwardedInputEvents >= 10 &&
            (bridge.AdaptiveAck >= 0 || DateTime.UtcNow - injectedAt >= TimeSpan.FromSeconds(7))) break;
        await Task.Delay(5);
    }
    Console.WriteLine($"APP={appName} HDR={hdr} MOUSE={(absoluteMouse?"absolute":"relative")} STATE={bridge.State} FRAMES={bridge.RenderedFrames} INPUT={bridge.ForwardedInputEvents} BITRATE={bridge.CurrentBitrate} ADAPTIVE_ACK={bridge.AdaptiveAck} ERROR={bridge.LastError}");
    if(benchmarkAt is not null){var elapsed=(DateTime.UtcNow-benchmarkAt.Value).TotalSeconds;var cpu=(testProcess.TotalProcessorTime-benchmarkStartCpu).TotalSeconds/elapsed/Environment.ProcessorCount*100;Console.WriteLine($"BENCHMARK_SECONDS={elapsed:F1} SUBMIT_FPS={(bridge.SubmittedVideoUnits-benchmarkStartSubmitted)/elapsed:F2} DECODE_FPS={(bridge.DecodedFrames-benchmarkStartDecoded)/elapsed:F2} RENDER_FPS={(bridge.RenderedFrames-benchmarkStartFrames)/elapsed:F2} INPUT_HZ={(bridge.ForwardedInputEvents-10)/elapsed:F1} CPU={cpu:F1}% WORKING_SET_MB={testProcess.WorkingSet64/1048576.0:F0}");}
    var finalState = bridge.State;
    bridge.Stop(); NativeWindow.DestroyWindow(hwnd);
    if (!injected || bridge.RenderedFrames < 30 || bridge.ForwardedInputEvents < 10 || (waitFailure && finalState != 4)) return 1;
}
return 0;

internal static partial class NativeWindow
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct Message { public IntPtr Hwnd; public uint Value; public UIntPtr WParam; public IntPtr LParam; public uint Time; public int X; public int Y; }
    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)] internal static partial IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [System.Runtime.InteropServices.LibraryImport("user32.dll")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] internal static partial bool DestroyWindow(IntPtr hwnd);
    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "PostMessageW")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] internal static partial bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [System.Runtime.InteropServices.LibraryImport("user32.dll")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] internal static partial bool SetForegroundWindow(IntPtr hwnd);
    [System.Runtime.InteropServices.LibraryImport("user32.dll")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] internal static partial bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "mouse_event")] internal static partial void MouseEvent(uint flags, int dx, int dy, int data, UIntPtr extraInfo);
    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "keybd_event")] internal static partial void KeyEvent(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "PeekMessageW")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] internal static partial bool PeekMessage(out Message message, IntPtr hwnd, uint min, uint max, uint remove);
    [System.Runtime.InteropServices.LibraryImport("user32.dll")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] internal static partial bool TranslateMessage(ref Message message);
    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")] internal static partial IntPtr DispatchMessage(ref Message message);
}
