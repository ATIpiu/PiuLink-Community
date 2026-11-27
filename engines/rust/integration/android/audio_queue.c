#include "Limelight-internal.h"

extern void* pl_audio_queue_new(bool bypass);
extern int pl_audio_queue_add(void*, const uint8_t*, uint16_t, uint32_t, uint64_t);
extern void* pl_audio_queue_pop(void*, uint16_t, void* (*)(size_t), uint16_t*);
extern void pl_audio_queue_free(void*, uint64_t*);

void RtpaInitializeQueue(PRTP_AUDIO_QUEUE queue) {
    queue->rust = pl_audio_queue_new(!APP_VERSION_AT_LEAST(7, 1, 415));
}

void RtpaCleanupQueue(PRTP_AUDIO_QUEUE queue) {
    uint64_t stats[4] = {0};
    pl_audio_queue_free(queue->rust, stats);
    queue->rust = NULL;
    Limelog("Rust audio queue direct=%llu buffered=%llu recovered=%llu concealed=%llu\n",
            (unsigned long long)stats[0], (unsigned long long)stats[1],
            (unsigned long long)stats[2], (unsigned long long)stats[3]);
}

int RtpaAddPacket(PRTP_AUDIO_QUEUE queue, PRTP_PACKET packet, uint16_t length) {
    return pl_audio_queue_add(queue->rust, (const uint8_t*)packet, length, AudioPacketDuration, PltGetMillis());
}

PRTP_PACKET RtpaGetQueuedPacket(PRTP_AUDIO_QUEUE queue, uint16_t prefix, uint16_t* length) {
    return pl_audio_queue_pop(queue->rust, prefix, malloc, length);
}
