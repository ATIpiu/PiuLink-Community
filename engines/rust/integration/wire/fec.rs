#[repr(C)]
pub struct VideoFec {
    pub index: u16,
    pub data: u16,
    pub percentage: u8,
    pub current: u8,
    pub last: u8,
}

#[no_mangle]
pub extern "C" fn pl_fec_info(index: u32, data: u32, percentage: u32) -> u32 {
    (index << 12) | (data << 22) | (percentage << 4)
}

#[no_mangle]
pub extern "C" fn pl_fec_blocks(current: u8, last: u8) -> u8 {
    (current << 4) | (last << 6)
}

#[no_mangle]
pub extern "C" fn pl_fec_read(info: u32, blocks: u8) -> VideoFec {
    VideoFec {
        index: ((info >> 12) & 1023) as u16,
        data: (info >> 22) as u16,
        percentage: (info >> 4) as u8,
        current: (blocks >> 4) & 3,
        last: blocks >> 6,
    }
}

#[test]
fn packet_fields_match_known_wire_values() {
    assert_eq!(pl_fec_info(17, 42, 20), 0x0a811140);
    for index in 0..1024 {
        for blocks in 0..16 {
            let data = 1023 - index;
            let percentage = index as u8;
            let current = blocks & 3;
            let last = blocks >> 2;
            let packet = pl_fec_read(pl_fec_info(index, data, percentage.into()), pl_fec_blocks(current,last));
            assert_eq!((packet.index as u32, packet.data as u32, packet.percentage, packet.current, packet.last),
                       (index, data, percentage, current, last));
        }
    }
}
