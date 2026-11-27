/// Consumer-owned timing state. The native audio callback is the only writer.
#[repr(C)]
#[derive(Default)]
pub struct Playout {
    previous_us: u64,
    expires_us: u64,
    burst_frames: u32,
}

pub const STORAGE_MS: u32 = 200;

#[no_mangle]
pub extern "C" fn pl_audio_storage_ms() -> u32 {
    STORAGE_MS
}

/// All counts are PCM frames per channel. Keep enough data for a burst of
/// callbacks, while retaining the original 40/20 ms policy on regular routes.
#[no_mangle]
pub extern "C" fn pl_audio_trim_frames(
    state: &mut Playout,
    now_us: u64,
    rate: u32,
    requested: u32,
    available: u32,
) -> u32 {
    if rate == 0 || requested == 0 {
        return 0;
    }
    let rate = u64::from(rate);
    let quantum = u64::from(requested);
    let gap_us = now_us.saturating_sub(state.previous_us);
    if now_us >= state.expires_us || now_us < state.previous_us {
        state.burst_frames = 0;
    }
    if state.previous_us != 0 && gap_us > 35_000 && gap_us > 2 * quantum * 1_000_000 / rate {
        let gap_frames = gap_us.min(160_000) * rate / 1_000_000;
        state.burst_frames = state.burst_frames.max(gap_frames as u32);
        state.expires_us = now_us.saturating_add(2_000_000);
    }
    state.previous_us = now_us;
    let margin = rate / 50;
    let retained = if state.burst_frames > 0 {
        (u64::from(state.burst_frames) + quantum).min(rate * u64::from(STORAGE_MS) / 1000)
    } else {
        quantum.max(margin)
    };
    let available = u64::from(available);
    if available > retained + margin {
        (available - retained) as u32
    } else {
        0
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn regular_callbacks_keep_the_low_latency_cap() {
        let mut s = Playout::default();
        assert_eq!(pl_audio_trim_frames(&mut s, 1000, 48000, 240, 1920), 0);
        assert_eq!(pl_audio_trim_frames(&mut s, 6000, 48000, 240, 2160), 1200);
        assert_eq!(pl_audio_trim_frames(&mut s, 11000, 48000, 240, 240), 0);
    }

    #[test]
    fn split_callbacks_can_consume_the_whole_hundred_ms_batch() {
        let mut s = Playout::default();
        pl_audio_trim_frames(&mut s, 1, 48000, 960, 0);
        for burst in 1..=25 {
            let mut queued = 4800;
            for j in 0..5 {
                let now = 1 + burst * 100_000 + j * 100;
                assert_eq!(pl_audio_trim_frames(&mut s, now, 48000, 960, queued), 0);
                queued -= 960;
            }
        }
        for i in 1..=110 {
            pl_audio_trim_frames(&mut s, 2500401 + i * 20_000, 48000, 960, 960);
        }
        assert_eq!(
            pl_audio_trim_frames(&mut s, 4720401, 48000, 960, 4800),
            3840
        );
    }

    #[test]
    fn prolonged_stalls_and_clock_discontinuities_are_bounded() {
        let mut s = Playout::default();
        pl_audio_trim_frames(&mut s, 1000, 48000, 960, 960);
        assert_eq!(
            pl_audio_trim_frames(&mut s, u64::MAX, 48000, 960, 20_000),
            11360
        );
        assert_eq!(pl_audio_trim_frames(&mut s, 1, 48000, 960, 2400), 1440);
    }
}
