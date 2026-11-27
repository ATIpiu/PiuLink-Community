#[repr(C)]
pub struct FrameHeader {
    size: u8,
    kind: u8,
    latency: u16,
    tail: u16,
}

// The size includes the eight-byte header in the encoded frame, but not RTP overhead.
#[no_mangle]
pub extern "C" fn pl_write_header(
    out: &mut [u8; 8],
    payload: usize,
    capacity: u32,
    idr: bool,
    invalidated: bool,
    elapsed_us: i64,
) -> bool {
    if capacity == 0 || capacity > u16::MAX as u32 {
        return false;
    }
    let cap = capacity as usize;
    let tail = ((payload % cap + 7) % cap + 1) as u16;
    let latency = (elapsed_us.saturating_add(50) / 100).clamp(0, 65535) as u16;
    let [lo, hi] = latency.to_le_bytes();
    let [a, b] = tail.to_le_bytes();
    *out = [
        1,
        lo,
        hi,
        if idr {
            2
        } else if invalidated {
            5
        } else {
            1
        },
        a,
        b,
        0,
        0,
    ];
    true
}

#[no_mangle]
pub unsafe extern "C" fn pl_read_header(
    ptr: *const u8,
    len: u32,
    version: &[i32; 4],
    out: &mut FrameHeader,
) -> bool {
    let v = (version[0], version[1], version[2]);
    let data = if len == 0 || ptr.is_null() {
        &[][..]
    } else {
        core::slice::from_raw_parts(ptr, len as usize)
    };
    let short = data.first() == Some(&1);
    let size = if v >= (7, 1, 450) {
        if short {
            8
        } else {
            44
        }
    } else if v >= (7, 1, 446) {
        if short {
            8
        } else {
            41
        }
    } else if v >= (7, 1, 415) {
        if short {
            8
        } else {
            24
        }
    } else if v >= (7, 1, 350) {
        8
    } else if v >= (7, 1, 320) {
        12
    } else if v >= (5, 0, 0) {
        8
    } else {
        0
    };
    if data.len() < size {
        return false;
    }
    *out = FrameHeader {
        size: size as u8,
        kind: if v >= (7, 1, 350) { data[3] } else { 0 },
        latency: if version[3] < 0 && data.len() >= 3 {
            u16::from_le_bytes([data[1], data[2]])
        } else {
            0
        },
        tail: if data.len() >= 6 {
            u16::from_le_bytes([data[4], data[5]])
        } else {
            0
        },
    };
    true
}
#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn headers_match_modern_wire_and_legacy_size_transitions() {
        let mut out = FrameHeader {
            size: 0,
            kind: 0,
            latency: 0,
            tail: 0,
        };
        let mut h = [0; 8];
        for cap in [1, 8, 1024, 1392, 65535] {
            for len in [0, 1, 7, 8, cap - 1, cap, cap + 1, cap * 5 + 7] {
                for (idr, rfi, kind) in [(false, false, 1), (false, true, 5), (true, true, 2)] {
                    assert!(pl_write_header(&mut h, len, cap as u32, idr, rfi, 12345));
                    let expected = (len + 8) % cap;
                    let tail = if expected == 0 { cap } else { expected };
                    assert_eq!(h, [1, 123, 0, kind, tail as u8, (tail >> 8) as u8, 0, 0]);
                    assert!(unsafe { pl_read_header(h.as_ptr(), 8, &[7, 1, 450, -1], &mut out) });
                    assert_eq!(
                        (out.size, out.kind, out.latency, out.tail),
                        (8, kind, 123, tail as u16)
                    );
                }
            }
        }
        let mut long = [0; 44];
        long[0] = 0x81;
        for (v, size) in [
            ([4, 0, 0, 0], 0),
            ([5, 0, 0, 0], 8),
            ([7, 1, 320, 0], 12),
            ([7, 1, 350, 0], 8),
            ([7, 1, 415, 0], 24),
            ([7, 1, 446, 0], 41),
            ([7, 1, 450, 0], 44),
        ] {
            for len in 0..=44 {
                assert_eq!(
                    unsafe { pl_read_header(long.as_ptr(), len, &v, &mut out) },
                    len >= size
                );
            }
        }
    }
}
