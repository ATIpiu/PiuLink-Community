#include "rtsp_record.h"
#include "Limelight-internal.h"
#include "Rtsp.h"

#define RTSP_CONNECT_TIMEOUT_SEC 10
#define RTSP_RECEIVE_TIMEOUT_SEC 15
#define RTSP_RETRY_DELAY_MS 500

static int currentSeqNumber;
static char rtspTargetUrl[256];
static char* sessionIdString;
static bool hasSessionId;
static int rtspClientVersion;
static char urlAddr[URLSAFESTRING_LEN];
static bool useEnet;
static char* controlStreamId;
static bool encryptedRtspEnabled;

static uint32_t lastAuthenticatedReply;
static uint32_t encryptionSequenceNumber;

static SOCKET nextHandshakeSocket = INVALID_SOCKET;
static ENetHost* client;
static ENetPeer* peer;

#define CHAR_TO_INT(x) ((x) - '0')
#define CHAR_IS_DIGIT(x) ((x) >= '0' && (x) <= '9')

// Create RTSP Option
static POPTION_ITEM createOptionItem(char* option, char* content)
{
    POPTION_ITEM item = malloc(sizeof(*item));
    if (item == NULL) {
        return NULL;
    }

    item->option = strdup(option);
    if (item->option == NULL) {
        free(item);
        return NULL;
    }

    item->content = strdup(content);
    if (item->content == NULL) {
        free(item->option);
        free(item);
        return NULL;
    }

    item->next = NULL;
    item->flags = FLAG_ALLOCATED_OPTION_FIELDS;

    return item;
}

// Add an option to the RTSP Message
static bool addOption(PRTSP_MESSAGE msg, char* option, char* content)
{
    POPTION_ITEM item = createOptionItem(option, content);
    if (item == NULL) {
        return false;
    }

    insertOption(&msg->options, item);
    msg->flags |= FLAG_ALLOCATED_OPTION_ITEMS;

    return true;
}

// Create an RTSP Request
static bool initializeRtspRequest(PRTSP_MESSAGE msg, char* command, char* target)
{
    char sequenceNumberStr[16];
    char clientVersionStr[16];

    // FIXME: Hacked CSeq attribute due to RTSP parser bug
    createRtspRequest(msg, NULL, 0, command, target, "RTSP/1.0",
        0, NULL, NULL, 0);

    snprintf(sequenceNumberStr, sizeof(sequenceNumberStr), "%d", currentSeqNumber++);
    snprintf(clientVersionStr, sizeof(clientVersionStr), "%d", rtspClientVersion);
    if (!addOption(msg, "CSeq", sequenceNumberStr) ||
        !addOption(msg, "X-GS-ClientVersion", clientVersionStr) ||
        (!useEnet && !addOption(msg, "Host", urlAddr))) {
        freeMessage(msg);
        return false;
    }

    return true;
}

#define ENCRYPTED_RTSP_BIT 0x80000000

typedef struct _ENC_RTSP_HEADER {
    uint32_t typeAndLength; // BE
    uint32_t sequenceNumber; // BE
    uint8_t tag[16];
} ENC_RTSP_HEADER, *PENC_RTSP_HEADER;

static char* sealRtspMessage(PRTSP_MESSAGE request, int* messageLen) {
    int len=0;
    char *plain=serializeRtspMessage(request,&len);
    if (!plain) return NULL;
    if (!encryptedRtspEnabled) { *messageLen=len; return plain; }
    if (len<=0 || len>INT32_MAX-24 || encryptionSequenceNumber==UINT32_MAX) { free(plain); return NULL; }
    uint8_t *wire=malloc(len+24);
    if (wire) memcpy(wire+24,plain,len);
    free(plain);
    if (!wire) return NULL;
    if (!pl_rtsp_record((const uint8_t*)StreamConfig.remoteInputAesKey,sizeof(StreamConfig.remoteInputAesKey),
                        false,true,++encryptionSequenceNumber,wire,len+24)) { free(wire); return NULL; }
    *messageLen=len+24;
    return (char*)wire;
}
static bool unsealRtspMessage(char *wire,int len,PRTSP_MESSAGE response) {
    if (len<=0) return false;
    if (encryptedRtspEnabled) {
        if (!pl_rtsp_record((const uint8_t*)StreamConfig.remoteInputAesKey,sizeof(StreamConfig.remoteInputAesKey),
                            true,false,0,(uint8_t*)wire,len)) return false;
        uint32_t seq;
        memcpy(&seq,wire+4,sizeof(seq));
        seq = BE32(seq);
        if (seq <= lastAuthenticatedReply) return false;
        lastAuthenticatedReply = seq;
        wire+=24;
        len-=24;
    }
    return parseRtspMessage(response,wire,len)==RTSP_ERROR_SUCCESS;
}

