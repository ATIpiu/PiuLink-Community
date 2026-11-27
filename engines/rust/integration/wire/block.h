#pragma once
#include <stdint.h>
#include <stdbool.h>
typedef struct {
    uint64_t seen[64];
    void *entries[1024];
    uint16_t base, highest;
    bool fast;
} PlVideoBlock;
void pl_block_reset(PlVideoBlock *, uint16_t);
uint8_t pl_block_accept(PlVideoBlock *, uint16_t, uint32_t *, uint32_t, uint32_t, void *);
void *pl_block_entry(const PlVideoBlock *, uint32_t);
