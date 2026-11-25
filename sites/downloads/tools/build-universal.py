"""Combine the x64 and ARM64 client releases without modifying either runtime."""
from io import BytesIO
from pathlib import Path
import argparse
import plistlib
import re
import tarfile
import zipfile

MAC_LAUNCH = """#!/bin/sh
set -eu
root=$(cd "$(dirname "$0")/../Resources" && pwd)
arch=$(uname -m)
case "$arch" in
  arm64) rid=osx-arm64 ;;
  x86_64) rid=osx-x64 ;;
  *) echo "Unsupported architecture: $arch" >&2; exit 1 ;;
esac
exec "$root/$rid/PiuLink.Mac" "$@"
"""
LINUX_LAUNCH = """#!/bin/sh
set -eu
root=$(cd "$(dirname "$0")" && pwd)
case "$(uname -m)" in
  aarch64|arm64) rid=linux-arm64 ;;
  x86_64|amd64) rid=linux-x64 ;;
  *) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac
exec "$root/$rid/PiuLink.Linux" "$@"
"""

def zip_item(path, mode, data, dest):
    info = zipfile.ZipInfo(path)
    info.create_system = 3
    info.external_attr = (0o100000 | mode) << 16
    dest.writestr(info, data, compress_type=zipfile.ZIP_DEFLATED, compresslevel=5)

def mac(packages, version, output):
    with zipfile.ZipFile(output, "w") as dest:
        for rid in ("osx-arm64", "osx-x64"):
            with zipfile.ZipFile(packages / f"PiuLink-Mac-{version}-{rid}-unsigned.zip") as source:
                for item in source.infolist():
                    root, _, leaf = item.filename.partition("/Contents/")
                    if root != "PiuLink.app" or ".." in leaf.split("/"):
                        raise ValueError(item.filename)
                    if leaf.startswith("MacOS/"):
                        path = "PiuLink.app/Contents/Resources/" + rid + "/" + leaf[6:]
                        zip_item(path, (item.external_attr >> 16) & 0o777, source.read(item), dest)
                    elif rid == "osx-arm64" and leaf:
                        data = source.read(item)
                        if leaf == "Info.plist":
                            info = plistlib.loads(data)
                            info["CFBundleExecutable"] = "PiuLink"
                            data = plistlib.dumps(info)
                        zip_item(item.filename, 0o644, data, dest)
        zip_item("PiuLink.app/Contents/MacOS/PiuLink", 0o755, MAC_LAUNCH.encode(), dest)

def linux(packages, version, output):
    with tarfile.open(output,"w:gz",compresslevel=5) as dest:
        for rid in ("linux-arm64", "linux-x64"):
            with tarfile.open(packages / f"PiuLink-Client-{version}-{rid}.tar.gz") as source:
                for item in source:
                    if not item.isfile() or not item.name.startswith("PiuLink/") or ".." in item.name.split("/"):
                        raise ValueError(item.name)
                    data = source.extractfile(item)
                    item.name = "PiuLink/" + rid + "/" + item.name[len("PiuLink/"):]
                    dest.addfile(item, data)
        launcher = LINUX_LAUNCH.encode()
        info = tarfile.TarInfo("PiuLink/PiuLink")
        info.size, info.mode = len(launcher), 0o755
        dest.addfile(info, BytesIO(launcher))

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("packages", type=Path)
    parser.add_argument("--version", required=True)
    args = parser.parse_args()
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", args.version):
        parser.error("Expected a numeric major.minor.patch version")
    for build, name in [(mac, f"PiuLink-Mac-{args.version}-universal-unsigned.zip"),
                        (linux, f"PiuLink-Client-{args.version}-linux-universal.tar.gz")]:
        output = args.packages / name
        pending = output.with_name(output.name + ".partial")
        build(args.packages, args.version, pending)
        pending.replace(output)
        print(output.name, output.stat().st_size)

if __name__ == "__main__":
    main()
