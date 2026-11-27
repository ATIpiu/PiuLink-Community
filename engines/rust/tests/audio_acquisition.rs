// Replay the queue's first eight data packets on a deterministic RTP clock.
#[path = "../integration/wire/audio_queue.rs"]
mod audio_queue;
#[path = "../integration/wire/recovery.rs"]
mod recovery;

fn main() {
    for duration in [5u32, 10] {
        for offset in 0..4u16 {
            let queue = audio_queue::pl_audio_queue_new(false);
            for restart in [false, true] {
                let base: u16 = if restart { 1200 } else { 65532 };
                let start = if restart { 2000 } else { 0 };
                let mut first = None;
                let mut delivered = 0;
                for i in offset..8 {
                    let mut packet = [0u8; 32];
                    packet[..2].copy_from_slice(&[0x80, 97]);
                    packet[2..4].copy_from_slice(&base.wrapping_add(i).to_ne_bytes());
                    packet[4..8].copy_from_slice(&(u32::from(i) * duration).to_ne_bytes());
                    let elapsed = u64::from(i - offset) * u64::from(duration);
                    let status = unsafe {
                        audio_queue::pl_audio_queue_add(&mut *queue, packet.as_ptr(), 32, duration, start + elapsed)
                    };
                    assert!(status == 0 || status == 4);
                    if status == 4 {
                        first.get_or_insert(elapsed);
                        delivered += 1;
                    }
                }
                println!("duration={duration} offset={offset} resume={restart} first_ms={} packets={delivered}", first.unwrap());
            }
            unsafe { audio_queue::pl_audio_queue_free(queue, &mut [0; 4]) };
        }
    }
}
