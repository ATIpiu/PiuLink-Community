#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
bool pl_rtsp_record(const uint8_t *,size_t,bool,bool,uint32_t,uint8_t *,size_t);
#ifdef __cplusplus
}
#endif
