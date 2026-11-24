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
}
