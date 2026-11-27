use super::fec::{pl_fec_blocks, pl_fec_info};

/// Source-only bytes must be set before RS coding. On repair packets the
/// remaining header bytes are themselves parity and must survive finalization.
#[no_mangle]
pub extern "C" fn pl_video_source(
    out: &mut [u8; 32],
    seq: u32,
    i: u32,
    count: u32,
    frame: u32,
    block: u8,
    last: u8,
) {
    out[16..20].copy_from_slice(&seq.wrapping_add(i).wrapping_shl(8).to_le_bytes());
    out[20..24].copy_from_slice(&frame.to_le_bytes());
    out[24] = 1 | if i == 0 { 4 } else { 0 } | if i + 1 == count { 2 } else { 0 };
    out[26] = 0x10;
    out[27] = pl_fec_blocks(block, last);
}
#[no_mangle]
pub extern "C" fn pl_video_shard(
    out: &mut [u8; 32],
    seq: u32,
    i: u32,
    pts: u32,
    frame: u32,
    data: u32,
    percent: u32,
    block: u8,
    last: u8,
) {
    out[0] = 0x90;
    out[2..4].copy_from_slice(&(seq.wrapping_add(i) as u16).to_be_bytes());
    out[4..8].copy_from_slice(&pts.to_be_bytes());
    out[20..24].copy_from_slice(&frame.to_le_bytes());
    out[27] = pl_fec_blocks(block, last);
    out[28..32].copy_from_slice(&pl_fec_info(i, data, percent).to_le_bytes());
}
#[repr(C)]
#[derive(Default)]
pub struct Pacing {
    per_ms: u32,
    batch: u32,
    group: u32,
    sent: u32,
}
#[no_mangle]
pub extern "C" fn pl_pacing_init(p: &mut Pacing, packet: usize) -> bool {
    if packet == 0 || packet > 65536 {
        return false;
    }
    *p = Pacing {
        per_ms: (100_000 / packet) as u32,
        batch: (65536 / packet).min(64) as u32,
        group: 0,
        sent: 0,
    };
    true
}
#[no_mangle]
pub extern "C" fn pl_pacing_due(p: &mut Pacing, n: u32, tail: bool, offset: &mut i64) -> bool {
    if n < p.batch && !tail {
        return false;
    }
    if p.group >= p.per_ms || p.sent == 0 {
        *offset = pl_pacing_end(p);
        p.group = 0;
    } else {
        *offset = -1;
    }
    p.group += n;
    p.sent += n;
    true
}
#[no_mangle]
pub extern "C" fn pl_pacing_end(p: &Pacing) -> i64 {
    i64::from(p.sent) * 1_000_000 / i64::from(p.per_ms.max(1))
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn header_changes_only_wire_fields() {
        let mut b = [0x77; 32];
        pl_video_source(&mut b, 0xffff, 1, 2, 123, 1, 2);
        assert_eq!(&b[16..28], &[0, 0, 0, 1, 123, 0, 0, 0, 3, 0x77, 0x10, 0x90]);
        pl_video_shard(&mut b, 0xffff, 1, 0x1020304, 123, 2, 20, 1, 2);
        assert_eq!(
            &b[..12],
            &[144, 0x77, 0, 0, 1, 2, 3, 4, 0x77, 0x77, 0x77, 0x77]
        );
        assert_eq!(&b[28..], &pl_fec_info(1, 2, 20).to_le_bytes());
        pl_video_source(&mut b, 0, 0, 1, 0, 0, 0);
        assert_eq!(b[24], 7);
    }
    #[test]
    fn deadlines_across_multiple_blocks_match_sender_clock() {
        for bytes in [128usize, 512, 1024, 1472, 1232, 65536] {
            let mut p = Pacing::default();
            assert!(pl_pacing_init(&mut p, bytes));
            let (mut total, mut group) = (0i64, 0i64);
            for count in [1usize, 215, 514, 27] {
                let mut first = 0;
                for i in 0..count {
                    let n = i - first + 1;
                    let tail = i + 1 == count;
                    let mut due = -9;
                    let send = pl_pacing_due(&mut p, n as u32, tail, &mut due);
                    assert_eq!(send, tail || n >= (65536 / bytes).min(64));
                    if send {
                        let rate = (100_000 / bytes) as i64;
                        if total == 0 || group >= rate {
                            assert_eq!(due, total * 1_000_000 / rate);
                            group = 0;
                        } else {
                            assert_eq!(due, -1);
                        }
                        group += n as i64;
                        total += n as i64;
                        first = i + 1;
                    }
                }
                assert_eq!(
                    pl_pacing_end(&p),
                    total * 1_000_000 / (100_000 / bytes) as i64
                );
            }
        }
    }
}
