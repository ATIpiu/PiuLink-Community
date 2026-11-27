#!/bin/bash
set -euo pipefail
scratch=$1
pid=$(xcrun simctl spawn booted launchctl list | awk '/labs[.]atipiu[.]piulink[.]ios/ && $1 ~ /^[0-9]+$/ {print $1;exit}')
"$scratch/output-tap" "$pid" "$scratch/output.raw" > "$scratch/format.txt" &
monitor=$!
xcrun simctl io booted screenshot "$scratch/phone.png"
sleep 2
afplay "$scratch/tones.wav"
wait "$monitor"
