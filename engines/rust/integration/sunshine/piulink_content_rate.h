#pragma once
#include <cstdint>
#include <cstddef>
#include <optional>

extern "C" {
struct pl_rate;
pl_rate* pl_rate_new();
void pl_rate_free(pl_rate*);
void pl_rate_reset(pl_rate*, uint32_t, uint32_t);
void pl_rate_enable(pl_rate*, uint8_t);
void pl_rate_ceiling(pl_rate*, uint32_t);
void pl_rate_applied(pl_rate*, uint32_t);
uint32_t pl_rate_observe(pl_rate*, uint64_t, uint32_t, uint8_t);
uint32_t pl_rate_output(const pl_rate*);
uint32_t pl_rate_qp(const pl_rate*);
}

namespace nvenc::piulink {
class content_rate_controller_t {
    pl_rate* state_ = pl_rate_new();
    uint32_t ceiling_ = 0;
    bool enabled_ = false;
public:
    content_rate_controller_t() = default;
    ~content_rate_controller_t() { pl_rate_free(state_); }
    content_rate_controller_t(const content_rate_controller_t&) = delete;
    content_rate_controller_t& operator=(const content_rate_controller_t&) = delete;
    void reset(uint32_t rate, uint32_t fps) { ceiling_ = rate; pl_rate_reset(state_, rate, fps); }
    void set_enabled(bool enabled) { enabled_ = enabled; pl_rate_enable(state_, enabled); }
    bool enabled() const { return enabled_; }
    uint32_t ceiling_kbps() const { return ceiling_; }
    uint32_t output_kbps() const { return pl_rate_output(state_); }
    uint32_t average_qp_x100() const { return pl_rate_qp(state_); }
    void set_ceiling(uint32_t rate) { ceiling_ = rate; pl_rate_ceiling(state_, rate); }
    void applied(uint32_t rate) { pl_rate_applied(state_, rate); }
    std::optional<uint32_t> observe(std::size_t bytes, uint32_t qp, bool idr) {
        auto target = pl_rate_observe(state_, bytes, qp, idr);
        return target ? std::optional<uint32_t>{target} : std::nullopt;
    }
};
}
