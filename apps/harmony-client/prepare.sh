#!/usr/bin/env bash
set -euo pipefail
port=$(cd -- "$(dirname -- "$0")" && pwd)
git apply "$port/piulink.patch" "$port/update-ui.patch"
cp -- "$port/ReleaseUpdate.ets" entry/src/main/ets/service/
cp -- "$port/UpdatePage.ets" entry/src/main/ets/pages/
