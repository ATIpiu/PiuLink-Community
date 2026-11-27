#!/usr/bin/env bash
set -euo pipefail
[[ $# == 1 ]] || { echo 'Usage: bash build-x11-input.sh <output-file>' >&2; exit 2; }
source_dir=$(cd -- "$(dirname -- "$0")" && pwd)
headers=()
if [[ -n "${PIULINK_X11_HEADERS:-}" ]]; then
    headers=(-I"$PIULINK_X11_HEADERS" -I"$PIULINK_X11_HEADERS/libevdev-1.0")
else
    read -r -a headers <<< "$(pkg-config --cflags x11 xtst libevdev)"
fi
temporary="$1.$$.tmp"
trap 'rm -f -- "$temporary"' EXIT
cc -std=gnu11 -O2 -Wall -Wextra -Werror -fPIC -shared -Wl,-z,defs \
    "${headers[@]}" "$source_dir/input/x11-input.c" \
    -l:libX11.so.6 -l:libXtst.so.6 -l:libevdev.so.2 -ldl -pthread -o "$temporary"
mv -f -- "$temporary" "$1"
