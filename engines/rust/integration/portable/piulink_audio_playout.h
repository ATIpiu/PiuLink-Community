#pragma once
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif
typedef struct {
    uint64_t previous_us;
    uint64_t expires_us;
    uint32_t burst_frames;
} PiuAudioPlayout;

uint32_t pl_audio_storage_ms(void);
uint32_t pl_audio_trim_frames(PiuAudioPlayout *state, uint64_t now_us, uint32_t rate,
                              uint32_t requested_frames, uint32_t available_frames);
#ifdef __cplusplus
}
#endif
