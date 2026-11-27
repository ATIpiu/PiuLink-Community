use super::MUL;

#[cfg(any(
    target_arch = "x86_64",
    target_arch = "x86",
    all(target_arch = "aarch64", target_feature = "neon")
))]
const fn high_nibbles() -> [[u8; 16]; 256] {
    let mut high = [[0; 16]; 256];
    let mut k = 0;
    while k < 256 {
        let mut i = 0;
        while i < 16 {
            high[k][i] = MUL[k][i << 4];
            i += 1;
        }
        k += 1;
    }
    high
}
#[cfg(any(
    target_arch = "x86_64",
    target_arch = "x86",
    all(target_arch = "aarch64", target_feature = "neon")
))]
static HIGH: [[u8; 16]; 256] = high_nibbles();

// GF multiplication distributes over XOR. Two 16-entry register lookups replace
// sixteen indexed memory reads, for both encoding and Gaussian elimination.
#[cfg(any(target_arch = "x86_64", target_arch = "x86"))]
#[target_feature(enable = "ssse3")]
unsafe fn chunks<const ACCUMULATE: bool>(
    dst: *mut u8,
    src: *const u8,
    len: usize,
    k: usize,
) -> usize {
    #[cfg(target_arch = "x86")]
    use core::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use core::arch::x86_64::*;
    let low = _mm_loadu_si128(MUL[k].as_ptr().cast());
    let high = _mm_loadu_si128(HIGH[k].as_ptr().cast());
    let mask = _mm_set1_epi8(15);
    let end = len & !15;
    for i in (0..end).step_by(16) {
        let input = _mm_loadu_si128(src.add(i).cast());
        let a = _mm_shuffle_epi8(low, _mm_and_si128(input, mask));
        let b = _mm_shuffle_epi8(high, _mm_and_si128(_mm_srli_epi16::<4>(input), mask));
        let mut result = _mm_xor_si128(a, b);
        if ACCUMULATE {
            result = _mm_xor_si128(result, _mm_loadu_si128(dst.add(i).cast()));
        }
        _mm_storeu_si128(dst.add(i).cast(), result);
    }
    end
}

#[cfg(any(target_arch = "x86_64", target_arch = "x86"))]
#[target_feature(enable = "avx2")]
unsafe fn wide_chunks<const ACCUMULATE: bool>(
    dst: *mut u8,
    src: *const u8,
    len: usize,
    k: usize,
) -> usize {
    #[cfg(target_arch = "x86")]
    use core::arch::x86::*;
    #[cfg(target_arch = "x86_64")]
    use core::arch::x86_64::*;
    let low = _mm256_broadcastsi128_si256(_mm_loadu_si128(MUL[k].as_ptr().cast()));
    let high = _mm256_broadcastsi128_si256(_mm_loadu_si128(HIGH[k].as_ptr().cast()));
    let mask = _mm256_set1_epi8(15);
    let end = len & !31;
    for i in (0..end).step_by(32) {
        let input = _mm256_loadu_si256(src.add(i).cast());
        let a = _mm256_shuffle_epi8(low, _mm256_and_si256(input, mask));
        let b = _mm256_shuffle_epi8(high, _mm256_and_si256(_mm256_srli_epi16::<4>(input), mask));
        let mut result = _mm256_xor_si256(a, b);
        if ACCUMULATE {
            result = _mm256_xor_si256(result, _mm256_loadu_si256(dst.add(i).cast()));
        }
        _mm256_storeu_si256(dst.add(i).cast(), result);
    }
    end
}

#[cfg(all(target_arch = "aarch64", target_feature = "neon"))]
unsafe fn chunks<const ACCUMULATE: bool>(
    dst: *mut u8,
    src: *const u8,
    len: usize,
    k: usize,
) -> usize {
    use core::arch::aarch64::*;
    let low = vld1q_u8(MUL[k].as_ptr());
    let high = vld1q_u8(HIGH[k].as_ptr());
    let mask = vdupq_n_u8(15);
    let end = len & !15;
    for i in (0..end).step_by(16) {
        let input = vld1q_u8(src.add(i));
        let a = vqtbl1q_u8(low, vandq_u8(input, mask));
        let b = vqtbl1q_u8(high, vshrq_n_u8::<4>(input));
        let mut result = veorq_u8(a, b);
        if ACCUMULATE {
            result = veorq_u8(result, vld1q_u8(dst.add(i)));
        }
        vst1q_u8(dst.add(i), result);
    }
    end
}

// For in-place scaling, src and dst may be identical. Other buffers must be disjoint.
// In overwrite mode the destination need not be initialized.
#[inline]
pub(super) unsafe fn combine<const ACCUMULATE: bool>(
    dst: *mut u8,
    src: *const u8,
    len: usize,
    factor: u8,
) {
    if ACCUMULATE && factor == 0 {
        return;
    }
    let k = factor as usize;
    #[cfg(any(target_arch = "x86_64", target_arch = "x86"))]
    let start = if len >= 512 && std::is_x86_feature_detected!("avx2") {
        wide_chunks::<ACCUMULATE>(dst, src, len, k)
    } else if len >= 16 && std::is_x86_feature_detected!("ssse3") {
        chunks::<ACCUMULATE>(dst, src, len, k)
    } else {
        0
    };
    #[cfg(all(target_arch = "aarch64", target_feature = "neon"))]
    let start = if len >= 16 {
        chunks::<ACCUMULATE>(dst, src, len, k)
    } else {
        0
    };
    #[cfg(not(any(
        target_arch = "x86_64",
        target_arch = "x86",
        all(target_arch = "aarch64", target_feature = "neon")
    )))]
    let start = 0;
    for i in start..len {
        let product = MUL[k][*src.add(i) as usize];
        *dst.add(i) = if ACCUMULATE {
            *dst.add(i) ^ product
        } else {
            product
        };
    }
}

#[test]
fn scalar_equivalence_for_all_factors_and_unaligned_tails() {
    let input: Vec<_> = (0..1450).map(|i| (i * 37) as u8).collect();
    for k in 0..=255u8 {
        for len in [1, 15, 16, 17, 31, 32, 33, 255, 256, 269, 511, 512, 513, 1408, 1439] {
            let mut dst = vec![0xa5; len + 7];
            unsafe {
                combine::<true>(dst.as_mut_ptr().add(3), input.as_ptr().add(1), len, k);
            }
            for i in 0..len {
                assert_eq!(dst[3 + i], 0xa5 ^ MUL[k as usize][input[i + 1] as usize]);
            }
            let original = dst.clone();
            let data = unsafe { dst.as_mut_ptr().add(3) };
            unsafe {
                combine::<false>(data, data, len, k);
            }
            for i in 0..len {
                assert_eq!(dst[3 + i], MUL[k as usize][original[3 + i] as usize]);
            }
            assert_eq!(&dst[..3], &[0xa5; 3]);
            assert_eq!(&dst[len + 3..], &[0xa5; 4]);
        }
    }
}
