"""Package PiuLink's release archives with dpkg-deb, without installing them."""
import argparse
import hashlib
import os
from pathlib import Path, PurePosixPath
import shutil
import struct
import subprocess
import tarfile
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
DESKTOP = """[Desktop Entry]
Type=Application
Name=PiuLink
Comment=Connect to a remote PiuLink host
Comment[zh_CN]=连接远端 PiuLink 主机
Exec=/usr/bin/piulink-client
Icon=piulink-client
Terminal=false
Categories=Network;RemoteAccess;
"""
HOST_LAUNCH = """#!/bin/sh
export PIULINK_HOST_APP_DIR=/usr/lib/piulink-host/app
export PIULINK_X11_INPUT_LIB=/usr/lib/piulink-host/native/libpiulink-x11-input.so
exec /usr/lib/piulink-host/piulink-host "$@"
"""
HOST_SETUP = """#!/bin/sh
set -eu
/usr/lib/piulink-host/bootstrap-ubuntu-26.04-x64.sh
mkdir -p "$HOME/.local/bin"
launcher="$HOME/.local/bin/piulink-host"
if [ ! -e "$launcher" ] && [ ! -L "$launcher" ]; then
    ln -s /usr/bin/piulink-host "$launcher"
fi
systemctl --user daemon-reload
echo 'Choose your desktop mode with /usr/bin/piulink-host setup desktop or setup headless.'
echo 'Then start it with systemctl --user enable --now piulink-host.service.'
"""
CLIENT_DEPS = (
    "libc6, libgcc-s1, libstdc++6, zlib1g, libgssapi-krb5-2, libssl3t64 | libssl3, "
    "libicu78 | libicu76 | libicu74 | libicu72 | libicu70, libx11-6, libxext6, "
    "libxrandr2, libxi6, libxcursor1, libxrender1, libfontconfig1, libgl1"
)
HOST_DEPS = (
    "bash, apt, ca-certificates, curl, openssl, systemd, util-linux, xauth, "
    "x11-xserver-utils, libx11-6, libxtst6, libevdev2"
)


def write(path, text, mode=0o644):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")
    path.chmod(mode)


def unpack(archive, prefix, destination):
    with tarfile.open(archive) as source:
        for item in source:
            name = PurePosixPath(item.name)
            if not item.isfile() or name.parts[0] != prefix or ".." in name.parts:
                raise ValueError(f"Unexpected entry in {archive}: {name}")
            path = destination.joinpath(*name.parts[1:])
            path.parent.mkdir(parents=True, exist_ok=True)
            with source.extractfile(item) as data, path.open("wb") as target:
                shutil.copyfileobj(data, target)
            path.chmod(0o755 if item.mode & 0o111 else 0o644)


def verify_elf(path, arch):
    with path.open("rb") as binary:
        header = binary.read(20)
    if header[:6] != b"\x7fELF\x02\x01" or struct.unpack_from("<H", header, 18)[0] != {"amd64": 62, "arm64": 183}[arch]:
        raise ValueError(f"{path.name} does not contain a {arch} Linux binary")


def client(root, docs, arch, version, args):
    rid = "linux-x64" if arch == "amd64" else "linux-arm64"
    payload = root / "usr/lib/piulink-client"
    unpack(args.packages / f"PiuLink-Client-{version}-{rid}.tar.gz", "PiuLink", payload)
    verify_elf(payload / "PiuLink.Linux", arch)
    write(root / "usr/bin/piulink-client", '#!/bin/sh\nexec /usr/lib/piulink-client/PiuLink.Linux "$@"\n', 0o755)
    write(root / "usr/share/applications/piulink-client.desktop", DESKTOP)
    icon = root / "usr/share/icons/hicolor/512x512/apps/piulink-client.png"
    icon.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(ROOT / "assets/brand/exports/google-play/piulink-google-play-512.png", icon)
    shutil.copyfile(ROOT / "apps/linux-client/README.md", docs / "README.md")
    shutil.copytree(args.client_notices, docs / "third-party")


