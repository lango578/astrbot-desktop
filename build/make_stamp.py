"""把构建戳写进 launcher/AstrBotStamp.cs（编译前跑一次）。

用户报「还是卡」时，第一件要确认的事是他跑的是哪一份。没有可看的版本标识，
这件事就只能靠猜，所以每次编译都把源码指纹写进 exe：标题栏、诊断包、自检输出都能看到它。
"""

from __future__ import annotations

import datetime
import hashlib
import re
from pathlib import Path

BUILD = Path(__file__).resolve().parent
ROOT = BUILD.parent
TARGET = ROOT / "launcher" / "AstrBotStamp.cs"
# 只算真正参与编译的输入，且不含本文件生成的那个 stamp（否则每次哈希都会变）
SOURCES = (
    ROOT / "launcher" / "AstrBotLauncher.cs",
    ROOT / "launcher" / "AstrBotControls.cs",
    ROOT / "launcher" / "AstrBotPanelView.cs",
    ROOT / "launcher" / "AstrBotLauncherUi.cs",
    ROOT / "launcher" / "AstrBotRecovery.cs",
    ROOT / "launcher" / "AstrBotBind.cs",
    ROOT / "launcher" / "AstrBotOneBot.cs",
    ROOT / "launcher" / "AstrBotOneBotInstall.cs",
    BUILD / "astrbot_desktop.py",
    BUILD / "README.txt",
)
BLOCK = re.compile(r"(public const string Stamp = \")[^\"]*(\";)")


def main() -> int:
    digest = hashlib.sha256()
    for path in SOURCES:
        if not path.is_file():
            print(f"[fail] 缺源文件 {path}")
            return 1
        digest.update(path.name.encode())
        digest.update(path.read_bytes())
    stamp = datetime.date.today().strftime("%m%d") + "-" + digest.hexdigest()[:8]
    text = TARGET.read_text(encoding="utf-8-sig")
    if not BLOCK.search(text):
        print("[fail] AstrBotStamp.cs 里没有可替换的 Stamp 常量")
        return 1
    text = BLOCK.sub(lambda m: m.group(1) + stamp + m.group(2), text, count=1)
    TARGET.write_bytes(text.encode("utf-8"))
    print(f"[stamp] {stamp}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