// Send RTSP message and get response over ENet
static bool transactRtspMessageEnet(PRTSP_MESSAGE request, PRTSP_MESSAGE response, bool expectingPayload, int* error) {
    ENetEvent event;
    char* serializedMessage;
    int messageLen;
    int offset;
    ENetPacket* packet;
    char* payload;
    int payloadLength;
    bool ret;
    char* responseBuffer;

    // RTSP encryption is not supported using ENet due to our special handling
    // of the payload below. Modern versions of Sunshine use TCP for RTSP.
    LC_ASSERT(!encryptedRtspEnabled);

    *error = -1;
    ret = false;
    responseBuffer = NULL;

    // We're going to handle the payload separately, so temporarily set the payload to NULL
    payload = request->payload;
    payloadLength = request->payloadLength;
    request->payload = NULL;
    request->payloadLength = 0;
    
    // Serialize the RTSP message into a message buffer
    serializedMessage = serializeRtspMessage(request, &messageLen);
    if (serializedMessage == NULL) {
        goto Exit;
    }
    
    // Create the reliable packet that describes our outgoing message
    packet = enet_packet_create(serializedMessage, messageLen, ENET_PACKET_FLAG_RELIABLE);
    if (packet == NULL) {
        goto Exit;
    }
    
    // Send the message
    if (enet_peer_send(peer, 0, packet) < 0) {
        enet_packet_destroy(packet);
        goto Exit;
    }
    enet_host_flush(client);

    // If we have a payload to send, we'll need to send that separately
    if (payload != NULL) {
        packet = enet_packet_create(payload, payloadLength, ENET_PACKET_FLAG_RELIABLE);
        if (packet == NULL) {
            goto Exit;
        }

        // Send the payload
        if (enet_peer_send(peer, 0, packet) < 0) {
            enet_packet_destroy(packet);
            goto Exit;
        }
        
        enet_host_flush(client);
    }
    
    // Wait for a reply
    if (serviceEnetHost(client, &event, RTSP_RECEIVE_TIMEOUT_SEC * 1000) <= 0 ||
        event.type != ENET_EVENT_TYPE_RECEIVE) {
        Limelog("Failed to receive RTSP reply: %d\n", LastSocketFail());
        goto Exit;
    }

    responseBuffer = malloc(event.packet->dataLength);
    if (responseBuffer == NULL) {
        Limelog("Failed to allocate RTSP response buffer\n");
        enet_packet_destroy(event.packet);
        goto Exit;
    }

    // Copy the data out and destroy the packet
    memcpy(responseBuffer, event.packet->data, event.packet->dataLength);
    offset = (int) event.packet->dataLength;
    enet_packet_destroy(event.packet);

    // Wait for the payload if we're expecting some
    if (expectingPayload) {
        // The payload comes in a second packet
        if (serviceEnetHost(client, &event, RTSP_RECEIVE_TIMEOUT_SEC * 1000) <= 0 ||
            event.type != ENET_EVENT_TYPE_RECEIVE) {
            Limelog("Failed to receive RTSP reply payload: %d\n", LastSocketFail());
            goto Exit;
        }

        responseBuffer = extendBuffer(responseBuffer, event.packet->dataLength + offset);
        if (responseBuffer == NULL) {
            Limelog("Failed to extend RTSP response buffer\n");
            enet_packet_destroy(event.packet);
            goto Exit;
        }

        // Copy the payload out to the end of the response buffer and destroy the packet
        memcpy(&responseBuffer[offset], event.packet->data, event.packet->dataLength);
        offset += (int) event.packet->dataLength;
        enet_packet_destroy(event.packet);
    }
        
    if (parseRtspMessage(response, responseBuffer, offset) == RTSP_ERROR_SUCCESS) {
        // Successfully parsed response
        ret = true;
    }
    else {
        Limelog("Failed to parse RTSP response\n");
    }

Exit:
    // Swap back the payload pointer to avoid leaking memory later
    request->payload = payload;
    request->payloadLength = payloadLength;

    // Free the serialized buffer
    if (serializedMessage != NULL) {
        free(serializedMessage);
    }

    // Free the response buffer
    if (responseBuffer != NULL) {
        free(responseBuffer);
    }

    return ret;
}

