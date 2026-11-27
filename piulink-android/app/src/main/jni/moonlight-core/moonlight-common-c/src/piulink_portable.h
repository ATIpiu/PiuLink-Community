#pragma once
#include <stdbool.h>
#include <stdint.h>
uint8_t pl_block_readiness(uint32_t total, uint32_t needed, uint32_t pending,
                           uint32_t missing, bool reported, bool reordered);
int32_t pl_video_recover(uint8_t *const *shards, const uint8_t *marks,
                         uint32_t data, uint32_t parity, uint32_t bytes);
