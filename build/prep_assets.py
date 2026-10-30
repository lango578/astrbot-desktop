"""Prepare build assets: application icon and the ffmpeg binary for QQ voice."""

import sys
from pathlib import Path

from PIL import Image

BUILD_DIR = Path(__file__).resolve().parent
ROOT = BUILD_DIR.parent
SRC = ROOT / "src" / "AstrBot-master"
ASSETS = BUILD_DIR / "assets"
ASSETS.mkdir(exist_ok=True)

BANNER = SRC / "dashboard" / "src" / "assets" / "images" / "astrbot_banner.png"
ICON = ASSETS / "astrbot.ico"
LOGO = ASSETS / "astrbot.png"
FFMPEG = ASSETS / "ffmpeg.exe"


def make_icon() -> None:
    if not BANNER.is_file():
        print(f"[prep] banner image missing, skipping icon: {BANNER}")
        return
    img = Image.open(BANNER).convert("RGBA")
    # The banner is a horizontal lockup: the mark sits at the far left, so a
    # centre crop would slice through the wordmark instead of the logo.
    side = img.height
    if img.width < side:
        side = img.width
    img = img.crop((0, 0, side, side)).resize((256, 256), Image.LANCZOS)
    img.save(ICON, sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)])
    img.save(LOGO)
    print(f"[prep] icon -> {ICON}")
    print(f"[prep] logo -> {LOGO}")


def make_ffmpeg() -> None:
    if FFMPEG.is_file():
        print(f"[prep] ffmpeg already staged: {FFMPEG}")
        return
    try:
        import imageio_ffmpeg
    except ImportError:
        print("[prep] imageio-ffmpeg not installed, skipping ffmpeg")
        return
    src = Path(imageio_ffmpeg.get_ffmpeg_exe())
    FFMPEG.write_bytes(src.read_bytes())
    print(f"[prep] ffmpeg {src.name} -> {FFMPEG} ({FFMPEG.stat().st_size / 1048576:.1f} MB)")


if __name__ == "__main__":
    make_icon()
    make_ffmpeg()
    sys.exit(0)
