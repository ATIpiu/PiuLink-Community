#pragma once
#include <cstddef>
#include <cstdint>

struct PlWasapi {
  void *client, *event;
  uint32_t channels, timeout;
  bool continuous;
  float *buffer;
  size_t capacity, *used;
  void *ctx;
  bool (*changed)(void *);
  void (*report)(uint32_t);
};
extern "C" int pl_wasapi_sample(PlWasapi *, float *, size_t);
