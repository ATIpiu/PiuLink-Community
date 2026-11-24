using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PiuLink.Client;

// Win32/WPF port of Moonlight Qt input.cpp, mouse.cpp, and keyboard.cpp (f786e94c).
public sealed class EmbeddedStreamSurface : HwndHost
{
    private const int WsChild=0x40000000, WsVisible=0x10000000, WsClipChildren=0x02000000, WsTabStop=0x00010000;
    private const int WmInput=0x00ff, WmMouseMove=0x0200, WmLButtonDown=0x0201, WmLButtonUp=0x0202, WmRButtonDown=0x0204, WmRButtonUp=0x0205,
        WmMButtonDown=0x0207, WmMButtonUp=0x0208, WmXButtonDown=0x020b, WmXButtonUp=0x020c, WmMouseWheel=0x020a, WmMouseHWheel=0x020e,
        WmKeyDown=0x0100, WmKeyUp=0x0101, WmSysKeyDown=0x0104, WmSysKeyUp=0x0105, WmKillFocus=0x0008;
    private const int RidevInputSink=0x100, RimTypeMouse=0, RidInput=0x10000003;
    private readonly HashSet<short> _keysDown=[];
    private int _buttonsDown;
    private bool _captureActive;
    private int _streamWidth=1920, _streamHeight=1080;

    public IntPtr NativeHandle { get; private set; }
    public event Action<short,short,short,short>? MousePosition;
    public event Action<short,short>? MouseDelta;
    public event Action<int,int>? MouseButton;
    public event Action<short>? StreamMouseWheel;
    public event Action<short>? StreamMouseHWheel;
    public event Action<short,int,byte>? Keyboard;
    public bool AbsoluteMouseMode { get; set; }=true;

    public void Configure(int width,int height,bool absoluteMouse) { _streamWidth=Math.Max(1,width); _streamHeight=Math.Max(1,height); AbsoluteMouseMode=absoluteMouse; }
    public void SetInputCapture(bool active)
    {
        if (_captureActive==active) return;
        _captureActive=active;
        if (active && NativeHandle!=IntPtr.Zero) { SetFocus(NativeHandle); if (!AbsoluteMouseMode) { SetCapture(NativeHandle); ShowCursor(false); } }
        else { ReleaseAll(); ReleaseCapture(); if (!AbsoluteMouseMode) ShowCursor(true); }
    }

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        NativeHandle=CreateWindowEx(0,"STATIC","",WsChild|WsVisible|WsClipChildren|WsTabStop,0,0,Math.Max(1,(int)ActualWidth),Math.Max(1,(int)ActualHeight),parent.Handle,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        var device=new RawInputDevice { UsagePage=1, Usage=2, Flags=RidevInputSink, Target=NativeHandle };
        RegisterRawInputDevices([device],1,(uint)Marshal.SizeOf<RawInputDevice>());
        return new HandleRef(this,NativeHandle);
    }

    protected override IntPtr WndProc(IntPtr hwnd,int msg,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        var x=unchecked((short)((long)lParam&0xffff)); var y=unchecked((short)(((long)lParam>>16)&0xffff));
        switch(msg)
        {
            case WmMouseMove when AbsoluteMouseMode && _captureActive:
                while (PeekMessage(out var pending,hwnd,WmMouseMove,WmMouseMove,1))
                {
                    x=unchecked((short)((long)pending.LParam&0xffff));
                    y=unchecked((short)(((long)pending.LParam>>16)&0xffff));
                }
                var region=VideoRegion(hwnd);
                if (x>=region.Left && x<region.Right && y>=region.Top && y<region.Bottom)
                    MousePosition?.Invoke((short)(x-region.Left),(short)(y-region.Top),(short)(region.Right-region.Left),(short)(region.Bottom-region.Top));
                break;
            case WmInput when !AbsoluteMouseMode && _captureActive: ReadRawMouse(lParam); handled=true; break;
            case WmLButtonDown: Button(hwnd,1,true); handled=true; break;
            case WmLButtonUp: Button(hwnd,1,false); handled=true; break;
            case WmMButtonDown: Button(hwnd,2,true); handled=true; break;
            case WmMButtonUp: Button(hwnd,2,false); handled=true; break;
            case WmRButtonDown: Button(hwnd,3,true); handled=true; break;
            case WmRButtonUp: Button(hwnd,3,false); handled=true; break;
            case WmXButtonDown: Button(hwnd,HiWord(wParam)==1?4:5,true); handled=true; break;
            case WmXButtonUp: Button(hwnd,HiWord(wParam)==1?4:5,false); handled=true; break;
            case WmMouseWheel when _captureActive: StreamMouseWheel?.Invoke(unchecked((short)HiWord(wParam))); handled=true; break;
            case WmMouseHWheel when _captureActive: StreamMouseHWheel?.Invoke(unchecked((short)HiWord(wParam))); handled=true; break;
            case WmKeyDown: case WmSysKeyDown:
                var pressed=(short)wParam;
                if ((Modifiers()&7)==7)
                {
                    if (pressed==0x5a) { SetInputCapture(!_captureActive); handled=true; break; }
                    if (pressed==0x4d) { var active=_captureActive; SetInputCapture(false); AbsoluteMouseMode=!AbsoluteMouseMode; if(active) SetInputCapture(true); handled=true; break; }
                }
                if (_captureActive) { _keysDown.Add(pressed); Keyboard?.Invoke(pressed,3,Modifiers()); handled=true; } break;
            case WmKeyUp: case WmSysKeyUp:
                if (_keysDown.Remove((short)wParam)) Keyboard?.Invoke((short)wParam,4,Modifiers()); handled=true; break;
            case WmKillFocus: SetInputCapture(false); break;
        }
        return base.WndProc(hwnd,msg,wParam,lParam,ref handled);
    }

