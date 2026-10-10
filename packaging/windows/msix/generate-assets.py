"""Regenerates the MSIX visual assets in ./Assets from the app's master icon.

Run from anywhere: python3 packaging/windows/msix/generate-assets.py
The outputs are committed, so CI never needs Pillow. Re-run only when the icon artwork changes.
"""
from pathlib import Path

from PIL import Image, ImageChops

HERE = Path(__file__).resolve().parent
MASTER = HERE.parents[2] / "src" / "Capture.App" / "Assets" / "Brand" / "capture-icon-master.png"
OUT = HERE / "Assets"

# (asset name, base width, base height, full bleed)
# Full-bleed tiles are filled edge to edge with the icon's own teal background. Windows draws logos on the
# accent colour wherever they're transparent, and the App Installer dialog and Store listing have no
# unplated form, so a transparent margin or rounded corner shows up as a coloured box.
TILES = [
    ("Square44x44Logo", 44, 44, False),
    ("Square71x71Logo", 71, 71, True),
    ("Square150x150Logo", 150, 150, True),
    ("Wide310x150Logo", 310, 150, True),
    ("StoreLogo", 50, 50, True),
]
SCALES = [100, 125, 150, 200, 400]
# The app-list/taskbar icon also comes in exact pixel sizes. The "unplated" forms tell Windows to draw
# the icon as-is; without them it fills the transparent corners with the accent colour, so the rounded
# icon sits in a coloured square (in the taskbar and Start).
TARGET_SIZES = [16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256]
TARGET_FORMS = ["", "_altform-unplated", "_altform-lightunplated"]
# Windows only picks between these qualified files through the package's resources.pri index, which
# build-msix.ps1 generates with makepri. The manifest still references the plain names (Assets\StoreLogo.png).

# How much of the icon's edge (rounded rim and drop shadow) a full-bleed tile trims, and how far in from
# the trimmed edge it fades into the background gradient, as fractions of the icon's width.
BLEED_INSET = 0.06
BLEED_FEATHER = 0.1


def background_rows(icon):
    """The icon's background colour for each row, sampled from its plain left and right margins."""
    width, height = icon.size
    pixels = icon.load()
    bands = ((0.03, 0.11), (0.89, 0.97), (0.45, 0.55))
    columns = [x for lo, hi in bands for x in range(int(width * lo), int(width * hi))]
    rows = []
    for y in range(height):
        samples = [pixels[x, y][:3] for x in columns if pixels[x, y][3] > 240 and sum(pixels[x, y][:3]) < 400]
        rows.append(tuple(sorted(c[i] for c in samples)[len(samples) // 2] for i in range(3)) if samples else None)
    known = [i for i, row in enumerate(rows) if row]
    return [rows[i] or rows[min(known, key=lambda k: abs(k - i))] for i in range(height)]


def bleed_icon(icon):
    """The icon with its rim trimmed and its outer edge faded to transparent."""
    inset = round(icon.width * BLEED_INSET)
    trimmed = icon.crop((inset, inset, icon.width - inset, icon.height - inset))
    width, height = trimmed.size
    ramp = max(1, round(width * BLEED_FEATHER))
    horizontal = Image.new("L", (width, 1))
    horizontal.putdata([min(255, 255 * min(x, width - 1 - x) // ramp) for x in range(width)])
    vertical = Image.new("L", (1, height))
    vertical.putdata([min(255, 255 * min(y, height - 1 - y) // ramp) for y in range(height)])
    mask = ImageChops.multiply(horizontal.resize((width, height)), vertical.resize((width, height)))
    trimmed.putalpha(ImageChops.multiply(trimmed.getchannel("A"), mask))
    return trimmed


def tile(icon, width, height, background=None):
    if background:
        strip = Image.new("RGB", (1, len(background)))
        strip.putdata(background)
        canvas = strip.resize((width, height), Image.BILINEAR).convert("RGBA")
    else:
        canvas = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    scaled = icon.resize((height, height), Image.LANCZOS)
    canvas.alpha_composite(scaled, ((width - height) // 2, 0))
    return canvas


def main():
    icon = Image.open(MASTER).convert("RGBA")
    bled = bleed_icon(icon)
    background = background_rows(icon)
    OUT.mkdir(parents=True, exist_ok=True)
    for old in OUT.glob("*.png"):
        old.unlink()
    for name, width, height, full_bleed in TILES:
        for scale in SCALES:
            w, h = round(width * scale / 100), round(height * scale / 100)
            image = tile(bled, w, h, background) if full_bleed else tile(icon, w, h)
            image.save(OUT / f"{name}.scale-{scale}.png")
    for size in TARGET_SIZES:
        image = tile(icon, size, size)
        for form in TARGET_FORMS:
            image.save(OUT / f"Square44x44Logo.targetsize-{size}{form}.png")


if __name__ == "__main__":
    main()
