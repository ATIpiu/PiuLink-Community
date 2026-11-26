"""Prepare the PiuLink iPhone Xcode project from its exact upstream revision."""
import argparse
import os
import sys
from pathlib import Path
import shutil
import subprocess
import tempfile


UPSTREAM = "https://github.com/moonlight-stream/moonlight-ios.git"
REVISION = "02dc9780496eeeac6d01c8bbdccb8b6fe71ef28a"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, help="new directory for the editable Xcode project")
    parser.add_argument("--sources-only", action="store_true", help="apply patches without building the Apple transport framework")
    args = parser.parse_args()
    dest = args.output.resolve()
    if dest.exists():
        parser.error(f"Choose a new directory to preserve existing work: {dest}")
    sources = Path(__file__).resolve().parent
    dest.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=dest.parent) as temp:
        checkout = Path(temp) / "src"
        subprocess.run(["git", "init", "-q", str(checkout)], check=True)

        def git(*args):
            subprocess.run(["git", "-C", str(checkout), *args], check=True)

        git("remote", "add", "origin", UPSTREAM)
        git("fetch", "--depth=1", "origin", REVISION)
        git("checkout", "--detach", REVISION)
        git("submodule", "update", "--init", "--recursive", "--depth", "1")
        git("apply", str(sources / "piulink-ios.patch"))
        git("apply", str(sources / "rendezvous.patch"))
        git("apply", str(sources / "p2p.patch"))
        git("apply", str(sources / "diagnostics.patch"))
        for name in ("PLDiagnostics.h", "PLDiagnostics.m"):
            shutil.copyfile(sources / name, checkout / "Limelight" / name)
        if not args.sources_only:
            if sys.platform != "darwin":
                parser.error("Building the iOS transport requires Xcode. Use --sources-only to inspect the sources.")
            transport = sources.parents[1] / "packages/direct-tunnel"
            tools = Path(temp) / "bin"
            tools.mkdir()
            env = dict(os.environ, GOBIN=str(tools), PATH=str(tools) + os.pathsep + os.environ["PATH"])
            for name in ("gobind", "gomobile"):
                subprocess.run(["go", "install", "golang.org/x/mobile/cmd/" + name], cwd=transport, env=env, check=True)
            subprocess.run(["gomobile", "bind", "-target=ios,iossimulator", "-iosversion=15.0",
                            "-ldflags=-s -w", "-o", str(checkout / "Mobile.xcframework"), "./mobile"],
                           cwd=transport, env=env, check=True)
            shutil.copytree(transport / "licenses", checkout / "PiuLink-Transport-Licenses")
        shutil.copyfile(sources / "PiuLink.png", checkout / "Limelight/Images.xcassets/AppIcon.appiconset/PiuLink.png")
        shutil.move(str(checkout), str(dest))
    print(dest / "Moonlight.xcodeproj")


if __name__ == "__main__":
    main()
