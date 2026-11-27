use core::{ptr, slice};

// GameStream video uses a Cauchy matrix over GF(256), polynomial 0x11d.
const fn tables() -> [[u8; 256]; 256] {
    let mut rows = [[0; 256]; 256];
    let mut x = 0;
    while x < 256 {
        let mut bit = 1;
        let mut product = x as u8;
        while bit < 256 {
            let mut y = 0;
            while y < bit {
                rows[x][bit + y] = rows[x][y] ^ product;
                y += 1;
            }
            product = (product << 1) ^ if product >= 128 { 0x1d } else { 0 };
            bit <<= 1;
        }
        x += 1;
    }
    rows
}
static MUL: [[u8; 256]; 256] = tables();
const fn inverses() -> [u8; 256] {
    let table = tables();
    let mut inv = [0; 256];
    let mut i = 1;
    while i < 256 {
        let mut j = 1;
        while table[i][j] != 1 {
            j += 1;
        }
        inv[i] = j as u8;
        i += 1;
    }
    inv
}
static INV: [u8; 256] = inverses();

fn coefficient(data_index: usize, parity_row: usize, parity: usize) -> u8 {
    INV[((data_index + parity) ^ parity_row) as u8 as usize]
}

#[path = "gf256.rs"]
mod gf256;
use gf256::combine;

#[no_mangle]
pub unsafe extern "C" fn pl_video_encode(
    shards: *const *mut u8,
    data: u32,
    parity: u32,
    bytes: u32,
) -> i32 {
    encode(shards, data, parity, bytes, coefficient)
}

#[no_mangle]
pub unsafe extern "C" fn pl_audio_encode(shards: *const *mut u8, bytes: u32) -> i32 {
    encode(shards, 4, 2, bytes, audio_coefficient)
}

unsafe fn encode(
    shards: *const *mut u8,
    data: u32,
    parity: u32,
    bytes: u32,
    coefficient: impl Fn(usize, usize, usize) -> u8,
) -> i32 {
    if !valid(shards, data, parity, bytes) {
        return -1;
    }
    let (n, p, len) = (data as usize, parity as usize, bytes as usize);
    for j in 0..p {
        let dst = *shards.add(n + j);
        combine::<false>(dst, *shards, len, coefficient(0, j, p));
        for i in 1..n {
            combine::<true>(dst, *shards.add(i), len, coefficient(i, j, p));
        }
    }
    0
}

unsafe fn valid(shards: *const *mut u8, n: u32, p: u32, len: u32) -> bool {
    n != 0
        && p != 0
        && n <= 254
        && p <= 255 - n
        && len != 0
        && len <= i32::MAX as u32
        && !shards.is_null()
        && slice::from_raw_parts(shards, (n + p) as usize)
            .iter()
            .all(|s| !s.is_null())
}

#[no_mangle]
pub unsafe extern "C" fn pl_video_recover(
    shards: *const *mut u8,
    marks: *const u8,
    data: u32,
    parity: u32,
    bytes: u32,
) -> i32 {
    recover(shards, marks, data, parity, bytes, coefficient)
}

#[no_mangle]
pub unsafe extern "C" fn pl_audio_recover(
    shards: *const *mut u8,
    marks: *const u8,
    bytes: u32,
) -> i32 {
    recover(shards, marks, 4, 2, bytes, audio_coefficient)
}

unsafe fn recover(
    shards: *const *mut u8,
    marks: *const u8,
    data: u32,
    parity: u32,
    bytes: u32,
    coefficient: impl Fn(usize, usize, usize) -> u8,
) -> i32 {
    if marks.is_null() || !valid(shards, data, parity, bytes) {
        return -1;
    }
    let (n, p, len) = (data as usize, parity as usize, bytes as usize);
    let mut missing = [0u8; 256];
    let mut count = 0;
    for i in 0..n {
        if *marks.add(i) != 0 {
            missing[count] = i as u8;
            count += 1;
        }
    }
    if count == 0 {
        return 0;
    }
    let mut rows = [0u8; 256];
    let mut available = 0;
    for j in 0..p {
        if *marks.add(n + j) == 0 {
            rows[available] = j as u8;
            available += 1;
            if available == count {
                break;
            }
        }
    }
    if available < count {
        return -1;
    }
    // A single missing shard needs only one parity equation.
    if count == 1 {
        let index = missing[0] as usize;
        let row = rows[0] as usize;
        let dst = *shards.add(index);
        // Distribute the inverse over the parity equation. The first pass
        // scales the parity into the destination; no final rescale is needed.
        let inverse = INV[coefficient(index, row, p) as usize];
        let weights = &MUL[inverse as usize];
        combine::<false>(dst, *shards.add(n + row), len, inverse);
        for i in 0..n {
            if i != index {
                let weight = weights[coefficient(i, row, p) as usize];
                combine::<true>(dst, *shards.add(i), len, weight);
            }
        }
        return 1;
    }
    // At most 127 missing data shards can be recovered from a 255-shard block.
    let mut matrix = [[0u8; 128]; 128];
    for r in 0..count {
        let dst = *shards.add(missing[r] as usize);
        let j = rows[r] as usize;
        ptr::copy_nonoverlapping(*shards.add(n + j), dst, len);
        for i in 0..n {
            if *marks.add(i) == 0 {
                combine::<true>(dst, *shards.add(i), len, coefficient(i, j, p));
            }
        }
        for c in 0..count {
            matrix[r][c] = coefficient(missing[c] as usize, j, p);
        }
    }
    // Every leading principal minor of this Cauchy submatrix is nonsingular.
    for c in 0..count {
        let inv = INV[matrix[c][c] as usize];
        if inv == 0 {
            return -1;
        }
        let scale = &MUL[inv as usize];
        for k in c..count {
            matrix[c][k] = scale[matrix[c][k] as usize];
        }
        let pivot = *shards.add(missing[c] as usize);
        combine::<false>(pivot, pivot, len, inv);
        for r in 0..count {
            if r == c {
                continue;
            }
            let factor = matrix[r][c];
            for k in c..count {
                matrix[r][k] ^= MUL[factor as usize][matrix[c][k] as usize];
            }
            combine::<true>(*shards.add(missing[r] as usize), pivot, len, factor);
        }
    }
    count as i32
}

fn audio_coefficient(data: usize, row: usize, _: usize) -> u8 {
    const AUDIO: [[u8; 4]; 2] = [[0x77, 0x40, 0x38, 0x0e], [0xc7, 0xa7, 0x0d, 0x6c]];
    AUDIO[row][data]
}