    private void Button(IntPtr hwnd,int button,bool down)
    {
        if (!_captureActive) { if (!down) SetInputCapture(true); return; }
        var mask=1<<(button-1);
        if (down) { _buttonsDown|=mask; SetFocus(hwnd); SetCapture(hwnd); MouseButton?.Invoke(7,button); }
        else if ((_buttonsDown&mask)!=0)
        {
            _buttonsDown&=~mask; MouseButton?.Invoke(8,button);
            // Match Moonlight Qt: absolute mode captures only during a drag.
            // Releasing the final button must let the pointer reach local window controls.
            if (_buttonsDown==0 && AbsoluteMouseMode) ReleaseCapture();
        }
    }
    private void ReleaseAll()
    {
        foreach(var key in _keysDown.ToArray()) Keyboard?.Invoke(key,4,Modifiers()); _keysDown.Clear();
        for(var button=1;button<=5;button++) if ((_buttonsDown&(1<<(button-1)))!=0) MouseButton?.Invoke(8,button);
        _buttonsDown=0;
    }
    private void ReadRawMouse(IntPtr handle)
    {
        uint size=0, header=(uint)Marshal.SizeOf<RawInputHeader>();
        if (GetRawInputData(handle,RidInput,IntPtr.Zero,ref size,header)!=0 || size==0) return;
        var memory=Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(handle,RidInput,memory,ref size,header)!=size) return;
            var raw=Marshal.PtrToStructure<RawInput>(memory);
            if (raw.Header.Type==RimTypeMouse && (raw.Mouse.LastX!=0 || raw.Mouse.LastY!=0))
                MouseDelta?.Invoke((short)Math.Clamp(raw.Mouse.LastX,short.MinValue,short.MaxValue),(short)Math.Clamp(raw.Mouse.LastY,short.MinValue,short.MaxValue));
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    private Rect VideoRegion(IntPtr hwnd)
    {
        GetClientRect(hwnd,out var client); var cw=Math.Max(1,client.Right); var ch=Math.Max(1,client.Bottom);
        var scale=Math.Min((double)cw/_streamWidth,(double)ch/_streamHeight);
        var w=Math.Max(1,(int)Math.Round(_streamWidth*scale)); var h=Math.Max(1,(int)Math.Round(_streamHeight*scale));
        var left=(cw-w)/2; var top=(ch-h)/2; return new Rect { Left=left,Top=top,Right=left+w,Bottom=top+h };
    }
    private static int HiWord(IntPtr value)=>unchecked((ushort)(((long)value>>16)&0xffff));
    private static byte Modifiers()=>(byte)((Down(0x10)?1:0)|(Down(0x11)?2:0)|(Down(0x12)?4:0)|((Down(0x5b)||Down(0x5c))?8:0));
    private static bool Down(int key)=>(GetKeyState(key)&0x8000)!=0;

    protected override void DestroyWindowCore(HandleRef hwnd) { SetInputCapture(false); DestroyWindow(hwnd.Handle); NativeHandle=IntPtr.Zero; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct RawInputDevice { public ushort UsagePage,Usage; public int Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] private struct RawInputHeader { public int Type,Size; public IntPtr Device,WParam; }
    [StructLayout(LayoutKind.Explicit)] private struct RawMouse { [FieldOffset(0)] public ushort Flags; [FieldOffset(12)] public int LastX; [FieldOffset(16)] public int LastY; }
    [StructLayout(LayoutKind.Sequential)] private struct RawInput { public RawInputHeader Header; public RawMouse Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public IntPtr Hwnd; public uint Value; public IntPtr WParam,LParam; public uint Time; public int X,Y; }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr CreateWindowEx(int exStyle,string className,string name,int style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern int ShowCursor(bool show);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd,out Rect rect);
    [DllImport("user32.dll")] private static extern bool RegisterRawInputDevices(RawInputDevice[] devices,uint count,uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr input,uint command,IntPtr data,ref uint size,uint headerSize);
    [DllImport("user32.dll",EntryPoint="PeekMessageW")] private static extern bool PeekMessage(out Message message,IntPtr hwnd,uint min,uint max,uint remove);
}
