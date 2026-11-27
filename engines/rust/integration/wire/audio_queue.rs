use super::recovery::pl_audio_recover;
use core::convert::TryInto;
use std::{collections::VecDeque, ffi::c_void, ptr, slice};

const RTP: usize = 12;
const DATA: usize = 4;
const TOTAL: usize = 6;
// Limit active and cached shard storage before reserving memory.
const MAX_SHARD: usize = 4096;
const MAX_BLOCKS: usize = 32;
const RESYNC_MS: u64 = 1000;
const READY: i32 = 2;
const DIRECT: i32 = 4;

#[derive(Clone, Copy)]
struct Header {
    base: u16,
    timestamp: u32,
    ssrc: u32,
    kind: u8,
}
struct Block {
    h: Header,
    len: usize,
    storage: Vec<u8>,
    missing: [u8; TOTAL],
    headers: [[u8; RTP]; DATA],
    cursor: usize,
    queued_at: u64,
    discontinuity: bool,
}
impl Block {
    fn shard(&self, i: usize) -> &[u8] {
        &self.storage[i * self.len..(i + 1) * self.len]
    }
    fn complete(&mut self) -> u32 {
        if self.missing[..DATA].iter().all(|&v| v == 0)
            || self.missing.iter().filter(|&&v| v == 0).count() < DATA
        {
            return 0;
        }
        let mut shards = [ptr::null_mut(); TOTAL];
        for (slot, bytes) in shards
            .iter_mut()
            .zip(self.storage.chunks_exact_mut(self.len))
        {
            *slot = bytes.as_mut_ptr();
        }
        let recovered =
            unsafe { pl_audio_recover(shards.as_ptr(), self.missing.as_ptr(), self.len as u32) };
        if recovered > 0 {
            self.missing[..DATA].fill(0);
            recovered as u32
        } else {
            0
        }
    }
}
fn before(a: u16, b: u16) -> bool {
    (a.wrapping_sub(b) as i16) < 0
}

