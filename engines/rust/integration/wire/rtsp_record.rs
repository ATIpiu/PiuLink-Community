use aes_gcm::{
    aead::{AeadInPlace, KeyInit},
    Aes128Gcm, Nonce, Tag,
};
use std::slice;
const HEADER: usize = 24;
const ENCRYPTED: u32 = 1 << 31;

fn nonce(seq: u32, host: bool) -> [u8; 12] {
    let mut bytes = [0; 12];
    bytes[..4].copy_from_slice(&seq.to_le_bytes());
    bytes[10] = if host { b'H' } else { b'C' };
    bytes[11] = b'R';
    bytes
}
fn seal(key: &[u8], host: bool, seq: u32, msg: &mut [u8]) -> bool {
    if msg.len() <= HEADER || msg.len() - HEADER >= ENCRYPTED as usize || seq == 0 {
        return false;
    }
    let Ok(cipher) = Aes128Gcm::new_from_slice(key) else {
        return false;
    };
    let (hdr, body) = msg.split_at_mut(HEADER);
    let Ok(tag) = cipher.encrypt_in_place_detached(Nonce::from_slice(&nonce(seq, host)), b"", body)
    else {
        return false;
    };
    hdr[..4].copy_from_slice(&(ENCRYPTED | body.len() as u32).to_be_bytes());
    hdr[4..8].copy_from_slice(&seq.to_be_bytes());
    hdr[8..].copy_from_slice(&tag);
    true
}
fn open(key: &[u8], host: bool, msg: &mut [u8]) -> bool {
    if msg.len() <= HEADER || msg.len() - HEADER >= ENCRYPTED as usize {
        return false;
    }
    let (hdr, body) = msg.split_at_mut(HEADER);
    if hdr[..4] != (ENCRYPTED | body.len() as u32).to_be_bytes() {
        return false;
    }
    let seq = u32::from_be_bytes(hdr[4..8].try_into().unwrap());
    let Ok(cipher) = Aes128Gcm::new_from_slice(key) else {
        return false;
    };
    cipher
        .decrypt_in_place_detached(
            Nonce::from_slice(&nonce(seq, host)),
            b"",
            body,
            Tag::from_slice(&hdr[8..]),
        )
        .is_ok()
}
#[no_mangle]
pub unsafe extern "C" fn pl_rtsp_record(
    key: *const u8,
    key_len: usize,
    host: bool,
    encrypt: bool,
    seq: u32,
    record: *mut u8,
    len: usize,
) -> bool {
    if key.is_null() || key_len != 16 || record.is_null() || len > i32::MAX as usize {
        return false;
    }
    let key = slice::from_raw_parts(key, key_len);
    let bytes = slice::from_raw_parts_mut(record, len);
    if encrypt {
        seal(key, host, seq, bytes)
    } else {
        open(key, host, bytes)
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn authenticates_direction_sequence_tag_and_exact_payload() {
        let key = [0x5a; 16];
        let mut record = vec![0; HEADER];
        record.extend_from_slice(b"OPTIONS / RTSP/1.0\r\nCSeq: 2\r\n\r\n");
        let plain = record[HEADER..].to_vec();
        assert!(seal(&key, false, 0x12345678, &mut record));
        assert_eq!(record[4..8], [0x12, 0x34, 0x56, 0x78]);
        assert_ne!(&record[HEADER..], &plain);
        for i in 0..record.len() {
            let mut invalid = record.clone();
            invalid[i] ^= 1;
            assert!(!open(&key, false, &mut invalid));
        }
        assert!(!open(&key, true, &mut record.clone()));
        assert!(!open(&key, false, &mut record[1..].to_vec()));
        assert!(open(&key, false, &mut record));
        assert_eq!(&record[HEADER..], &plain);
        assert!(!seal(&key, true, 0, &mut record));
    }
}
