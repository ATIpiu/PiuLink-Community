/// Combine consecutive input reports without losing edges, changing coordinate systems,
/// or wrapping a relative movement. The caller holds the session's queue lock.
#[repr(u8)]
#[derive(Debug, PartialEq, Eq)]
pub enum Batch {
    Merged,
    Skip,
    Stop,
}
use self::Batch::*;

fn merge_delta(dest: &mut [u8], src: &[u8], relative: bool, duplicate: bool) -> Batch {
    let sum = |i: usize| {
        i16::from_be_bytes([dest[i], dest[i + 1]])
            .checked_add(i16::from_be_bytes([src[i], src[i + 1]]))
    };
    let Some(x) = sum(8) else { return Stop };
    let y = if relative {
        let Some(y) = sum(10) else { return Stop };
        Some(y)
    } else if duplicate {
        Some(x)
    } else {
        None
    };
    dest[8..10].copy_from_slice(&x.to_be_bytes());
    if let Some(y) = y {
        dest[10..12].copy_from_slice(&y.to_be_bytes());
    }
    Merged
}

fn contact(dest: &[u8], src: &[u8], controller: bool) -> Batch {
    let index = if controller { 9 } else { 8 };
    if !matches!(dest[index], 0 | 3) {
        return Stop;
    }
    if controller && dest[8] != src[8] {
        return Skip;
    }
    if !matches!(src[index], 0 | 3) {
        return Stop;
    }
    if dest[12..16] != src[12..16] {
        return Skip;
    }
    if dest[index] != src[index] {
        Stop
    } else {
        Merged
    }
}

fn batch(dest: &mut [u8], src: &[u8]) -> Batch {
    if src.len() != dest.len() || src.len() < 8 || dest[..8] != src[..8] {
        return Stop;
    }
    let magic = u32::from_le_bytes([src[4], src[5], src[6], src[7]]);
    let decision = match (magic, src.len()) {
        (7, 12) => return merge_delta(dest, src, true, false),
        (10, 14) => return merge_delta(dest, src, false, true),
        (0x55000001, 10) => return merge_delta(dest, src, false, false),
        (5, 18) if dest[14..18] == src[14..18] => Merged,
        (12 | 13, 34) if dest[12..14] == src[12..14] => {
            if dest[10..12] != src[10..12] {
                Skip
            } else if dest[16..18] == src[16..18] && dest[30..32] == src[30..32] {
                Merged
            } else {
                Stop
            }
        }
        (0x55000002, 36) => contact(dest, src, false),
        (0x55000003, 36) if matches!(dest[8], 0 | 3) && dest[8..11] == src[8..11] => Merged,
        (0x55000005, 28) => contact(dest, src, true),
        (0x55000006, 24) if dest[8..10] != src[8..10] => Skip,
        (0x55000006, 24) => Merged,
        _ => Stop,
    };
    if decision == Merged {
        dest.copy_from_slice(src);
    }
    decision
}

#[no_mangle]
pub unsafe extern "C" fn pl_batch_input(
    dest: *mut u8,
    n: usize,
    src: *const u8,
    m: usize,
) -> Batch {
    if dest.is_null() || src.is_null() || n != m || !(8..=36).contains(&n) {
        return Stop;
    }
    batch(
        core::slice::from_raw_parts_mut(dest, n),
        core::slice::from_raw_parts(src, m),
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    fn report(magic: u32, len: usize) -> Vec<u8> {
        let mut b = vec![0; len];
        b[..4].copy_from_slice(&((len - 4) as u32).to_be_bytes());
        b[4..8].copy_from_slice(&magic.to_le_bytes());
        b
    }
    #[test]
    fn relative_overflow_keeps_both_events_and_axes() {
        for x in [i16::MIN, -2, 0, 1, i16::MAX] {
            for y in [i16::MIN, -1, 0, 2, i16::MAX] {
                for d in [-121i16, 0, 121] {
                    let mut a = report(7, 12);
                    let mut b = a.clone();
                    a[8..10].copy_from_slice(&x.to_be_bytes());
                    a[10..12].copy_from_slice(&y.to_be_bytes());
                    b[8..10].copy_from_slice(&d.to_be_bytes());
                    b[10..12].copy_from_slice(&d.to_be_bytes());
                    let old = a.clone();
                    let result = batch(&mut a, &b);
                    match (x.checked_add(d), y.checked_add(d)) {
                        (Some(x), Some(y)) => {
                            assert_eq!(result, Merged);
                            assert_eq!(&a[8..10], &x.to_be_bytes());
                            assert_eq!(&a[10..], &y.to_be_bytes());
                        }
                        _ => {
                            assert_eq!(result, Stop);
                            assert_eq!(a, old);
                        }
                    }
                }
            }
        }
    }
    #[test]
    fn state_edges_are_preserved() {
        for (magic, len, offset, expected) in [
            (5, 18, 14, Stop),
            (12, 34, 10, Skip),
            (12, 34, 12, Stop),
            (12, 34, 30, Stop),
            (0x55000002, 36, 12, Skip),
            (0x55000003, 36, 9, Stop),
            (0x55000005, 28, 8, Skip),
            (0x55000006, 24, 9, Skip),
        ] {
            let mut a = report(magic, len);
            let mut b = a.clone();
            b[offset] = 1;
            let old = a.clone();
            assert_eq!(batch(&mut a, &b), expected);
            assert_eq!(a, old);
        }
        let mut a = report(10, 14);
        let mut b = a.clone();
        for packet in [&mut a, &mut b] {
            packet[8..10].copy_from_slice(&120i16.to_be_bytes());
        }
        assert_eq!(batch(&mut a, &b), Merged);
        assert_eq!(&a[8..12], &[0, 240, 0, 240]);
    }
}

/// Check the received vector length before native dispatch casts its payload.
#[no_mangle]
pub unsafe extern "C" fn pl_valid_input(data: *const u8, n: usize) -> bool {
    if data.is_null() || n < 8 {
        return false;
    }
    let h = core::slice::from_raw_parts(data, 8);
    let declared = u32::from_be_bytes([h[0], h[1], h[2], h[3]]) as usize;
    if declared != n - 4 {
        return false;
    }
    match u32::from_le_bytes([h[4], h[5], h[6], h[7]]) {
        3 | 4 | 10 => n == 14,
        5 => n == 18,
        7 => n == 12,
        8 | 9 => n == 9,
        12 => n == 34,
        0x17 => true,
        0x55000001 => n == 10,
        0x55000002 | 0x55000003 => n == 36,
        0x55000004 => n == 16,
        0x55000005 => n == 28,
        0x55000006 => n == 24,
        0x55000007 => n == 12,
        _ => false,
    }
}