// Send RTSP message and get response over TCP
typedef struct {
    int (*open)(bool);
    bool (*interrupted)(void);
    uint8_t *(*serialize)(void *,int *);
    void (*release)(void *);
    void (*preconnect)(void *);
    bool (*finish)(void *,int,uint8_t *,int);
} PlRtspTcp;
extern bool pl_rtsp_tcp(void *,bool,const PlRtspTcp *,int *);
typedef struct {PRTSP_MESSAGE request,response;} TcpContext;

static int openRtspSocket(bool pending) {
    int fd;
    if (pending) {
        fd=finishTcpConnect(nextHandshakeSocket,RTSP_CONNECT_TIMEOUT_SEC);
        nextHandshakeSocket=INVALID_SOCKET;
    } else fd=connectTcpSocket(&RemoteAddr,AddrLen,RtspPortNumber,RTSP_CONNECT_TIMEOUT_SEC);
    if (fd != INVALID_SOCKET) return fd;
    int error=LastSocketError();
    return error ? -error : -EIO;
}
static bool rtspInterrupted(void) {return ConnectionInterrupted;}
static uint8_t *sealRequest(void *ctx,int *size) {
    return (uint8_t*)sealRtspMessage(((TcpContext*)ctx)->request,size);
}
static void preconnectRtsp(void *ctx) {
    if (IS_SUNSHINE() && !ConnectionInterrupted && strcmp(((TcpContext*)ctx)->request->message.request.command,"PLAY")!=0)
        nextHandshakeSocket=beginTcpConnect(&RemoteAddr,AddrLen,RtspPortNumber);
}
static bool finishRtsp(void *ctx,int fd,uint8_t *bytes,int size) {
    if (!LocalAddr.ss_family) {
        SOCKADDR_LEN len=sizeof(LocalAddr);
        if (getsockname(fd,(struct sockaddr*)&LocalAddr,&len)<0) memset(&LocalAddr,0,sizeof(LocalAddr));
    }
    TcpContext *transaction = (TcpContext*)ctx;
    if (!unsealRtspMessage((char*)bytes,size,transaction->response)) return false;
    if (encryptedRtspEnabled) {
        const char *sent = getOptionContent(transaction->request->options, "CSeq");
        const char *reply = getOptionContent(transaction->response->options, "CSeq");
        if (!sent || !reply || strcmp(sent,reply)) {
            freeMessage(transaction->response);
            return false;
        }
    }
    return true;
}
static bool transactRtspMessageTcp(PRTSP_MESSAGE request,PRTSP_MESSAGE response,int *error) {
    static const PlRtspTcp ops={openRtspSocket,rtspInterrupted,sealRequest,free,preconnectRtsp,finishRtsp};
    TcpContext ctx={request,response};
    return pl_rtsp_tcp(&ctx,nextHandshakeSocket!=INVALID_SOCKET,&ops,error);
}

static bool transactRtspMessage(PRTSP_MESSAGE request, PRTSP_MESSAGE response, bool expectingPayload, int* error) {
    if (ConnectionInterrupted) {
        *error = -1;
        return false;
    }

    if (useEnet) {
        return transactRtspMessageEnet(request, response, expectingPayload, error);
    }
    else {
        return transactRtspMessageTcp(request, response, error);
    }
}

// Send RTSP OPTIONS request
static bool requestOptions(PRTSP_MESSAGE response, int* error) {
    RTSP_MESSAGE request;
    bool ret;

    *error = -1;

    ret = initializeRtspRequest(&request, "OPTIONS", rtspTargetUrl);
    if (ret) {
        ret = transactRtspMessage(&request, response, false, error);
        freeMessage(&request);
    }

    return ret;
}

