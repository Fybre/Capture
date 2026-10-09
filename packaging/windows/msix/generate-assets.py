"""Regenerates the MSIX visual assets in ./Assets from the app's master icon.

Run from anywhere: python3 packaging/windows/msix/generate-assets.py
The outputs are committed, so CI never needs Pillow. Re-run only when the icon artwork changes.
"""
from pathlib import Path

from PIL import Image

HERE = Path(__file__).resolve().parent
MASTER = HERE.parents[2] / "src" / "Capture.App" / "Assets" / "Brand" / "capture-icon-master.png"
OUT = HERE / "Assets"

# (asset name, base width, base height, fraction of the tile height the icon fills)
TILES = [
    ("Square44x44Logo", 44, 44, 1.0),
    ("Square71x71Logo", 71, 71, 0.8),
    ("Square150x150Logo", 150, 150, 0.66),
    ("Wide310x150Logo", 310, 150, 0.66),
    ("StoreLogo", 50, 50, 1.0),
]
# One plain file per logo, at 200% of its base size. Windows only chooses between .scale-N/.targetsize-N
# variants when the package carries a resources.pri index, which build-msix.ps1 doesn't generate, so
# plain names are what the manifest can reference. Windows scales these as needed.
SCALE = 200


def tile(icon, width, height, fill):
    canvas = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    size = max(1, round(height * fill))
    scaled = icon.resize((size, size), Image.LANCZOS)
    canvas.paste(scaled, ((width - size) // 2, (height - size) // 2), scaled)
    return canvas


def main():
    icon = Image.open(MASTER).convert("RGBA")
    OUT.mkdir(parents=True, exist_ok=True)
    for old in OUT.glob("*.png"):
        old.unlink()
    for name, width, height, fill in TILES:
        w, h = round(width * SCALE / 100), round(height * SCALE / 100)
        tile(icon, w, h, fill).save(OUT / f"{name}.png")


if __name__ == "__main__":
    main()
