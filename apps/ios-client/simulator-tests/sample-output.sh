#!/bin/bash
# The source player and the Simulator render to separate CoreAudio devices.
set -euo pipefail
scratch=$1
udid=$(xcrun simctl list devices booted -j | python3 -c 'import sys,json; print(next(d["udid"] for ds in json.load(sys.stdin)["devices"].values() for d in ds if d["state"]=="Booted"))')
xcrun simctl io "$udid" screenshot "$scratch/phone.png"
printf '48000 1\n' > "$scratch/format.txt"
sox -q -t coreaudio 'BlackHole 2ch' -c 1 -r 48000 -b 32 -e floating-point -t raw "$scratch/output.raw" trim 0 9 &
monitor=$!
trap 'kill "$monitor" 2>/dev/null || true' EXIT
sleep 1.5
sox -q "$scratch/tones.wav" -t coreaudio 'BlackHole 16ch'
sleep 1
"$scratch/axe-bin" key 19 --udid "$udid"
wait "$monitor"
wc -c "$scratch/output.raw"
touch "$scratch/output-finished"
