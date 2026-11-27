// Included inside Moonlight's RtspConnection.c after the network exchange helpers.
enum { PL_OPTIONS, PL_DESCRIBE, PL_AUDIO, PL_VIDEO, PL_CONTROL, PL_ANNOUNCE, PL_PLAY };
extern int pl_rtsp_handshake(void*, int, bool, int (*)(void*, uint8_t, const char*));
extern uint16_t pl_transport_port(const char*);
extern int pl_rtsp_describe(const uint8_t*, int, uint32_t, uint32_t, int, int*,
                            POPUS_MULTISTREAM_CONFIGURATION, POPUS_MULTISTREAM_CONFIGURATION, bool*, bool*);

static int applyRustDescription(PSERVER_INFORMATION server, PRTSP_MESSAGE msg) {
    _Static_assert(sizeof(OPUS_MULTISTREAM_CONFIGURATION) == 28, "Unexpected Opus ABI");
    int err = pl_rtsp_describe((const uint8_t*)msg->payload, msg->payloadLength,
            StreamConfig.supportedVideoFormats, server->serverCodecModeSupport,
            CHANNEL_COUNT_FROM_AUDIO_CONFIGURATION(StreamConfig.audioConfiguration), &NegotiatedVideoFormat,
            &NormalQualityOpusConfig, &HighQualityOpusConfig, &HighQualitySurroundSupported,
            &ReferenceFrameInvalidationSupported);
    if (err) return err;
    if (!parseSdpAttributeToUInt(msg->payload, "x-ss-general.featureFlags", &SunshineFeatureFlags))
        SunshineFeatureFlags = 0;
    if (!parseSdpAttributeToUInt(msg->payload, "x-ss-general.encryptionSupported", &EncryptionFeaturesSupported))
        EncryptionFeaturesSupported = 0;
    if (!parseSdpAttributeToUInt(msg->payload, "x-ss-general.encryptionRequested", &EncryptionFeaturesRequested))
        EncryptionFeaturesRequested = 0;
    EncryptionFeaturesEnabled = 0;
    return 0;
}

static int applyRustSetup(uint8_t stream, PRTSP_MESSAGE msg) {
    uint16_t port = pl_transport_port(getOptionContent(msg->options, "Transport"));
    if (stream == PL_CONTROL) {
        char* connectData = getOptionContent(msg->options, "X-SS-Connect-Data");
        ControlConnectData = connectData ? (uint32_t)strtoul(connectData, NULL, 0) : 0;
        ControlPortNumber = port ? port : 47999;
        return 0;
    }
    PSS_PING ping = stream == PL_AUDIO ? &AudioPingPayload : &VideoPingPayload;
    const char* value = getOptionContent(msg->options, "X-SS-Ping-Payload");
    memset(ping, 0, sizeof(*ping));
    if (value && strlen(value) == sizeof(ping->payload))
        memcpy(ping->payload, value, sizeof(ping->payload));
    if (stream == PL_VIDEO) {
        VideoPortNumber = port ? port : 47998;
        return 0;
    }
    AudioPortNumber = port ? port : 48000;
    notifyAudioPortNegotiationComplete();
    char* raw = getOptionContent(msg->options, "Session");
    char* ctx = NULL;
    char* id = raw ? strtok_r(raw, ";", &ctx) : NULL;
    if (!id || !(sessionIdString = strdup(id))) return -1;
    hasSessionId = true;
    return 0;
}

static int exchangeRustRtsp(void* server, uint8_t stage, const char* target) {
    RTSP_MESSAGE response = {0};
    int err = -1;
    bool ok;
    switch (stage) {
        case PL_OPTIONS: ok = requestOptions(&response, &err); break;
        case PL_DESCRIBE: ok = requestDescribe(&response, &err); break;
        case PL_AUDIO:
        case PL_VIDEO:
        case PL_CONTROL: ok = setupStream(&response, (char*)target, &err); break;
        case PL_ANNOUNCE: ok = sendVideoAnnounce(&response, &err); break;
        case PL_PLAY: ok = playStream(&response, (char*)target, &err); break;
        default: return -1;
    }
    if (!ok) {
        Limelog("RTSP exchange stage %u transport error %d\n", stage, err);
        return err ? err : -1;
    }
    err = response.message.response.statusCode;
    if (err == 200) {
        if (stage == PL_DESCRIBE) err = applyRustDescription(server, &response);
        else if (stage >= PL_AUDIO && stage <= PL_CONTROL) err = applyRustSetup(stage, &response);
        else err = 0;
    } else if (!err) err = -1;
    freeMessage(&response);
    if (err) Limelog("RTSP exchange stage %u failed with %d\n", stage, err);
    return err;
}
