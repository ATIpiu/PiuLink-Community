using System.Runtime.InteropServices;

namespace PiuLink.Client.Core.Streaming;

public sealed class PiuLinkNativeBridge : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StartDelegate(IntPtr hwnd, [MarshalAs(UnmanagedType.LPUTF8Str)] string address, [MarshalAs(UnmanagedType.LPUTF8Str)] string appVersion, [MarshalAs(UnmanagedType.LPUTF8Str)] string gfeVersion, [MarshalAs(UnmanagedType.LPUTF8Str)] string sessionUrl, int codecs, int videoFormats, int width, int height, int fps, int bitrate, int absoluteMouse, int autoBitrate, int contentAdaptive, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string iv);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void StopDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetManagedInputDelegate(int enabled);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IntDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong FramesDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int MousePositionDelegate(short x, short y, short width, short height);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int MouseMoveDelegate(short x, short y);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int MouseButtonDelegate(int action, int button);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ScrollDelegate(short amount);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int KeyboardDelegate(short key, int action, byte modifiers);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ControllerDelegate(int flags, byte leftTrigger, byte rightTrigger, short leftX, short leftY, short rightX, short rightY);
    private readonly IntPtr _library; private readonly StartDelegate _start; private readonly StopDelegate _stop; private readonly SetManagedInputDelegate _setManagedInput;
    private readonly IntDelegate _state, _error, _currentBitrate, _adaptiveAck; private readonly FramesDelegate _frames, _submittedVideoUnits, _decodedFrames, _inputEvents;
    private readonly MousePositionDelegate _mousePosition; private readonly MouseMoveDelegate _mouseMove; private readonly MouseButtonDelegate _mouseButton;
    private readonly ScrollDelegate _scroll, _hscroll; private readonly KeyboardDelegate _keyboard; private readonly ControllerDelegate _controller;

    public PiuLinkNativeBridge(string path)
    {
        _library=NativeLibrary.Load(path); _start=Load<StartDelegate>("PiuStart"); _stop=Load<StopDelegate>("PiuStop"); _setManagedInput=Load<SetManagedInputDelegate>("PiuSetManagedInput");
        _state=Load<IntDelegate>("PiuState"); _error=Load<IntDelegate>("PiuLastError"); _frames=Load<FramesDelegate>("PiuRenderedFrames"); _submittedVideoUnits=Load<FramesDelegate>("PiuSubmittedVideoUnits"); _decodedFrames=Load<FramesDelegate>("PiuDecodedFrames"); _inputEvents=Load<FramesDelegate>("PiuForwardedInputEvents");
        _currentBitrate=Load<IntDelegate>("PiuCurrentBitrate"); _adaptiveAck=Load<IntDelegate>("PiuAdaptiveAck"); _mousePosition=Load<MousePositionDelegate>("PiuMousePosition");
        _mouseMove=Load<MouseMoveDelegate>("PiuMouseMove"); _mouseButton=Load<MouseButtonDelegate>("PiuMouseButton"); _scroll=Load<ScrollDelegate>("PiuScroll");
        _hscroll=Load<ScrollDelegate>("PiuHScroll"); _keyboard=Load<KeyboardDelegate>("PiuKeyboard"); _controller=Load<ControllerDelegate>("PiuController");
    }
    private T Load<T>(string name) where T:Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library,name));
    public int Start(IntPtr hwnd, GameStreamLaunch launch, int videoFormats, int width, int height, int fps, int bitrate, bool absoluteMouse, bool autoBitrate, bool contentAdaptive) => _start(hwnd,launch.Address,launch.AppVersion,launch.GfeVersion??"",launch.SessionUrl??"",launch.CodecSupport,videoFormats,width,height,fps,bitrate,absoluteMouse?1:0,autoBitrate?1:0,contentAdaptive?1:0,Convert.ToHexString(launch.RemoteInputKey),Convert.ToHexString(launch.RemoteInputIv));
    public void Stop()=>_stop(); public void SetManagedInput(bool enabled)=>_setManagedInput(enabled?1:0);
    public int State=>_state(); public int LastError=>_error(); public ulong RenderedFrames=>_frames(); public ulong SubmittedVideoUnits=>_submittedVideoUnits(); public ulong DecodedFrames=>_decodedFrames(); public ulong ForwardedInputEvents=>_inputEvents(); public int CurrentBitrate=>_currentBitrate(); public int AdaptiveAck=>_adaptiveAck();
    public int MousePosition(short x,short y,short width,short height)=>_mousePosition(x,y,width,height); public int MouseMove(short x,short y)=>_mouseMove(x,y);
    public int MouseButton(int action,int button)=>_mouseButton(action,button); public int Scroll(short amount)=>_scroll(amount); public int HScroll(short amount)=>_hscroll(amount);
    public int Keyboard(short key,int action,byte modifiers)=>_keyboard(key,action,modifiers);
    public int Controller(int flags,byte leftTrigger,byte rightTrigger,short leftX,short leftY,short rightX,short rightY)=>_controller(flags,leftTrigger,rightTrigger,leftX,leftY,rightX,rightY);
    public void Dispose()=>NativeLibrary.Free(_library);
}
