from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[2]
ANDROID = ROOT / "piulink-android"
BLACK = (0, 0, 0, 255)
WHITE = (255, 255, 255, 255)
TRANSPARENT = (0, 0, 0, 0)
MASTER_SIZE = 1024
SUPERSAMPLE = 4

LEFT_PORTAL = [
    (448, 350),
    (304, 266),
    (244, 231),
    (208, 246),
    (186, 292),
    (186, 342),
    (186, 682),
    (208, 748),
    (244, 778),
    (304, 758),
    (448, 674),
    (448, 350),
]
RIGHT_PORTAL = [(MASTER_SIZE - x, y) for x, y in LEFT_PORTAL]


def _transform(points: list[tuple[int, int]], scale: float) -> list[tuple[float, float]]:
    return [
        ((x - MASTER_SIZE / 2) * scale + MASTER_SIZE / 2,
         (y - MASTER_SIZE / 2) * scale + MASTER_SIZE / 2)
        for x, y in points
    ]


def _draw_mark(draw: ImageDraw.ImageDraw, scale: float) -> None:
    line_width = round(88 * scale * SUPERSAMPLE)
    for portal in (LEFT_PORTAL, RIGHT_PORTAL):
        points = [
            (round(x * SUPERSAMPLE), round(y * SUPERSAMPLE))
            for x, y in _transform(portal, scale)
        ]
        draw.line(points, fill=BLACK, width=line_width, joint="curve")

    left = (448 - MASTER_SIZE / 2) * scale + MASTER_SIZE / 2
    right = (576 - MASTER_SIZE / 2) * scale + MASTER_SIZE / 2
    top = (486 - MASTER_SIZE / 2) * scale + MASTER_SIZE / 2
    bottom = (538 - MASTER_SIZE / 2) * scale + MASTER_SIZE / 2
    draw.rectangle(
        tuple(round(value * SUPERSAMPLE) for value in (left, top, right, bottom)),
        fill=BLACK,
    )


def render_master(*, plate: str, mark_scale: float) -> Image.Image:
    canvas = Image.new(
        "RGBA",
        (MASTER_SIZE * SUPERSAMPLE, MASTER_SIZE * SUPERSAMPLE),
        TRANSPARENT if plate in {"transparent", "rounded"} else WHITE,
    )
    draw = ImageDraw.Draw(canvas)
    if plate == "rounded":
        inset = 12 * SUPERSAMPLE
        draw.rounded_rectangle(
            (inset, inset, MASTER_SIZE * SUPERSAMPLE - inset, MASTER_SIZE * SUPERSAMPLE - inset),
            radius=220 * SUPERSAMPLE,
            fill=WHITE,
        )
    _draw_mark(draw, mark_scale)
    return canvas.resize((MASTER_SIZE, MASTER_SIZE), Image.Resampling.LANCZOS)


def save_png(image: Image.Image, path: Path, size: tuple[int, int]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    resized = image.resize(size, Image.Resampling.LANCZOS)
    resized.save(path, format="PNG", optimize=True)


def main() -> None:
    store_icon = render_master(plate="square", mark_scale=0.82)
    legacy_icon = render_master(plate="rounded", mark_scale=0.82)
    foreground = render_master(plate="transparent", mark_scale=0.82)

    brand_google = ROOT / "assets" / "brand" / "exports" / "google-play"
    brand_windows = ROOT / "assets" / "brand" / "exports" / "windows"
    save_png(store_icon, brand_google / "piulink-google-play-512.png", (512, 512))

    save_png(store_icon, ANDROID / "app" / "src" / "main" / "ic_launcher-web.png", (512, 512))
    save_png(store_icon, ANDROID / "fastlane" / "metadata" / "android" / "en-US" / "images" / "icon.png", (512, 512))
    save_png(store_icon, ANDROID / "app" / "src" / "main" / "res" / "drawable" / "app_icon.png", (96, 96))

    density_sizes = {
        "mdpi": 48,
        "hdpi": 72,
        "xhdpi": 96,
        "xxhdpi": 144,
        "xxxhdpi": 192,
    }
    foreground_sizes = {
        "mdpi": 108,
        "hdpi": 162,
        "xhdpi": 216,
        "xxhdpi": 324,
        "xxxhdpi": 432,
    }
    for density, size in density_sizes.items():
        directory = ANDROID / "app" / "src" / "main" / "res" / f"mipmap-{density}"
        save_png(legacy_icon, directory / "ic_launcher.png", (size, size))
        foreground_size = foreground_sizes[density]
        save_png(
            foreground,
            directory / "ic_launcher_foreground.png",
            (foreground_size, foreground_size),
        )

    windows_source = ROOT / "apps" / "windows-host" / "src" / "PiuLink.Host" / "Assets"
    windows_store = ROOT / "apps" / "windows-host" / "store-assets"
    save_png(legacy_icon, windows_source / "PiuLink-256.png", (256, 256))
    save_png(store_icon, windows_store / "StoreListing-300.png", (300, 300))
    save_png(store_icon, brand_windows / "piulink-microsoft-store-300.png", (300, 300))

    app_list_sizes = (16, 20, 24, 30)
    for size in app_list_sizes:
        save_png(legacy_icon, windows_store / f"AppList.targetsize-{size}.png", (size, size))

    scales = {100: 1.0, 125: 1.25, 150: 1.5, 200: 2.0, 400: 4.0}
    for scale_name, multiplier in scales.items():
        store_logo_size = round(50 * multiplier)
        square_44_size = round(44 * multiplier)
        square_150_size = round(150 * multiplier)
        save_png(
            legacy_icon,
            windows_store / f"StoreLogo.scale-{scale_name}.png",
            (store_logo_size, store_logo_size),
        )
        save_png(
            legacy_icon,
            windows_store / f"Square44x44Logo.scale-{scale_name}.png",
            (square_44_size, square_44_size),
        )
        save_png(
            store_icon,
            windows_store / f"Square150x150Logo.scale-{scale_name}.png",
            (square_150_size, square_150_size),
        )

    ico_source = legacy_icon.resize((256, 256), Image.Resampling.LANCZOS)
    ico_path = windows_source / "PiuLink.ico"
    ico_source.save(
        ico_path,
        format="ICO",
        sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
    )
    ico_source.save(
        brand_windows / "piulink-host.ico",
        format="ICO",
        sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
    )


if __name__ == "__main__":
    main()
