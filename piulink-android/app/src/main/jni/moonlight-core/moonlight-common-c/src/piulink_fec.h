#pragma once
#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>
#ifdef __cplusplus
extern "C" {
#endif
typedef struct { uint16_t index, data; uint8_t percentage, current, last; } PlVideoFec;
uint32_t pl_fec_info(uint32_t index, uint32_t data, uint32_t percentage);
uint8_t pl_fec_blocks(uint8_t current, uint8_t last);
typedef struct { size_t lengths[4]; uint32_t blocks, percentage; } PlFrameLayout;
bool pl_frame_layout(size_t, size_t, uint32_t, bool, PlFrameLayout *);
typedef struct { uint8_t size, kind; uint16_t latency, tail; } PlFrameHeader;
size_t pl_packetized_size(size_t, size_t, size_t, size_t);
bool pl_packetize(uint8_t *, size_t, const uint8_t *, size_t, const uint8_t *, size_t, size_t, size_t);
typedef struct { int16_t slot; uint16_t mask; uint32_t buttons; uint8_t lt, rt; int16_t axes[4]; } PlPad;
int16_t pl_pad_write(uint8_t *, size_t, int32_t, bool, const PlPad *);
bool pl_pad_read(const uint8_t *, size_t, PlPad *);
enum { PL_BATCH_MERGED, PL_BATCH_SKIP, PL_BATCH_STOP };
uint8_t pl_batch_input(uint8_t *, size_t, const uint8_t *, size_t);
typedef struct { int16_t x, y, w, h; uint8_t button; bool release; } PlPointer;
bool pl_pointer_relative(uint8_t *, int32_t *, int32_t *, bool);
void pl_pointer_absolute(uint8_t *, int32_t, int32_t, int32_t, int32_t);
void pl_pointer_button(uint8_t *, uint8_t, uint8_t, bool);
void pl_pointer_scroll(uint8_t *, int16_t, bool);
void pl_pointer_hscroll(uint8_t *, int16_t);
bool pl_pointer_read(const uint8_t *, size_t, PlPointer *);
int32_t pl_scroll_delta(int16_t, bool, int32_t *);
typedef struct { uint8_t code, flags, modifiers; bool release; } PlKey;
void pl_write_key(uint8_t *, uint16_t, uint8_t, uint8_t, uint8_t, bool);
bool pl_read_key(const uint8_t *, PlKey *);
bool pl_write_header(uint8_t *, size_t, uint32_t, bool, bool, int64_t);
bool pl_read_header(const uint8_t *, uint32_t, const int32_t *, PlFrameHeader *);
int32_t pl_opus_crypt(const uint8_t*,size_t,const uint8_t*,bool,const uint8_t*,size_t,uint8_t*,size_t);
bool pl_control_record(const uint8_t*,size_t,bool,bool,uint32_t,uint8_t*,size_t);
typedef struct PlVideoCipher PlVideoCipher;
PlVideoCipher *pl_video_cipher_new(const uint8_t*,size_t);
void pl_video_cipher_free(PlVideoCipher*);
bool pl_video_cipher_seal(PlVideoCipher*,uint32_t,uint8_t*,uint8_t*,size_t);
typedef struct PlHostSession PlHostSession;
PlHostSession *pl_host_new(void);
void pl_host_free(PlHostSession *);
bool pl_host_begin(PlHostSession *);
bool pl_host_publish(PlHostSession *, bool);
bool pl_host_wait(PlHostSession *);
int32_t pl_host_state(PlHostSession *);
bool pl_host_stop(PlHostSession *);
bool pl_host_finish(PlHostSession *);
uint32_t pl_host_active(void);
typedef struct { uint32_t per_ms, batch, group, sent; } PlPacing;
bool pl_pacing_init(PlPacing *, size_t);
bool pl_pacing_due(PlPacing *, uint32_t, bool, int64_t *);
int64_t pl_pacing_end(const PlPacing *);
void pl_video_source(uint8_t *, uint32_t, uint32_t, uint32_t, uint32_t, uint8_t, uint8_t);
void pl_video_shard(uint8_t *, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t, uint32_t, uint8_t, uint8_t);
bool pl_valid_input(const uint8_t *, size_t);
void pl_audio_iv(uint8_t *, uint32_t, uint16_t);
bool pl_audio_data(uint8_t *, uint16_t *, uint32_t *, uint32_t);
void pl_audio_parity(uint8_t *, uint16_t, uint32_t, uint32_t, uint8_t);
int32_t pl_audio_encode(uint8_t *const *, uint32_t);
int32_t pl_audio_recover(uint8_t *const *, const uint8_t *, uint32_t);
int32_t pl_video_encode(uint8_t *const *, uint32_t, uint32_t, uint32_t);
int32_t pl_video_recover(uint8_t *const *, const uint8_t *, uint32_t, uint32_t, uint32_t);
PlVideoFec pl_fec_read(uint32_t info, uint8_t blocks);
#ifdef __cplusplus
}
#endif
