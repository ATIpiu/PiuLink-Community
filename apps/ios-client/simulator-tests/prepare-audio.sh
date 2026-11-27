#!/bin/bash
# Run on the disposable GitHub macOS runner before booting the iOS simulator.
set -euo pipefail
export HOMEBREW_NO_AUTO_UPDATE=1
brew install sox switchaudio-osx portaudio
brew install --cask blackhole-2ch blackhole-16ch
sudo killall coreaudiod || true
for i in {1..30}; do
  if SwitchAudioSource -a -t output | grep -Fxq 'BlackHole 16ch' && SwitchAudioSource -a -t output | grep -Fxq 'BlackHole 2ch'; then break; fi
  sleep 1
done
SwitchAudioSource -t output -s 'BlackHole 2ch'
test "$(SwitchAudioSource -c -t output)" = 'BlackHole 2ch'
