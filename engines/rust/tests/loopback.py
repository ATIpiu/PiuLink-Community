"""Exercise both Rust binaries on an OS-assigned TCP port."""
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[1]
binary = root / "target/release/stream"
out = root / "out"
with (out / "source.ts").open("rb") as video, (out / "received.ts").open("wb") as sink:
    receiver = subprocess.Popen([binary, "receive", "127.0.0.1:0"], stdout=sink,
                                stderr=subprocess.PIPE, text=True)
    try:
        address = receiver.stderr.readline().strip()
        subprocess.run([binary, "send", address], stdin=video, check=True, timeout=30)
        assert receiver.wait(timeout=10) == 0
    finally:
        receiver.kill()
        receiver.wait()
