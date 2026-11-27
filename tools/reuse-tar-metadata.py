"""Preserve timestamps of unchanged release files so their compressed bytes can be reused."""
import argparse
import gzip
import hashlib
import os
import tarfile
import tempfile
from pathlib import Path


def main(previous, target, level):
    with tempfile.NamedTemporaryFile(dir=target.parent, delete=False) as stage:
        pending = Path(stage.name)
    try:
        # Preserve the target file names, contents and permissions from the new build.
        with tarfile.open(previous) as old, tarfile.open(target) as new, pending.open("wb") as stream:
            with gzip.GzipFile(filename=target.name, mode="wb", compresslevel=level, fileobj=stream) as zipped:
                with tarfile.open(fileobj=zipped, mode="w|") as archive:
                    for item in new:
                        if not item.isfile():
                            archive.addfile(item)
                            continue
                        try:
                            prior = old.getmember(item.name)
                        except KeyError:
                            prior = None
                        # Spool each member once. Seeking backwards in a gzip tar would
                        # repeatedly decompress the prefix of a large release archive.
                        with new.extractfile(item) as data, tempfile.SpooledTemporaryFile(max_size=8 << 20) as body:
                            digest = hashlib.sha256()
                            while block := data.read(256 << 10):
                                digest.update(block)
                                body.write(block)
                            if prior and prior.isfile() and prior.size == item.size:
                                with old.extractfile(prior) as before:
                                    same = hashlib.file_digest(before, "sha256").digest() == digest.digest()
                                if same:
                                    item.mtime = prior.mtime
                                    if "mtime" in prior.pax_headers:
                                        item.pax_headers["mtime"] = prior.pax_headers["mtime"]
                                    else:
                                        item.pax_headers.pop("mtime", None)
                            body.seek(0)
                            archive.addfile(item, body)
        os.replace(pending, target)
    finally:
        pending.unlink(missing_ok=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("previous", type=Path)
    parser.add_argument("target", type=Path)
    parser.add_argument("--level", type=int, choices=range(1, 10), required=True)
    args = parser.parse_args()
    main(args.previous, args.target, args.level)

