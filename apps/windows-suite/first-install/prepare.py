"""Prepare the solid-compressed distribution from a verified PiuLink WiX bundle.
The original headers are retained. WiX rebuilds the six media cabinets on first use.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import struct
import subprocess
import tempfile


def sha(data):
    return hashlib.sha256(data).hexdigest()


def parse_cab(data, start):
    size, = struct.unpack_from("<I", data, start + 8)
    files_at, = struct.unpack_from("<I", data, start + 16)
    folders, count, flags = struct.unpack_from("<HHH", data, start + 26)
    if flags or not folders or start + size > len(data):
        raise ValueError("Spanned and reserved cabinets are unsupported")
    folder_table = [struct.unpack_from("<IHH", data, start + 36 + 8*i) for i in range(folders)]
    pos = start + files_at
    files = []
    for _ in range(count):
        length, offset, folder = struct.unpack_from("<IIH", data, pos)
        end = data.index(0, pos + 16, start + size)
        name = data[pos + 16:end].decode("ascii")
        if not re.fullmatch(r"[A-Za-z0-9_.-]+", name) or name in (".", ".."):
            raise ValueError("Unexpected cabinet file identifier")
        files.append((name, folder, offset, length))
        pos = end + 1
    return size, folder_table, files


def stored_blocks(data, start, folder):
    offset, count, compression = folder
    if compression:
        raise ValueError("Expected a stored Burn container")
    pos = start + offset
    blocks = []
    for _ in range(count):
        length, raw = struct.unpack_from("<HH", data, pos + 4)
        if raw != length:
            raise ValueError("Unexpected stored block length")
        blocks.append((pos + 8, raw))
        pos += 8 + raw
    return blocks


def prepare(bundle, wixnative, out):
    data = bundle.read_bytes()
    outer = None
    for candidate in re.finditer(b"MSCF\x00\x00\x00\x00", data):
        start = candidate.start()
        if start + 36 > len(data) or data[start+24:start+26] != b"\x03\x01":
            continue
        if start + struct.unpack_from("<I", data, start + 8)[0] != len(data):
            continue
        size, folders, files = parse_cab(data, start)
        if files and all(re.fullmatch(r"a[0-9]+", item[0]) for item in files):
            if outer is not None:
                raise ValueError("Ambiguous attached cabinet")
            outer = start, folders, files
    if outer is None:
        raise ValueError("Attached Burn container missing")
    start, folders, files = outer
    out.mkdir(parents=True)
    objects = out / "files"
    objects.mkdir()
    ranges = []
    cached = set()
    cabs = []
    names = {}
    objects_by_hash = {}
    with tempfile.TemporaryDirectory() as scratch:
        temp = Path(scratch)
        for _, folder, logical, size in files:
            key = (folder, logical, size)
            if key in cached:
                continue
            cached.add(key)
            pieces = []
            cursor = 0
            for physical, length in stored_blocks(data, start, folders[folder]):
                low, high = max(cursor, logical), min(cursor + length, logical + size)
                if low < high:
                    pieces.append((physical + low - cursor, low - logical, high - low))
                cursor += length
            body = b"".join(data[at:at+n] for at, _, n in pieces)
            if len(body) != size:
                raise ValueError("Incomplete Burn payload")
            if body.startswith(bytes.fromhex("d0cf11e0a1b11ae1")):
                msi = temp / "component.msi"
                msi.write_bytes(body)
                result = subprocess.check_output([
                    "pwsh", "-NoProfile", "-File", Path(__file__).with_name("Read-MsiNames.ps1"), msi
                ], encoding="utf-8-sig")
                names.update(json.loads(result))
            if not body.startswith(b"MSCF"):
                continue
            cab = f"media{len(cabs)}"
            path = temp / (cab + ".cab")
            path.write_bytes(body)
            _, inner, entries = parse_cab(body, 0)
            (out / (cab + ".header")).write_bytes(body[:inner[0][0]])
            extracted = temp / cab
            extracted.mkdir()
            subprocess.run([wixnative, "extractcab", path, str(extracted) + "\\"],
                           input=b":\r\n", stdout=subprocess.DEVNULL, check=True)
            rows = []
            for name, _, _, _ in entries:
                src = extracted / name
                digest = sha(src.read_bytes())
                # Keep the extension for 7-Zip's executable filters and solid sort.
                filename = re.sub(r"[^A-Za-z0-9_.+-]", "_", names.get(name, "file"))[-100:]
                dest = objects / objects_by_hash.setdefault(digest, digest + "-" + filename)
                if not dest.exists():
                    shutil.copyfile(src, dest)
                rows.append(f"{dest.name}\t{name}")
            (out / (cab + ".tsv")).write_text("\n".join(rows) + "\n", encoding="ascii")
            ranges.extend((at, cab, offset, n) for at, offset, n in pieces)
            cabs.append(f"{cab}\t{sha(body)}")
    if not cabs:
        raise ValueError("No MSI cabinets in this installer")
    (out / "cabinets.tsv").write_text("\n".join(cabs) + "\n", encoding="ascii")
    cursor = 0
    lines = []
    with (out / "envelope").open("wb") as envelope:
        for pos, cab, offset, length in sorted(ranges):
            if pos < cursor:
                raise ValueError("Overlapping cabinet data")
            if pos > cursor:
                lines.append(f"envelope\t{envelope.tell()}\t{pos-cursor}")
                envelope.write(data[cursor:pos])
            lines.append(f"{cab}.cab\t{offset}\t{length}")
            cursor = pos + length
        if cursor < len(data):
            lines.append(f"envelope\t{envelope.tell()}\t{len(data)-cursor}")
            envelope.write(data[cursor:])
    (out / "layout.tsv").write_text("\n".join(lines) + "\n", encoding="ascii")
    (out / "sha256.txt").write_text(sha(data), encoding="ascii")


if __name__ == "__main__":
    p = argparse.ArgumentParser(description=__doc__)
    for arg in ("bundle", "wixnative", "out"):
        p.add_argument(arg, type=Path)
    a = p.parse_args()
    prepare(a.bundle.resolve(), a.wixnative.resolve(), a.out.resolve())