// Send RTSP DESCRIBE request
static bool requestDescribe(PRTSP_MESSAGE response, int* error) {
    RTSP_MESSAGE request;
    bool ret;

    *error = -1;

    ret = initializeRtspRequest(&request, "DESCRIBE", rtspTargetUrl);
    if (ret) {
        if (addOption(&request, "Accept",
            "application/sdp") &&
            addOption(&request, "If-Modified-Since",
                "Thu, 01 Jan 1970 00:00:00 GMT")) {
            ret = transactRtspMessage(&request, response, true, error);
        }
        else {
            ret = false;
        }
        freeMessage(&request);
    }

    return ret;
}

// Send RTSP SETUP request
static bool setupStream(PRTSP_MESSAGE response, char* target, int* error) {
    RTSP_MESSAGE request;
    bool ret;
    char* transportValue;

    *error = -1;

    ret = initializeRtspRequest(&request, "SETUP", target);
    if (ret) {
        if (hasSessionId) {
            if (!addOption(&request, "Session", sessionIdString)) {
                ret = false;
                goto FreeMessage;
            }
        }

        if (AppVersionQuad[0] >= 6) {
            // It looks like GFE doesn't care what we say our port is but
            // we need to give it some port to successfully complete the
            // handshake process.
            transportValue = "unicast;X-GS-ClientPort=50000-50001";
        }
        else {
            transportValue = " ";
        }
        
        if (addOption(&request, "Transport", transportValue) &&
            addOption(&request, "If-Modified-Since",
                "Thu, 01 Jan 1970 00:00:00 GMT")) {
            ret = transactRtspMessage(&request, response, false, error);
        }
        else {
            ret = false;
        }

    FreeMessage:
        freeMessage(&request);
    }

    return ret;
}

// Send RTSP PLAY request
static bool playStream(PRTSP_MESSAGE response, char* target, int* error) {
    RTSP_MESSAGE request;
    bool ret;

    *error = -1;

    ret = initializeRtspRequest(&request, "PLAY", target);
    if (ret != 0) {
        if (addOption(&request, "Session", sessionIdString)) {
            ret = transactRtspMessage(&request, response, false, error);
        }
        else {
            ret = false;
        }
        freeMessage(&request);
    }

    return ret;
}

// Send RTSP ANNOUNCE message
static bool sendVideoAnnounce(PRTSP_MESSAGE response, int* error) {
    RTSP_MESSAGE request;
    bool ret;
    int payloadLength;
    char payloadLengthStr[16];

    *error = -1;

    ret = initializeRtspRequest(&request, "ANNOUNCE",
                                APP_VERSION_AT_LEAST(7, 1, 431) ? controlStreamId : "streamid=video");
    if (ret) {
        ret = false;

        if (!addOption(&request, "Session", sessionIdString) ||
            !addOption(&request, "Content-type", "application/sdp")) {
            goto FreeMessage;
        }

        request.payload = getSdpPayloadForStreamConfig(rtspClientVersion, &payloadLength);
        if (request.payload == NULL) {
            goto FreeMessage;
        }
        request.flags |= FLAG_ALLOCATED_PAYLOAD;
        request.payloadLength = payloadLength;

        snprintf(payloadLengthStr, sizeof(payloadLengthStr), "%d", payloadLength);
        if (!addOption(&request, "Content-length", payloadLengthStr)) {
            goto FreeMessage;
        }

        ret = transactRtspMessage(&request, response, false, error);

    FreeMessage:
        freeMessage(&request);
    }

    return ret;
}

