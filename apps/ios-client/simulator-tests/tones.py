import array
import math
from pathlib import Path
import struct
import sys
import wave

tones = (523, 997, 733, 1319)
if sys.argv[1] == "generate":
    with wave.open(sys.argv[2], "wb") as w:
        w.setparams((1, 2, 48000, 0, "NONE", ""))
        for freq in tones:
            w.writeframes(b"".join(struct.pack("<h", int(4800 * math.sin(2 * math.pi * freq * i / 48000))) for i in range(31200)))
            w.writeframes(bytes(24000))
else:
    rate, channels = map(int, Path(sys.argv[3]).read_text().split())
    samples = array.array("h" if sys.argv[1] == "pcm" else "f")
    samples.frombytes(Path(sys.argv[2]).read_bytes())
    chan = samples[::channels]
    step = rate // 10
    observed = []
    for start in range(0,len(chan)-step,step):
        block = chan[start:start+step]
        energy = sum(v*v for v in block)
        if energy < 1e-5: continue
        scores = []
        for freq in tones:
            coeff = 2*math.cos(2*math.pi*freq/rate)
            a = b = 0.
            for value in block: a,b = value+coeff*a-b,a
            scores.append((a*a+b*b-coeff*a*b)*2/(step*energy))
        if max(scores) > .55:
            label = tones[scores.index(max(scores))]
            if not observed or observed[-1] != label: observed.append(label)
    print("iOS output detected frequencies", observed)
    sys.exit(0 if list(tones) == observed else 1)
