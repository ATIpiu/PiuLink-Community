#!/usr/bin/env bash
set -euo pipefail
umask 077
if [[ $(id -u) == 0 || $(uname -m) != x86_64 ]]; then
    echo 'Run this Ubuntu 26.04 x86-64 bootstrap as the desktop user.' >&2
    exit 1
fi
. /etc/os-release
if [[ "$ID" != ubuntu || "$VERSION_ID" != 26.04 ]]; then
    echo 'Use the distro dependencies listed in README.md on this system.' >&2
    exit 1
fi

source_dir=$(cd -- "$(dirname -- "$0")" && pwd)
base=${XDG_DATA_HOME:-$HOME/.local/share}/piulink-linux
if systemctl --user is-active --quiet piulink-host.service; then
    echo 'Stop piulink-host.service before replacing its runtime.' >&2
    exit 1
fi
install -d -m 0700 "$base" "$base/downloads" "$base/runtime"
cd "$base/downloads"

download() {
    local name=$1 url=$2 checksum=$3 sum_command=$4
    if printf '%s  %s\n' "$checksum" "$name" | "$sum_command" --status -c 2>/dev/null; then return; fi
    curl -fL --retry 2 --connect-timeout 12 --max-time 300 --continue-at - --output "$name.partial" "$url"
    printf '%s  %s\n' "$checksum" "$name.partial" | "$sum_command" -c
    mv -f "$name.partial" "$name"
}

download sunshine.AppImage \
    https://github.com/LizardByte/Sunshine/releases/download/v2026.516.143833/sunshine.AppImage \
    d0ee0a9cfb66f27869b559455f84622d21615047ccf3443c9a2f572ca971c7a2 sha256sum
download dotnet-runtime.tar.gz \
    https://dotnetcli.blob.core.windows.net/dotnet/Runtime/8.0.28/dotnet-runtime-8.0.28-linux-x64.tar.gz \
    de3d06fbd5bd3ffe413e798867bbef24e218b863c7a224ca7f63c84e0b282c588fa0579a913d36e276e93516b24f3d32edf85ebb6d61152dbfdb8b81e8dc1cf1 sha512sum

mkdir -p "$base/runtime/dotnet" "$base/runtime/desktop"
chmod 700 sunshine.AppImage
tar -xzf dotnet-runtime.tar.gz -C "$base/runtime/dotnet"
(cd "$base/runtime"; ../downloads/sunshine.AppImage --appimage-extract > extract.log)

mkdir -p ubuntu
cd ubuntu
apt-get download xvfb xterm xfonts-base xfonts-utils libutempter0 pulseaudio-utils \
    openbox libobrender32 libobt2 libstartup-notification0 libimlib2t64 \
    tmux libjemalloc2 libevent-core-2.1-7t64
for package in ./*.deb; do
    dpkg-deb --extract "$package" "$base/runtime/desktop"
done
if [[ -r "$source_dir/native/libpiulink-x11-input.so" ]]; then
    install -m 0700 "$source_dir/native/libpiulink-x11-input.so" "$base/runtime/"
else
    mkdir -p "$base/runtime/headers" "$base/downloads/build"
    cd "$base/downloads/build"
    apt-get download libx11-dev libxtst-dev x11proto-dev libxi-dev libxext-dev libevdev-dev
    for package in ./*.deb; do
        dpkg-deb --extract "$package" "$base/runtime/headers"
    done
    PIULINK_X11_HEADERS="$base/runtime/headers/usr/include" \
        bash "$source_dir/build-x11-input.sh" "$base/runtime/libpiulink-x11-input.so"
fi
"$base/runtime/dotnet/dotnet" --list-runtimes
echo "PiuLink private runtime installed in $base/runtime."
