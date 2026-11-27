#include "piulink_content_rate.h"

#include <cassert>
#include <optional>

int main() {
  nvenc::piulink::content_rate_controller_t policy;
  policy.reset(20000, 60);
  policy.set_enabled(true);

  std::optional<uint32_t> reduced;
  for (int i = 0; i < 420 && !reduced; ++i) reduced = policy.observe(400, 24, false);
  assert(reduced && *reduced == 12000);
  policy.applied(*reduced);

  assert(!policy.observe(1000000, 24, true));

  std::optional<uint32_t> restored;
  for (int i = 0; i < 180 && !restored; ++i) restored = policy.observe(30000, 24, false);
  assert(restored && *restored == 20000);

  policy.applied(*restored);
  policy.set_ceiling(8000);
  assert(policy.ceiling_kbps() == 8000);

  policy.set_enabled(false);
  for (int i = 0; i < 300; ++i) assert(!policy.observe(1, 24, false));

  policy.reset(20000, 60);
  policy.set_enabled(true);
  for (int i = 0; i < 420; ++i) assert(!policy.observe(400, 40, false));

  policy.reset(20000, 60);
  policy.set_enabled(true);
  reduced.reset();
  for (int i = 0; i < 420 && !reduced; ++i) reduced = policy.observe(400, 24, false);
  assert(reduced);
  policy.applied(*reduced);
  restored.reset();
  for (int i = 0; i < 180 && !restored; ++i) restored = policy.observe(400, 42, false);
  assert(restored && *restored == 20000);
}
