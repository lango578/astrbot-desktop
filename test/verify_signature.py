"""Signature acceptance for the shipped binaries.

Asserts the launcher and the frozen core carry an Authenticode signature from *our*
build certificate, that the signature is timestamped (so it outlives the certificate),
and that the public certificate travels with the deliverable so a recipient can check.
It also covers the installer we compile ourselves (the file a user actually double-clicks).
It does not pretend a self-signed certificate removes SmartScreen warnings -- that
needs a purchased certificate, and this test deliberately fails if the signature is
missing or belongs to someone else.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
from pathlib import Path

BUILD = Path(__file__).resolve().parents[1]
APP = Path(os.environ.get("ASTRBOT_APP_DIR") or (BUILD / "dist-new" / "AstrBot"))
KEYS = BUILD / "keys"
TARGETS = ("AstrBot.exe", "core/AstrBotCore.exe")


def ps(script: str) -> str:
    out = subprocess.run(
        ["powershell", "-NoProfile", "-Command", script],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=180,
    )
    return (out.stdout or "").strip()


def sig_info(path: Path) -> dict:
    script = (
        "$s = Get-AuthenticodeSignature -FilePath '%s'; "
        "[pscustomobject]@{"
        "Status=[string]$s.Status;"
        "Thumb=if($s.SignerCertificate){$s.SignerCertificate.Thumbprint}else{'-'};"
        "Subject=if($s.SignerCertificate){$s.SignerCertificate.Subject}else{'-'};"
        "Expiry=if($s.SignerCertificate){$s.SignerCertificate.NotAfter.ToString('yyyy-MM-dd')}else{'-'};"
        "Stamp=if($s.TimeStamperCertificate){$s.TimeStamperCertificate.Subject}else{'-'}"
        "} | ConvertTo-Json -Compress" % str(path)
    )
    raw = ps(script)
    try:
        return json.loads(raw) if raw else {}
    except json.JSONDecodeError:
        return {"Status": f"unparsable:{raw[:80]}"}


def main() -> int:
    results: list[tuple[str, bool, str]] = []
    expected = ""
    stamp_file = KEYS / "signing-thumbprint.txt"
    if stamp_file.is_file():
        expected = stamp_file.read_text(encoding="ascii").strip()
    results.append(("build certificate fingerprint is recorded", len(expected) == 40, expected[:12] or "keys\\signing-thumbprint.txt missing"))

    for name in TARGETS:
        path = APP / name
        if not path.is_file():
            results.append((f"{name} exists", False, str(path)))
            continue
        info = sig_info(path)
        thumb = str(info.get("Thumb") or "")
        results.append(
            (
                f"{name} is signed by our build certificate",
                bool(expected) and thumb.upper() == expected.upper(),
                f"status={info.get('Status')} signer={thumb[:12] or '-'}",
            )
        )
        results.append(
            (
                f"{name} signature says who built it",
                "AstrBot Desktop Build" in str(info.get("Subject") or ""),
                f"{info.get('Subject')} 到期 {info.get('Expiry')}",
            )
        )
        # 时间戳是唯一能让签名在证书过期后仍然说明问题的东西。
        results.append((f"{name} signature is timestamped", str(info.get("Stamp") or "").startswith("CN="), str(info.get("Stamp"))[:60] or "no timestamp"))

    # 安装包是用户真正双击的那个文件，它由我们的 ISCC 构建产出，同样该带签名。
    # README 里承诺过「安装包带自签」，这条断言就是让那句话不能空口说；
    # 文件还没编出来时按 NOTRUN 记，不假装通过。
    setups = sorted((BUILD / "installer").glob("AstrBot-Setup-*.exe"))
    if setups:
        info = sig_info(setups[0])
        thumb = str(info.get("Thumb") or "")
        results.append(
            (
                f"{setups[0].name} is signed by our build certificate",
                bool(expected) and thumb.upper() == expected.upper(),
                f"status={info.get('Status')} signer={thumb[:12] or '-'}",
            )
        )
        results.append(
            (
                f"{setups[0].name} signature is timestamped",
                str(info.get("Stamp") or "").startswith("CN="),
                str(info.get("Stamp"))[:60] or "no timestamp",
            )
        )
    else:
        print("NOTRUN  installer\\AstrBot-Setup-*.exe 还不存在（先跑 ISCC），安装程序签名两条未测")

    cer = APP / "astrbot-signing.cer"
    results.append(("public certificate ships with the app", cer.is_file(), str(cer)))
    if cer.is_file():
        text = ps(
            "$c = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 '%s'; "
            "[pscustomobject]@{Thumb=$c.Thumbprint; HasKey=$c.HasPrivateKey; Expiry=$c.NotAfter.ToString('yyyy-MM-dd')} | ConvertTo-Json -Compress" % str(cer)
        )
        try:
            shipped = json.loads(text)
        except json.JSONDecodeError:
            shipped = {}
        results.append(
            (
                "shipped certificate is public only",
                not shipped.get("HasKey") and str(shipped.get("Thumb") or "").upper() == expected.upper(),
                f"thumb={str(shipped.get('Thumb'))[:12]} private_key={shipped.get('HasKey')}",
            )
        )

    print("\n==== signature report ====")
    failed = 0
    for name, ok, detail in results:
        print(f"{'PASS' if ok else 'FAIL'}  {name}  [{detail}]")
        failed += 0 if ok else 1
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
