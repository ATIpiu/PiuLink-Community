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
sunshine_name = PiuLink Simulator Host
origin_web_ui_allowed = pc
upnp = disabled
encoder = software
file_state = $scratch/state/sunshine_state.json
credentials_file = $scratch/state/sunshine_state.json
EOF
"$binary" "$scratch/state/sunshine.conf" --creds simtest "$PIULINK_TEST_PASSWORD"
"$binary" "$scratch/state/sunshine.conf" > "$scratch/host.log" 2>&1 &
host_pid=$!
trap 'kill "$host_pid" 2>/dev/null || true' EXIT
for i in {1..60}; do
  if curl --noproxy '*' -fsS http://127.0.0.1:47989/serverinfo | grep -q 'PiuLink Simulator Host'; then break; fi
  if ! kill -0 "$host_pid" 2>/dev/null; then tail -30 "$scratch/host.log"; exit 1; fi
  sleep 1
done
curl --noproxy '*' -fsS http://127.0.0.1:47989/serverinfo | grep -q 'PiuLink Simulator Host'
python3 "$GITHUB_WORKSPACE/apps/ios-client/simulator-tests/pin_helper.py" &
helper_pid=$!
trap 'kill "$host_pid" "$helper_pid" 2>/dev/null || true' EXIT
cd "$test_dir"
clang -fobjc-arc -framework Foundation -framework CoreAudio -framework AudioToolbox OutputTap.m -o "$scratch/output-tap"
python3 tones.py generate "$scratch/tones.wav"
swiftc DesktopProbe.swift -o "$scratch/probe"
"$scratch/probe" "$scratch/input-received" &
probe_pid=$!
trap 'kill "$host_pid" "$helper_pid" "$probe_pid" 2>/dev/null || true' EXIT
set +e
"$@"
test_rc=$?
set -e
if [[ -f "$scratch/input-received" ]]; then echo "Desktop test window received pointer input"; else echo "Desktop input unconfirmed"; test_rc=1; fi
if ! python3 tones.py check "$scratch/output.raw" "$scratch/format.txt"; then test_rc=1; fi
grep -Ei 'display|screen recording|capture|encoder|permission|microphone|audio device|audio sink|client connected|streaming session' "$scratch/host.log" | tail -80 || true
exit "$test_rc"
