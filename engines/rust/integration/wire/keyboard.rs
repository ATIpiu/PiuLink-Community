#[repr(C)]
pub struct Key {
    pub code: u8,
    pub flags: u8,
    pub modifiers: u8,
    pub release: bool,
}
#[no_mangle]
pub extern "C" fn pl_write_key(
    out: &mut [u8; 14],
    code: u16,
    action: u8,
    mut mods: u8,
    flags: u8,
    sunshine: bool,
) {
    if !sunshine {
        match code as u8 {
            0x5b | 0x5c => mods &= !8,
            k @ 0xa0..=0xa5 => {
                let bit = 1 << ((k - 0xa0) / 2);
                if k % 2 == 0 {
                    mods |= bit;
                } else {
                    mods &= !bit;
                }
            }
            _ => {}
        }
    }
    let [lo, hi] = code.to_le_bytes();
    *out = [
        0,
        0,
        0,
        10,
        action,
        0,
        0,
        0,
        if sunshine { flags } else { 0 },
        lo,
        hi,
        mods,
        0,
        0,
    ];
}
#[no_mangle]
pub extern "C" fn pl_read_key(packet: &[u8; 14], out: &mut Key) -> bool {
    let action = u32::from_le_bytes([packet[4], packet[5], packet[6], packet[7]]);
    if action != 3 && action != 4 {
        return false;
    }
    *out = Key {
        code: packet[9],
        flags: packet[8],
        modifiers: packet[11],
        release: action == 4,
    };
    true
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn round_trips_wire_packet_and_preserves_gfe_quirks() {
        for code in 0..=255u16 {
            for mods in 0..16 {
                for action in [3, 4] {
                    for sun in [false, true] {
                        let mut b = [0xff; 14];
                        pl_write_key(&mut b, 0x8000 | code, action, mods, 42, sun);
                        let mut key = Key {
                            code: 0,
                            flags: 0,
                            modifiers: 0,
                            release: false,
                        };
                        assert_eq!(b[..8], [0, 0, 0, 10, action, 0, 0, 0]);
                        assert!(pl_read_key(&b, &mut key));
                        assert_eq!(key.code, code as u8);
                        assert_eq!(key.release, action == 4);
                        let expected = if sun {
                            mods
                        } else {
                            match code {
                                91 | 92 => mods & !8,
                                160 => mods | 1,
                                161 => mods & !1,
                                162 => mods | 2,
                                163 => mods & !2,
                                164 => mods | 4,
                                165 => mods & !4,
                                _ => mods,
                            }
                        };
                        assert_eq!(key.modifiers, expected);
                        assert_eq!(key.flags, if sun { 42 } else { 0 });
                    }
                }
            }
        }
    }
}
