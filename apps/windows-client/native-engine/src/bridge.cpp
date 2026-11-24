#include <Limelight.h>
#include <SDL.h>
#include <opus_multistream.h>
extern "C" {
#include <libavcodec/avcodec.h>
#include <libswscale/swscale.h>
#include <libavutil/hwcontext.h>
}
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
extern "C" {
#include <libavutil/hwcontext_d3d11va.h>
}
#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cstdarg>
#include <cstdio>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_set>
#include <vector>

#define API extern "C" __declspec(dllexport)
extern "C" IMAGE_DOS_HEADER __ImageBase;
struct StartConfig {
    HWND target{}; std::string address, appVersion, gfeVersion, sessionUrl;
    int codecSupport{}, videoFormats{}, width{}, height{}, fps{}, bitrate{}; bool absoluteMouse{}, autoBitrate{}, contentAdaptive{}; unsigned char key[16]{}, iv[16]{};
};
static StartConfig cfg;
static std::thread worker;
static std::atomic<int> state{0}, lastError{0}; // 0 idle, 1 connecting, 2 connected, 3 stopped, 4 failed
static std::atomic<unsigned long long> renderedFrames{0};
static std::atomic<unsigned long long> decodedFrames{0};
static std::atomic<unsigned long long> submittedVideoUnits{0};
static std::atomic<unsigned long long> forwardedInputEvents{0};
static std::atomic<int> currentBitrate{0};
static std::atomic<bool> stopRequested{false};
static std::atomic<bool> managedInput{false};
static int adaptiveAttempts; static Uint64 lastAdaptiveRequest;
static SDL_Window* sdlWindow; static SDL_Renderer* renderer; static SDL_Texture* texture; static int textureWidth, textureHeight; static WNDPROC previousWindowProc;
static SDL_GameController* gamepad;
// Ported from Moonlight Qt app/streaming/input/{input,mouse,keyboard}.cpp (f786e94c).
// Keep capture, video-region, coalescing, and stuck-key semantics aligned with upstream.
static bool captureActive, mouseWasInVideoRegion, pendingButtonsOnLeave;
static bool capturedCursorHidden=true;
static std::unordered_set<short> keysDown;
static AVCodecContext* decoder; static AVFrame* pendingFrame; static AVBufferRef* hardwareDevice;
static std::thread decoderWorker; static std::mutex packetLock; static std::condition_variable packetReady;
static std::deque<std::vector<unsigned char>> packetQueue; static std::atomic<bool> decoderRunning{false};
static ID3D11Device* d3dDevice; static ID3D11DeviceContext* d3dContext; static IDXGISwapChain* d3dSwapChain;
static ID3D11VideoDevice* d3dVideoDevice; static ID3D11VideoContext* d3dVideoContext;
static ID3D11VideoProcessorEnumerator* d3dVideoEnumerator; static ID3D11VideoProcessor* d3dVideoProcessor;
static OpusMSDecoder* opusDecoder; static SDL_AudioDeviceID audioDevice; static int audioChannels;
static std::mutex frameLock; static std::condition_variable frameReady;

