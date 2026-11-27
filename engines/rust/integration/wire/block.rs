use core::ffi::c_void;

// Ten wire bits encode the source shard count. Even 255% parity fits 4096 slots.
#[repr(C)]
pub struct Block {
    seen: [u64; 64],
    entries: [*mut c_void; 1024],
    base: u16,
    highest: u16,
    fast: bool,
}

#[no_mangle]
pub unsafe extern "C" fn pl_block_reset(block: &mut Block, base: u16) {
    block.seen.fill(0);
    block.base = base;
    block.highest = 0;
    block.fast = true;
    // Only marked slots may be read. The packet list owns the entries.
}

// 0 = reject, 1 = accept, 2 = accept after a higher sequence number.
#[no_mangle]
pub extern "C" fn pl_block_accept(block: &mut Block, sequence: u16, next: &mut u32,
                                   data_count: u32, total: u32, entry: *mut c_void) -> u8 {
    let offset = sequence.wrapping_sub(block.base);
    if offset as u32 >= total || offset >= 4096 || data_count > 1023 || entry.is_null() {
        return 0;
    }
    let word = &mut block.seen[offset as usize / 64];
    let mask = 1u64 << (offset % 64);
    if *word & mask != 0 {
        return 0;
    }
    *word |= mask;
    if u32::from(offset) < data_count {
        block.entries[offset as usize] = entry;
    }
    let late = offset < block.highest;
    block.highest = block.highest.max(offset);
    if block.fast && sequence as u32 == *next {
        *next = sequence.wrapping_add(1) as u32;
    } else {
        block.fast = false;
    }
    if late { 2 } else { 1 }
}

#[no_mangle]
pub extern "C" fn pl_block_entry(block: &Block, offset: u32) -> *mut c_void {
    if offset >= 1023 || block.seen[offset as usize / 64] & (1 << (offset % 64)) == 0 {
        return core::ptr::null_mut();
    }
    block.entries[offset as usize]
}
