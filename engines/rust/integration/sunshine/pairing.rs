use aes_gcm::aes::cipher::{BlockDecrypt, BlockEncrypt, KeyInit};
use aes_gcm::aes::Aes128;
use sha2::{Digest, Sha256};
use subtle::ConstantTimeEq;
use zeroize::{Zeroize, ZeroizeOnDrop};

#[derive(Default, Zeroize, ZeroizeOnDrop)]
pub struct Pair {
    phase: u8,
    key: [u8; 16],
    secret: [u8; 16],
    challenge: [u8; 16],
    proof: [u8; 32],
}

impl Pair {
    fn begin(&mut self, salt: &[u8; 16], pin: &[u8]) -> bool {
        if self.phase != 0 || pin.len() != 4 || !pin.iter().all(u8::is_ascii_digit) {
            return false;
        }
        let mut digest = Sha256::new()
            .chain_update(salt)
            .chain_update(pin)
            .finalize();
        self.key.copy_from_slice(&digest[..16]);
        digest.as_mut_slice().zeroize();
        self.phase = 1;
        true
    }

    fn respond(
        &mut self,
        encrypted: &[u8; 16],
        cert_sig: &[u8],
        random: &[u8; 32],
    ) -> Option<[u8; 48]> {
        if self.phase != 1 || cert_sig.is_empty() {
            return None;
        }
        let cipher = Aes128::new((&self.key).into());
        let mut clear = *encrypted;
        cipher.decrypt_block((&mut clear).into());
        self.secret.copy_from_slice(&random[..16]);
        self.challenge.copy_from_slice(&random[16..]);
        let digest = Sha256::new()
            .chain_update(clear)
            .chain_update(cert_sig)
            .chain_update(self.secret)
            .finalize();
        clear.zeroize();
        let mut result = [0; 48];
        result[..32].copy_from_slice(&digest);
        result[32..].copy_from_slice(&self.challenge);
        for block in result.chunks_exact_mut(16) {
            cipher.encrypt_block(block.into());
        }
        self.phase = 2;
        Some(result)
    }

    fn receive_proof(&mut self, encrypted: &[u8; 32]) -> Option<[u8; 16]> {
        if self.phase != 2 {
            return None;
        }
        let cipher = Aes128::new((&self.key).into());
        self.proof = *encrypted;
        for block in self.proof.chunks_exact_mut(16) {
            cipher.decrypt_block(block.into());
        }
        self.phase = 3;
        Some(self.secret)
    }

    fn finish(&mut self, cert_sig: &[u8], secret: &[u8; 16], signature_valid: bool) -> bool {
        if self.phase != 3 || cert_sig.is_empty() {
            return false;
        }
        let digest = Sha256::new()
            .chain_update(self.challenge)
            .chain_update(cert_sig)
            .chain_update(secret)
            .finalize();
        let matches = bool::from(self.proof.ct_eq(digest.as_slice()));
        self.zeroize();
        self.phase = 4;
        matches && signature_valid
    }
}

// The C++ owner holds the pairing mutex and validates fixed-size buffers before calling.
#[no_mangle]
pub extern "C" fn pl_pair_new() -> *mut Pair {
    Box::into_raw(Box::default())
}
#[no_mangle]
pub unsafe extern "C" fn pl_pair_free(pair: *mut Pair) {
    if !pair.is_null() {
        drop(Box::from_raw(pair));
    }
}
#[no_mangle]
pub unsafe extern "C" fn pl_pair_fresh(pair: &Pair) -> bool {
    pair.phase == 0
}
#[no_mangle]
pub unsafe extern "C" fn pl_pair_begin(
    pair: &mut Pair,
    salt: &[u8; 16],
    pin: *const u8,
    n: usize,
) -> bool {
    pair.begin(salt, std::slice::from_raw_parts(pin, n))
}
#[no_mangle]
pub unsafe extern "C" fn pl_pair_challenge(
    pair: &mut Pair,
    encrypted: &[u8; 16],
    signature: *const u8,
    n: usize,
    random: &[u8; 32],
    out: &mut [u8; 48],
) -> bool {
    if let Some(response) =
        pair.respond(encrypted, std::slice::from_raw_parts(signature, n), random)
    {
        *out = response;
        true
    } else {
        false
    }
}
#[no_mangle]
pub extern "C" fn pl_pair_proof(pair: &mut Pair, encrypted: &[u8; 32], out: &mut [u8; 16]) -> bool {
    if let Some(secret) = pair.receive_proof(encrypted) {
        *out = secret;
        true
    } else {
        false
    }
}
#[no_mangle]
pub unsafe extern "C" fn pl_pair_finish(
    pair: &mut Pair,
    signature: *const u8,
    n: usize,
    secret: &[u8; 16],
    signature_valid: bool,
) -> bool {
    pair.finish(
        std::slice::from_raw_parts(signature, n),
        secret,
        signature_valid,
    )
}
#[cfg(test)]
mod tests {
    use super::*;
    // Generated with Node's OpenSSL AES-128-ECB and SHA-256 implementations.
    fn hex<const N: usize>(s: &str) -> [u8; N] {
        std::array::from_fn(|i| u8::from_str_radix(&s[i * 2..i * 2 + 2], 16).unwrap())
    }
    #[test]
    fn openssl_transcript_requires_both_proofs_and_correct_order() {
        for (corrupt, signed) in [(false, true), (true, true), (false, false)] {
            let mut pair = Pair::default();
            let challenge = hex("5febcdc985b2baddd65925da162380eb");
            let proof = hex("1b4a4e9d02ae9c5bede3d2dbac55f208cb0b1b3cffa684126a4e9cb589fb93a9");
            let random = std::array::from_fn(|i| 48 + i as u8);
            assert!(pair.receive_proof(&proof).is_none());
            assert!(pair.begin(&std::array::from_fn(|i| i as u8), b"1234"));
            assert_eq!(pair.respond(&challenge,b"host",&random).unwrap(),hex("8ddca6f07674cac8de2dbada0ce18eda935ef644b79568e37936ac93960642c2729847f14eec2c92157f58d16be4ef80"));
            assert_eq!(pair.receive_proof(&proof).unwrap(), random[..16]);
            let mut secret = std::array::from_fn(|i| 32 + i as u8);
            if corrupt {
                secret[0] ^= 1;
            }
            assert_eq!(pair.finish(b"client", &secret, signed), !corrupt && signed);
            assert!(!pair.finish(b"client", &secret, true));
        }
    }
}