pub struct Queue {
    blocks: VecDeque<Block>,
    cache: Vec<Vec<u8>>,
    first: Option<u16>,
    next: u16,
    synchronized: bool,
    last_late: Option<u16>,
    bypass: bool,
    duration: u32,
    last_admitted: u64,
    direct: u64,
    delivered: u64,
    recovered: u64,
    concealed: u64,
}
impl Queue {
    fn new(bypass: bool) -> Self {
        Self {
            blocks: VecDeque::new(),
            cache: Vec::new(),
            first: None,
            next: 0,
            synchronized: false,
            last_late: None,
            bypass,
            duration: 0,
            last_admitted: 0,
            direct: 0,
            delivered: 0,
            recovered: 0,
            concealed: 0,
        }
    }
    fn ready(&self) -> bool {
        self.blocks.front().is_some_and(|b| {
            b.h.base.wrapping_add(b.cursor as u16) == self.next
                && (b.discontinuity || b.missing[b.cursor] == 0)
        })
    }
    fn advance(&mut self) {
        let head = self.blocks.front_mut().unwrap();
        head.cursor += 1;
        self.next = self.next.wrapping_add(1);
        if head.cursor == DATA {
            let head = self.blocks.pop_front().unwrap();
            self.first = Some(self.next);
            self.synchronized = true;
            if self.cache.len() < 4 {
                self.cache.push(head.storage);
            }
        }
    }
    fn skip_missing(&mut self, now: u64) {
        if self.ready() {
            return;
        }
        let count = self.blocks.len();
        let Some(head) = self.blocks.front_mut() else {
            return;
        };
        if before(self.next, head.h.base) {
            self.next = head.h.base;
            self.first = Some(self.next);
        } else if count > 1
            && (self.last_late.is_none()
                || now.saturating_sub(head.queued_at) > u64::from(self.duration) * 4 + 10)
        {
            head.discontinuity = true;
        }
    }
    // The receive thread already converted the outer RTP fields to host byte order.
    fn add(&mut self, packet: &[u8], duration: u32, now: u64) -> i32 {
        if packet.len() <= RTP || packet[0] != 0x80 {
            return 0;
        }
        let kind = packet[1];
        if self.bypass {
            return if kind == 97 { DIRECT } else { 0 };
        }
        self.duration = duration;
        let seq = u16::from_ne_bytes([packet[2], packet[3]]);
        let (h, index, data) = match kind {
            97 => {
                if let Some(first) = self.first {
                    if self.synchronized && before(seq, first) {
                        self.last_late = Some(seq)
                    } else if self.last_late.is_some_and(|last| before(first, last)) {
                        self.last_late = None
                    }
                }
                let index = usize::from(seq % 4);
                let ts = u32::from_ne_bytes(packet[4..8].try_into().unwrap());
                (
                    Header {
                        base: seq & !3,
                        timestamp: ts.wrapping_sub((index as u32).wrapping_mul(duration)),
                        ssrc: u32::from_ne_bytes(packet[8..12].try_into().unwrap()),
                        kind,
                    },
                    index,
                    &packet[RTP..],
                )
            }
            127 if packet.len() >= 24 && packet[12] < 2 && packet[13] == 97 => {
                let base = u16::from_be_bytes([packet[14], packet[15]]);
                if base % 4 != 0 {
                    return 0;
                }
                (
                    Header {
                        base,
                        timestamp: u32::from_be_bytes(packet[16..20].try_into().unwrap()),
                        ssrc: u32::from_be_bytes(packet[20..24].try_into().unwrap()),
                        kind: packet[13],
                    },
                    DATA + usize::from(packet[12]),
                    &packet[24..],
                )
            }
            _ => return 0,
        };
        if data.is_empty() || data.len() > MAX_SHARD {
            return 0;
        }
        if now.saturating_sub(self.last_admitted) >= RESYNC_MS {
            self.blocks.clear();
            self.first = None;
            self.synchronized = false;
            self.last_late = None;
        }
        let first = match self.first {
            Some(first) => first,
            None => {
                // A data packet at index zero is already a complete block boundary.
                self.last_admitted = now;
                if index != 0 {
                    self.next = h.base.wrapping_add(DATA as u16);
                    self.first = Some(self.next);
                    return 0;
                }
                self.next = h.base;
                self.first = Some(h.base);
                h.base
            }
        };
        if h.base.wrapping_sub(first) as usize >= DATA * MAX_BLOCKS {
            return 0;
        }
        let pos = self
            .blocks
            .iter()
            .position(|b| b.h.base == h.base || before(h.base, b.h.base))
            .unwrap_or(self.blocks.len());
        if !self.blocks.get(pos).is_some_and(|b| b.h.base == h.base) {
            if self.blocks.len() >= MAX_BLOCKS {
                return 0;
            }
            let mut storage = self.cache.pop().unwrap_or_default();
            let size = TOTAL * data.len();
            if storage
                .try_reserve_exact(size.saturating_sub(storage.len()))
                .is_err()
                || self.blocks.try_reserve(1).is_err()
            {
                return 0;
            }
            storage.resize(size, 0);
            self.blocks.insert(
                pos,
                Block {
                    h,
                    len: data.len(),
                    storage,
                    missing: [1; TOTAL],
                    headers: [[0; RTP]; DATA],
                    cursor: 0,
                    queued_at: now,
                    discontinuity: false,
                },
            );
        }
        let block = &mut self.blocks[pos];
        if block.len != data.len() {
            return 0;
        }
        if block.missing[index] == 0 {
            return 0;
        }
        self.last_admitted = now;
        let start = index * block.len;
        block.storage[start..start + block.len].copy_from_slice(data);
        block.missing[index] = 0;
        if index < DATA {
            block.headers[index].copy_from_slice(&packet[..RTP]);
        }
        self.recovered += u64::from(block.complete());
        if kind == 97 && seq == self.next && pos == 0 && index == block.cursor {
            // The caller can decode its original packet immediately unless there
            // is already a successor to drain in this same receive callback.
            let buffered_next = if index + 1 < DATA {
                block.missing[index + 1] == 0
            } else {
                self.blocks
                    .get(1)
                    .is_some_and(|b| b.h.base == seq.wrapping_add(1) && b.missing[0] == 0)
            };
            if !buffered_next {
                self.advance();
                self.direct += 1;
                return DIRECT;
            }
        }
        self.skip_missing(now);
        if self.ready() {
            READY
        } else {
            0
        }
    }
    fn pop(
        &mut self,
        prefix: usize,
        alloc: extern "C" fn(usize) -> *mut c_void,
        length: &mut u16,
    ) -> *mut c_void {
        if !self.ready() {
            return ptr::null_mut();
        }
        let b = self.blocks.front().unwrap();
        let i = b.cursor;
        let n = if b.missing[i] == 0 { RTP + b.len } else { 0 };
        let out = alloc((prefix + n).max(1));
        if out.is_null() {
            return out;
        }
        if n != 0 {
            let bytes = unsafe { slice::from_raw_parts_mut(out.cast::<u8>().add(prefix), n) };
            if b.headers[i][0] != 0 {
                bytes[..RTP].copy_from_slice(&b.headers[i]);
            } else {
                bytes[0] = 0x80;
                bytes[1] = b.h.kind;
                bytes[2..4].copy_from_slice(&self.next.to_ne_bytes());
                bytes[4..8].copy_from_slice(
                    &b.h.timestamp
                        .wrapping_add((i as u32).wrapping_mul(self.duration))
                        .to_ne_bytes(),
                );
                bytes[8..12].copy_from_slice(&b.h.ssrc.to_ne_bytes());
            }
            bytes[RTP..].copy_from_slice(b.shard(i));
            self.delivered += 1;
        } else {
            self.concealed += 1;
        }
        *length = n as u16;
        self.advance();
        out
    }
}
#[no_mangle]
pub extern "C" fn pl_audio_queue_new(bypass: bool) -> *mut Queue {
    Box::into_raw(Box::new(Queue::new(bypass)))
}
#[no_mangle]
pub unsafe extern "C" fn pl_audio_queue_add(
    q: &mut Queue,
    p: *const u8,
    n: u16,
    duration: u32,
    now: u64,
) -> i32 {
    if p.is_null() {
        return 0;
    }
    q.add(slice::from_raw_parts(p, n as usize), duration, now)
}
#[no_mangle]
pub extern "C" fn pl_audio_queue_pop(
    q: &mut Queue,
    prefix: u16,
    alloc: extern "C" fn(usize) -> *mut c_void,
    n: &mut u16,
) -> *mut c_void {
    q.pop(prefix as usize, alloc, n)
}
#[no_mangle]
pub unsafe extern "C" fn pl_audio_queue_free(q: *mut Queue, stats: &mut [u64; 4]) {
    if !q.is_null() {
        let q = Box::from_raw(q);
        *stats = [q.direct, q.delivered, q.recovered, q.concealed];
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::recovery::pl_audio_encode;
    extern "C" {
        fn malloc(n: usize) -> *mut c_void;
        fn free(p: *mut c_void);
    }
    extern "C" fn allocate(n: usize) -> *mut c_void {
        unsafe { malloc(n) }
    }
    extern "C" fn fail(_: usize) -> *mut c_void {
        ptr::null_mut()
    }

    fn packets(base: u16, ts: u32) -> Vec<Vec<u8>> {
        let mut shards = [[0u8; 8]; 6];
        let mut ptrs = [ptr::null_mut(); 6];
        for (i, s) in shards.iter_mut().enumerate() {
            for (j, v) in s.iter_mut().enumerate() {
                *v = (base as u8).wrapping_add((i * 9 + j) as u8);
            }
            ptrs[i] = s.as_mut_ptr();
        }
        assert_eq!(unsafe { pl_audio_encode(ptrs.as_ptr(), 8) }, 0);
        (0..6)
            .map(|i| {
                let mut p = vec![0u8; if i < 4 { 12 } else { 24 }];
                p[0] = 0x80;
                p[1] = if i < 4 { 97 } else { 127 };
                p[2..4].copy_from_slice(&base.wrapping_add(i as u16).to_ne_bytes());
                if i < 4 {
                    p[4..8].copy_from_slice(&ts.wrapping_add(i as u32 * 5).to_ne_bytes());
                } else {
                    p[12] = i as u8 - 4;
                    p[13] = 97;
                    p[14..16].copy_from_slice(&base.to_be_bytes());
                    p[16..20].copy_from_slice(&ts.to_be_bytes());
                }
                p.extend_from_slice(&shards[i]);
                p
            })
            .collect()
    }
    fn drain(q: &mut Queue) -> Vec<Vec<u8>> {
        let mut all = vec![];
        loop {
            let mut len = 0;
            let p = q.pop(7, allocate, &mut len);
            if p.is_null() {
                return all;
            }
            unsafe {
                all.push(slice::from_raw_parts(p.cast::<u8>().add(7), len as usize).to_vec());
                free(p);
            }
        }
    }
    #[test]
    fn any_four_shards_recover_in_receive_order_at_wrap() {
        let p = packets(65532, u32::MAX - 7);
        for a in 0..6 {
            for b in 0..6 {
                for c in 0..6 {
                    for d in 0..6 {
                        let mut order = [a, b, c, d];
                        order.sort_unstable();
                        if order.windows(2).any(|v| v[0] == v[1]) {
                            continue;
                        }
                        let mut q = Queue::new(false);
                        q.add(&packets(65528, 0)[1], 5, 0);
                        let mut all = vec![];
                        for i in [a, b, c, d] {
                            let result = q.add(&p[i], 5, 5);
                            if result == DIRECT {
                                all.push(p[i].clone());
                            }
                            if result == READY {
                                all.extend(drain(&mut q));
                            }
                        }
                        assert_eq!(all, &p[..4], "order {:?}", [a, b, c, d]);
                        assert_eq!(q.next, 0);
                    }
                }
            }
        }
    }
    #[test]
    fn first_block_at_wrap_accepts_only_an_aligned_start() {
        let first = packets(65532, 0);
        let next = packets(0, 20);
        for start in 0..TOTAL {
            let mut q = Queue::new(false);
            for index in start..TOTAL {
                let expected = if start == 0 && index < DATA {
                    DIRECT
                } else {
                    0
                };
                assert_eq!(q.add(&first[index], 5, index as u64 * 5), expected);
                assert_eq!(q.add(&first[index], 5, index as u64 * 5), 0);
            }
            assert_eq!(q.add(&next[0], 5, 30), DIRECT);
            assert_eq!(q.concealed, 0);
        }
    }

    #[test]
    fn admission_window_wraps_and_resynchronizes_after_silence() {
        let mut q = Queue::new(false);
        q.add(&packets(65528, 0)[1], 5, 0);
        let beyond_window = packets(124, 1);
        assert_eq!(q.add(&beyond_window[0], 5, 1), 0);
        assert!(q.blocks.is_empty());

        let mut oversized = packets(65532, 2)[0].clone();
        oversized.resize(RTP + MAX_SHARD + 1, 0);
        assert_eq!(q.add(&oversized, 5, 2), 0);
        assert!(q.blocks.is_empty());
        assert_eq!(q.add(&packets(65532, 3)[0], 5, 3), DIRECT);

        // After an idle interval, a valid sender may have advanced past the window.
        let resumed = packets(800, 1000);
        for (i, packet) in resumed[..4].iter().enumerate() {
            assert_eq!(q.add(packet, 5, 1003 + 5 * i as u64), DIRECT);
        }
        assert_eq!(q.add(&packets(804, 1020)[0], 5, 1023), DIRECT);
    }

    #[test]
    fn discontinuity_late_data_restart_and_alloc_failure() {
        let mut q = Queue::new(false);
        q.add(&packets(0, 0)[1], 5, 0);
        let p = packets(4, 20);
        assert_eq!(q.add(&p[0], 5, 5), DIRECT);
        assert_eq!(q.add(&p[2], 5, 10), 0);
        assert_eq!(q.add(&packets(8, 40)[0], 5, 25), READY);
        let mut length = 321;
        assert!(q.pop(7, fail, &mut length).is_null());
        assert_eq!(length, 321);
        let mut actual = drain(&mut q);
        assert_eq!(actual.remove(0), Vec::<u8>::new());
        assert_eq!(actual.remove(0), p[2]);
        assert_eq!(actual.remove(0), Vec::<u8>::new());
        assert_eq!(actual.remove(0), packets(8, 40)[0]);
        assert_eq!(q.concealed, 2);
        assert_eq!(q.add(&p[1], 5, 30), 0);
        assert!(q.last_late.is_some());
        let mut legacy = Queue::new(true);
        assert_eq!(legacy.add(&p[0], 5, 30), DIRECT);
        assert_eq!(legacy.add(&p[4], 5, 30), 0);
    }
}
