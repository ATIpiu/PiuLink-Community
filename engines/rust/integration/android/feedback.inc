static bool feedbackStopped(void) { return PltIsThreadInterrupted(&lossStatsThread); }
static void feedbackSleep(uint32_t ms) { PltSleepMsInterruptible(&lossStatsThread, ms); }
static int sendQueuedFec(void) {
    PQUEUED_FRAME_FEC_STATUS item;
    if (LbqPollQueueElement(&frameFecStatusQueue,(void **)&item) != LBQ_SUCCESS) return 0;
    bool ok=sendMessageEnet(SS_FRAME_FEC_PTYPE,sizeof(item->fecStatus),&item->fecStatus,
                            CTRL_CHANNEL_GENERIC, ENET_PACKET_FLAG_UNSEQUENCED,
                            LbqGetItemCount(&frameFecStatusQueue)>0);
    free(item);
    return ok ? 1 : -1;
}
static bool sendFeedback(uint16_t type,const uint8_t *data,uint16_t size,bool reliable) {
    return sendMessageAndForget(type,size,data,CTRL_CHANNEL_GENERIC,reliable ? ENET_PACKET_FLAG_RELIABLE : 0,false);
}
static void lossStatsThreadFunc(void* context) {
    const PlFeedbackOps ops={feedbackStopped,sendQueuedFec,sendFeedback,feedbackSleep};
    if (!pl_loss_feedback(usePeriodicPing,IS_SUNSHINE(),packetTypes[IDX_LOSS_STATS],&ops))
        ListenerCallbacks.connectionTerminated(LastSocketFail());
}
static bool sendUrgent(uint16_t type,const void *data,uint16_t size) {
    return sendMessageAndDiscardReply(type,size,data,CTRL_CHANNEL_URGENT,ENET_PACKET_FLAG_RELIABLE,false);
}
static void requestIdrFrame(void) {
    if (!pl_request_reference(true,supportsIdrFrameRequest,0,0,packetTypes[IDX_REQUEST_IDR_FRAME],
        packetTypes[IDX_INVALIDATE_REF_FRAMES],preconstructedPayloads[IDX_REQUEST_IDR_FRAME],
        payloadLengths[IDX_REQUEST_IDR_FRAME],sendUrgent)) ListenerCallbacks.connectionTerminated(LastSocketFail());
    else Limelog("IDR frame request sent\n");
}
static void requestInvalidateReferenceFrames(uint32_t startFrame, uint32_t endFrame) {
    if (!pl_request_reference(false,false,startFrame,endFrame,0,packetTypes[IDX_INVALIDATE_REF_FRAMES],
                              NULL,0,sendUrgent)) ListenerCallbacks.connectionTerminated(LastSocketFail());
    else Limelog("Invalidate reference frame request sent (%u to %u)\n",startFrame,endFrame);
}
