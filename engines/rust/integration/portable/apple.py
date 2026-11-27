"""Build the Rust archives that the Apple clients link into their streaming process."""
import argparse
import os
from pathlib import Path
import subprocess

HERE = Path(__file__).resolve().parent


def run(*cmd, env=None):
    subprocess.run(cmd, check=True, env=env)


def build(platform, out):
    out.mkdir(parents=True, exist_ok=True)
    triples = (["aarch64-apple-ios", "aarch64-apple-ios-sim", "x86_64-apple-ios"]
               if platform == "ios" else ["aarch64-apple-darwin", "x86_64-apple-darwin"])
    env = dict(os.environ, IPHONEOS_DEPLOYMENT_TARGET="15.0", MACOSX_DEPLOYMENT_TARGET="12.0")
    archives = []
    for target in triples:
        run("cargo", "build", "--release", "--locked", "--manifest-path", HERE / "Cargo.toml",
            "--target-dir", out / "target", "--target", target, env=env)
        archives.append(out / "target" / target / "release/libpiulink_stream_core.a")
    if platform == "ios":
        simulator = out / "simulator/libpiulink_stream_core.a"
        simulator.parent.mkdir(exist_ok=True)
        run("xcrun", "lipo", "-create", *archives[1:], "-output", simulator)
        run("xcodebuild", "-create-xcframework", "-library", archives[0], "-library", simulator,
            "-output", out / "PiuLinkCore.xcframework")
    else:
        run("xcrun", "lipo", "-create", *archives, "-output", out / "libpiulink_stream_core.a")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("platform", choices=["ios", "macos"])
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    build(args.platform, args.output.resolve())
