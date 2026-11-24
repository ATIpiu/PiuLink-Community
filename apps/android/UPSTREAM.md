# Android upstream provenance

Inventory captured 2026-08-17. The prototype was reviewed read-only and was not changed.

| Field | Value |
| --- | --- |
| Prototype committed HEAD | `4fffc7e` — Fix first software keyboard input session |
| Fork remote | `https://github.com/ATIpiu/moonlight-android-full-keyboard-skylark.git` |
| Origin remote | `https://github.com/KaleidSkylark/moonlight-android-full-keyboard-skylark.git` |
| License observed | GNU GPL v3 (`LICENSE.txt`) |
| Prototype working tree | Existing tracked modifications and untracked embedded-Tailscale experiment; preserved unchanged |
| PiuLink GPL branch | `piulink/v0.1` in the separate `piulink-android` repository |
| PiuLink release source | `e86fcc9`, tag `v0.1.0-alpha.2` |

The PiuLink repository was created with Git history preserved from the committed prototype state;
native submodules are pinned. The uncommitted `tailscale-vpn` experiment was intentionally not
imported. Public corresponding source must match every distributed PiuLink Android binary,
including submodules, build scripts and all PiuLink patches.
