#!/bin/bash
# Run a disposable GameStream peer on the same macOS runner as the iPhone.
set -euo pipefail
test_dir="$PWD"
scratch="$RUNNER_TEMP/sunshine-test"
mkdir -p "$scratch/volume" "$scratch/state"
cd "$scratch"
curl -fsSL --retry 3 https://github.com/SunshineStream/Sunshine/releases/download/v2026.516.143833/Sunshine-macOS-arm64.dmg -o sunshine.dmg
echo "ab31ad716117b913c6aab104268e820595c0baf89b319fd3b75d34c9ae8ddd1e  sunshine.dmg" | shasum -a 256 -c -
hdiutil attach -nobrowse -readonly -mountpoint "$scratch/volume" sunshine.dmg <<< Y
binary=$(find "$scratch/volume" -path '*/Contents/MacOS/*' -type f -iname sunshine | head -1)
test -x "$binary"
export PIULINK_TEST_PASSWORD=$(openssl rand -hex 20)
cat > "$scratch/state/sunshine.conf" <<EOF
min_log_level = 1
sunshine_name = PiuLink Simulator Host
origin_web_ui_allowed = pc
upnp = disabled
encoder = software
audio_sink = BlackHole 16ch
file_state = $scratch/state/sunshine_state.json
credentials_file = $scratch/state/sunshine_state.json
EOF
"$binary" "$scratch/state/sunshine.conf" --creds simtest "$PIULINK_TEST_PASSWORD"
"$binary" "$scratch/state/sunshine.conf" > "$scratch/host.log" 2>&1 &
host_pid=$!
# Keep the source device clock active across AVFoundation capture startup.
sox --buffer 65536 -q -t coreaudio 'BlackHole 16ch' -n >"$scratch/source-clock.log" 2>&1 &
source_clock=$!
trap 'kill "$source_clock" "$host_pid" 2>/dev/null || true' EXIT
for i in {1..60}; do
  if curl --noproxy '*' -fsS http://127.0.0.1:47989/serverinfo | grep -q 'PiuLink Simulator Host'; then break; fi
  if ! kill -0 "$host_pid" 2>/dev/null; then tail -30 "$scratch/host.log"; exit 1; fi
  sleep 1
done
curl --noproxy '*' -fsS http://127.0.0.1:47989/serverinfo | grep -q 'PiuLink Simulator Host'
python3 "$GITHUB_WORKSPACE/apps/ios-client/simulator-tests/pin_helper.py" &
helper_pid=$!
trap 'kill "$source_clock" "$host_pid" "$helper_pid" 2>/dev/null || true' EXIT
cd "$test_dir"
curl -fsSL --retry 3 https://github.com/cameroncooke/AXe/releases/download/v1.0.0/AXe-macOS-v1.0.0.tar.gz -o "$scratch/axe.tgz"
echo "5d32037aaef3bd8c57b454cf43b40b470c209d5dd2a39ab78eeb7b9c438817f9  $scratch/axe.tgz" | shasum -a 256 -c -
mkdir -p "$scratch/axe"
tar -xzf "$scratch/axe.tgz" -C "$scratch/axe"
ln -s "$(find "$scratch/axe" -type f -name axe | head -1)" "$scratch/axe-bin"
test -x "$scratch/axe-bin"
pa=$(brew --prefix portaudio)
clang -O2 -I"$pa/include" -L"$pa/lib" play-tones.c -lportaudio -o "$scratch/play-tones"
perl -e 'alarm 20; exec @ARGV or die' sox --buffer 65536 -q -t coreaudio 'BlackHole 2ch' -c 1 -r 48000 -b 32 -e floating-point -t raw "$scratch/negative.raw" trim 0 6 &
negative_pid=$!
negative_ok=1
sleep 1
perl -e 'alarm 20; exec @ARGV or die' "$scratch/play-tones" || negative_ok=0
wait "$negative_pid" || negative_ok=0
printf '48000 1\n' > "$scratch/negative.format"
python3 tones.py quiet "$scratch/negative.raw" "$scratch/negative.format" || negative_ok=0
swiftc DesktopProbe.swift -o "$scratch/probe"
"$scratch/probe" "$scratch/input-received" &
probe_pid=$!
trap 'kill "$source_clock" "$host_pid" "$helper_pid" "$probe_pid" 2>/dev/null || true' EXIT
set +e
"$@"
test_rc=$?
[[ "$negative_ok" == 1 ]] || test_rc=1
set -e
if [[ -f "$scratch/input-received" ]]; then echo "Desktop test window received pointer input"; else echo "Desktop input unconfirmed"; test_rc=1; fi
for i in {1..40}; do
  [[ -f "$scratch/output-finished" ]] && break
  sleep .25
done
if [[ -f "$scratch/key-received" ]]; then echo "Desktop test window received PiuLink test key"; else echo "Desktop key unconfirmed"; test_rc=1; fi
cat "$scratch/sample.log" || true
data=$(xcrun simctl get_app_container booted labs.atipiu.piulink.ios data)
python3 tones.py pcm "$data/Library/Caches/qa-played.s16" "$data/Library/Caches/qa-played.format" || test_rc=1
echo "Measuring simulator output bus"
if ! python3 tones.py check "$scratch/output.raw" "$scratch/format.txt"; then test_rc=1; fi
# Limit simulator diagnostics to sound events without any launch request parameters.
xcrun simctl spawn booted log show --style compact --last 4m --predicate 'process == "Moonlight" AND (eventMessage CONTAINS[c] "audio" OR eventMessage CONTAINS[c] "Opus")' | grep -Eiv 'https?://|rikey|token|secret|credential' | tail -40 || true
grep -Ei 'display|screen recording|capture|encoder|permission|microphone|audio sample|audio device|audio sink|client connected|streaming session' "$scratch/host.log" | tail -80 || true
# Summarize channel destination ports only. Omit the session ping payload.
python3 - "$scratch/host.log" <<'PY'
from pathlib import Path
import re
import sys
from collections import defaultdict
destinations = defaultdict(set)
for line in Path(sys.argv[1]).read_text(errors="replace").splitlines():
    match = re.search(r"RAISE: .*:(\d+) :: (AUDIO|VIDEO)", line)
    if match:
        destinations[match[2]].add(int(match[1]))
print("Host UDP channel endpoint ports", dict(destinations))
PY
exit "$test_rc"