def host(root, docs, arch, version, args):
    payload = root / "usr/lib/piulink-host"
    unpack(args.packages / f"PiuLink-Host-{version}-ubuntu26.04-x64.tar.gz", "PiuLink-Host", payload)
    verify_elf(payload / "native/libpiulink-x11-input.so", arch)
    # The Debian launcher points to the package-managed program and keeps user
    # configuration, credentials and optional downloaded runtimes in user data.
    host_script = (ROOT / "apps/linux-host/piulink-host").read_text(encoding="utf-8")
    write(payload / "piulink-host", host_script, 0o755)
    write(root / "usr/bin/piulink-host", HOST_LAUNCH, 0o755)
    write(root / "usr/bin/piulink-host-setup-user", HOST_SETUP, 0o755)
    unit = payload / "piulink-host.service"
    original = unit.read_text()
    original_exec = "ExecStart=%h/.local/bin/piulink-host run"
    if original.count(original_exec) != 1:
        raise ValueError("Review the upstream systemd launcher")
    write(root / "usr/lib/systemd/user/piulink-host.service",
          original.replace(original_exec, "ExecStart=/usr/bin/piulink-host run"))
    unit.unlink()
    (payload / "install-user.sh").unlink()
    (payload / "README.md").rename(docs / "README-upstream.md")
    write(docs / "README.Debian",
          "On Ubuntu 26.04 x86-64, run piulink-host-setup-user as your desktop user.\n"
          "This downloads the pinned runtime and Sunshine. Then choose your mode with\n"
          "/usr/bin/piulink-host setup desktop (or headless) and run\n"
          "systemctl --user enable --now piulink-host.service.\n")


def build(role, arch, version, args):
    package = "piulink-" + role
    output = args.packages / f"{package}_{version}-1_{arch}.deb"
    with tempfile.TemporaryDirectory() as temp:
        root = Path(temp) / "root"
        docs = root / "usr/share/doc" / package
        docs.mkdir(parents=True)
        notice = (args.licenses / "PiuLink-Notice.txt").read_text(encoding="utf-8")
        terms = (args.licenses / "PolyForm-Noncommercial-1.0.0.txt").read_text(encoding="utf-8")
        write(docs / "copyright", notice + "\n" + terms)
        if role == "client":
            client(root, docs, arch, version, args)
            desc = "Remote desktop streaming client for PiuLink"
            deps = CLIENT_DEPS
        else:
            host(root, docs, arch, version, args)
            desc = "PiuLink streaming host with per-user setup for Ubuntu 26.04 x86-64"
            deps = HOST_DEPS
        files = sorted(p for p in root.rglob("*") if p.is_file())
        write(root / "DEBIAN/md5sums", "".join(
            f"{hashlib.md5(p.read_bytes()).hexdigest()}  {p.relative_to(root)}\n" for p in files))
        size = (sum(p.stat().st_size for p in files) + 1023) // 1024
        write(root / "DEBIAN/control",
              f"Package: {package}\nVersion: {version}-1\nArchitecture: {arch}\n"
              "Maintainer: ATIPiu Labs <51857097+ATIpiu@users.noreply.github.com>\n"
              f"Section: net\nPriority: optional\nInstalled-Size: {size}\nDepends: {deps}\n"
              "Homepage: https://github.com/ATIpiu/PiuLink-Community\n"
              + ("Recommends: fonts-wqy-microhei | fonts-noto-cjk\n" if role == "client" else "")
              + f"Description: {desc}\n")
        with tempfile.TemporaryDirectory(dir=output.parent) as staging:
            pending = Path(staging) / output.name
            subprocess.run(["dpkg-deb", "--root-owner-group", "-Zxz", "-z3", "--build", str(root), str(pending)], check=True)
            os.replace(pending, output)
    print(output.name, output.stat().st_size, flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--packages", type=Path, required=True, help="release archives and output location")
    parser.add_argument("--licenses", type=Path, required=True, help="licenses directory from the public source release")
    parser.add_argument("--client-notices", type=Path, required=True, help="notices for this release's NuGet libraries")
    args = parser.parse_args()
    for role, project, arches in [
        ("client", "apps/macos-client/src/PiuLink.Mac/PiuLink.Mac.csproj", ("amd64", "arm64")),
        ("host", "apps/linux-host/src/PiuLink.Host.Linux/PiuLink.Host.Linux.csproj", ("amd64",)),
    ]:
        version = ET.parse(ROOT / project).findtext("./PropertyGroup/Version")
        for arch in arches:
            build(role, arch, version, args)
