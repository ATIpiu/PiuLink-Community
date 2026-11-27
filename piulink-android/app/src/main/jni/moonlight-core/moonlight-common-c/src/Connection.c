#include "Limelight-internal.h"

static ConnListenerConnectionTerminated originalTerminationCallback;
static PLT_THREAD terminationCallbackThread;
static int terminationCallbackErrorCode;

// Common globals
char* RemoteAddrString;
struct sockaddr_storage RemoteAddr;
struct sockaddr_storage LocalAddr;
SOCKADDR_LEN AddrLen;
int AppVersionQuad[4];
STREAM_CONFIGURATION StreamConfig;
CONNECTION_LISTENER_CALLBACKS ListenerCallbacks;
DECODER_RENDERER_CALLBACKS VideoCallbacks;
AUDIO_RENDERER_CALLBACKS AudioCallbacks;
int NegotiatedVideoFormat;
bool HighQualitySurroundSupported;
bool HighQualitySurroundEnabled;
OPUS_MULTISTREAM_CONFIGURATION NormalQualityOpusConfig;
OPUS_MULTISTREAM_CONFIGURATION HighQualityOpusConfig;
int AudioPacketDuration;
bool AudioEncryptionEnabled;
bool ReferenceFrameInvalidationSupported;
uint16_t RtspPortNumber;
uint16_t ControlPortNumber;
uint16_t AudioPortNumber;
uint16_t VideoPortNumber;
SS_PING AudioPingPayload;
SS_PING VideoPingPayload;
uint32_t ControlConnectData;
uint32_t SunshineFeatureFlags;
uint32_t EncryptionFeaturesSupported;
uint32_t EncryptionFeaturesRequested;
uint32_t EncryptionFeaturesEnabled;

// Connection stages
static const char* stageNames[STAGE_MAX] = {
    "none",
    "platform initialization",
    "name resolution",
    "audio stream initialization",
    "RTSP handshake",
    "control stream initialization",
    "video stream initialization",
    "input stream initialization",
    "control stream establishment",
    "video stream establishment",
    "audio stream establishment",
    "input stream establishment"
};

// Get the name of the current stage based on its number
const char* LiGetStageName(int stage) {
    return stageNames[stage];
}

extern void pl_connection_reset(void);
extern void pl_connection_interrupt(void);
extern bool pl_connection_claim_termination(void);
extern int pl_connection_start(void*, int (*)(void*, uint8_t), void (*)(uint8_t, uint8_t, int));
extern void pl_connection_stop(void (*)(uint8_t));
extern uint16_t pl_rtsp_url_port(const char*);
extern int pl_resolve_host(const char*, uint16_t, int (*)(const char*, uint16_t), void (*)(int));

typedef struct {
    PSERVER_INFORMATION server;
    void* render;
    void* audio;
    int videoFlags;
    int audioFlags;
} PlConnectionContext;

static void releaseRustStage(uint8_t stage) {
    switch (stage) {
        case STAGE_INPUT_STREAM_START: stopInputStream(); break;
        case STAGE_AUDIO_STREAM_START: stopAudioStream(); break;
        case STAGE_VIDEO_STREAM_START: stopVideoStream(); break;
        case STAGE_CONTROL_STREAM_START: stopControlStream(); break;
        case STAGE_INPUT_STREAM_INIT: destroyInputStream(); break;
        case STAGE_VIDEO_STREAM_INIT: destroyVideoStream(); break;
        case STAGE_CONTROL_STREAM_INIT: destroyControlStream(); break;
        case STAGE_AUDIO_STREAM_INIT: destroyAudioStream(); break;
        case STAGE_PLATFORM_INIT: cleanupPlatform(); break;
    }
}

static int resolveRustAddress(const char* host, uint16_t port) {
    return resolveHostName(host, AF_UNSPEC, port, &RemoteAddr, &AddrLen);
}

static void delayRustProbe(int milliseconds) { PltSleepMs(milliseconds); }

