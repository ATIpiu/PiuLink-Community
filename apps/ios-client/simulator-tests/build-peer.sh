#!/bin/bash
set -euo pipefail
export HOMEBREW_NO_AUTO_UPDATE=1
brew install cmake pkgconf miniupnpc openssl@3 opus icu4c@78
src="$RUNNER_TEMP/qa-peer-src"
git init -q "$src"
git -C "$src" fetch -q --depth 1 https://github.com/LizardByte/Sunshine.git 14ffa6fdaa53f7b51512be2b3d24f3939695403c
git -C "$src" checkout -q --detach FETCH_HEAD
git -C "$src" submodule update --init --recursive --depth 1
git -C "$src" apply "$PWD/apps/ios-client/simulator-tests/peer-endpoints.patch"
cd "$src"
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release -DBUILD_TESTS=OFF -DBUILD_DOCS=OFF -DBUILD_WERROR=OFF \
  -DICU_ROOT="$(brew --prefix icu4c@78)" -DOPENSSL_ROOT_DIR="$(brew --prefix openssl@3)" -DOpus_ROOT_DIR="$(brew --prefix opus)"
cmake --build build -j "$(sysctl -n hw.ncpu)"
cpack -G DragNDrop --config build/CPackConfig.cmake
cp build/cpack_artifacts/Sunshine.dmg "$HOME/piulink-qa-peer.dmg"
