use aes_gcm::{
    aead::{AeadInPlace, KeyInit},
    aes::{
        self,
        cipher::{block_padding::Pkcs7, BlockDecryptMut, BlockEncryptMut, KeyIvInit},
    },
    Aes128Gcm, Nonce, Tag,
};
use std::{ptr, slice};

/// The outer four bytes count only the following sequence, tag and ciphertext.
fn control(key: &[u8], host: bool, encrypt: bool, seq: u32, wire: &mut [u8]) -> bool {
    if !(28..=65539).contains(&wire.len()) {
        return false;
    }
    let length = (wire.len() - 4) as u16;
    let (hdr, body) = wire.split_at_mut(24);
    let seq = if encrypt {
        seq
    } else {
        if hdr[..2] != 1u16.to_le_bytes() || hdr[2..4] != length.to_le_bytes() {
            return false;
        }
        u32::from_le_bytes(hdr[4..8].try_into().unwrap())
    };
    let mut nonce = [0; 12];
    nonce[..4].copy_from_slice(&seq.to_le_bytes());
    nonce[10] = if host { b'H' } else { b'C' };
    nonce[11] = b'C';
    let Ok(aes) = Aes128Gcm::new_from_slice(key) else {
        return false;
    };
    if encrypt {
        if u16::from_le_bytes([body[2], body[3]]) as usize != body.len() - 4 {
            return false;
        }
        let Ok(tag) = aes.encrypt_in_place_detached(Nonce::from_slice(&nonce), b"", body) else {
            return false;
        };
        hdr[..2].copy_from_slice(&1u16.to_le_bytes());
        hdr[2..4].copy_from_slice(&length.to_le_bytes());
        hdr[4..8].copy_from_slice(&seq.to_le_bytes());
        hdr[8..].copy_from_slice(&tag);
        true
    } else {
        aes.decrypt_in_place_detached(
            Nonce::from_slice(&nonce),
            b"",
            body,
            Tag::from_slice(&hdr[8..]),
        )
        .is_ok()
            && u16::from_le_bytes([body[2], body[3]]) as usize == body.len() - 4
    }
}

#[no_mangle]
pub unsafe extern "C" fn pl_control_record(
    key: *const u8,
    len: usize,
    host: bool,
    encrypt: bool,
    seq: u32,
    record: *mut u8,
    size: usize,
) -> bool {
    if key.is_null() || len != 16 || record.is_null() || size > 65539 {
        return false;
    }
    control(
        slice::from_raw_parts(key, len),
        host,
        encrypt,
        seq,
        slice::from_raw_parts_mut(record, size),
    )
}

#[no_mangle]
pub unsafe extern "C" fn pl_opus_crypt(
    key: *const u8,
    keylen: usize,
    iv: *const u8,
    encrypt: bool,
    input: *const u8,
    length: usize,
    output: *mut u8,
    cap: usize,
) -> i32 {
    if key.is_null()
        || keylen != 16
        || iv.is_null()
        || input.is_null()
        || output.is_null()
        || length == 0
        || length > 65535
    {
        return -1;
    }
    let needed = if encrypt {
        length / 16 * 16 + 16
    } else {
        length
    };
    if cap < needed || (!encrypt && length % 16 != 0) {
        return -1;
    }
    let key = &*key.cast::<[u8; 16]>();
    let iv = &*iv.cast::<[u8; 16]>();
    ptr::copy(input, output, length);
    let buf = slice::from_raw_parts_mut(output, needed);
    if encrypt {
        cbc::Encryptor::<aes::Aes128>::new(key.into(), iv.into())
            .encrypt_padded_mut::<Pkcs7>(buf, length)
            .map_or(-1, |x| x.len() as i32)
    } else {
        cbc::Decryptor::<aes::Aes128>::new(key.into(), iv.into())
            .decrypt_padded_mut::<Pkcs7>(buf)
            .map_or(-1, |x| x.len() as i32)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn control_direction_and_nested_payload_length() {
        let mut wire = vec![0; 24];
        wire.extend_from_slice(&[0, 2, 2, 0, 4, 0]);
        assert!(control(&[0x65; 16], false, true, 0, &mut wire));
        assert!(!control(&[0x65; 16], true, false, 0, &mut wire));
        let mut truncated = wire.clone();
        truncated.pop();
        assert!(!control(&[0x65; 16], false, false, 0, &mut truncated));
        assert!(control(&[0x65; 16], false, false, 0, &mut wire));
        assert_eq!(&wire[24..], &[0, 2, 2, 0, 4, 0]);
        wire[26] = 4;
        assert!(!control(&[0x65; 16], true, true, 2, &mut wire));
    }
    #[test]
    fn opus_cbc_checks_padding_and_output_capacity() {
        let key = [0x03; 16];
        let iv = [0x08; 16];
        let mut buf = [0x41; 64];
        let mut dst = [0u8; 64];
        for n in [1, 15, 16, 17, 31, 32] {
            let len = unsafe {
                pl_opus_crypt(
                    key.as_ptr(),
                    16,
                    iv.as_ptr(),
                    true,
                    buf.as_ptr(),
                    n,
                    dst.as_mut_ptr(),
                    64,
                )
            };
            assert_eq!(len as usize, n / 16 * 16 + 16);
            let clear = unsafe {
                pl_opus_crypt(
                    key.as_ptr(),
                    16,
                    iv.as_ptr(),
                    false,
                    dst.as_ptr(),
                    len as usize,
                    buf.as_mut_ptr(),
                    64,
                )
            };
            assert_eq!(clear, n as i32);
            assert_eq!(&buf[..n], &[0x41; 64][..n]);
            buf.fill(0x41);
        }
        assert_eq!(
            unsafe {
                pl_opus_crypt(
                    key.as_ptr(),
                    16,
                    iv.as_ptr(),
                    true,
                    buf.as_ptr(),
                    16,
                    dst.as_mut_ptr(),
                    16,
                )
            },
            -1
        );
    }
}
