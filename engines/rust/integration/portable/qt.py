"""Prepare the Rust media core for a pinned Windows or Linux Qt build."""
import argparse
from pathlib import Path
import shutil
import subprocess

from client import integrate

HERE = Path(__file__).resolve().parent
PINS = {
    "windows": ("f786e94c7b2f943e24e65d7d74deb539b827fc84", "8599b6042a4ba27749b0f94134dd614b4328a9bc"),
    "linux": ("de2467e433821664cdd2224aad8c89a625be1ad9", "f900dd4767759c7b9d0e93bcea666b55c69ea62f"),
}


def prepare(platform, source, build):
    common = source / "moonlight-common-c/moonlight-common-c"
    for directory, pin in zip((source, common), PINS[platform]):
        revision = subprocess.check_output(["git", "-C", directory, "rev-parse", "HEAD"], text=True).strip()
        if revision != pin:
            raise ValueError(f"{directory}: expected {pin}, found {revision}")
    stage = build / "piulink-core"
    src = stage / "src"
    shutil.copytree(common / "src", src, dirs_exist_ok=True)
    integrate(src, platform)
    cargo = shutil.which("cargo") or str(Path.home() / ".cargo/bin/cargo")
    subprocess.run([cargo, "build", "--release", "--locked", "--manifest-path", HERE / "Cargo.toml",
                    "--target-dir", stage / "target"], check=True)
    cache = build / ".qmake.cache"
    flag = "CONFIG += piulink_media"
    if not cache.exists() or flag not in cache.read_text().splitlines():
        with cache.open("a") as output:
            output.write("\n" + flag + "\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("platform", choices=PINS)
    parser.add_argument("source", type=Path)
    parser.add_argument("build", type=Path)
    args = parser.parse_args()
    prepare(args.platform, args.source.resolve(), args.build.resolve())