static void logLine(const char* format, ...) {
    va_list args; va_start(args, format); char line[2048]; vsnprintf(line,sizeof(line),format,args); va_end(args);
    OutputDebugStringA(line); OutputDebugStringA("\n");
    char base[MAX_PATH]{}; GetModuleFileNameA((HMODULE)&__ImageBase,base,MAX_PATH); char* slash=strrchr(base,'\\'); if(slash)*slash=0;
    std::string path=std::string(base)+"\\PiuLink.Native.log"; FILE* file=fopen(path.c_str(),"a"); if(file){fprintf(file,"%s\n",line);fclose(file);}
}
static std::vector<unsigned char> fromHex(const char* value) { std::vector<unsigned char> out; while(value&&*value&&value[1]){char p[3]{value[0],value[1],0};out.push_back((unsigned char)strtoul(p,nullptr,16));value+=2;}return out; }
template<class T> static void releaseCom(T*& value){if(value){value->Release();value=nullptr;}}
static void cleanupD3D(){releaseCom(d3dVideoProcessor);releaseCom(d3dVideoEnumerator);releaseCom(d3dVideoContext);releaseCom(d3dVideoDevice);releaseCom(d3dSwapChain);releaseCom(d3dContext);releaseCom(d3dDevice);}
static bool setupD3D(int width,int height){
    if(av_hwdevice_ctx_create(&hardwareDevice,AV_HWDEVICE_TYPE_D3D11VA,nullptr,nullptr,0)<0)return false;
    auto hw=(AVHWDeviceContext*)hardwareDevice->data;auto d3d=(AVD3D11VADeviceContext*)hw->hwctx;
    d3dDevice=d3d->device;d3dDevice->AddRef();d3dContext=d3d->device_context;d3dContext->AddRef();
    IDXGIDevice* dxgiDevice=nullptr;IDXGIAdapter* adapter=nullptr;IDXGIFactory* factory=nullptr;
    HRESULT hr=d3dDevice->QueryInterface(__uuidof(IDXGIDevice),(void**)&dxgiDevice);
    if(SUCCEEDED(hr))hr=dxgiDevice->GetAdapter(&adapter);
    if(SUCCEEDED(hr))hr=adapter->GetParent(__uuidof(IDXGIFactory),(void**)&factory);
    RECT client{};GetClientRect(cfg.target,&client);DXGI_SWAP_CHAIN_DESC desc{};desc.BufferCount=2;desc.BufferDesc.Width=std::max(1L,client.right);desc.BufferDesc.Height=std::max(1L,client.bottom);desc.BufferDesc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;desc.OutputWindow=cfg.target;desc.SampleDesc.Count=1;desc.Windowed=TRUE;desc.SwapEffect=DXGI_SWAP_EFFECT_DISCARD;
    if(SUCCEEDED(hr))hr=factory->CreateSwapChain(d3dDevice,&desc,&d3dSwapChain);
    if(SUCCEEDED(hr))hr=d3dDevice->QueryInterface(__uuidof(ID3D11VideoDevice),(void**)&d3dVideoDevice);
    if(SUCCEEDED(hr))hr=d3dContext->QueryInterface(__uuidof(ID3D11VideoContext),(void**)&d3dVideoContext);
    D3D11_VIDEO_PROCESSOR_CONTENT_DESC content{};content.InputFrameFormat=D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;content.InputWidth=width;content.InputHeight=height;content.OutputWidth=desc.BufferDesc.Width;content.OutputHeight=desc.BufferDesc.Height;content.Usage=D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;content.InputFrameRate={60,1};content.OutputFrameRate={60,1};
    if(SUCCEEDED(hr))hr=d3dVideoDevice->CreateVideoProcessorEnumerator(&content,&d3dVideoEnumerator);
    if(SUCCEEDED(hr))hr=d3dVideoDevice->CreateVideoProcessor(d3dVideoEnumerator,0,&d3dVideoProcessor);
    releaseCom(factory);releaseCom(adapter);releaseCom(dxgiDevice);
    if(FAILED(hr)){cleanupD3D();av_buffer_unref(&hardwareDevice);return false;}logLine("video renderer: D3D11VA zero-copy video processor");return true;
}
static AVPixelFormat selectPixelFormat(AVCodecContext*,const AVPixelFormat* formats){for(auto p=formats;*p!=AV_PIX_FMT_NONE;p++)if(*p==AV_PIX_FMT_D3D11)return *p;return formats[0];}
static void decoderLoop();
static int videoSetup(int format,int width,int height,int,void*,int){
    AVCodecID id=(format&0xF000)?AV_CODEC_ID_AV1:(format&0x0F00)?AV_CODEC_ID_HEVC:AV_CODEC_ID_H264;
    auto codec=avcodec_find_decoder(id); if(!codec)return -1; decoder=avcodec_alloc_context3(codec);
    if(decoder){decoder->width=width;decoder->height=height;decoder->flags|=AV_CODEC_FLAG_LOW_DELAY|AV_CODEC_FLAG_OUTPUT_CORRUPT;decoder->flags2|=AV_CODEC_FLAG2_SHOW_ALL;decoder->err_recognition=AV_EF_EXPLODE;}
    if(decoder&&setupD3D(width,height)){decoder->thread_count=1;decoder->hw_device_ctx=av_buffer_ref(hardwareDevice);decoder->get_format=selectPixelFormat;logLine("video decoder: FFmpeg D3D11VA");}
    else if(decoder){decoder->thread_count=std::min(16,SDL_GetCPUCount());decoder->thread_type=FF_THREAD_SLICE;logLine("video decoder: FFmpeg low-latency software fallback");}
    if(!decoder||avcodec_open2(decoder,codec,nullptr)<0)return -1;
    {std::lock_guard lock(packetLock);packetQueue.clear();}decoderRunning=true;decoderWorker=std::thread(decoderLoop);
    logLine("video setup %dx%d format=%x; asynchronous packet queue enabled",width,height,format); return 0;
}
static void noOp(){} static void videoCleanup(){decoderRunning=false;packetReady.notify_all();if(decoderWorker.joinable())decoderWorker.join();{std::lock_guard packetGuard(packetLock);packetQueue.clear();}{std::lock_guard lock(frameLock);av_frame_free(&pendingFrame);avcodec_free_context(&decoder);av_buffer_unref(&hardwareDevice);cleanupD3D();}}
static int submitVideo(PDECODE_UNIT unit){
    submittedVideoUnits++;
    std::vector<unsigned char> bytes; bytes.reserve(unit->fullLength+AV_INPUT_BUFFER_PADDING_SIZE); for(auto e=unit->bufferList;e;e=e->next)bytes.insert(bytes.end(),(unsigned char*)e->data,(unsigned char*)e->data+e->length);bytes.resize(unit->fullLength+AV_INPUT_BUFFER_PADDING_SIZE,0);
    {std::lock_guard lock(packetLock);if(packetQueue.size()>=12)packetQueue.pop_front();packetQueue.push_back(std::move(bytes));}packetReady.notify_one();return DR_OK;
}
static void decoderLoop(){
    auto frame=av_frame_alloc();
    while(decoderRunning){
        std::vector<unsigned char> bytes;{std::unique_lock lock(packetLock);packetReady.wait(lock,[]{return !packetQueue.empty()||!decoderRunning;});if(!decoderRunning&&packetQueue.empty())break;bytes=std::move(packetQueue.front());packetQueue.pop_front();}
        auto drain=[&](){bool received=false;while(avcodec_receive_frame(decoder,frame)==0){received=true;decodedFrames++;{std::lock_guard lock(frameLock);av_frame_free(&pendingFrame);pendingFrame=av_frame_clone(frame);}frameReady.notify_one();av_frame_unref(frame);}return received;};
        AVPacket packet{};packet.data=bytes.data();packet.size=(int)bytes.size()-AV_INPUT_BUFFER_PADDING_SIZE;int rc;while((rc=avcodec_send_packet(decoder,&packet))==AVERROR(EAGAIN))if(!drain())break;if(rc>=0)drain();else logLine("video packet decode failed: %d",rc);
    }
    av_frame_free(&frame);
}
static int audioInit(int,const POPUS_MULTISTREAM_CONFIGURATION opus,void*,int){
    int error; audioChannels=opus->channelCount; opusDecoder=opus_multistream_decoder_create(opus->sampleRate,opus->channelCount,opus->streams,opus->coupledStreams,opus->mapping,&error);if(!opusDecoder)return error;
    SDL_AudioSpec wanted{};wanted.freq=opus->sampleRate;wanted.format=AUDIO_S16SYS;wanted.channels=(Uint8)opus->channelCount;wanted.samples=1024;audioDevice=SDL_OpenAudioDevice(nullptr,0,&wanted,nullptr,0);return audioDevice?0:-1;
}
static void audioStart(){if(audioDevice)SDL_PauseAudioDevice(audioDevice,0);} static void audioStop(){if(audioDevice)SDL_PauseAudioDevice(audioDevice,1);}
static void audioCleanup(){if(audioDevice){SDL_CloseAudioDevice(audioDevice);audioDevice=0;}if(opusDecoder){opus_multistream_decoder_destroy(opusDecoder);opusDecoder=nullptr;}}
static void playAudio(char* data,int length){if(!opusDecoder||!audioDevice)return;short pcm[8*5760];int samples=opus_multistream_decode(opusDecoder,(unsigned char*)data,length,pcm,5760,0);if(samples>0)SDL_QueueAudio(audioDevice,pcm,samples*audioChannels*sizeof(short));}
static void stageStarting(int stage){logLine("stage starting: %s",LiGetStageName(stage));} static void stageComplete(int stage){logLine("stage complete: %s",LiGetStageName(stage));}
static void stageFailed(int stage,int error){lastError=error;state=4;logLine("stage failed: %s (%d)",LiGetStageName(stage),error);frameReady.notify_one();}
static void requestAdaptive(){
    adaptiveAttempts++;lastAdaptiveRequest=SDL_GetTicks64();
    auto rc=LiSendPiuLinkControl(3,cfg.contentAdaptive?1:0);
    logLine("content adaptive request attempt=%d enabled=%d result=%d",adaptiveAttempts,cfg.contentAdaptive?1:0,rc);
}
static void connectionStarted(){state=2;currentBitrate=cfg.bitrate;if(cfg.autoBitrate)LiSendPiuLinkControl(1,cfg.bitrate);if(cfg.absoluteMouse){auto cursorRc=LiSendPiuLinkControl(2,1);logLine("local cursor enabled; host cursor hide request=%d",cursorRc);}requestAdaptive();logLine("connection started; bitrate=%d adaptive=%d",cfg.bitrate,cfg.contentAdaptive?1:0);} static void connectionTerminated(int error){lastError=error;state=error?4:3;stopRequested=true;frameReady.notify_one();}
static void rumble(unsigned short controller,unsigned short low,unsigned short high){if(controller==0&&gamepad)SDL_GameControllerRumble(gamepad,low,high,500);} static void statusUpdate(int){} static void hdr(bool){} static void trigger(uint16_t,uint16_t,uint16_t){} static void motion(uint16_t,uint8_t,uint16_t){} static void led(uint16_t,uint8_t,uint8_t,uint8_t){}
static char keyboardModifiers(SDL_Keymod m){return (char)(((m&KMOD_SHIFT)?MODIFIER_SHIFT:0)|((m&KMOD_CTRL)?MODIFIER_CTRL:0)|((m&KMOD_ALT)?MODIFIER_ALT:0)|((m&KMOD_GUI)?MODIFIER_META:0));}
static int mouseButton(Uint8 button){switch(button){case SDL_BUTTON_LEFT:return BUTTON_LEFT;case SDL_BUTTON_RIGHT:return BUTTON_RIGHT;case SDL_BUTTON_MIDDLE:return BUTTON_MIDDLE;case SDL_BUTTON_X1:return BUTTON_X1;case SDL_BUTTON_X2:return BUTTON_X2;default:return 0;}}
static void recordInput(int rc){if(rc==0){auto count=++forwardedInputEvents;if(count==1)logLine("first embedded input forwarded to host");}}
static LRESULT CALLBACK managedWindowProc(HWND hwnd,UINT message,WPARAM wParam,LPARAM lParam){if(managedInput){int button=0,action=0;switch(message){case WM_LBUTTONDOWN:button=BUTTON_LEFT;action=BUTTON_ACTION_PRESS;break;case WM_LBUTTONUP:button=BUTTON_LEFT;action=BUTTON_ACTION_RELEASE;break;case WM_MBUTTONDOWN:button=BUTTON_MIDDLE;action=BUTTON_ACTION_PRESS;break;case WM_MBUTTONUP:button=BUTTON_MIDDLE;action=BUTTON_ACTION_RELEASE;break;case WM_RBUTTONDOWN:button=BUTTON_RIGHT;action=BUTTON_ACTION_PRESS;break;case WM_RBUTTONUP:button=BUTTON_RIGHT;action=BUTTON_ACTION_RELEASE;break;case WM_XBUTTONDOWN:button=HIWORD(wParam)==XBUTTON1?BUTTON_X1:BUTTON_X2;action=BUTTON_ACTION_PRESS;break;case WM_XBUTTONUP:button=HIWORD(wParam)==XBUTTON1?BUTTON_X1:BUTTON_X2;action=BUTTON_ACTION_RELEASE;break;}if(button){if(action==BUTTON_ACTION_PRESS)SetFocus(hwnd);recordInput(LiSendMouseButtonEvent(action,button));return 0;}}return previousWindowProc?CallWindowProcW(previousWindowProc,hwnd,message,wParam,lParam):DefWindowProcW(hwnd,message,wParam,lParam);}
static SDL_Rect videoRegion(){int w=1,h=1;SDL_GetWindowSize(sdlWindow,&w,&h);SDL_Rect r{0,0,w,h};int scaledH=(int)ceil((double)w*cfg.height/cfg.width),scaledW=(int)ceil((double)h*cfg.width/cfg.height);if(scaledH>h){r.x=(w-scaledW)/2;r.w=scaledW;}else{r.y=(h-scaledH)/2;r.h=scaledH;}return r;}
static bool inVideoRegion(int x,int y){auto r=videoRegion();return x>=r.x&&x<=r.x+r.w&&y>=r.y&&y<=r.y+r.h;}
static bool currentMouseInVideoRegion(){int x=0,y=0;SDL_GetMouseState(&x,&y);return inVideoRegion(x,y);}
static void raiseAllKeys(){for(auto key:keysDown)recordInput(LiSendKeyboardEvent((short)(0x8000|(unsigned short)key),KEY_ACTION_UP,0));keysDown.clear();}
static void setCaptureActive(bool active){captureActive=active;if(!sdlWindow)return;if(cfg.absoluteMouse){SDL_SetRelativeMouseMode(SDL_FALSE);SDL_ShowCursor(active&&capturedCursorHidden?SDL_DISABLE:SDL_ENABLE);SDL_SetWindowMouseGrab(sdlWindow,SDL_FALSE);}else{if(SDL_SetRelativeMouseMode(active?SDL_TRUE:SDL_FALSE)<0)SDL_ShowCursor(active&&capturedCursorHidden?SDL_DISABLE:SDL_ENABLE);SDL_SetWindowMouseGrab(sdlWindow,active?SDL_TRUE:SDL_FALSE);}if(!active){SDL_CaptureMouse(SDL_FALSE);raiseAllKeys();}logLine("input capture %s (%s mouse)",active?"active":"released",cfg.absoluteMouse?"absolute":"relative");}
static short qtVirtualKey(SDL_Scancode scan){if(scan>=SDL_SCANCODE_1&&scan<=SDL_SCANCODE_9)return(short)(0x31+scan-SDL_SCANCODE_1);if(scan>=SDL_SCANCODE_A&&scan<=SDL_SCANCODE_Z)return(short)(0x41+scan-SDL_SCANCODE_A);if(scan>=SDL_SCANCODE_F1&&scan<=SDL_SCANCODE_F12)return(short)(0x70+scan-SDL_SCANCODE_F1);if(scan>=SDL_SCANCODE_F13&&scan<=SDL_SCANCODE_F24)return(short)(0x7C+scan-SDL_SCANCODE_F13);if(scan>=SDL_SCANCODE_KP_1&&scan<=SDL_SCANCODE_KP_9)return(short)(0x61+scan-SDL_SCANCODE_KP_1);switch(scan){case SDL_SCANCODE_BACKSPACE:return 0x08;case SDL_SCANCODE_TAB:return 0x09;case SDL_SCANCODE_CLEAR:return 0x0C;case SDL_SCANCODE_KP_ENTER:case SDL_SCANCODE_RETURN:return 0x0D;case SDL_SCANCODE_PAUSE:return 0x13;case SDL_SCANCODE_CAPSLOCK:return 0x14;case SDL_SCANCODE_ESCAPE:return 0x1B;case SDL_SCANCODE_SPACE:return 0x20;case SDL_SCANCODE_PAGEUP:return 0x21;case SDL_SCANCODE_PAGEDOWN:return 0x22;case SDL_SCANCODE_END:return 0x23;case SDL_SCANCODE_HOME:return 0x24;case SDL_SCANCODE_LEFT:return 0x25;case SDL_SCANCODE_UP:return 0x26;case SDL_SCANCODE_RIGHT:return 0x27;case SDL_SCANCODE_DOWN:return 0x28;case SDL_SCANCODE_PRINTSCREEN:return 0x2C;case SDL_SCANCODE_INSERT:return 0x2D;case SDL_SCANCODE_DELETE:return 0x2E;case SDL_SCANCODE_KP_0:return 0x60;case SDL_SCANCODE_0:return 0x30;case SDL_SCANCODE_KP_MULTIPLY:return 0x6A;case SDL_SCANCODE_KP_PLUS:return 0x6B;case SDL_SCANCODE_KP_COMMA:return 0x6C;case SDL_SCANCODE_KP_MINUS:return 0x6D;case SDL_SCANCODE_KP_PERIOD:return 0x6E;case SDL_SCANCODE_KP_DIVIDE:return 0x6F;case SDL_SCANCODE_NUMLOCKCLEAR:return 0x90;case SDL_SCANCODE_SCROLLLOCK:return 0x91;case SDL_SCANCODE_LSHIFT:return 0xA0;case SDL_SCANCODE_RSHIFT:return 0xA1;case SDL_SCANCODE_LCTRL:return 0xA2;case SDL_SCANCODE_RCTRL:return 0xA3;case SDL_SCANCODE_LALT:return 0xA4;case SDL_SCANCODE_RALT:return 0xA5;case SDL_SCANCODE_LGUI:return 0x5B;case SDL_SCANCODE_RGUI:return 0x5C;case SDL_SCANCODE_APPLICATION:return 0x5D;case SDL_SCANCODE_SEMICOLON:return 0xBA;case SDL_SCANCODE_EQUALS:return 0xBB;case SDL_SCANCODE_COMMA:return 0xBC;case SDL_SCANCODE_MINUS:return 0xBD;case SDL_SCANCODE_PERIOD:return 0xBE;case SDL_SCANCODE_SLASH:return 0xBF;case SDL_SCANCODE_GRAVE:return 0xC0;case SDL_SCANCODE_LEFTBRACKET:return 0xDB;case SDL_SCANCODE_BACKSLASH:return 0xDC;case SDL_SCANCODE_RIGHTBRACKET:return 0xDD;case SDL_SCANCODE_APOSTROPHE:return 0xDE;case SDL_SCANCODE_NONUSBACKSLASH:return 0xE2;default:return 0;}}
static int gamepadButtons(){if(!gamepad)return 0;int f=0;
    if(SDL_GameControllerGetButton(gamepad,SDL_CONTROLLER_BUTTON_A))f|=A_FLAG;if(SDL_GameControllerGetButton(gamepad,SDL_CONTROLLER_BUTTON_B))f|=B_FLAG;
    if(SDL_GameControllerGetButton(gamepad,SDL_CONTROLLER_BUTTON_X))f|=X_FLAG;if(SDL_GameControllerGetButton(gamepad,SDL_CONTROLLER_BUTTON_Y))f|=Y_FLAG;
    if(SDL_GameControllerGetButton(gamepad,SDL_CONTROLLER_BUTTON_DPAD_UP))f|=UP_FLAG;if(SDL_GameControllerGetButton(gamepad,SDL_CONTROLLER_BUTTON_DPAD_DOWN))f|=DOWN_FLAG;
    if(SDL_GameControllerGetButton(gamepad,SDL_CONTROLLER_BUTTON_DPAD_LEFT))f|=LEFT_FLAG;if(SDL_GameControllerGetButton(gamepad,SDL_CONTROLLER_BUTTON_DPAD_RIGHT))f|=RIGHT_FLAG;
    if(SDL_GameControllerGetButton(gamepad,SDL_CONTROLLER_BUTTON_LEFTSHOULDER))f|=LB_FLAG;if(SDL_GameControllerGetButton(gamepad,SDL_CONTROLLER_BUTTON_RIGHTSHOULDER))f|=RB_FLAG;
    if(SDL_GameControllerGetButton(gamepad,SDL_CONTROLLER_BUTTON_START))f|=PLAY_FLAG;if(SDL_GameControllerGetButton(gamepad,SDL_CONTROLLER_BUTTON_BACK))f|=BACK_FLAG;
    if(SDL_GameControllerGetButton(gamepad,SDL_CONTROLLER_BUTTON_LEFTSTICK))f|=LS_CLK_FLAG;if(SDL_GameControllerGetButton(gamepad,SDL_CONTROLLER_BUTTON_RIGHTSTICK))f|=RS_CLK_FLAG;
    if(SDL_GameControllerGetButton(gamepad,SDL_CONTROLLER_BUTTON_GUIDE))f|=SPECIAL_FLAG;return f;}