static bool parseUrlAddrFromRtspUrlString(const char* rtspUrlString, char* destination, size_t destinationLength) {
    char* rtspUrlScratchBuffer;
    char* portSeparator;
    char* v6EscapeEndChar;
    char* urlPathSeparator;
    int prefixLen;

    // Create a copy that we can modify
    rtspUrlScratchBuffer = strdup(rtspUrlString);
    if (rtspUrlScratchBuffer == NULL) {
        return false;
    }

    // If we have a v6 address, we want to stop one character after the closing ]
    // If we have a v4 address, we want to stop at the port separator
    portSeparator = strrchr(rtspUrlScratchBuffer, ':');
    v6EscapeEndChar = strchr(rtspUrlScratchBuffer, ']');

    // Count the prefix length to skip past the initial rtsp:// or rtspru:// part
    for (prefixLen = 2; rtspUrlScratchBuffer[prefixLen - 2] != 0 && (rtspUrlScratchBuffer[prefixLen - 2] != '/' || rtspUrlScratchBuffer[prefixLen - 1] != '/'); prefixLen++);

    // If we hit the end of the string prior to parsing the prefix, we cannot proceed
    if (rtspUrlScratchBuffer[prefixLen - 2] == 0) {
        free(rtspUrlScratchBuffer);
        return false;
    }

    // Look for a slash at the end of the host portion of the URL (may not be present)
    urlPathSeparator = strchr(rtspUrlScratchBuffer + prefixLen, '/');

    // Check for a v6 address first since they also have colons
    if (v6EscapeEndChar) {
        // Terminate the string at the next character
        *(v6EscapeEndChar + 1) = 0;
    }
    else if (portSeparator) {
        // Terminate the string prior to the port separator
        *portSeparator = 0;
    }
    else if (urlPathSeparator) {
        // Terminate the string prior to the path separator
        *urlPathSeparator = 0;
    }

    if (!PltSafeStrcpy(destination, destinationLength, rtspUrlScratchBuffer + prefixLen)) {
        free(rtspUrlScratchBuffer);
        return false;
    }

    free(rtspUrlScratchBuffer);
    return true;
}

// SDP attributes are in the form:
// a=x-nv-bwe.bwuSafeZoneLowLimit:70\r\n
bool parseSdpAttributeToUInt(const char* payload, const char* name, unsigned int* val) {
    // Find the entry for the specified attribute name
    char* attribute = strstr(payload, name);
    if (!attribute) {
        return false;
    }

    // Locate the start of the value
    char* valst = strstr(attribute, ":");
    if (!valst) {
        return false;
    }

    // Read the integer up to the newline at the end of the SDP attribute
    *val = strtoul(valst + 1, NULL, 0);

    return true;
}

bool parseSdpAttributeToInt(const char* payload, const char* name, int* val) {
    // Find the entry for the specified attribute name
    char* attribute = strstr(payload, name);
    if (!attribute) {
        return false;
    }

    // Locate the start of the value
    char* valst = strstr(attribute, ":");
    if (!valst) {
        return false;
    }

    // Read the integer up to the newline at the end of the SDP attribute
    *val = strtol(valst + 1, NULL, 0);

    return true;
}

// Perform RTSP Handshake with the streaming server machine as part of the connection process
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

