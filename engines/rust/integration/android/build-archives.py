"""Build the four Rust archives referenced by the checked-in Android.mk."""
import sys
from pathlib import Path

from wire import build_archives

if __name__ == "__main__":
    build_archives(Path(sys.argv[1]).resolve())
