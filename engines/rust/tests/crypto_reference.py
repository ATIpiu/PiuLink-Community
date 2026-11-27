"""Compare Rust wire records with Node/OpenSSL using deterministic synthetic keys."""
import ctypes as c
import json
from pathlib import Path
import subprocess
import sys

dll = c.CDLL(str(Path(sys.argv[1]).resolve()))
P = c.c_void_p
U = c.c_uint32
N = c.c_size_t
B = c.c_bool
record_args = [P, N, B, B, U, P, N]
for name in ("pl_control_record", "pl_rtsp_record"):
    fn = getattr(dll, name)
    fn.argtypes = record_args
    fn.restype = B
dll.pl_video_cipher_new.argtypes = [P, N]
dll.pl_video_cipher_new.restype = P
dll.pl_video_cipher_free.argtypes = [P]
dll.pl_video_cipher_seal.argtypes = [P, U, P, P, N]
dll.pl_video_cipher_seal.restype = B
dll.pl_opus_crypt.argtypes = [P, N, P, B, P, N, P, N]
dll.pl_opus_crypt.restype = c.c_int
reference = """
const crypto = require('node:crypto');
const vectors = JSON.parse(require('node:fs').readFileSync(0,'utf8'));
console.log(JSON.stringify(vectors.map(({key,iv,data,mode}) => {
  const cipher = crypto.createCipheriv(mode,Buffer.from(key,'hex'),Buffer.from(iv,'hex'));
  const body = Buffer.concat([cipher.update(Buffer.from(data,'hex')),cipher.final()]);
  return {body:body.toString('hex'),tag:mode.endsWith('gcm')?cipher.getAuthTag().toString('hex'):''};
})));
"""
key = bytes(range(16))
vectors = []
expected = []


def compare(mode, iv, data, cipher, tag=b""):
    vectors.append(dict(mode=mode, key=key.hex(), iv=iv.hex(), data=data.hex()))
    expected.append(dict(body=cipher.hex(), tag=tag.hex()))


for host in (False, True):
    for seq in (1, 0x01020304, 0xffffffff):
        for size in (1, 16, 97, 1350):
            data = bytes(i % 256 for i in range(size))
            for kind, fn in ((b'R', dll.pl_rtsp_record), (b'C', dll.pl_control_record)):
                body = data if kind == b'R' else b'\x02\x55' + size.to_bytes(2,'little') + data
                wire = c.create_string_buffer(bytes(24)+body, len(body)+24)
                assert fn(key,16,host,True,seq,wire,len(wire))
                iv = seq.to_bytes(4,'little') + bytes(6) + (b'H' if host else b'C') + kind
                compare("aes-128-gcm",iv,body,wire.raw[24:],wire.raw[8:24])
                assert fn(key,16,host,False,0,wire,len(wire)) and wire.raw[24:] == body
video = dll.pl_video_cipher_new(key,16)
assert video
try:
    for seq in range(6):
        data = bytes(range(128))*10
        header = c.create_string_buffer(32)
        body = c.create_string_buffer(data,len(data))
        assert dll.pl_video_cipher_seal(video,151,header,body,len(body))
        iv=seq.to_bytes(8,'little')+bytes(3)+b'V'
        assert header.raw[:12] == iv and int.from_bytes(header.raw[12:16],'little') == 151
        compare("aes-128-gcm",iv,data,body.raw,header.raw[16:])
finally:
    dll.pl_video_cipher_free(video)
for n in (1,15,16,32,135,1279,2048):
    data = bytes(i%256 for i in range(n))
    iv = (0xfffff001+n).to_bytes(4,'big')+bytes(12)
    output = c.create_string_buffer(n+16)
    length = dll.pl_opus_crypt(key,16,iv,True,data,n,output,len(output))
    assert length==(n//16+1)*16
    compare("aes-128-cbc",iv,data,output.raw[:length])
    clear=c.create_string_buffer(length)
    assert dll.pl_opus_crypt(key,16,iv,False,output,length,clear,length)==n and clear.raw[:n]==data
actual=json.loads(subprocess.check_output(["node","-e",reference],text=True,input=json.dumps(vectors)))
assert actual==expected
print(f"{len(expected)} wire ciphertext/tag vectors matched Node/OpenSSL")
