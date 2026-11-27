#include "piulink_media.h"
#include "piulink_fec.h"
#include "Limelight-internal.h"

static SOCKET rtpSocket = INVALID_SOCKET;

static LINKED_BLOCKING_QUEUE packetQueue;
static RTP_AUDIO_QUEUE rtpAudioQueue;

static PLT_THREAD udpPingThread;
static PLT_THREAD receiveThread;
static PLT_THREAD decoderThread;

static uint32_t avRiKeyId;

static unsigned short lastSeq;

static bool pingThreadStarted;

#ifdef LC_DEBUG
#define INVALID_OPUS_HEADER 0x00
static uint8_t opusHeaderByte;
#endif

#define MAX_PACKET_SIZE 1400

typedef struct _QUEUE_AUDIO_PACKET_HEADER {
    LINKED_BLOCKING_QUEUE_ENTRY lentry;
    int size;
} QUEUED_AUDIO_PACKET_HEADER, *PQUEUED_AUDIO_PACKET_HEADER;

typedef struct _QUEUED_AUDIO_PACKET {
    QUEUED_AUDIO_PACKET_HEADER header;
    char data[MAX_PACKET_SIZE];
} QUEUED_AUDIO_PACKET, *PQUEUED_AUDIO_PACKET;

static bool pingInterrupted(void) { return PltIsThreadInterrupted(&udpPingThread); }
static void sleepForNextPing(void) { PltSleepMsInterruptible(&udpPingThread, 500); }
static void AudioPingThreadProc(void* context) {
    LC_SOCKADDR peer;
    memcpy(&peer, &RemoteAddr, sizeof(peer));
    SET_PORT(&peer, AudioPortNumber);
    pl_media_ping(rtpSocket, &peer, AddrLen, (const uint8_t*)AudioPingPayload.payload, pingInterrupted, sleepForNextPing);
}

// Initialize the audio stream and start
int initializeAudioStream(void) {
    LbqInitializeLinkedBlockingQueue(&packetQueue, 30);
    RtpaInitializeQueue(&rtpAudioQueue);
    lastSeq = 0;
    pl_media_reset(false);
    pingThreadStarted = false;
#ifdef LC_DEBUG
    opusHeaderByte = INVALID_OPUS_HEADER;
#endif

    // Copy and byte-swap the AV RI key ID used for the audio encryption IV
    memcpy(&avRiKeyId, StreamConfig.remoteInputAesIv, sizeof(avRiKeyId));
    avRiKeyId = BE32(avRiKeyId);

    return 0;
}

// This is called when the RTSP SETUP message is parsed and the audio port
// number is parsed out of it. Alternatively, it's also called if parsing fails
// and will use the well known audio port instead.
int notifyAudioPortNegotiationComplete(void) {
    LC_ASSERT(!pingThreadStarted);
    LC_ASSERT(AudioPortNumber != 0);

    // For GFE 3.22 compatibility, we must start the audio ping thread before the RTSP handshake.
    // It will not reply to our RTSP PLAY request until the audio ping has been received.
    rtpSocket = bindUdpSocket(RemoteAddr.ss_family, &LocalAddr, AddrLen, 0, SOCK_QOS_TYPE_AUDIO);
    if (rtpSocket == INVALID_SOCKET) {
        return LastSocketFail();
    }

    // We may receive audio before our threads are started, but that's okay. We'll
    // drop the first 1 second of audio packets to catch up with the backlog.
    int err = PltCreateThread("AudioPing", AudioPingThreadProc, NULL, &udpPingThread);
    if (err != 0) {
        return err;
    }

    pingThreadStarted = true;
    return 0;
}

static void freePacketList(PLINKED_BLOCKING_QUEUE_ENTRY entry) {
    PLINKED_BLOCKING_QUEUE_ENTRY nextEntry;

    while (entry != NULL) {
        nextEntry = entry->flink;

        // The entry is stored within the data allocation
        free(entry->data);

        entry = nextEntry;
    }
}

// Tear down the audio stream once we're done with it
void destroyAudioStream(void) {
    if (rtpSocket != INVALID_SOCKET) {
        if (pingThreadStarted) {
            PltInterruptThread(&udpPingThread);
            PltJoinThread(&udpPingThread);
        }

        closeSocket(rtpSocket);
        rtpSocket = INVALID_SOCKET;
    }

    freePacketList(LbqDestroyLinkedBlockingQueue(&packetQueue));
    RtpaCleanupQueue(&rtpAudioQueue);
}

