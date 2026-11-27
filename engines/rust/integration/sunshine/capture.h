#pragma once
#include <stdint.h>
extern "C" {
struct PlCaptureRate { uint32_t num, den; };
struct PlCaptureOps {
    bool (*current)(void*);
    int64_t (*now)();
    void (*wait)(void*, int64_t);
    int (*take)(void*, int, int64_t*, bool*);
    bool (*push)(void*, bool);
    int (*release)(void*);
};
int pl_dxgi_acquire(void*,bool*,uint32_t,void*,void**,int32_t*);
int pl_dxgi_release(void*,bool*,int32_t*);
PlCaptureRate pl_capture_rate(PlCaptureRate,PlCaptureRate,uint32_t,uint32_t);
int pl_capture_run(void*,PlCaptureRate,const PlCaptureOps*);
}
