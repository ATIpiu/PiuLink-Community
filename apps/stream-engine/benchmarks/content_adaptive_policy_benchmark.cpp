#include "piulink_content_rate.h"

#include <cstdint>
#include <iostream>
#include <optional>
#include <string>
#include <vector>

struct phase_t {
  std::string name;
  int seconds;
  std::size_t bytes_per_frame;
  uint32_t qp;
};

int main(int argc, char **argv) {
  const uint32_t ceiling = argc > 1 ? static_cast<uint32_t>(std::stoul(argv[1])) : 20000;
  const uint32_t fps = argc > 2 ? static_cast<uint32_t>(std::stoul(argv[2])) : 60;
  const std::vector<phase_t> phases {
    {"motion_warmup", 3, static_cast<std::size_t>(ceiling * 1000 / 8 / fps * 8 / 10), 27},
    {"static_desktop", 12, 400, 24},
    {"motion_resume", 6, static_cast<std::size_t>(ceiling * 1000 / 8 / fps * 8 / 10), 30},
    {"static_return", 8, 400, 24},
  };

  nvenc::piulink::content_rate_controller_t policy;
  policy.reset(ceiling, fps);
  policy.set_enabled(true);
  uint32_t target = ceiling;
  uint64_t target_sum = 0;
  uint64_t frames = 0;
  int transitions = 0;
  int first_reduce = -1;
  int first_restore = -1;

  std::cout << "phase,frame,seconds,target_kbps,event\n";
  for (const auto &phase : phases) {
    for (int frame = 0; frame < phase.seconds * static_cast<int>(fps); ++frame) {
      auto update = policy.observe(phase.bytes_per_frame, phase.qp, false);
      std::string event;
      if (update) {
        target = *update;
        policy.applied(target);
        event = target < ceiling ? "reduce" : "restore";
        if (event == "reduce" && first_reduce < 0) first_reduce = static_cast<int>(frames);
        if (event == "restore" && first_restore < 0) first_restore = static_cast<int>(frames);
        ++transitions;
      }
      target_sum += target;
      ++frames;
      if (!event.empty())
        std::cout << phase.name << ',' << frame << ',' << static_cast<double>(frames) / fps
                  << ',' << target << ',' << event << '\n';
    }
  }
  std::cerr << "{\"ceilingKbps\":" << ceiling
            << ",\"fps\":" << fps
            << ",\"averageTargetKbps\":" << (frames ? target_sum / frames : 0)
            << ",\"targetSavingPercent\":" << (frames ? 100.0 * (ceiling - target_sum / frames) / ceiling : 0)
            << ",\"transitions\":" << transitions
            << ",\"firstReduceSeconds\":" << (first_reduce < 0 ? -1.0 : static_cast<double>(first_reduce) / fps)
            << ",\"firstRestoreSeconds\":" << (first_restore < 0 ? -1.0 : static_cast<double>(first_restore) / fps)
            << "}\n";
}
