#include "live_capture.h"
#include <cassert>
#include <future>
#include <thread>

int main() {
  video::live_capture_t route("primary");
  assert(!route.select("secondary"));
  route.captured("primary");
  auto switch_to = [&](const char *target, bool available) {
    auto request = std::async(std::launch::async, [&] { return route.select(target); });
    for (int i = 0; i < 1000 && !route.pending; ++i)
      std::this_thread::sleep_for(std::chrono::milliseconds(1));
    assert(route.pending && route.target() == target);
    if (available) route.captured(target);
    else {
      assert(route.reject(target));
      route.captured(route.target());
    }
    assert(request.get() == available);
    assert(!route.pending);
  };
  switch_to("secondary", true);
  switch_to("missing", false);
  assert(route.target() == "secondary");
  switch_to("primary", true);
  assert(route.select("primary"));

  auto &size = route.resolution;
  int w = 1920, h = 1080, fps = 60, exact = 5994;
  const unsigned initial = size.configure(w, h, fps, exact);
  assert(!size.resize(3840, 2160, 30));
  size.encoded(initial, w, h, fps);
  auto larger = std::async(std::launch::async, [&] { return size.resize(3840, 2160, 30); });
  while (size.generation == initial) std::this_thread::yield();
  auto next = size.configure(w, h, fps, exact);
  assert(w == 3840 && h == 2160 && fps == 30 && exact == 0);
  size.encoded(initial, 1920, 1080, 60);
  size.encoded(next, 1920, 1080, 60);
  size.encoded(next, 3840, 2160, 60);
  assert(larger.wait_for(std::chrono::milliseconds(20)) == std::future_status::timeout);
  size.encoded(next, w, h, fps);
  assert(larger.get() && size.resize(3840, 2160, 30));
  auto smaller = std::async(std::launch::async, [&] { return size.resize(1920, 1080, 0); });
  while (size.generation == next) std::this_thread::yield();
  assert(size.rollback(size.generation));
  assert(!smaller.get());
  next = size.configure(w, h, fps, exact);
  assert(w == 3840 && h == 2160 && fps == 30 && exact == 0);
  size.encoded(next, w, h, fps);
  assert(size.current(w, h, fps) && size.resize(3840, 2160, 30));

  // A rate-only request must wait for a matching keyframe even at identical dimensions.
  auto rate_only = std::async(std::launch::async, [&] { return size.resize(w, h, 60); });
  while (size.generation == next) std::this_thread::yield();
  next = size.configure(w, h, fps, exact);
  assert(w == 3840 && h == 2160 && fps == 60);
  size.encoded(next, w, h, 30);
  assert(rate_only.wait_for(std::chrono::milliseconds(20)) == std::future_status::timeout);
  size.encoded(next, w, h, fps);
  assert(rate_only.get());

  // Old clients omit the rate when changing only the size.
  auto legacy = std::async(std::launch::async, [&] { return size.resize(1920, 1080, 0); });
  while (size.generation == next) std::this_thread::yield();
  next = size.configure(w, h, fps, exact);
  assert(w == 1920 && h == 1080 && fps == 60);
  size.encoded(next, w, h, fps);
  assert(legacy.get() && size.current(w, h, fps) && fps == 60);
  assert(!size.resize(w, h, 24));
  size.configure(w, h, fps, exact);
  assert(w == 1920 && h == 1080 && fps == 60);
}
