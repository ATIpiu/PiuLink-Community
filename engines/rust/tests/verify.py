"""Replay the original policy and its Rust port with identical acknowledgements."""
from pathlib import Path
import csv
import random
import subprocess
import sys

root = Path(__file__).resolve().parents[1]
out = root / "out"
out.mkdir(exist_ok=True)
engine = Path(sys.argv[1]) / "apps/stream-engine"
patch = (engine / "patches/0002-content-adaptive-encoding.patch").read_text()
hunk = patch.split("@@ -0,0 +1,98 @@\n", 1)[1].split("diff --git", 1)[0]
header = "\n".join(line[1:] for line in hunk.splitlines() if line.startswith("+"))
(out / "piulink_content_rate.h").write_text(header + "\n")
for name, source in [("original", engine / "tests/content_adaptive_rate_test.cpp"),
                     ("reference", root / "tests/reference.cpp")]:
    subprocess.run(["g++", "-std=c++17", "-I", out, source, "-o", out / name], check=True)
subprocess.run([out / "original"], check=True)
subprocess.run(["cargo", "build", "--release"], cwd=root, check=True)
rust = root / "target/release/piulink-stream-rate"

def replay(events):
    def run(program):
        return subprocess.run([program], input="\n".join(events) + "\n", text=True,
                              capture_output=True, check=True).stdout.splitlines()
    expected, actual = run(out / "reference"), run(rust)
    assert len(actual) == len(expected) == len(events)
    for index, (a, b) in enumerate(zip(actual, expected)):
        assert a == b, (index, events[index], a, b)
    return [int(row.split()[0]) for row in actual]

def acknowledged(events):
    # Discover the next original encoder request, insert its acknowledgement, and replay.
    index = 0
    changes = []
    while True:
        outputs = replay(events)
        hit = next((i for i in range(index, len(outputs)) if outputs[i]), None)
        if hit is None:
            return changes
        changes.append((hit, outputs[hit]))
        events.insert(hit + 1, f"applied {outputs[hit]}")
        index = hit + 2

rng = random.Random(71)
events = ["reset 20000 60", "enable 1"]
for fps in (0, 24, 60, 120):
    events += [f"reset 20000 {fps}", "enable 0", "frame 100 24 0", "enable 1"]
    events += ["frame 400 24 0"] * 900 + ["frame 400 42 0"] * 400
    events += ["ceiling 8000"]
    events += [f"frame {rng.randrange(100000)} {rng.randrange(52)} {int(i % 60 == 0)}"
               for i in range(500)]
changes = acknowledged(events)
assert {12000, 20000}.issubset(v for _, v in changes), changes
print(f"Synthetic: {len(events)} event outputs matched; {len(changes)} rate requests")
with Path(sys.argv[2]).open(encoding="utf-8-sig") as stream:
    packets = list(csv.reader(stream))
events = ["reset 20000 60", "enable 1"]
events += [f"frame {size} 0 {int('K' in flags)}" for size, flags in packets]
changes = acknowledged(events)
assert len(packets) == 720 and [v for _, v in changes] == [12000, 20000], changes
print(f"NVENC: {len(packets)} packets; {len(events)} event outputs matched; {changes}")