static unsigned char gamepadTrigger(SDL_GameControllerAxis axis){if(!gamepad)return 0;return (unsigned char)std::clamp((SDL_GameControllerGetAxis(gamepad,axis)+32768)/256,0,255);}
static void sendGamepad(){if(!gamepad)return;recordInput(LiSendMultiControllerEvent(0,1,gamepadButtons(),gamepadTrigger(SDL_CONTROLLER_AXIS_TRIGGERLEFT),gamepadTrigger(SDL_CONTROLLER_AXIS_TRIGGERRIGHT),SDL_GameControllerGetAxis(gamepad,SDL_CONTROLLER_AXIS_LEFTX),(short)-SDL_GameControllerGetAxis(gamepad,SDL_CONTROLLER_AXIS_LEFTY),SDL_GameControllerGetAxis(gamepad,SDL_CONTROLLER_AXIS_RIGHTX),(short)-SDL_GameControllerGetAxis(gamepad,SDL_CONTROLLER_AXIS_RIGHTY)));}
static void openGamepad(int index){if(gamepad||!SDL_IsGameController(index))return;gamepad=SDL_GameControllerOpen(index);if(gamepad){LiSendControllerArrivalEvent(0,1,LI_CTYPE_XBOX,A_FLAG|B_FLAG|X_FLAG|Y_FLAG|UP_FLAG|DOWN_FLAG|LEFT_FLAG|RIGHT_FLAG|LB_FLAG|RB_FLAG|PLAY_FLAG|BACK_FLAG|LS_CLK_FLAG|RS_CLK_FLAG|SPECIAL_FLAG,LI_CCAP_ANALOG_TRIGGERS|LI_CCAP_RUMBLE);logLine("gamepad connected: %s",SDL_GameControllerName(gamepad));sendGamepad();}}
static void forwardInput(){SDL_Event e;while(SDL_PollEvent(&e)){if(managedInput&&(e.type==SDL_MOUSEMOTION||e.type==SDL_MOUSEBUTTONDOWN||e.type==SDL_MOUSEBUTTONUP||e.type==SDL_MOUSEWHEEL||e.type==SDL_KEYDOWN||e.type==SDL_KEYUP))continue;switch(e.type){
    case SDL_MOUSEMOTION:{if(!captureActive||e.motion.which==SDL_TOUCH_MOUSEID)break;Sint32 x=e.motion.x,y=e.motion.y,xrel=e.motion.xrel,yrel=e.motion.y;SDL_Event n;while(SDL_PeepEvents(&n,1,SDL_GETEVENT,SDL_MOUSEMOTION,SDL_MOUSEMOTION)>0)if(n.motion.which!=SDL_TOUCH_MOUSEID){x=n.motion.x;y=n.motion.y;xrel+=n.motion.xrel;yrel+=n.motion.yrel;}if(cfg.absoluteMouse){auto r=videoRegion();bool inside=inVideoRegion(x,y);x=std::clamp(x-r.x,0,r.w);y=std::clamp(y-r.y,0,r.h);auto buttons=SDL_GetMouseState(nullptr,nullptr);if(!buttons&&pendingButtonsOnLeave){SDL_CaptureMouse(SDL_FALSE);pendingButtonsOnLeave=false;}if(inside||mouseWasInVideoRegion||pendingButtonsOnLeave)recordInput(LiSendMousePositionEvent((short)x,(short)y,(short)std::clamp(r.w,1,32767),(short)std::clamp(r.h,1,32767)));if(!inside&&buttons)pendingButtonsOnLeave=true;mouseWasInVideoRegion=inside;}else recordInput(LiSendMouseMoveEvent((short)std::clamp(xrel,-32768,32767),(short)std::clamp(yrel,-32768,32767)));break;}
    case SDL_MOUSEBUTTONDOWN:case SDL_MOUSEBUTTONUP:{if(e.button.which==SDL_TOUCH_MOUSEID)break;if(!captureActive){if(e.type==SDL_MOUSEBUTTONUP&&e.button.button==SDL_BUTTON_LEFT&&inVideoRegion(e.button.x,e.button.y))setCaptureActive(true);break;}if(cfg.absoluteMouse&&!inVideoRegion(e.button.x,e.button.y)&&e.type==SDL_MOUSEBUTTONDOWN)break;int b=mouseButton(e.button.button);if(b){if(e.type==SDL_MOUSEBUTTONDOWN){SetFocus(cfg.target);SDL_CaptureMouse(SDL_TRUE);}recordInput(LiSendMouseButtonEvent(e.type==SDL_MOUSEBUTTONDOWN?BUTTON_ACTION_PRESS:BUTTON_ACTION_RELEASE,b));if(e.type==SDL_MOUSEBUTTONUP&&SDL_GetMouseState(nullptr,nullptr)==0)SDL_CaptureMouse(SDL_FALSE);}break;}
    case SDL_MOUSEWHEEL:if(!captureActive||e.wheel.which==SDL_TOUCH_MOUSEID||(cfg.absoluteMouse&&!currentMouseInVideoRegion()))break;
        if(e.wheel.preciseY!=0)recordInput(LiSendHighResScrollEvent((short)(e.wheel.preciseY*120)));
        if(e.wheel.preciseX!=0)recordInput(LiSendHighResHScrollEvent((short)(e.wheel.preciseX*120)));
        break;
    case SDL_KEYDOWN:case SDL_KEYUP:{if(e.key.repeat)break;if(e.type==SDL_KEYDOWN&&(e.key.keysym.mod&KMOD_CTRL)&&(e.key.keysym.mod&KMOD_ALT)&&(e.key.keysym.mod&KMOD_SHIFT)){if(e.key.keysym.scancode==SDL_SCANCODE_Z){setCaptureActive(!captureActive);break;}if(e.key.keysym.scancode==SDL_SCANCODE_M){setCaptureActive(false);cfg.absoluteMouse=!cfg.absoluteMouse;setCaptureActive(true);break;}if(e.key.keysym.scancode==SDL_SCANCODE_C){capturedCursorHidden=!capturedCursorHidden;if(cfg.absoluteMouse)SDL_ShowCursor(capturedCursorHidden?SDL_DISABLE:SDL_ENABLE);break;}if(e.key.keysym.scancode==SDL_SCANCODE_Q){stopRequested=true;LiInterruptConnection();frameReady.notify_one();break;}}if(!captureActive)break;auto vk=qtVirtualKey(e.key.keysym.scancode);if(vk){if(e.type==SDL_KEYDOWN)keysDown.insert(vk);else keysDown.erase(vk);recordInput(LiSendKeyboardEvent((short)(0x8000|(unsigned short)vk),e.type==SDL_KEYDOWN?KEY_ACTION_DOWN:KEY_ACTION_UP,keyboardModifiers((SDL_Keymod)e.key.keysym.mod)));}break;}
    case SDL_WINDOWEVENT:if(e.window.event==SDL_WINDOWEVENT_FOCUS_LOST){raiseAllKeys();if(!cfg.absoluteMouse)setCaptureActive(false);}else if(e.window.event==SDL_WINDOWEVENT_FOCUS_GAINED&&cfg.absoluteMouse)setCaptureActive(true);break;
    case SDL_CONTROLLERDEVICEADDED:openGamepad(e.cdevice.which);break;
    case SDL_CONTROLLERDEVICEREMOVED:if(gamepad&&SDL_JoystickInstanceID(SDL_GameControllerGetJoystick(gamepad))==e.cdevice.which){SDL_GameControllerClose(gamepad);gamepad=nullptr;LiSendMultiControllerEvent(0,0,0,0,0,0,0,0,0);logLine("gamepad disconnected");}break;
    case SDL_CONTROLLERBUTTONDOWN:case SDL_CONTROLLERBUTTONUP:case SDL_CONTROLLERAXISMOTION:sendGamepad();break;
}}}
static bool renderD3DFrame(AVFrame* frame){
    if(!d3dSwapChain||frame->format!=AV_PIX_FMT_D3D11)return false;
    auto inputTexture=(ID3D11Texture2D*)frame->data[0];UINT slice=(UINT)(uintptr_t)frame->data[1];
    D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC inputDesc{};inputDesc.ViewDimension=D3D11_VPIV_DIMENSION_TEXTURE2D;inputDesc.Texture2D.ArraySlice=slice;
    ID3D11VideoProcessorInputView* inputView=nullptr;if(FAILED(d3dVideoDevice->CreateVideoProcessorInputView(inputTexture,d3dVideoEnumerator,&inputDesc,&inputView)))return false;
    ID3D11Texture2D* backBuffer=nullptr;HRESULT hr=d3dSwapChain->GetBuffer(0,__uuidof(ID3D11Texture2D),(void**)&backBuffer);
    D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC outputDesc{};outputDesc.ViewDimension=D3D11_VPOV_DIMENSION_TEXTURE2D;
    ID3D11VideoProcessorOutputView* outputView=nullptr;if(SUCCEEDED(hr))hr=d3dVideoDevice->CreateVideoProcessorOutputView(backBuffer,d3dVideoEnumerator,&outputDesc,&outputView);
    RECT client{};GetClientRect(cfg.target,&client);int cw=std::max(1L,client.right),ch=std::max(1L,client.bottom);double scale=std::min((double)cw/frame->width,(double)ch/frame->height);int w=(int)(frame->width*scale),h=(int)(frame->height*scale);RECT source{0,0,frame->width,frame->height},dest{(cw-w)/2,(ch-h)/2,(cw+w)/2,(ch+h)/2};
    D3D11_VIDEO_COLOR background{};background.RGBA.A=1.0f;d3dVideoContext->VideoProcessorSetOutputBackgroundColor(d3dVideoProcessor,FALSE,&background);d3dVideoContext->VideoProcessorSetStreamSourceRect(d3dVideoProcessor,0,TRUE,&source);d3dVideoContext->VideoProcessorSetStreamDestRect(d3dVideoProcessor,0,TRUE,&dest);
    D3D11_VIDEO_PROCESSOR_STREAM stream{};stream.Enable=TRUE;stream.pInputSurface=inputView;
    if(SUCCEEDED(hr))hr=d3dVideoContext->VideoProcessorBlt(d3dVideoProcessor,outputView,0,1,&stream);
    if(SUCCEEDED(hr))hr=d3dSwapChain->Present(0,0);
    releaseCom(outputView);releaseCom(backBuffer);releaseCom(inputView);if(SUCCEEDED(hr)){renderedFrames++;return true;}return false;
}
static bool renderFrame(AVFrame* source){
    if(source->format==AV_PIX_FMT_D3D11)return renderD3DFrame(source);
    if(!renderer)renderer=SDL_CreateRenderer(sdlWindow,-1,SDL_RENDERER_ACCELERATED);
    AVFrame* display=source; AVFrame* converted=nullptr; SwsContext* converter=nullptr;
    if(source->format!=AV_PIX_FMT_YUV420P&&source->format!=AV_PIX_FMT_YUVJ420P){
        converted=av_frame_alloc(); if(!converted)return false;
        converted->format=AV_PIX_FMT_YUV420P;converted->width=source->width;converted->height=source->height;
        if(av_frame_get_buffer(converted,32)<0){av_frame_free(&converted);return false;}
        converter=sws_getContext(source->width,source->height,(AVPixelFormat)source->format,
            source->width,source->height,AV_PIX_FMT_YUV420P,SWS_BILINEAR,nullptr,nullptr,nullptr);
        if(!converter||sws_scale(converter,source->data,source->linesize,0,source->height,converted->data,converted->linesize)<=0){
            sws_freeContext(converter);av_frame_free(&converted);return false;
        }
        display=converted;
    }
    if(!texture||textureWidth!=display->width||textureHeight!=display->height){
        SDL_DestroyTexture(texture);texture=SDL_CreateTexture(renderer,SDL_PIXELFORMAT_IYUV,SDL_TEXTUREACCESS_STREAMING,display->width,display->height);
        textureWidth=display->width;textureHeight=display->height;
    }
    bool rendered=texture&&SDL_UpdateYUVTexture(texture,nullptr,display->data[0],display->linesize[0],display->data[1],display->linesize[1],display->data[2],display->linesize[2])==0;
    if(rendered){SDL_RenderClear(renderer);SDL_RenderCopy(renderer,texture,nullptr,nullptr);SDL_RenderPresent(renderer);renderedFrames++;}
    sws_freeContext(converter);av_frame_free(&converted);return rendered;
}
static void run(){
    state=1;lastError=0;renderedFrames=0;decodedFrames=0;SDL_SetHint(SDL_HINT_MOUSE_RELATIVE_SCALING,"0");SDL_SetHint(SDL_HINT_MOUSE_AUTO_CAPTURE,"0");SDL_SetHint(SDL_HINT_MOUSE_FOCUS_CLICKTHROUGH,"1");SDL_SetHint("SDL_ALLOW_ALT_TAB_WHILE_GRABBED","0");SDL_SetHint("SDL_JOYSTICK_HIDAPI_PS4_RUMBLE","1");SDL_SetHint("SDL_JOYSTICK_HIDAPI_PS5_RUMBLE","1");SDL_Init(SDL_INIT_VIDEO|SDL_INIT_AUDIO|SDL_INIT_GAMECONTROLLER);sdlWindow=SDL_CreateWindowFrom(cfg.target);renderer=nullptr;previousWindowProc=(WNDPROC)SetWindowLongPtrW(cfg.target,GWLP_WNDPROC,(LONG_PTR)managedWindowProc);captureActive=false;capturedCursorHidden=!cfg.absoluteMouse;mouseWasInVideoRegion=false;pendingButtonsOnLeave=false;keysDown.clear();setCaptureActive(true);
    SERVER_INFORMATION server{};server.address=cfg.address.c_str();server.serverInfoAppVersion=cfg.appVersion.c_str();server.serverInfoGfeVersion=cfg.gfeVersion.empty()?nullptr:cfg.gfeVersion.c_str();server.rtspSessionUrl=cfg.sessionUrl.empty()?nullptr:cfg.sessionUrl.c_str();server.serverCodecModeSupport=cfg.codecSupport;
    STREAM_CONFIGURATION stream{};stream.width=cfg.width;stream.height=cfg.height;stream.fps=cfg.fps;stream.bitrate=cfg.bitrate;stream.packetSize=1392;stream.streamingRemotely=STREAM_CFG_LOCAL;stream.audioConfiguration=AUDIO_CONFIGURATION_STEREO;stream.supportedVideoFormats=cfg.videoFormats;stream.clientRefreshRateX100=cfg.fps*100;stream.encryptionFlags=ENCFLG_ALL;memcpy(stream.remoteInputAesKey,cfg.key,16);memcpy(stream.remoteInputAesIv,cfg.iv,16);
    DECODER_RENDERER_CALLBACKS video{videoSetup,noOp,noOp,videoCleanup,submitVideo,CAPABILITY_DIRECT_SUBMIT};AUDIO_RENDERER_CALLBACKS audio{audioInit,audioStart,audioStop,audioCleanup,playAudio,CAPABILITY_DIRECT_SUBMIT|CAPABILITY_SUPPORTS_ARBITRARY_AUDIO_DURATION};CONNECTION_LISTENER_CALLBACKS listener{stageStarting,stageComplete,stageFailed,connectionStarted,connectionTerminated,logLine,rumble,statusUpdate,hdr,trigger,motion,led};
    int rc=LiStartConnection(&server,&stream,&listener,&video,&audio,nullptr,0,nullptr,0);if(rc){lastError=rc;state=4;}else for(int i=0;i<SDL_NumJoysticks();i++)openGamepad(i);while(!stopRequested&&state!=4){forwardInput();if(state==2&&LiGetPiuLinkAck(3)<0&&adaptiveAttempts<8&&SDL_GetTicks64()-lastAdaptiveRequest>=750)requestAdaptive();std::unique_lock lock(frameLock);frameReady.wait_for(lock,std::chrono::milliseconds(8),[]{return pendingFrame||stopRequested;});if(pendingFrame){auto frame=pendingFrame;pendingFrame=nullptr;lock.unlock();renderFrame(frame);av_frame_free(&frame);}}
    raiseAllKeys();LiStopConnection();if(previousWindowProc){SetWindowLongPtrW(cfg.target,GWLP_WNDPROC,(LONG_PTR)previousWindowProc);previousWindowProc=nullptr;}if(gamepad){SDL_GameControllerClose(gamepad);gamepad=nullptr;}SDL_DestroyTexture(texture);texture=nullptr;textureWidth=textureHeight=0;SDL_DestroyRenderer(renderer);renderer=nullptr;SDL_DestroyWindow(sdlWindow);sdlWindow=nullptr;SDL_Quit();if(state!=4)state=3;
}
API int PiuStart(HWND target,const char* address,const char* appVersion,const char* gfeVersion,const char* sessionUrl,int codecSupport,int videoFormats,int width,int height,int fps,int bitrate,int absoluteMouse,int autoBitrate,int contentAdaptive,const char* keyHex,const char* ivHex){if(worker.joinable())return -1;auto key=fromHex(keyHex),iv=fromHex(ivHex);if(!target||key.size()!=16||iv.size()!=16)return -2;cfg={};cfg.target=target;cfg.address=address;cfg.appVersion=appVersion;cfg.gfeVersion=gfeVersion?gfeVersion:"";cfg.sessionUrl=sessionUrl?sessionUrl:"";cfg.codecSupport=codecSupport;cfg.videoFormats=videoFormats;cfg.width=width;cfg.height=height;cfg.fps=fps;cfg.bitrate=bitrate;cfg.absoluteMouse=absoluteMouse!=0;cfg.autoBitrate=autoBitrate!=0;cfg.contentAdaptive=contentAdaptive!=0;memcpy(cfg.key,key.data(),16);memcpy(cfg.iv,iv.data(),16);stopRequested=false;lastError=0;renderedFrames=0;forwardedInputEvents=0;currentBitrate=bitrate;adaptiveAttempts=0;lastAdaptiveRequest=0;state=1;worker=std::thread(run);return 0;}
API void PiuStop(){stopRequested=true;LiInterruptConnection();frameReady.notify_one();if(worker.joinable())worker.join();}
API int PiuState(){return state;} API int PiuLastError(){return lastError;} API unsigned long long PiuRenderedFrames(){return renderedFrames;}
API unsigned long long PiuSubmittedVideoUnits(){return submittedVideoUnits;}
API unsigned long long PiuDecodedFrames(){return decodedFrames;}
API unsigned long long PiuForwardedInputEvents(){return forwardedInputEvents;}
API int PiuCurrentBitrate(){int ack=LiGetPiuLinkAck(1);return ack>0?ack:currentBitrate.load();} API int PiuAdaptiveAck(){return LiGetPiuLinkAck(3);}
API void PiuSetManagedInput(int enabled){managedInput=enabled!=0;}
API int PiuMousePosition(short x,short y,short w,short h){int rc=LiSendMousePositionEvent(x,y,w,h);recordInput(rc);return rc;} API int PiuMouseMove(short x,short y){int rc=LiSendMouseMoveEvent(x,y);recordInput(rc);return rc;} API int PiuMouseButton(int action,int button){int rc=LiSendMouseButtonEvent(action,button);recordInput(rc);return rc;} API int PiuScroll(short amount){int rc=LiSendHighResScrollEvent(amount);recordInput(rc);return rc;} API int PiuHScroll(short amount){int rc=LiSendHighResHScrollEvent(amount);recordInput(rc);return rc;} API int PiuKeyboard(short key,int action,char modifiers){int rc=LiSendKeyboardEvent((short)(0x8000|(unsigned short)key),action,modifiers);recordInput(rc);return rc;}
API int PiuController(int flags,unsigned char leftTrigger,unsigned char rightTrigger,short leftX,short leftY,short rightX,short rightY){int rc=LiSendMultiControllerEvent(0,1,flags,leftTrigger,rightTrigger,leftX,leftY,rightX,rightY);recordInput(rc);return rc;}
