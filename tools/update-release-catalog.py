"""Index supported installers from a GitHub release after verifying their GitHub digests."""
import gzip
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
REPO = "ATIpiu/PiuLink-Community"
VERSION = r"(?P<v>\d+\.\d+\.\d+)"
FORMATS = {
    "win-suite": rf"PiuLink-Windows-{VERSION}-win-x64\.exe",
    "win-host": rf"PiuLink-Host-{VERSION}-win-x64-preview\.msi",
    "win-client": rf"PiuLink-Client-{VERSION}-win-x64\.msi",
    "android-client": rf"PiuLink-Android-{VERSION}-nonRoot-universal-release\.apk",
    "android-arm64": rf"PiuLink-Android-{VERSION}-nonRoot-arm64-v8a-release\.apk",
    "harmony-client": rf"PiuLink-HarmonyOS-{VERSION}-unsigned\.hap",
    "ios-client": rf"PiuLink-iOS-{VERSION}-arm64-unsigned\.ipa",
    "mac-universal": rf"PiuLink-Mac-{VERSION}-universal-unsigned\.zip",
    "linux-universal": rf"PiuLink-Client-{VERSION}-linux-universal\.tar\.gz",
    "linux-host": rf"piulink-host_{VERSION}-1_amd64\.deb",
    "linux-host-portable": rf"PiuLink-Host-{VERSION}-ubuntu26\.04-x64\.tar\.gz",
}
for arch, deb in [("arm64", "arm64"), ("x64", "amd64")]:
    FORMATS["mac-" + arch] = rf"PiuLink-Mac-{VERSION}-osx-{arch}-unsigned\.zip"
    FORMATS["linux-" + arch] = rf"piulink-client_{VERSION}-1_{deb}\.deb"
    FORMATS["linux-" + arch + "-portable"] = rf"PiuLink-Client-{VERSION}-linux-{arch}\.tar\.gz"


def harmony_compatible_map(record, maps):
    # 0.1.3/0.1.4 read the entire 4 MiB inflate buffer on some HarmonyOS
    # versions. JSON whitespace keeps those installed clients upgradeable.
    blocks = gzip.decompress((maps / (record["mapSha256"] + ".gz")).read_bytes())
    if len(blocks) > 4 << 20:
        raise ValueError("HarmonyOS block map exceeds the client limit")
    packed = gzip.compress(blocks.ljust(4 << 20, b" "), compresslevel=6, mtime=0)
    digest = hashlib.sha256(packed).hexdigest()
    (maps / (digest + ".gz")).write_bytes(packed)
    return {**record, "mapSha256": digest}


def main(tag):
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,150}", tag):
        raise ValueError("Invalid release tag")
    dist = ROOT / "updates"
    catalog = dist / "channels.json"
    channels = json.loads(catalog.read_text("utf-8"))
    release = json.loads(subprocess.check_output(["gh", "release", "view", tag, "-R", REPO, "--json", "assets,isDraft"]))
    if release["isDraft"]:
        raise ValueError("Publish the release before indexing")
    with tempfile.TemporaryDirectory() as folder:
        temp = Path(folder)
        builder = temp / ("mkmap.exe" if sys.platform == "win32" else "mkmap")
        subprocess.run(["go", "build", "-o", builder, "./cmd/piulink-update-map"], cwd=ROOT/"packages/direct-tunnel", check=True)
        for asset in release["assets"]:
            name = asset["name"]
            for channel, pattern in FORMATS.items():
                match = re.fullmatch(pattern, name)
                if not match:
                    continue
                version = match["v"]
                old = channels.get(channel)
                if old and tuple(map(int, old["version"].split("."))) >= tuple(map(int, version.split("."))):
                    break
                if not 0 < asset["size"] <= 1 << 30:
                    raise ValueError("Unexpected asset size")
                subprocess.run(["gh", "release", "download", tag, "-R", REPO, "--pattern", name, "--dir", temp], check=True)
                file = temp / name
                with file.open("rb") as stream:
                    digest = hashlib.file_digest(stream, "sha256").hexdigest()
                if "sha256:" + digest != asset["digest"] or file.stat().st_size != asset["size"]:
                    raise ValueError(f"GitHub digest mismatch for {name}")
                record = json.loads(subprocess.check_output([builder, file, dist/"maps", version, tag]))
                channels[channel] = harmony_compatible_map(record, dist/"maps") if channel == "harmony-client" else record
                break
    # Keep previous maps available to downloads that are already in progress.
    new = catalog.with_suffix(".tmp")
    new.write_text(json.dumps(channels, indent=2) + "\n", "utf-8")
    new.replace(catalog)


if __name__ == "__main__":
    main(sys.argv[1])
