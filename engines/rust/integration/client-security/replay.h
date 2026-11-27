#pragma once
#include <stdbool.h>
#include <stdint.h>

// One receive window per control channel direction and session, on its receive thread.
typedef struct { uint64_t seen; uint32_t newest; } PlReplayWindow;
static inline bool pl_accept_authenticated(PlReplayWindow *window, uint32_t seq) {
    if (!window->seen || seq > window->newest) {
        uint32_t advance = seq - window->newest;
        window->seen = (advance >= 64 ? 0 : window->seen << advance) | 1;
        window->newest = seq;
        return true;
    }
    uint32_t behind = window->newest - seq;
    if (behind >= 64 || (window->seen & (UINT64_C(1) << behind))) return false;
    window->seen |= UINT64_C(1) << behind;
    return true;
}
