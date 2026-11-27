#!/bin/bash
set -euo pipefail
export HOMEBREW_NO_AUTO_UPDATE=1
brew install cmake pkgconf miniupnpc openssl@3 opus icu4c@78
# Build and link against the same OpenSSL ABI on the disposable runner.
if [[ -L "$(brew --prefix)/include/openssl" ]]; then
  linked="$(readlink "$(brew --prefix)/include/openssl")"
  case "$linked" in *openssl@4*) brew unlink openssl@4 ;; esac
fi
ssl="$(brew --prefix openssl@3)"
export CPPFLAGS="-I$ssl/include"
export CFLAGS="-I$ssl/include"
export CXXFLAGS="-I$ssl/include"
src="$RUNNER_TEMP/qa-peer-src"
git init -q "$src"
git -C "$src" fetch -q --depth 1 https://github.com/LizardByte/Sunshine.git 14ffa6fdaa53f7b51512be2b3d24f3939695403c
git -C "$src" checkout -q --detach FETCH_HEAD
git -C "$src" submodule update --init --recursive --depth 1
git -C "$src" apply "$PWD/apps/ios-client/simulator-tests/peer-endpoints.patch"
cd "$src"
configure() {
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release -DBUILD_TESTS=OFF -DBUILD_DOCS=OFF -DBUILD_WERROR=OFF \
  -DICU_ROOT="$(brew --prefix icu4c@78)" -DOPENSSL_ROOT_DIR="$(brew --prefix openssl@3)" -DOpus_ROOT_DIR="$(brew --prefix opus)"
}
if ! configure; then
  # CMake checks this IDE source group more strictly than Boost.Locale's original build.
  python3 - <<'PY'
from pathlib import Path
import re
p = Path("build/_deps/boost-src/libs/locale/CMakeLists.txt")
s = p.read_text()
s, count = re.subn(r'(source_group\(TREE\s+[^)\s]*?include/boost)/locale(?=["\s])', r'\1', s, count=1, flags=re.I)
if count != 1:
    raise SystemExit("Could not identify Boost.Locale source-group root")
p.write_text(s)
PY
  configure
fi
cmake --build build -j "$(sysctl -n hw.ncpu)"
cpack -G DragNDrop --config build/CPackConfig.cmake
cp build/cpack_artifacts/Sunshine.dmg "$HOME/piulink-qa-peer.dmg"