static int runRustStage(void* context, uint8_t stage) {
    PlConnectionContext* ctx = context;
    switch (stage) {
        case STAGE_PLATFORM_INIT: return initializePlatform();
        case STAGE_NAME_RESOLUTION: return pl_resolve_host(ctx->server->address, RtspPortNumber, resolveRustAddress, delayRustProbe);
        case STAGE_AUDIO_STREAM_INIT:
            if (StreamConfig.streamingRemotely == STREAM_CFG_AUTO) {
                StreamConfig.streamingRemotely = isPrivateNetworkAddress(&RemoteAddr) ? STREAM_CFG_LOCAL : STREAM_CFG_REMOTE;
                if (StreamConfig.streamingRemotely == STREAM_CFG_REMOTE && StreamConfig.packetSize > 1024) {
                    Limelog("Packet size capped at 1KB for remote streaming\n");
                    StreamConfig.packetSize = 1024;
                }
            }
            return initializeAudioStream();
        case STAGE_RTSP_HANDSHAKE: return performRtspHandshake(ctx->server);
        case STAGE_CONTROL_STREAM_INIT: return initializeControlStream();
        case STAGE_VIDEO_STREAM_INIT: initializeVideoStream(); return 0;
        case STAGE_INPUT_STREAM_INIT: initializeInputStream(); return 0;
        case STAGE_CONTROL_STREAM_START: return startControlStream();
        case STAGE_VIDEO_STREAM_START: return startVideoStream(ctx->render, ctx->videoFlags);
        case STAGE_AUDIO_STREAM_START: return startAudioStream(ctx->audio, ctx->audioFlags);
        case STAGE_INPUT_STREAM_START: return startInputStream();
        default: return -1;
    }
}
static void notifyRustStage(uint8_t stage, uint8_t event, int err) {
    switch (event) {
        case 0: ListenerCallbacks.stageStarting(stage); break;
        case 1: ListenerCallbacks.stageComplete(stage); break;
        case 2: ListenerCallbacks.stageFailed(stage, err); break;
    }
}

// Interrupt a pending connection attempt. This interruption happens asynchronously
// so it is not safe to start another connection before LiStartConnection() returns.
void LiInterruptConnection(void) {
    // Signal anyone waiting on the global interrupted flag
    pl_connection_interrupt();
}

// Stop the connection by undoing the step at the current stage and those before it
void LiStopConnection(void) {
    pl_connection_stop(releaseRustStage);
    if (RemoteAddrString != NULL) {
        free(RemoteAddrString);
        RemoteAddrString = NULL;
    }
}

static void terminationCallbackThreadFunc(void* context)
{
    // Invoke the client's termination callback
    originalTerminationCallback(terminationCallbackErrorCode);
}

// This shim callback runs the client's connectionTerminated() callback on a
// separate thread. This is neccessary because other internal threads directly
// invoke this callback. That can result in a deadlock if the client
// calls LiStopConnection() in the callback when the cleanup code
// attempts to join the thread that the termination callback (and LiStopConnection)
// is running on.
static void ClInternalConnectionTerminated(int errorCode)
{
    int err;

    // Avoid recursion and issuing multiple callbacks
    if (!pl_connection_claim_termination()) {
        return;
    }

    terminationCallbackErrorCode = errorCode;

    // Invoke the termination callback on a separate thread
    err = PltCreateThread("AsyncTerm", terminationCallbackThreadFunc, NULL, &terminationCallbackThread);
    if (err != 0) {
        // Nothing we can safely do here, so we'll just assert on debug builds
        Limelog("Failed to create termination thread: %d\n", err);
        LC_ASSERT(err == 0);
    }

    // Detach the thread since we never wait on it
    PltDetachThread(&terminationCallbackThread);
}

