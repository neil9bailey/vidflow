"""Generates the Microsoft Store package images (packaging/store/Assets) from the same drawing as
assets/VidFlow.ico, at 100% and 200% scale plus the unplated taskbar sizes."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from PIL import Image  # noqa: E402
from make_icon import tile  # noqa: E402


def framed(width: int, height: int, fill: float) -> Image.Image:
    """The icon centred on a transparent canvas, its side `fill` times the canvas height."""
    canvas = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    side = max(16, round(height * fill))
    icon = tile(side)
    canvas.paste(icon, ((width - side) // 2, (height - side) // 2), icon)
    return canvas


def main(out: str) -> None:
    os.makedirs(out, exist_ok=True)
    images = {}
    for scale, suffix in ((1, ""), (2, ".scale-200")):
        images[f"StoreLogo{suffix}.png"] = tile(50 * scale)
        images[f"Square44x44Logo{suffix}.png"] = tile(44 * scale)
        images[f"Square71x71Logo{suffix}.png"] = framed(71 * scale, 71 * scale, 0.66)
        images[f"Square150x150Logo{suffix}.png"] = framed(150 * scale, 150 * scale, 0.56)
        images[f"Wide310x150Logo{suffix}.png"] = framed(310 * scale, 150 * scale, 0.56)
        images[f"Square310x310Logo{suffix}.png"] = framed(310 * scale, 310 * scale, 0.5)
    # Taskbar, Start list and Alt+Tab use these, drawn without the accent-coloured plate.
    for size in (16, 24, 32, 48, 256):
        images[f"Square44x44Logo.targetsize-{size}_altform-unplated.png"] = tile(size)
    for name, img in images.items():
        img.save(os.path.join(out, name), format="PNG", optimize=True)
    print(f"{len(images)} images -> {out}")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "packaging", "store", "Assets"))
