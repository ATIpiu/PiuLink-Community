#[repr(C)]
#[derive(Default)]
pub struct Layout {
    pub lengths: [usize; 4],
    pub blocks: u32,
    pub percentage: u32,
}

// Input length includes space for the per-packet headers. Match Sunshine's
// floor-before-rounding split; the last block consumes the exact remainder.
#[no_mangle]
pub extern "C" fn pl_frame_layout(
    length: usize,
    packet: usize,
    percentage: u32,
    split_for_test: bool,
    out: &mut Layout,
) -> bool {
    if packet == 0 || length == 0 {
        return false;
    }
    let per_block = 25500 / (100u64 + u64::from(percentage));
    let Some(limit) = packet.checked_mul(per_block as usize) else {
        return false;
    };
    if limit == 0 {
        return false;
    }
    let needed = length.div_ceil(limit);
    let mut blocks = needed.min(4);
    if split_for_test && blocks == 1 && length / packet >= 12 && percentage > 0 {
        blocks = 2;
    }
    let Some(aligned) = (length / blocks).div_ceil(packet).checked_mul(packet) else {
        return false;
    };
    let mut layout = Layout {
        blocks: blocks as u32,
        percentage: if needed > 4 { 0 } else { percentage },
        ..Layout::default()
    };
    let mut remaining = length;
    for i in 0..blocks {
        let size = if i + 1 == blocks { remaining } else { aligned };
        // The 10-bit data-shard count must fit even when parity is disabled.
        if size == 0 || size > remaining || size.div_ceil(packet) > 1023 {
            return false;
        }
        layout.lengths[i] = size;
        remaining -= size;
    }
    *out = layout;
    true
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn compare_sunshine_boundaries() {
        for packet in [256, 1024, 1440] {
            for parity in [0, 1, 10, 20, 50, 100, 255] {
                for shards in 1..=4097 {
                    for tail in [1, packet] {
                        let bytes = (shards - 1) * packet + tail;
                        let required = (bytes + packet * (25500 / (100 + parity)) - 1)
                            / (packet * (25500 / (100 + parity)));
                        let count = required.min(4);
                        let stride = (bytes / count + packet - 1) / packet * packet;
                        let mut expected = [0; 4];
                        for i in 0..count {
                            expected[i] = if i + 1 == count {
                                bytes - i * stride
                            } else {
                                stride
                            };
                        }
                        let mut out = Layout::default();
                        let valid = expected[..count]
                            .iter()
                            .all(|&s| s > 0 && s.div_ceil(packet) <= 1023);
                        assert_eq!(
                            pl_frame_layout(bytes, packet, parity as u32, false, &mut out),
                            valid
                        );
                        if valid {
                            assert_eq!(out.lengths, expected);
                            assert_eq!(out.blocks, count as u32);
                            assert_eq!(
                                out.percentage,
                                if required > 4 { 0 } else { parity as u32 }
                            );
                        }
                    }
                }
            }
        }
    }
}
