#!/usr/bin/env bash
set -euo pipefail
port=$(cd -- "$(dirname -- "$0")" && pwd)
git apply "$port/piulink.patch" "$port/update-ui.patch" "$port/audio-playout.patch"
cp -- "$port/ReleaseUpdate.ets" entry/src/main/ets/service/
cp -- "$port/UpdatePage.ets" entry/src/main/ets/pages/

"${PYTHON:-python3}" "$port/../../packages/client-security/prepare.py" harmony "$PWD"
core="$port/../../engines/rust/integration"
native=nativelib/src/main/cpp
"${PYTHON:-python3}" "$core/portable/client.py" harmony "$native/moonlight-common-c/src"
mkdir -p "$native/rust/portable" "$native/rust/wire"
cp "$core/portable/"{Cargo.toml,Cargo.lock,lib.rs} "$native/rust/portable/"
cp "$core/wire/"*.rs "$native/rust/wire/"
cp "$core/portable/piulink_audio_playout.h" "$native/"
cp "$port/rust-core.cmake" "$native/"
printf '\n' >> "$native/CMakeLists.txt"
echo 'include("${CMAKE_CURRENT_LIST_DIR}/rust-core.cmake")' >> "$native/CMakeLists.txt"
