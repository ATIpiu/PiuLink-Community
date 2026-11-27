#pragma once
#include <stdint.h>
// Include after nvEncodeAPI.h. The driver fills its SDK-versioned structures in-place.
struct PlNvOps {
    PNVENCMAPINPUTRESOURCE map;
    PNVENCENCODEPICTURE submit;
    PNVENCLOCKBITSTREAM lock;
    PNVENCUNLOCKBITSTREAM unlock;
    PNVENCUNMAPINPUTRESOURCE unmap;
    void (*prepare)(void*);
    bool (*wait)(void*, uint32_t);
    bool (*copy)(void*);
    void (*report)(void*, uint8_t, int32_t);
};
struct PlNvFrame {
    void *encoder, *ctx, *map, *picture, *lock;
    void *const *resource;
    void *output;
    bool asynchronous;
};
extern "C" bool pl_encode_frame(const PlNvFrame*,const PlNvOps*);
extern "C" bool pl_nv_invalidate(void*,PNVENCINVALIDATEREFFRAMES,uint64_t,uint64_t,uint64_t,uint32_t,uint64_t*,uint64_t*,bool*);
