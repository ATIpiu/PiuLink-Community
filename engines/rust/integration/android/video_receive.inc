static bool rxInterrupted(void) { return PltIsThreadInterrupted(&receiveThread); }
static uint32_t currentRustFrame(void) { return RtpvGetCurrentFrameNumber(&rtpQueue); }
static bool queueRustVideo(uint8_t* data, int size, uint8_t* entry) {
    return RtpvAddPacket(&rtpQueue, (PRTP_PACKET)data, size, (PRTPV_QUEUE_ENTRY)entry) == RTPF_RET_QUEUED;
}
static void VideoReceiveThreadProc(void* context) {
    const PlVideoBackend ops = {rxInterrupted, currentRustFrame, queueRustVideo};
    int err = pl_receive_video(rtpSocket, StreamConfig.packetSize + MAX_RTP_HEADER_SIZE, sizeof(RTPV_QUEUE_ENTRY),
                               (EncryptionFeaturesEnabled & SS_ENC_VIDEO) ? (const uint8_t*)StreamConfig.remoteInputAesKey : NULL,
                               setNonFatalRecvTimeoutMs(rtpSocket, UDP_RECV_POLL_TIMEOUT_MS) < 0, &ops);
    if (err) ListenerCallbacks.connectionTerminated(err);
}
