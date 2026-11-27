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
