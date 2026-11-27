#!/bin/bash
set -euo pipefail
scratch=$1
pid=$(xcrun simctl spawn booted launchctl list | awk '/labs[.]atipiu[.]piulink[.]ios/ && $1 ~ /^[0-9]+$/ {print $1;exit}')
"$scratch/output-tap" "$pid" "$scratch/output.raw" > "$scratch/format.txt" &
monitor=$!
xcrun simctl io booted screenshot "$scratch/phone.png"
for i in {1..20}; do
  [[ -s "$scratch/format.txt" ]] && break
  kill -0 "$monitor" 2>/dev/null || break
  sleep .2
done
echo "Starting test tones"
sleep .5
afplay "$scratch/tones.wav"
wait "$monitor"
wc -c "$scratch/output.raw"
touch "$scratch/output-finished"
