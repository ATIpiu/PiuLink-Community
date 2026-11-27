#pragma once
#include <stddef.h>
#include <stdint.h>
struct PlUdpSpan { const uint8_t *data; size_t len; };
struct PlUdpBatch {
    uintptr_t socket;
    const void *target;
    int target_len;
    const uint8_t *source;
    bool source_v6;
    const uint8_t *headers;
    size_t header_size;
    const PlUdpSpan *payloads;
    size_t spans, unit, offset, count;
};
extern "C" bool pl_udp_send(const PlUdpBatch *);