static bool queuePacketToLbq(PQUEUED_AUDIO_PACKET* packet) {
    int err;

    do {
        err = LbqOfferQueueItem(&packetQueue, *packet, &(*packet)->header.lentry);
        if (err == LBQ_SUCCESS) {
            // The LBQ owns the buffer now
            *packet = NULL;
        }
        else if (err == LBQ_BOUND_EXCEEDED) {
            Limelog("Audio packet queue overflow\n");

            // The audio queue is full, so free all existing items and try again
            freePacketList(LbqFlushQueueItems(&packetQueue));
        }
    } while (err == LBQ_BOUND_EXCEEDED);

    return err == LBQ_SUCCESS;
}

static void decodeInputData(PQUEUED_AUDIO_PACKET packet) {
    // If the packet size is zero, this is a placeholder for a missing
    // packet. Trigger packet loss concealment logic in libopus by
    // invoking the decoder with a NULL buffer.
    if (packet->header.size == 0) {
        AudioCallbacks.decodeAndPlaySample(NULL, 0);
        return;
    }

    PRTP_PACKET rtp = (PRTP_PACKET)&packet->data[0];
    if (lastSeq != 0 && (unsigned short)(lastSeq + 1) != rtp->sequenceNumber) {
        Limelog("Network dropped audio data (expected %d, but received %d)\n", lastSeq + 1, rtp->sequenceNumber);
    }

    lastSeq = rtp->sequenceNumber;

    if (AudioEncryptionEnabled) {
        // We must have room for the AES padding which may be written to the buffer
        unsigned char decryptedOpusData[ROUND_TO_PKCS7_PADDED_LEN(MAX_PACKET_SIZE)];
        unsigned char iv[16] = { 0 };
        int dataLength = packet->header.size - sizeof(*rtp);

        LC_ASSERT(dataLength <= MAX_PACKET_SIZE);

        // The IV is the avkeyid (equivalent to the rikeyid) +
        // the RTP sequence number, in big endian.
        pl_audio_iv(iv, avRiKeyId, rtp->sequenceNumber);

        dataLength=pl_opus_crypt((const uint8_t*)StreamConfig.remoteInputAesKey,sizeof(StreamConfig.remoteInputAesKey),
            iv,false,(const uint8_t*)(rtp+1),dataLength,decryptedOpusData,sizeof(decryptedOpusData));
        if (dataLength<=0) {
            Limelog("Failed to decrypt audio packet (sequence number: %u)\n", rtp->sequenceNumber);
            LC_ASSERT_VT(false);
            return;
        }

#ifdef LC_DEBUG
        if (opusHeaderByte == INVALID_OPUS_HEADER) {
            opusHeaderByte = decryptedOpusData[0];
            LC_ASSERT_VT(opusHeaderByte != INVALID_OPUS_HEADER);
        }
        else {
            // Opus header should stay constant for the entire stream.
            // If it doesn't, it may indicate that the RtpAudioQueue
            // incorrectly recovered a data shard or the decryption
            // of the audio packet failed. Sunshine violates this for
            // surround sound in some cases, so just ignore it.
            LC_ASSERT_VT(decryptedOpusData[0] == opusHeaderByte || IS_SUNSHINE());
        }
#endif

        AudioCallbacks.decodeAndPlaySample((char*)decryptedOpusData, dataLength);
    }
    else {
#ifdef LC_DEBUG
        if (opusHeaderByte == INVALID_OPUS_HEADER) {
            opusHeaderByte = ((uint8_t*)(rtp + 1))[0];
            LC_ASSERT_VT(opusHeaderByte != INVALID_OPUS_HEADER);
        }
        else {
            // Opus header should stay constant for the entire stream.
            // If it doesn't, it may indicate that the RtpAudioQueue
            // incorrectly recovered a data shard.
            LC_ASSERT_VT(((uint8_t*)(rtp + 1))[0] == opusHeaderByte);
        }
#endif

        AudioCallbacks.decodeAndPlaySample((char*)(rtp + 1), packet->header.size - sizeof(*rtp));
    }
}

