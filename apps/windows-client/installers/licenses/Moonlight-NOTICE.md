# Moonlight PC notice

PiuLink Windows Client ships a modified Moonlight Qt executable under `moonlight-qt` and uses it
for pairing, application discovery, streaming and disconnect. The package also includes the native
streaming library built with moonlight-common-c, FFmpeg, SDL and Opus. Moonlight is an independent
project. The PiuLink Qt patch set and build scripts are in `apps/windows-client/engine`.

- Project: https://github.com/moonlight-stream/moonlight-qt
- Pinned release: v6.1.0
- License: GNU General Public License version 3
- Corresponding release source: https://github.com/moonlight-stream/moonlight-qt/releases/download/v6.1.0/MoonlightSrc-6.1.0.tar.gz

The native library is built reproducibly from the source checkout pinned by the repository build.
The applicable upstream license text is included beside this notice.
