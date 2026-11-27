use aes_gcm::aes::{
    cipher::{BlockDecrypt, BlockEncrypt, KeyInit},
    Aes128,
};
use sha2::{Digest, Sha256};
use std::ffi::c_void;
use subtle::ConstantTimeEq;
use zeroize::Zeroizing;

const LIMIT: usize = 16 * 1024;
const PAIRED: i32 = 1;
const WRONG_PIN: i32 = 2;
const FAILED: i32 = 3;
const BUSY: i32 = 4;
type Backend = unsafe extern "C" fn(*mut c_void, i32, *const u8, usize, *mut u8, usize) -> i32;

fn digest(modern: bool, parts: &[&[u8]]) -> Vec<u8> {
    if modern {
        let mut h = Sha256::new();
        for part in parts {
            h.update(part);
        }
        h.finalize().to_vec()
    } else {
        let mut h = sha1::Sha1::new();
        for part in parts {
            h.update(part);
        }
        h.finalize().to_vec()
    }
}

fn blocks(cipher: &Aes128, data: &[u8], encrypt: bool) -> Zeroizing<Vec<u8>> {
    let mut result = Zeroizing::new(data.to_vec());
    result.resize(data.len().div_ceil(16) * 16, 0);
    for block in result.chunks_exact_mut(16) {
        if encrypt {
            cipher.encrypt_block(block.into())
        } else {
            cipher.decrypt_block(block.into())
        }
    }
    result
}

fn pair(
    modern: bool,
    pin: &[u8],
    mut op: impl FnMut(i32, &[u8]) -> Result<Zeroizing<Vec<u8>>, ()>,
) -> Result<i32, ()> {
    // Java supplies Android's SecureRandom, certificate store, signatures and HTTP transport.
    let entropy = op(0, &[])?;
    if entropy.len() != 48 || pin.len() != 4 || !pin.iter().all(u8::is_ascii_digit) {
        return Ok(FAILED);
    }
    let salt = &entropy[..16];
    let challenge = &entropy[16..32];
    let secret = &entropy[32..];
    let key = Zeroizing::new(digest(modern, &[salt, pin]));
    let cipher = Aes128::new_from_slice(&key[..16]).unwrap();
    let cert = op(1, salt)?;
    if cert.first() == Some(&0) {
        return Ok(FAILED);
    }
    if cert.first() == Some(&2) {
        op(8, &[])?;
        return Ok(BUSY);
    }
    if cert.as_slice() != [1] {
        return Ok(FAILED);
    }
    let response = op(2, &blocks(&cipher, challenge, true))?;
    let n = if modern { 32 } else { 20 };
    let fail = |op: &mut dyn FnMut(i32, &[u8]) -> Result<Zeroizing<Vec<u8>>, ()>, reason| {
        op(8, &[])?;
        Ok(reason)
    };
    if response.len() != 48 {
        return fail(&mut op, FAILED);
    }
    let clear = blocks(&cipher, &response, false);
    let client_sig = op(3, &[])?;
    let hash = Zeroizing::new(digest(modern, &[&clear[n..n + 16], &client_sig, secret]));
    let signed = op(4, &blocks(&cipher, &hash, true))?;
    if signed.len() <= 16 || op(5, &signed)?.as_slice() != [1] {
        return fail(&mut op, FAILED);
    }
    let server_sig = op(6, &[])?;
    let expected = Zeroizing::new(digest(modern, &[challenge, &server_sig, &signed[..16]]));
    if !bool::from(expected.ct_eq(&clear[..n])) {
        return fail(&mut op, WRONG_PIN);
    }
    if op(7, secret)?.as_slice() != [1] || op(9, &[])?.as_slice() != [1] {
        return fail(&mut op, FAILED);
    }
    Ok(PAIRED)
}

#[no_mangle]
pub unsafe extern "C" fn pl_client_pair(
    modern: bool,
    pin: *const u8,
    n: usize,
    ctx: *mut c_void,
    backend: Backend,
) -> i32 {
    let call = |step, data: &[u8]| {
        let mut output = Zeroizing::new(vec![0; LIMIT]);
        let count = backend(
            ctx,
            step,
            data.as_ptr(),
            data.len(),
            output.as_mut_ptr(),
            output.len(),
        );
        if count < 0 || count as usize > LIMIT {
            return Err(());
        }
        output.truncate(count as usize);
        Ok(output)
    };
    pair(modern, std::slice::from_raw_parts(pin, n), call).unwrap_or(FAILED)
}
#[cfg(test)]
mod tests {
    use super::*;
    fn hex(s: &str) -> Vec<u8> {
        (0..s.len())
            .step_by(2)
            .map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap())
            .collect()
    }
    #[test]
    fn modern_and_legacy_pairing_match_openssl_and_reject_bad_pin() {
        for (modern,challenge,reply,proof) in [
            (true,"5febcdc985b2baddd65925da162380eb","8ddca6f07674cac8de2dbada0ce18eda935ef644b79568e37936ac93960642c2729847f14eec2c92157f58d16be4ef80","1b4a4e9d02ae9c5bede3d2dbac55f208cb0b1b3cffa684126a4e9cb589fb93a9"),
            (false,"74835d5e27e70f2e39548f80ad3cbff8","2a0ebfe2b17f4d37b090e6799f733572b947b4e8df49e71f6e51c45c66df1fd3d20d8294ee24cd72613b23287e812fb2","45b38aab6b05934974b7b540d8743f99352532679acd10a11c7621db86141e48")] {
            for corrupt in [false,true] {
                let mut signed=false;
                let status=pair(modern,b"1234",|step,input|Ok(Zeroizing::new(match step {
                    0=>(0..48).collect(),1=>vec![1],
                    2=>{assert_eq!(input,hex(challenge));hex(reply)},
                    3=>b"client".to_vec(),
                    4=>{assert_eq!(input,hex(proof));(48..=64).collect()},
                    5=>vec![1],6=>if corrupt {b"fake".to_vec()}else{b"host".to_vec()},
                    7=>{signed=true;assert_eq!(input,(32..48).collect::<Vec<u8>>());vec![1]},
                    8=>vec![],9=>vec![1],_=>panic!()
                }))).unwrap();
                assert_eq!(status,if corrupt {WRONG_PIN}else{PAIRED});
                assert_eq!(signed,!corrupt);
            }
        }
    }
}
