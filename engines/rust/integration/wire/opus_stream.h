#pragma once
#include <stddef.h>
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
typedef struct { int32_t rate, channels, streams, coupled, samples; uint8_t mapping[8]; } PlOpusFormat;
typedef struct {
    void *(*create)(const PlOpusFormat*, int32_t, int32_t*);
    int32_t (*configure)(void*, int32_t, int32_t);
    void (*destroy)(void*);
    int32_t (*read)(void*, const float**, size_t*);
    int32_t (*encode)(void*, const float*, int32_t, uint8_t*, int32_t);
    int32_t (*send)(void*, const uint8_t*, int32_t);
} PlEncoderOps;
typedef struct {
    void *(*create)(const PlOpusFormat*, int32_t*);
    int32_t (*decode)(void*, const uint8_t*, int32_t, int16_t*, int32_t, int32_t);
    void (*destroy)(void*);
} PlDecoderOps;
typedef struct PlOpusDecoder PlOpusDecoder;
int32_t pl_opus_encode_stream(const PlOpusFormat*, int32_t, const PlEncoderOps*, void*);
PlOpusDecoder *pl_opus_decoder_new(const PlOpusFormat*,const PlDecoderOps*,int32_t*);
int32_t pl_opus_decode(PlOpusDecoder*,const uint8_t*,int32_t,const int16_t**);
void pl_opus_decoder_free(PlOpusDecoder*);
#ifdef __cplusplus
}
#endif
