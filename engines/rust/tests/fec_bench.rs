use std::{hint::black_box, time::Instant};
#[path = "../integration/wire/recovery.rs"]
mod recovery;

// Compare release builds on the same machine.  Report median time per complete FEC operation.
fn measure(label: &str, loops: usize, mut work: impl FnMut()) {
    for _ in 0..loops / 4 {
        work();
    }
    let mut times = Vec::new();
    for _ in 0..9 {
        let start = Instant::now();
        for _ in 0..loops {
            work();
        }
        times.push(start.elapsed().as_nanos() as f64 / 1000.0 / loops as f64);
    }
    times.sort_by(f64::total_cmp);
    println!("{label}: {:.3} us", times[4]);
}

fn main() {
    let (n, p, size) = (32, 8, 1408);
    let mut shards: Vec<Vec<u8>> = (0..n + p)
        .map(|i| {
            (0..size)
                .map(|j| ((j * 61 + i * 13) ^ (j >> 3)) as u8)
                .collect()
        })
        .collect();
    let pointers = shards
        .iter_mut()
        .map(|s| s.as_mut_ptr())
        .collect::<Vec<_>>();
    let mut marks = vec![0; n + p];
    let mut encode = || unsafe {
        assert_eq!(
            recovery::pl_video_encode(
                black_box(pointers.as_ptr()),
                n as u32,
                p as u32,
                size as u32
            ),
            0
        )
    };
    measure("encode-32+8", 400, &mut encode);
    for lost in [1, 4] {
        marks[..lost].fill(1);
        measure(&format!("repair-{lost}"), 600, || unsafe {
            assert_eq!(
                recovery::pl_video_recover(
                    black_box(pointers.as_ptr()),
                    marks.as_ptr(),
                    n as u32,
                    p as u32,
                    size as u32
                ),
                lost as i32
            )
        });
    }
    let audio = vec![1, 0, 0, 0, 0, 0];
    unsafe {
        assert_eq!(recovery::pl_audio_encode(pointers.as_ptr(), 257), 0);
    }
    measure("audio-repair-1", 5000, || unsafe {
        assert_eq!(
            recovery::pl_audio_recover(black_box(pointers.as_ptr()), audio.as_ptr(), 257),
            1
        )
    });
}
