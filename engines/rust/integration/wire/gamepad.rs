#[repr(C)]
#[derive(Copy, Clone, Debug, Default, PartialEq, Eq)]
pub struct Pad {
    slot: i16,
    mask: u16,
    buttons: u32,
    lt: u8,
    rt: u8,
    axes: [i16; 4],
}

fn put(buf: &mut [u8], i: usize, n: u16) {
    buf[i..i + 2].copy_from_slice(&n.to_le_bytes());
}
fn get(buf: &[u8], i: usize) -> u16 {
    u16::from_le_bytes([buf[i], buf[i + 1]])
}

#[no_mangle]
pub unsafe extern "C" fn pl_pad_write(
    out: *mut u8,
    capacity: usize,
    version: i32,
    sunshine: bool,
    state: &Pad,
) -> i16 {
    let legacy = version == 3;
    let len = if legacy { 28 } else { 34 };
    if out.is_null() || capacity < len || state.slot < 0 {
        return -1;
    }
    let buf = core::slice::from_raw_parts_mut(out, len);
    let slot = state.slot % if sunshine { 16 } else { 4 };
    let mut buttons = state.buttons;
    if buttons >> 31 != 0 {
        buttons &= 0xffff;
    }
    if !sunshine && buttons & 0x200000 != 0 {
        buttons |= 0x400;
    }
    buf[..4].copy_from_slice(&((len - 4) as u32).to_be_bytes());
    let magic: u32 = if legacy {
        10
    } else if version >= 5 {
        12
    } else {
        13
    };
    buf[4..8].copy_from_slice(&magic.to_le_bytes());
    let base = if legacy {
        put(buf, 8, 0x1400);
        10
    } else {
        put(buf, 8, 0x1a);
        put(buf, 10, slot as u16);
        put(
            buf,
            12,
            if sunshine {
                state.mask
            } else {
                state.mask & 0xf
            },
        );
        put(buf, 14, 0x14);
        16
    };
    put(buf, base, buttons as u16);
    buf[base + 2] = state.lt;
    buf[base + 3] = state.rt;
    for (i, axis) in state.axes.iter().enumerate() {
        put(buf, base + 4 + i * 2, *axis as u16);
    }
    put(buf, base + 12, 0x9c);
    put(
        buf,
        base + 14,
        if sunshine && !legacy {
            (buttons >> 16) as u16
        } else {
            0
        },
    );
    put(buf, base + 16, 0x55);
    slot
}
#[no_mangle]
pub unsafe extern "C" fn pl_pad_read(data: *const u8, len: usize, state: &mut Pad) -> bool {
    if data.is_null() || len != 34 {
        return false;
    }
    let b = core::slice::from_raw_parts(data, len);
    if b[..8] != [0, 0, 0, 30, 12, 0, 0, 0] {
        return false;
    }
    *state = Pad {
        slot: get(b, 10) as i16,
        mask: get(b, 12),
        buttons: u32::from(get(b, 16)) | (u32::from(get(b, 30)) << 16),
        lt: b[18],
        rt: b[19],
        axes: [
            get(b, 20) as i16,
            get(b, 22) as i16,
            get(b, 24) as i16,
            get(b, 26) as i16,
        ],
    };
    true
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn controller_generations_and_compatibility() {
        for version in [3, 4, 5, 7] {
            for sunshine in [false, true] {
                for flags in [0x8000, 0xffff8000, 0x208020, 0x7fff1234] {
                    let p = Pad {
                        slot: 15,
                        mask: 0xff80,
                        buttons: flags,
                        lt: 0,
                        rt: 255,
                        axes: [-32768, 32767, -31, 20000],
                    };
                    let mut b = [0xaa; 34];
                    let index = unsafe { pl_pad_write(b.as_mut_ptr(), 34, version, sunshine, &p) };
                    assert_eq!(index, if sunshine { 15 } else { 3 });
                    let start = if version == 3 { 10 } else { 16 };
                    assert_eq!(
                        get(&b, start),
                        flags as u16
                            | if !sunshine && flags >> 31 == 0 && flags & 0x200000 != 0 {
                                0x400
                            } else {
                                0
                            }
                    );
                    assert_eq!(
                        &b[start + 2..start + 12],
                        &[0, 255, 0, 128, 255, 127, 225, 255, 32, 78]
                    );
                    let high = if version != 3 && sunshine && flags >> 31 == 0 {
                        flags >> 16
                    } else {
                        0
                    };
                    assert_eq!(get(&b, start + 14), high as u16);
                    assert_eq!(get(&b, start + 16), 0x55);
                    if version == 3 {
                        assert_eq!(&b[28..], &[0xaa; 6]);
                    }
                    if version >= 5 && sunshine {
                        let mut result = Pad::default();
                        assert!(unsafe { pl_pad_read(b.as_ptr(), 34, &mut result) });
                        assert_eq!(
                            result,
                            Pad {
                                buttons: flags & 0xffff | (high << 16),
                                ..p
                            }
                        );
                    }
                }
            }
        }
    }
}