// Starts the connection to the streaming machine
int LiStartConnection(PSERVER_INFORMATION serverInfo, PSTREAM_CONFIGURATION streamConfig, PCONNECTION_LISTENER_CALLBACKS clCallbacks,
    PDECODER_RENDERER_CALLBACKS drCallbacks, PAUDIO_RENDERER_CALLBACKS arCallbacks, void* renderContext, int drFlags,
    void* audioContext, int arFlags) {
    int err;

    if (drCallbacks != NULL && (drCallbacks->capabilities & CAPABILITY_PULL_RENDERER) && drCallbacks->submitDecodeUnit) {
        Limelog("CAPABILITY_PULL_RENDERER cannot be set with a submitDecodeUnit callback\n");
        LC_ASSERT(false);
        err = -1;
        goto Cleanup;
    }

    if (drCallbacks != NULL && (drCallbacks->capabilities & CAPABILITY_PULL_RENDERER) && (drCallbacks->capabilities & CAPABILITY_DIRECT_SUBMIT)) {
        Limelog("CAPABILITY_PULL_RENDERER and CAPABILITY_DIRECT_SUBMIT cannot be set together\n");
        LC_ASSERT(false);
        err = -1;
        goto Cleanup;
    }

    if (serverInfo->serverCodecModeSupport == 0) {
        Limelog("serverCodecModeSupport field in SERVER_INFORMATION must be set!\n");
        LC_ASSERT(false);
        err = -1;
        goto Cleanup;
    }

    // Extract the appversion from the supplied string
    if (extractVersionQuadFromString(serverInfo->serverInfoAppVersion,
                                     AppVersionQuad) < 0) {
        Limelog("Invalid appversion string: %s\n", serverInfo->serverInfoAppVersion);
        err = -1;
        goto Cleanup;
    }

    // Replace missing callbacks with placeholders
    fixupMissingCallbacks(&drCallbacks, &arCallbacks, &clCallbacks);
    memcpy(&VideoCallbacks, drCallbacks, sizeof(VideoCallbacks));
    memcpy(&AudioCallbacks, arCallbacks, sizeof(AudioCallbacks));

#ifdef LC_DEBUG_RECORD_MODE
    // Install the pass-through recorder callbacks
    setRecorderCallbacks(&VideoCallbacks, &AudioCallbacks);
#endif

    // Hook the termination callback so we can avoid issuing a termination callback
    // after LiStopConnection() is called.
    //
    // Initialize ListenerCallbacks before anything that could call Limelog().
    originalTerminationCallback = clCallbacks->connectionTerminated;
    memcpy(&ListenerCallbacks, clCallbacks, sizeof(ListenerCallbacks));
    ListenerCallbacks.connectionTerminated = ClInternalConnectionTerminated;

    memset(&LocalAddr, 0, sizeof(LocalAddr));
    NegotiatedVideoFormat = 0;
    memcpy(&StreamConfig, streamConfig, sizeof(StreamConfig));
    RemoteAddrString = strdup(serverInfo->address);

    // The values in RTSP SETUP will be used to populate these.
    VideoPortNumber = 0;
    ControlPortNumber = 0;
    AudioPortNumber = 0;

    RtspPortNumber = pl_rtsp_url_port(serverInfo->rtspSessionUrl);
    pl_connection_reset();

    // Validate the audio configuration
    if (MAGIC_BYTE_FROM_AUDIO_CONFIG(StreamConfig.audioConfiguration) != 0xCA ||
            CHANNEL_COUNT_FROM_AUDIO_CONFIGURATION(StreamConfig.audioConfiguration) > AUDIO_CONFIGURATION_MAX_CHANNEL_COUNT) {
        Limelog("Invalid audio configuration specified\n");
        err = -1;
        goto Cleanup;
    }

    // FEC only works in 16 byte chunks, so we must round down
    // the given packet size to the nearest multiple of 16.
    StreamConfig.packetSize -= StreamConfig.packetSize % 16;

    if (StreamConfig.packetSize == 0) {
        Limelog("Invalid packet size specified\n");
        err = -1;
        goto Cleanup;
    }

    // Height must not be odd or NVENC will fail to initialize
    if (StreamConfig.height & 0x1) {
        Limelog("Encoder height must not be odd. Rounding %d to %d\n",
                StreamConfig.height,
                StreamConfig.height & ~0x1);
        StreamConfig.height = StreamConfig.height & ~0x1;
    }

    // Dimensions over 4096 are only supported with HEVC on NVENC
    if (!(StreamConfig.supportedVideoFormats & ~VIDEO_FORMAT_MASK_H264) &&
            (StreamConfig.width > 4096 || StreamConfig.height > 4096)) {
        Limelog("WARNING: Streaming at resolutions above 4K using H.264 will likely fail! Trying anyway!\n");
    }
    // Dimensions over 8192 aren't supported at all (even on Turing)
    else if (StreamConfig.width > 8192 || StreamConfig.height > 8192) {
        Limelog("WARNING: Streaming at resolutions above 8K will likely fail! Trying anyway!\n");
    }

    // Reference frame invalidation doesn't seem to work with resolutions much
    // higher than 1440p. I haven't figured out a pattern to indicate which
    // resolutions will work and which won't, but we can at least exclude
    // 4K from RFI to avoid significant persistent artifacts after frame loss.
    if (StreamConfig.width == 3840 && StreamConfig.height == 2160 &&
            (VideoCallbacks.capabilities & CAPABILITY_REFERENCE_FRAME_INVALIDATION_AVC) &&
            !IS_SUNSHINE()) {
        Limelog("Disabling reference frame invalidation for 4K streaming with GFE\n");
        VideoCallbacks.capabilities &= ~CAPABILITY_REFERENCE_FRAME_INVALIDATION_AVC;
    }
    
    PlConnectionContext ctx = {serverInfo, renderContext, audioContext, drFlags, arFlags};
    err = pl_connection_start(&ctx, runRustStage, notifyRustStage);
    if (err) goto Cleanup;

    // Wiggle the mouse a bit to wake the display up
    LiSendMouseMoveEvent(1, 1);
    PltSleepMs(10);
    LiSendMouseMoveEvent(-1, -1);
    PltSleepMs(10);

    ListenerCallbacks.connectionStarted();

Cleanup:
    if (err != 0) {
        // Undo any work we've done here before failing
        LiStopConnection();
    }
    return err;
}

const char* LiGetLaunchUrlQueryParameters() {
    // v0 = Video encryption and control stream encryption v2
    // v1 = RTSP encryption
    return "&corever=1";
}