static bool rxInterrupted(void) { return PltIsThreadInterrupted(&receiveThread); }
static int deliverRustAudio(uint8_t* base, uint16_t len) {
    PQUEUED_AUDIO_PACKET packet = (PQUEUED_AUDIO_PACKET)base;
    packet->header.size = len;
    if (AudioCallbacks.capabilities & CAPABILITY_DIRECT_SUBMIT) {
        decodeInputData(packet);
        return 0;
    }
    return queuePacketToLbq(&packet) ? 1 : -1;
}
static void AudioReceiveThreadProc(void* context) {
    const PlAudioBackend ops = {rxInterrupted, deliverRustAudio, PltGetMillis};
    int err = pl_receive_audio(rtpSocket, MAX_PACKET_SIZE, offsetof(QUEUED_AUDIO_PACKET,data),
                               AudioPacketDuration, setNonFatalRecvTimeoutMs(rtpSocket, UDP_RECV_POLL_TIMEOUT_MS) < 0,
                               rtpAudioQueue.rust, &ops);
    if (err) ListenerCallbacks.connectionTerminated(err);
}


static void AudioDecoderThreadProc(void* context) {
    int err;
    PQUEUED_AUDIO_PACKET packet;

    while (!PltIsThreadInterrupted(&decoderThread)) {
        err = LbqWaitForQueueElement(&packetQueue, (void**)&packet);
        if (err != LBQ_SUCCESS) {
            // An exit signal was received
            return;
        }

        decodeInputData(packet);

        free(packet);
    }
}

void stopAudioStream(void) {
    if (!pl_media_seen(false)) {
        Limelog("No audio traffic was ever received from the host!\n");
    }

    AudioCallbacks.stop();

    PltInterruptThread(&receiveThread);
    if ((AudioCallbacks.capabilities & CAPABILITY_DIRECT_SUBMIT) == 0) {        
        // Signal threads waiting on the LBQ
        LbqSignalQueueShutdown(&packetQueue);
        PltInterruptThread(&decoderThread);
    }
    
    PltJoinThread(&receiveThread);
    if ((AudioCallbacks.capabilities & CAPABILITY_DIRECT_SUBMIT) == 0) {
        PltJoinThread(&decoderThread);
    }

    AudioCallbacks.cleanup();
}

int startAudioStream(void* audioContext, int arFlags) {
    int err;
    OPUS_MULTISTREAM_CONFIGURATION chosenConfig;

    if (HighQualitySurroundEnabled) {
        LC_ASSERT(HighQualitySurroundSupported);
        LC_ASSERT(HighQualityOpusConfig.channelCount != 0);
        LC_ASSERT(HighQualityOpusConfig.streams != 0);
        chosenConfig = HighQualityOpusConfig;
    }
    else {
        LC_ASSERT(NormalQualityOpusConfig.channelCount != 0);
        LC_ASSERT(NormalQualityOpusConfig.streams != 0);
        chosenConfig = NormalQualityOpusConfig;
    }

    chosenConfig.samplesPerFrame = 48 * AudioPacketDuration;

    err = AudioCallbacks.init(StreamConfig.audioConfiguration, &chosenConfig, audioContext, arFlags);
    if (err != 0) {
        return err;
    }

    AudioCallbacks.start();

    err = PltCreateThread("AudioRecv", AudioReceiveThreadProc, NULL, &receiveThread);
    if (err != 0) {
        AudioCallbacks.stop();
        AudioCallbacks.cleanup();
        return err;
    }

    if ((AudioCallbacks.capabilities & CAPABILITY_DIRECT_SUBMIT) == 0) {
        err = PltCreateThread("AudioDec", AudioDecoderThreadProc, NULL, &decoderThread);
        if (err != 0) {
            AudioCallbacks.stop();
            PltInterruptThread(&receiveThread);
            PltJoinThread(&receiveThread);
                AudioCallbacks.cleanup();
            return err;
        }
    }

    return 0;
}

int LiGetPendingAudioFrames(void) {
    return LbqGetItemCount(&packetQueue);
}

int LiGetPendingAudioDuration(void) {
    return LiGetPendingAudioFrames() * AudioPacketDuration;
}
