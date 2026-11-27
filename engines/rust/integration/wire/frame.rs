use core::{ffi::c_void, mem, ptr};

#[derive(Copy, Clone)]
#[repr(C)]
pub struct List {
    head: *mut c_void,
    tail: *mut c_void,
    count: u32,
}
const EMPTY: List = List {
    head: ptr::null_mut(),
    tail: ptr::null_mut(),
    count: 0,
};

#[repr(C)]
pub struct Frame {
    blocks: [List; 4],
    number: u32,
    count: u8,
    last: u8,
}

type Consume = unsafe extern "C" fn(*mut List, bool);

impl Frame {
    unsafe fn drain(&mut self, deliver: bool, consume: Consume) {
        let count = mem::take(&mut self.count);
        for i in 0..count {
            let mut owned = mem::replace(&mut self.blocks[i as usize], EMPTY);
            consume(&mut owned, deliver);
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn pl_frame_discard(frame: &mut Frame, consume: Consume) {
    frame.drain(false, consume);
}

// Move one complete, ordered block. Transfer entries to the decoder only after
// the last block of the same frame has arrived. C owns packet destruction.
#[no_mangle]
pub unsafe extern "C" fn pl_frame_stage(
    frame: &mut Frame,
    number: u32,
    index: u8,
    last: u8,
    block: &mut List,
    expected: u32,
    consume: Consume,
) -> i32 {
    if last >= 4
        || index > last
        || index != frame.count
        || (index != 0 && (frame.number != number || frame.last != last))
        || expected == 0
        || block.count != expected
    {
        frame.drain(false, consume);
        consume(block, false);
        return -1;
    }
    frame.number = number;
    frame.last = last;
    frame.blocks[index as usize] = mem::replace(block, EMPTY);
    frame.count += 1;
    if index < last {
        return 0;
    }
    frame.drain(true, consume);
    1
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::RefCell;
    thread_local! { static SEEN: RefCell<Vec<(usize,bool)>> = const { RefCell::new(Vec::new()) }; }
    unsafe extern "C" fn consume(list: *mut List, deliver: bool) {
        let id = (*list).head as usize;
        SEEN.with(|s| s.borrow_mut().push((id, deliver)));
        *list = EMPTY;
    }
    fn list(id: usize) -> List {
        List {
            head: id as *mut _,
            tail: id as *mut _,
            count: 1,
        }
    }
    fn events() -> Vec<(usize, bool)> {
        SEEN.with(|s| std::mem::take(&mut *s.borrow_mut()))
    }
    #[test]
    fn complete_frames_transfer_once_and_in_order() {
        let mut frame = Frame {
            blocks: [EMPTY; 4],
            number: 0,
            count: 0,
            last: 0,
        };
        for last in 0..4 {
            for i in 0..=last {
                let mut block = list(1 + i as usize);
                let action = unsafe {
                    pl_frame_stage(&mut frame, u32::MAX, i, last, &mut block, 1, consume)
                };
                assert_eq!(action, i32::from(i == last));
                assert!(block.head.is_null());
                if i < last {
                    assert!(events().is_empty());
                }
            }
            assert_eq!(
                events(),
                (1..=last as usize + 1)
                    .map(|i| (i, true))
                    .collect::<Vec<_>>()
            );
        }
    }
    #[test]
    fn broken_frames_release_every_owned_block_and_allow_restart() {
        for (number, index, last, count) in [(8, 2, 2, 1), (9, 1, 2, 1), (8, 1, 1, 1), (8, 1, 2, 2)]
        {
            let mut frame = Frame {
                blocks: [EMPTY; 4],
                number: 0,
                count: 0,
                last: 0,
            };
            unsafe {
                assert_eq!(
                    pl_frame_stage(&mut frame, 8, 0, 2, &mut list(1), 1, consume),
                    0
                );
                assert_eq!(
                    pl_frame_stage(
                        &mut frame,
                        number,
                        index,
                        last,
                        &mut list(2),
                        count,
                        consume
                    ),
                    -1
                );
                pl_frame_discard(&mut frame, consume);
                assert_eq!(
                    pl_frame_stage(&mut frame, 9, 0, 0, &mut list(3), 1, consume),
                    1
                );
            }
            assert_eq!(events(), [(1, false), (2, false), (3, true)]);
        }
    }
}
