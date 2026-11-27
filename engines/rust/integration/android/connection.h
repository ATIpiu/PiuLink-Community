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
