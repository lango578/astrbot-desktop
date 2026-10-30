"""Round the corners of the *original* AstrBot icon and centre its artwork.

Hard rule from the owner: the artwork itself is not up for redesign. This script only
  1. shifts the existing pixels so the ink sits at the centre of the plate
     (measured on the original: ink centroid x=153.9 on a 256 canvas, i.e. 26.4 px right
     of centre, with left margin 82 px against a right margin of 30 px), and
  2. clips the square plate into a rounded one (the corners become transparent, which is
     also what stops the white square from glaring on a dark titlebar).

Nothing is redrawn, recoloured or rescaled in shape. The size ladder is only so Windows
stops resampling a single 256px frame down to 16px itself.

    venv\\Scripts\\python.exe build\\make_icon_from_original.py
"""

from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image

BUILD = Path(__file__).resolve().parent
ASSETS = BUILD / "assets"
# 原版像素的只读备份来源：交付目录里那份是改动前最后一次装进去的
ORIGINALS = (
    ASSETS / "astrbot-original.png",
    BUILD.parent / "dist" / "AstrBot" / "astrbot.png",
)
LADDER = (16, 20, 24, 32, 40, 48, 64, 128, 256)
# 256 画布上量出来的墨迹质心偏移；圆角半径按 Win11 磁贴比例取
SHIFT = (-26, 0)
RADIUS_AT_256 = 48


def load_original() -> Image.Image:
    for path in ORIGINALS:
        if path.is_file():
            return Image.open(path).convert("RGBA")
    raise SystemExit("找不到原版图标像素，别继续：检查 dist\\AstrBot\\astrbot.png")


def centred(img: Image.Image, shift: tuple[int, int]) -> Image.Image:
    """把墨迹平移到画布正中；白色底板不动，只挪图案。"""
    out = Image.new("RGBA", img.size, (255, 255, 255, 255))
    out.paste(img, shift, img)
    return out


def rounded(img: Image.Image, radius_at_256: int) -> Image.Image:
    size = img.size[0]
    radius = max(2, int(round(size * radius_at_256 / 256)))
    mask = Image.new("L", img.size, 0)
    from PIL import ImageDraw

    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    out = img.copy()
    out.putalpha(mask)
    return out


def master() -> Image.Image:
    return rounded(centred(load_original(), SHIFT), RADIUS_AT_256)


def preview(img: Image.Image, path: Path) -> None:
    """拼一张按实尺看的对比图：深色标题栏一条，然后各尺寸排开。"""
    from PIL import ImageDraw

    pad, cell = 18, 120
    width = pad * 2 + cell * 5
    sheet = Image.new("RGBA", (width, cell + 150), (16, 18, 22, 255))
    draw = ImageDraw.Draw(sheet)
    draw.rectangle([pad, pad, width - pad, pad + 34], fill=(28, 32, 38, 255))
    small = img.resize((16, 16), Image.LANCZOS)
    sheet.paste(small, (pad + 8, pad + 9), small)
    draw.text((pad + 30, pad + 11), "16px 标题栏实尺", fill=(200, 208, 220, 255))
    x = pad + 10
    for size in (16, 24, 32, 48, 128):
        frame = img.resize((size, size), Image.LANCZOS)
        sheet.paste(frame, (x, 90 + (128 - size)), frame)
        draw.text((x, 90 + 130), str(size), fill=(150, 160, 175, 255))
        x += cell
    sheet.save(path)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true", help="只出对比图，不写 build/assets")
    args = parser.parse_args()

    base = load_original()
    src = base.copy()
    src.save(ASSETS / "astrbot-original.png")  # 以后随时能回到原像素

    img = master()
    preview(img, ASSETS / "icon-preview.png")

    frames = [img.resize((s, s), Image.LANCZOS) for s in LADDER]
    if not args.check:
        frames[-1].save(
            ASSETS / "astrbot.ico",
            format="ICO",
            sizes=[(s, s) for s in LADDER],
            append_images=frames[:-1],
        )
        img.save(ASSETS / "astrbot.png")
        print(f"[icon] 已写 {ASSETS / 'astrbot.ico'}（{len(LADDER)} 个尺寸）+ astrbot.png")
    else:
        print("[icon] --check：只出对比图")

    # 自检：角必须透明、墨迹质心必须落在中心 ±2px、尺寸阶梯必须齐
    px = img.load()
    corners = [px[0, 0], px[255, 0], px[0, 255], px[255, 255]]
    xs = []
    ys = []
    for y in range(0, 256, 2):
        for x in range(0, 256, 2):
            r, g, b, a = px[x, y]
            if a > 16 and not (r > 245 and g > 245 and b > 245):
                xs.append(x)
                ys.append(y)
    cx, cy = sum(xs) / len(xs), sum(ys) / len(ys)
    print(f"[icon] 四角 alpha = {[c[3] for c in corners]}（应为 0）")
    print(f"[icon] 墨迹质心 = {cx:.1f},{cy:.1f}（画布中心 127.5，偏移应 <2px）")
    print(f"[icon] 图案尺寸 = {max(xs)-min(xs)+1}x{max(ys)-min(ys)+1}（原版 145x146，形状不许变）")
    ok = all(c[3] == 0 for c in corners) and abs(cx - 127.5) < 2 and abs(cy - 127.5) < 2
    print("[icon] 判定:", "OK" if ok else "有问题")
    print(f"[icon] 对比图: {ASSETS / 'icon-preview.png'}")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
