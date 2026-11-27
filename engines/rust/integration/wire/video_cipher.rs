use aes_gcm::{
    aead::{AeadInPlace, KeyInit},
    Aes128Gcm, Nonce, Tag,
};
use std::{ptr, slice};

pub struct VideoCipher {
    aes: Aes128Gcm,
    counter: u64,
}
impl VideoCipher {
    pub fn new(key: &[u8]) -> Option<Self> {
        Some(Self {
            aes: Aes128Gcm::new_from_slice(key).ok()?,
            counter: 0,
        })
    }
    fn seal(&mut self, frame: u32, header: &mut [u8; 32], body: &mut [u8]) -> bool {
        let seq = self.counter;
        let Some(next) = seq.checked_add(1) else {
            return false;
        };
        self.counter = next;
        let mut nonce = [0u8; 12];
        nonce[..8].copy_from_slice(&seq.to_le_bytes());
        nonce[11] = b'V';
        let Ok(tag) = self
            .aes
            .encrypt_in_place_detached(Nonce::from_slice(&nonce), b"", body)
        else {
            return false;
        };
        header[..12].copy_from_slice(&nonce);
        header[12..16].copy_from_slice(&frame.to_le_bytes());
        header[16..].copy_from_slice(&tag);
        true
    }
    pub fn open(&self, record: &mut [u8]) -> bool {
        if record.len() < 32 {
            return false;
        }
        let (header, body) = record.split_at_mut(32);
        self.aes
            .decrypt_in_place_detached(
                Nonce::from_slice(&header[..12]),
                b"",
                body,
                Tag::from_slice(&header[16..]),
            )
            .is_ok()
    }
}
#[no_mangle]
pub unsafe extern "C" fn pl_video_cipher_new(key: *const u8, len: usize) -> *mut VideoCipher {
    if key.is_null() || len != 16 {
        return ptr::null_mut();
    }
    VideoCipher::new(slice::from_raw_parts(key, len))
        .map_or(ptr::null_mut(), |v| Box::into_raw(Box::new(v)))
}
#[no_mangle]
pub unsafe extern "C" fn pl_video_cipher_free(cipher: *mut VideoCipher) {
    if !cipher.is_null() {
        drop(Box::from_raw(cipher));
    }
}
#[no_mangle]
pub unsafe extern "C" fn pl_video_cipher_seal(
    cipher: &mut VideoCipher,
    frame: u32,
    header: &mut [u8; 32],
    body: *mut u8,
    len: usize,
) -> bool {
    if body.is_null() || len > 65535 {
        return false;
    }
    cipher.seal(frame, header, slice::from_raw_parts_mut(body, len))
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn each_shard_gets_a_fresh_nonce_and_checks_its_tag() {
        let mut cipher = VideoCipher::new(&[8; 16]).unwrap();
        for i in 0..4 {
            let mut header = [0; 32];
            let mut data = *b"test shard";
            assert!(cipher.seal(123, &mut header, &mut data));
            assert_eq!(header[0], i);
            assert_eq!(header[11], b'V');
            assert_eq!(header[12], 123);
            let mut wire = [header.as_slice(), &data].concat();
            wire[16] ^= 1;
            assert!(!cipher.open(&mut wire));
            wire[16] ^= 1;
            assert!(cipher.open(&mut wire));
            assert_eq!(&wire[32..], b"test shard");
        }
        cipher.counter = u64::MAX;
        assert!(!cipher.seal(0, &mut [0; 32], &mut [0; 12]));
    }
}