int performRtspHandshake(PSERVER_INFORMATION serverInfo) {
    int ret;

    LC_ASSERT(RtspPortNumber != 0);

    // Initialize global state
    useEnet = (AppVersionQuad[0] >= 5) && (AppVersionQuad[0] <= 7) && (AppVersionQuad[2] < 404);
    lastAuthenticatedReply = 0;
    currentSeqNumber = 1;
    hasSessionId = false;
    controlStreamId = APP_VERSION_AT_LEAST(7, 1, 431) ? "streamid=control/13/0" : "streamid=control/1/0";
    AudioEncryptionEnabled = false;
    encryptedRtspEnabled = serverInfo->rtspSessionUrl && strstr(serverInfo->rtspSessionUrl, "rtspenc://");

    // HACK: In order to get GFE to respect our request for a lower audio bitrate, we must
    // fake our target address so it doesn't match any of the PC's local interfaces. It seems
    // that the only way to get it to give you "low quality" stereo audio nowadays is if it
    // thinks you are remote (target address != any local address).
    //
    // We will enable high quality audio if the following are all true:
    // 1. Video bitrate is higher than 15 Mbps (to ensure most bandwidth is reserved for video)
    // 2. The audio decoder has not declared that it is slow
    // 3. The stream is either local or not surround sound (to prevent MTU issues over the Internet)
    LC_ASSERT(StreamConfig.streamingRemotely != STREAM_CFG_AUTO);
    if (StreamConfig.bitrate >= HIGH_AUDIO_BITRATE_THRESHOLD &&
            (AudioCallbacks.capabilities & CAPABILITY_SLOW_OPUS_DECODER) == 0 &&
            (StreamConfig.streamingRemotely != STREAM_CFG_REMOTE || CHANNEL_COUNT_FROM_AUDIO_CONFIGURATION(StreamConfig.audioConfiguration) <= 2)) {
        // If we have an RTSP URL string and it was successfully parsed and copied, use that string
        if (serverInfo->rtspSessionUrl == NULL ||
                !parseUrlAddrFromRtspUrlString(serverInfo->rtspSessionUrl, urlAddr, sizeof(urlAddr)) ||
                !PltSafeStrcpy(rtspTargetUrl, sizeof(rtspTargetUrl), serverInfo->rtspSessionUrl)) {
            // If an RTSP URL string was not provided or failed to parse, we will construct one now as best we can.
            //
            // NB: If the remote address is not a LAN address, the host will likely not enable high quality
            // audio since it only does that for local streaming normally. We can avoid this limitation,
            // but only if the caller gave us the RTSP session URL that it received from the host during launch.
            addrToUrlSafeString(&RemoteAddr, urlAddr, sizeof(urlAddr));
            snprintf(rtspTargetUrl, sizeof(rtspTargetUrl), "rtsp%s://%s:%u", useEnet ? "ru" : "", urlAddr, RtspPortNumber);
        }
    }
    else {
        PltSafeStrcpy(urlAddr, sizeof(urlAddr), "0.0.0.0");
        snprintf(rtspTargetUrl, sizeof(rtspTargetUrl), "rtsp%s://%s:%u", useEnet ? "ru" : "", urlAddr, RtspPortNumber);
    }

    switch (AppVersionQuad[0]) {
        case 3:
            rtspClientVersion = 10;
            break;
        case 4:
            rtspClientVersion = 11;
            break;
        case 5:
            rtspClientVersion = 12;
            break;
        case 6:
            // Gen 6 has never been seen in the wild
            rtspClientVersion = 13;
            break;
        case 7:
        default:
            rtspClientVersion = 14;
            break;
    }
    
    // Setup ENet if required by this GFE version
    if (useEnet) {
        ENetAddress address;
        ENetEvent event;
        
        enet_address_set_address(&address, (struct sockaddr *)&RemoteAddr, AddrLen);
        enet_address_set_port(&address, RtspPortNumber);
        
        // Create a client that can use 1 outgoing connection and 1 channel
        client = enet_host_create(RemoteAddr.ss_family, NULL, 1, 1, 0, 0);
        if (client == NULL) {
            ret = -1;
            goto Exit;
        }
    
        // Connect to the host
        peer = enet_host_connect(client, &address, 1, 0);
        if (peer == NULL) {
            enet_host_destroy(client);
            client = NULL;
            ret = -1;
            goto Exit;
        }
    
        // Wait for the connect to complete
        if (serviceEnetHost(client, &event, RTSP_CONNECT_TIMEOUT_SEC * 1000) <= 0 ||
            event.type != ENET_EVENT_TYPE_CONNECT) {
            Limelog("RTSP: Failed to connect to UDP port %u: error %d\n", RtspPortNumber, LastSocketFail());
            enet_peer_reset(peer);
            peer = NULL;
            enet_host_destroy(client);
            client = NULL;
            ret = -1;
            goto Exit;
        }

        // Ensure the connect verify ACK is sent immediately
        enet_host_flush(client);
    }

    ret = pl_rtsp_handshake(serverInfo, AppVersionQuad[0], APP_VERSION_AT_LEAST(7, 1, 431), exchangeRustRtsp);
Exit:
    // Cleanup the ENet stuff
    if (nextHandshakeSocket != INVALID_SOCKET) {
        closeSocket(nextHandshakeSocket);
        nextHandshakeSocket = INVALID_SOCKET;
    }
    if (useEnet) {
        if (peer != NULL) {
            enet_peer_disconnect_now(peer, 0);
            peer = NULL;
        }
        
        if (client != NULL) {
            enet_host_destroy(client);
            client = NULL;
        }
    }

    if (sessionIdString != NULL) {
        free(sessionIdString);
        sessionIdString = NULL;
    }


    return ret;
}
