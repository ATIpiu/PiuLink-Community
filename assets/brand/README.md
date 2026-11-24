# PiuLink icon system

The selected PiuLink mark is a monochrome pair of rounded portals joined by one direct bridge. It represents a host and client becoming one continuous remote workspace.

## Canonical assets

- `piulink-mark.svg` — transparent black master mark.
- `piulink-app-icon-light.svg` — black mark on a white, unmasked square canvas.
- `tools/brand/generate_icons.py` — deterministic raster and ICO exporter.

Only pure black (`#000000`) and white (`#FFFFFF`) are brand colors for this icon direction. Platform masks create final Android and Store silhouettes; do not bake a rounded mask into Google Play artwork.

The Google Play export is a 512×512 32-bit PNG as required by the current listing specification:
https://support.google.com/googleplay/android-developer/answer/9866151

Run the exporter from the repository root with the Python with Pillow installed:

```powershell
python .\tools\brand\generate_icons.py
```
