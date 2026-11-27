#pragma once
#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>
typedef struct {
    bool (*interrupted)(void);
    uint32_t (*current)(void);
    bool (*queue)(uint8_t*, int, uint8_t*);
} PlVideoBackend;
typedef struct {
    bool (*interrupted)(void);
    int (*push)(uint8_t*, uint16_t);
    uint64_t (*now)(void);
} PlAudioBackend;
void pl_media_reset(bool);
bool pl_media_seen(bool);
void pl_media_keyframe(void);
void pl_media_ping(int, const void*, uint32_t, const uint8_t*, bool (*)(void), void (*)(void));
int pl_receive_video(int,int,size_t,const uint8_t*,bool,const PlVideoBackend*);
int pl_receive_audio(int,uint16_t,uint16_t,uint32_t,bool,void*,const PlAudioBackend*);
