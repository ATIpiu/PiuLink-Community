use core::slice;

// Return the exact allocation size. Zero also denotes invalid geometry.
#[no_mangle]
pub extern "C" fn pl_packetized_size(
    header: usize,
    frame: usize,
    prefix: usize,
    capacity: usize,
) -> usize {
    if capacity == 0 {
        return 0;
    }
    let Some(data) = header.checked_add(frame) else {
        return 0;
    };
    data.div_ceil(capacity)
        .checked_mul(prefix)
        .and_then(|p| data.checked_add(p))
        .filter(|&n| n <= isize::MAX as usize)
        .unwrap_or(0)
}

// The caller owns the output allocation. Source slices must not alias it.
#[no_mangle]
pub unsafe extern "C" fn pl_packetize(
    out: *mut u8,
    size: usize,
    header: *const u8,
    header_len: usize,
    frame: *const u8,
    frame_len: usize,
    prefix: usize,
    capacity: usize,
) -> bool {
    if size == 0
        || size != pl_packetized_size(header_len, frame_len, prefix, capacity)
        || out.is_null()
        || (header_len > 0 && header.is_null())
        || (frame_len > 0 && frame.is_null())
    {
        return false;
    }
    let mut h = if header_len == 0 {
        &[][..]
    } else {
        slice::from_raw_parts(header, header_len)
    };
    let mut f = if frame_len == 0 {
        &[][..]
    } else {
        slice::from_raw_parts(frame, frame_len)
    };
    let mut dst = slice::from_raw_parts_mut(out, size);
    while !dst.is_empty() {
        let (reserved, rest) = dst.split_at_mut(prefix);
        reserved.fill(0);
        let hlen = h.len().min(capacity);
        let flen = f.len().min(capacity - hlen);
        let (packet, remaining) = rest.split_at_mut(hlen + flen);
        packet[..hlen].copy_from_slice(&h[..hlen]);
        packet[hlen..].copy_from_slice(&f[..flen]);
        h = &h[hlen..];
        f = &f[flen..];
        dst = remaining;
    }
    true
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn byte_layout_matches_concatenate_then_split() {
        for cap in [1, 2, 7, 8, 9, 16, 1024, 1392] {
            for prefix in [0, 4, 32] {
                for h in [0, 1, 8, 17] {
                    for f in [0, 1, cap - 1, cap, cap + 1, 3 * cap + 9] {
                        let header = vec![0xcd; h];
                        let frame = (0..f).map(|n| (n % 251) as u8).collect::<Vec<_>>();
                        let concatenated = [header.as_slice(), frame.as_slice()].concat();
                        let mut expected = Vec::new();
                        for chunk in concatenated.chunks(cap) {
                            expected.resize(expected.len() + prefix, 0);
                            expected.extend_from_slice(chunk);
                        }
                        let size = pl_packetized_size(h, f, prefix, cap);
                        assert_eq!(size, expected.len());
                        if size > 0 {
                            let mut dst = vec![0xff; size];
                            assert!(unsafe {
                                pl_packetize(
                                    dst.as_mut_ptr(),
                                    size,
                                    header.as_ptr(),
                                    h,
                                    frame.as_ptr(),
                                    f,
                                    prefix,
                                    cap,
                                )
                            });
                            assert_eq!(dst, expected);
                        }
                    }
                }
            }
        }
        assert_eq!(pl_packetized_size(8, usize::MAX, 32, 1200), 0);
    }
}
