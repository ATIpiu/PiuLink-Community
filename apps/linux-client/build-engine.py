"""Build the integrated x64 Qt receiver from the project's pinned recursive checkout."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("build", type=Path)
    parser.add_argument("payload", type=Path)
    args = parser.parse_args()
    source, build, payload = (p.resolve() for p in (args.source, args.build, args.payload))
    portable = Path(__file__).resolve().parents[2] / "engines/rust/integration/portable"
    build.mkdir(parents=True, exist_ok=True)
    subprocess.run([sys.executable, portable / "qt.py", "linux", source, build], check=True)
    patch = Path(__file__).with_name("qt-brand.patch").resolve()
    git_apply = ["git", "-C", source, "apply", "--ignore-space-change"]
    if subprocess.run([*git_apply, "--reverse", "--check", patch], capture_output=True).returncode:
        subprocess.run([*git_apply, patch], check=True)
    icon = Path(__file__).resolve().parents[2] / "assets/brand/piulink-app-icon-light.svg"
    shutil.copyfile(icon, source / "app/res/moonlight.svg")
    env = dict(os.environ, QMAKEFEATURES=str(portable), PIULINK_CORE_STAGE=str(build / "piulink-core"))
    subprocess.run(["qmake6", "-r", source / "moonlight-qt.pro", "CONFIG+=release"], cwd=build, env=env, check=True)
    subprocess.run(["make", "-j" + str(min(os.cpu_count() or 1, 8)), "release"], cwd=build, env=env, check=True)
    executable = build / "app/moonlight"
    payload.mkdir(parents=True, exist_ok=True)
    shutil.copy2(executable, payload / "PiuLink-Stream")
    subprocess.run(["strip", "--strip-unneeded", payload / "PiuLink-Stream"], check=True)
    shutil.copyfile(source / "LICENSE", payload / "stream-engine-LICENSE")
    shutil.copyfile(source / "app/SDL_GameControllerDB/gamecontrollerdb.txt", payload / "gamecontrollerdb.txt")
    shutil.copyfile(source / "app/SDL_GameControllerDB/LICENSE", payload / "gamecontrollerdb-LICENSE")


if __name__ == "__main__":
    main()
