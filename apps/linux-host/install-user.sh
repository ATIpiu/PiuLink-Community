#!/usr/bin/env bash
set -euo pipefail
umask 077
[[ $# == 1 && -f "$1/PiuLink.Host.Linux.dll" ]] || { echo 'Usage: bash install-user.sh <published-app-directory>' >&2; exit 2; }
if [[ $(id -u) == 0 ]]; then echo 'Install as the desktop user.' >&2; exit 1; fi
source=$(cd -- "$(dirname -- "$0")" && pwd)
base=${XDG_DATA_HOME:-$HOME/.local/share}/piulink-linux
units="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
if systemctl --user is-active --quiet piulink-host.service; then
    echo 'Stop piulink-host.service before replacing its binaries.' >&2
    exit 1
fi
install -d -m 0700 "$base" "$base/app"
mkdir -p "$HOME/.local/bin" "$units"
install -m 0600 "$1"/PiuLink.Host.Linux.dll "$1"/PiuLink.Host.Linux.deps.json "$1"/PiuLink.Host.Linux.runtimeconfig.json "$base/app/"
if [[ -r "$source/native/libpiulink-x11-input.so" ]]; then
    install -d -m 0700 "$base/runtime"
    install -m 0700 "$source/native/libpiulink-x11-input.so" "$base/runtime/"
fi
install -m 0700 "$source/piulink-host" "$HOME/.local/bin/piulink-host"
install -m 0644 "$source/piulink-host.service" "$units/piulink-host.service"
systemctl --user daemon-reload
echo 'PiuLink Host installed. Run piulink-host setup desktop|headless, then start piulink-host.service.'
