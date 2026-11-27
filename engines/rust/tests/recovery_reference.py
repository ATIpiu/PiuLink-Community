"""Compare production Windows Rust parity and recovery against Moonlight rs.c."""
import argparse
import ctypes as c
import random
from pathlib import Path

p = argparse.ArgumentParser()
p.add_argument("rust", type=Path)
p.add_argument("reference", type=Path)
args = p.parse_args()
rust, ref = (c.CDLL(str(path.resolve())) for path in (args.rust, args.reference))
ref.reed_solomon_new.restype = c.c_void_p
ref.reed_solomon_release.argtypes = [c.c_void_p]
ref.reed_solomon_init()
rng = random.Random(20261127)
cases = missing_total = 0
for n, parity in [(1,1), (2,3), (4,2), (10,4), (32,16), (127,128), (220,35), (254,1)]:
    for size in [1, 37, 1408]:
        for missing in sorted({0, 1, min(3,n,parity), min(n,parity)}):
            data = [rng.randbytes(size) for _ in range(n)]
            buffers = [c.create_string_buffer(b,size) for b in data + [bytes(size)]*parity]
            pointers = (c.c_void_p*(n+parity))(*(c.addressof(b) for b in buffers))
            codec = c.c_void_p(ref.reed_solomon_new(n,parity))
            assert codec and ref.reed_solomon_encode(codec,pointers,n+parity,size)==0
            expected = [b.raw for b in buffers[n:]]
            ref.reed_solomon_release(codec)
            for b in buffers[n:]: c.memset(b,0xaa,size)
            assert rust.pl_video_encode(pointers,n,parity,size)==0
            assert expected == [b.raw for b in buffers[n:]]
            marks = (c.c_ubyte*(n+parity))()
            for i in rng.sample(range(n),missing):
                marks[i]=1
                c.memset(buffers[i],0x55,size)
            for j in rng.sample(range(parity),parity-missing): marks[n+j]=1
            if missing:
                available = next(j for j in range(n,n+parity) if not marks[j])
                marks[available]=1
                before=[b.raw for b in buffers]
                assert rust.pl_video_recover(pointers,marks,n,parity,size)<0
                assert before == [b.raw for b in buffers]
                marks[available]=0
            assert rust.pl_video_recover(pointers,marks,n,parity,size)==missing
            assert [b.raw for b in buffers[:n]] == data
            cases+=1
            missing_total+=missing
print(f"{cases} cases matched; {missing_total} missing shards recovered byte-for-byte")

# Moonlight overrides the standard video matrix for 4+2 audio.
class Codec(c.Structure):
    _fields_ = [("data",c.c_int),("parity",c.c_int),("total",c.c_int),
                ("matrix",c.c_void_p),("coeff",c.c_void_p)]

matrix=bytes.fromhex("77 40 38 0e c7 a7 0d 6c")
codec=c.c_void_p(ref.reed_solomon_new(4,2))
native=Codec.from_address(codec.value)
c.memmove(native.coeff,matrix,8)
c.memmove(native.matrix+16,matrix,8)
for mask in range(64):
    original=[rng.randbytes(257) for _ in range(4)]
    packets=[c.create_string_buffer(b,257) for b in original+[bytes(257)]*2]
    pointers=(c.c_void_p*6)(*(c.addressof(b) for b in packets))
    assert ref.reed_solomon_encode(codec,pointers,6,257)==0
    parity_bytes=[b.raw for b in packets[4:]]
    assert rust.pl_audio_encode(pointers,257)==0
    assert parity_bytes==[b.raw for b in packets[4:]]
    marks=(c.c_ubyte*6)(*((mask>>i)&1 for i in range(6)))
    for i in range(6):
        if marks[i]: c.memset(packets[i],0xcc,257)
    missing=sum(marks[:4])
    recovered=rust.pl_audio_recover(pointers,marks,257)
    if missing<=2-sum(marks[4:]):
        assert recovered==missing and original==[b.raw for b in packets[:4]]
    else:
        assert recovered==-1
ref.reed_solomon_release(codec)
print("64 audio loss masks matched, including all recoverable payloads")
