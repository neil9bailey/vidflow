"""Generates assets/VidFlow.ico: rounded dark tile, blue play triangle inside viewfinder corners."""
import sys
from PIL import Image, ImageDraw


def tile(size: int) -> Image.Image:
    ss = 4  # supersample for smooth edges
    s = size * ss
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    r = int(s * 0.22)
    # vertical gradient background
    top, bottom = (40, 44, 52), (14, 15, 18)
    grad = Image.new("RGBA", (s, s))
    gd = ImageDraw.Draw(grad)
    for y in range(s):
        t = y / (s - 1)
        c = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,)
        gd.line([(0, y), (s, y)], fill=c)
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, s - 1, s - 1], radius=r, fill=255)
    img.paste(grad, (0, 0), mask)

    accent = (64, 156, 255, 255)
    # viewfinder corners
    m, ln, w = s * 0.17, s * 0.17, max(ss, int(s * 0.055))
    corners = [(m, m, 1, 1), (s - m, m, -1, 1), (m, s - m, 1, -1), (s - m, s - m, -1, -1)]
    if size >= 24:
        for x, y, dx, dy in corners:
            d.line([(x, y), (x + dx * ln, y)], fill=(230, 230, 230, 255), width=w)
            d.line([(x, y), (x, y + dy * ln)], fill=(230, 230, 230, 255), width=w)
    # play triangle
    cx, cy, h = s * 0.53, s * 0.5, s * (0.40 if size >= 24 else 0.56)
    tri = [(cx - h * 0.45, cy - h / 2), (cx - h * 0.45, cy + h / 2), (cx + h * 0.55, cy)]
    d.polygon(tri, fill=accent)
    return img.resize((size, size), Image.LANCZOS)


def main(out: str) -> None:
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    imgs = [tile(sz) for sz in sizes]
    imgs[-1].save(out, format="ICO", sizes=[(sz, sz) for sz in sizes], append_images=imgs[:-1])


if __name__ == "__main__":
    main(sys.argv[1])
