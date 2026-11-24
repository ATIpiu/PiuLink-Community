using PiuLink.Client.Core.Models;
using PiuLink.Client.Core.Networking;

namespace PiuLink.Client.Core.Streaming;

public sealed record MoonlightCommandResult(int ExitCode, string StandardOutput, string StandardError) { public bool IsSuccess => ExitCode == 0; }

public interface IStreamSession : IAsyncDisposable
{
    int ExitCode { get; }
    void Stop();
    Task WaitForExitAsync(CancellationToken token = default);
    int MousePosition(short x, short y, short width, short height);
    int MouseMove(short x, short y);
    int MouseButton(int action, int button);
    int Scroll(short amount);
    int HScroll(short amount);
    int Keyboard(short key, int action, byte modifiers);
}

public sealed class PiuLinkStreamSession : IStreamSession
{
    private readonly PiuLinkNativeBridge _bridge; private readonly CancellationTokenSource _monitor = new(); private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously); private readonly Task _monitorTask;
    internal PiuLinkStreamSession(PiuLinkNativeBridge bridge) { _bridge=bridge; _monitorTask=MonitorAsync(); }
    public PiuLinkEngineChannel? Channel => null; public int ExitCode { get; private set; }
    public ulong RenderedFrames => _bridge.RenderedFrames; public ulong ForwardedInputEvents => _bridge.ForwardedInputEvents; public int CurrentBitrateKbps=>_bridge.CurrentBitrate; public int AdaptiveAck=>_bridge.AdaptiveAck;
    public void Stop() { _bridge.Stop(); }
    public Task WaitForExitAsync(CancellationToken token=default) => _completion.Task.WaitAsync(token);
    private async Task MonitorAsync() { try { while(!_monitor.IsCancellationRequested){var state=_bridge.State;if(state is 3 or 4){ExitCode=state==3?0:_bridge.LastError;_completion.TrySetResult(ExitCode);return;}await Task.Delay(100,_monitor.Token);} } catch(OperationCanceledException){} }
    public void SetManagedInput(bool enabled)=>_bridge.SetManagedInput(enabled); public int MousePosition(short x,short y,short w,short h)=>_bridge.MousePosition(x,y,w,h); public int MouseMove(short x,short y)=>_bridge.MouseMove(x,y); public int MouseButton(int action,int button)=>_bridge.MouseButton(action,button); public int Scroll(short amount)=>_bridge.Scroll(amount); public int HScroll(short amount)=>_bridge.HScroll(amount); public int Keyboard(short key,int action,byte modifiers)=>_bridge.Keyboard(key,action,modifiers);
    public async ValueTask DisposeAsync(){_monitor.Cancel();try{await _monitorTask.ConfigureAwait(false);}catch{} _bridge.Stop();_bridge.Dispose();_completion.TrySetResult(ExitCode);_monitor.Dispose();}
}

public sealed class MoonlightProcessService
{
    private readonly GameStreamIdentity _identity=GameStreamIdentity.LoadOrCreate();
    public MoonlightProcessService(string libraryPath){ExecutablePath=Path.GetFullPath(libraryPath);if(!File.Exists(ExecutablePath))throw new FileNotFoundException("PiuLink native library was not found.",ExecutablePath);}
    public string ExecutablePath{get;}
    public async Task<MoonlightCommandResult> PairAsync(HostEndpoint endpoint,string pin,TimeSpan timeout,Func<CancellationToken,Task> approvePin,CancellationToken cancellationToken=default){using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);linked.CancelAfter(timeout);try{await new GameStreamHttpClient(endpoint,_identity).PairAsync(pin,approvePin,linked.Token);return new(0,"Pairing completed successfully","");}catch(Exception error)when(error is not OperationCanceledException){return new(1,"",error.Message);}}
    public async Task<MoonlightCommandResult> ListAppsAsync(HostEndpoint endpoint,TimeSpan timeout,CancellationToken cancellationToken=default){using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);linked.CancelAfter(timeout);try{var apps=await new GameStreamHttpClient(endpoint,_identity).GetAppsAsync(linked.Token);return new(0,string.Join(Environment.NewLine,apps.Select(a=>a.Name)),"");}catch(Exception error)when(error is not OperationCanceledException){return new(1,"",error.Message);}}
    public async Task<PiuLinkStreamSession> LaunchStreamAsync(IntPtr target,HostEndpoint endpoint,string appName,StreamProfile profile,TimeSpan timeout,CancellationToken token=default){if(target==IntPtr.Zero)throw new InvalidOperationException("Stream surface is not ready.");var size=profile.Resolution.Split('x');var width=int.Parse(size[0]);var height=int.Parse(size[1]);var launch=await new GameStreamHttpClient(endpoint,_identity).LaunchAppAsync(appName,width,height,profile.FramesPerSecond,profile.EnableHdr,token);var formats=VideoFormats(endpoint,profile);var bridge=new PiuLinkNativeBridge(ExecutablePath);try{var rc=bridge.Start(target,launch,formats,width,height,profile.FramesPerSecond,profile.BitrateKbps,profile.DesktopMouseMode,false,false);if(rc!=0)throw new InvalidOperationException($"Native stream start failed ({rc}).");var started=DateTime.UtcNow;while(bridge.State==1&&DateTime.UtcNow-started<timeout){await Task.Delay(100,token);}if(bridge.State==4)throw new IOException($"Native stream failed ({bridge.LastError}).");if(bridge.State!=2)throw new TimeoutException($"Timed out waiting for native stream connection (state={bridge.State}, error={bridge.LastError}).");return new(bridge);}catch{bridge.Stop();bridge.Dispose();throw;}}
    private static int VideoFormats(HostEndpoint endpoint,StreamProfile profile){if(profile.VideoCodec=="auto"){var tenBit=profile.EnableHdr?0x2200:0;return AutoCodecPolicy.Decide(endpoint,profile).Mode==AutoCodecMode.Efficiency?0x1101|tenBit:0x0101|(profile.EnableHdr?0x0200:0);}return profile.VideoCodec switch{"AV1"=>profile.EnableHdr?0x3000:0x1000,"HEVC"=>profile.EnableHdr?0x0300:0x0100,_=>0x0001};}
    public bool IsPiuLinkEngine=>Path.GetFileName(ExecutablePath).Equals("PiuLink.Native.dll",StringComparison.OrdinalIgnoreCase);
    public static IReadOnlyList<string> ParseAppList(string output)=>output.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
}
