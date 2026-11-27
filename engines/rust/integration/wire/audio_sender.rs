/// GameStream uses a sequence number per audio data packet. Its two repair
/// packets have their own headers and do not advance the next data sequence.
#[no_mangle]
pub extern "C" fn pl_audio_iv(out: &mut [u8; 16], key_id: u32, seq: u16) {
    out.fill(0);
    out[..4].copy_from_slice(&key_id.wrapping_add(u32::from(seq)).to_be_bytes());
}
fn rtp(out: &mut [u8], kind: u8, seq: u16, pts: u32) {
    out[..2].copy_from_slice(&[0x80, kind]);
    out[2..4].copy_from_slice(&seq.to_be_bytes());
    out[4..8].copy_from_slice(&pts.to_be_bytes());
    out[8..12].fill(0);
}
#[no_mangle]
pub extern "C" fn pl_audio_data(
    out: &mut [u8; 12],
    seq: &mut u16,
    pts: &mut u32,
    duration: u32,
) -> bool {
    rtp(out, 97, *seq, *pts);
    *seq = seq.wrapping_add(1);
    *pts = pts.wrapping_add(duration);
    *seq % 4 == 0
}
#[no_mangle]
pub extern "C" fn pl_audio_parity(
    out: &mut [u8; 24],
    last: u16,
    pts: u32,
    duration: u32,
    index: u8,
) {
    rtp(out, 127, last.wrapping_add(1 + u16::from(index)), 0);
    out[12..14].copy_from_slice(&[index, 97]);
    out[14..16].copy_from_slice(&(last & !3).to_be_bytes());
    out[16..20].copy_from_slice(
        &pts.wrapping_sub(u32::from(last % 4).wrapping_mul(duration))
            .to_be_bytes(),
    );
    out[20..24].fill(0);
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn data_parity_and_iv_survive_rollover() {
        let mut seq = 65532;
        let mut pts = u32::MAX - 4;
        let start = pts;
        let mut hdr = [255u8; 12];
        for i in 0..4 {
            let at = pts;
            let n = seq;
            assert_eq!(pl_audio_data(&mut hdr, &mut seq, &mut pts, 5), i == 3);
            assert_eq!(&hdr[..2], &[0x80, 97]);
            assert_eq!(&hdr[2..4], &n.to_be_bytes());
            assert_eq!(&hdr[4..8], &at.to_be_bytes());
            if i == 3 {
                for j in 0..2 {
                    let mut b = [255; 24];
                    pl_audio_parity(&mut b, n, at, 5, j);
                    assert_eq!(&b[..8], &[128, 127, 0, j, 0, 0, 0, 0]);
                    assert_eq!(&b[12..16], &[j, 97, 255, 252]);
                    assert_eq!(&b[16..20], &start.to_be_bytes());
                }
            }
        }
        assert_eq!((seq, pts), (0, 15));
        let mut iv = [255; 16];
        pl_audio_iv(&mut iv, u32::MAX, 2);
        assert_eq!(iv[3], 1);
        assert_eq!(&iv[4..], &[0; 12]);
    }
}
