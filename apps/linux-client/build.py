"""Publish the shared Avalonia desktop client as a portable Linux archive."""
import argparse
from pathlib import Path
import subprocess
import tarfile
import tempfile
import xml.etree.ElementTree as ET


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime", choices=("linux-x64", "linux-arm64"), default="linux-x64")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    project = Path(__file__).resolve().parents[1] / "macos-client/src/PiuLink.Mac/PiuLink.Mac.csproj"
    version = ET.parse(project).findtext("./PropertyGroup/Version")
    args.output.mkdir(parents=True, exist_ok=True)
    archive = args.output / f"PiuLink-Client-{version}-{args.runtime}.tar.gz"
    if archive.exists():
        parser.error(f"Archive already exists: {archive}")
    with tempfile.TemporaryDirectory(dir=args.output) as stage:
        payload = Path(stage) / "PiuLink"
        subprocess.run(["dotnet", "publish", str(project), "-c", "Release", "-r", args.runtime,
                        "--self-contained", "true", "-p:DebugType=None", "-p:DebugSymbols=false", "-o", str(payload)], check=True)
        if not (payload / "PiuLink.Linux").is_file():
            raise FileNotFoundError("Linux apphost missing from publish output")
        staging_archive = Path(stage) / archive.name
        with tarfile.open(staging_archive, "w:gz", compresslevel=5) as bundle:
            for item in sorted(payload.rglob("*")):
                if item.is_file():
                    info = bundle.gettarinfo(str(item), str(item.relative_to(payload.parent)).replace("\\", "/"))
                    info.mode = 0o755 if item.name in ("PiuLink.Linux", "piulink-direct") else 0o644
                    with item.open("rb") as source:
                        bundle.addfile(info, source)
        staging_archive.rename(archive)
    print(archive)


if __name__ == "__main__":
    main()
