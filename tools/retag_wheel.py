"""把 wheel 的平台标签从 none-any 改成真实平台。

为什么需要：setuptools 构建纯 Python 工程时默认给 `py3-none-any`，
但这个包里装的是 Windows 宿主 exe。`none-any` 的含义是「任何平台都能装」，
pip 在 Linux/macOS 上会判定兼容、装成功，然后启动时才炸。

改成 `win_amd64` 后，pip 在非 Windows 上直接拒绝，问题在安装那一刻就暴露。

用法：
    python tools/retag_wheel.py dist/pywinui3-0.2.2-py3-none-any.whl
    python tools/retag_wheel.py dist/xxx.whl --platform win_arm64
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import shutil
import sys
import zipfile
from pathlib import Path


def record_hash(data: bytes) -> str:
    digest = hashlib.sha256(data).digest()
    return "sha256=" + base64.urlsafe_b64encode(digest).decode().rstrip("=")


def retag(wheel_path: Path, platform_tag: str) -> Path:
    with zipfile.ZipFile(wheel_path) as archive:
        names = archive.namelist()
        entries = {name: archive.read(name) for name in names}

    # 找到 .dist-info/WHEEL
    wheel_meta = next((name for name in names if name.endswith(".dist-info/WHEEL")), None)
    if wheel_meta is None:
        raise SystemExit(f"{wheel_path.name} 里没有 WHEEL 文件，不像是标准 wheel")

    text = entries[wheel_meta].decode("utf-8")
    lines = []
    replaced = False

    for line in text.splitlines():
        if line.startswith("Tag: "):
            # py3-none-any -> py3-none-win_amd64
            parts = line[len("Tag: "):].split("-")
            if len(parts) == 3:
                parts[2] = platform_tag
                line = "Tag: " + "-".join(parts)
                replaced = True
        elif line.startswith("Root-Is-Purelib:"):
            # 包里带 Windows 宿主 exe，已经不是纯 Python 包了
            line = "Root-Is-Purelib: false"
        lines.append(line)

    if not replaced:
        raise SystemExit(f"{wheel_meta} 里没有找到 Tag 行")

    entries[wheel_meta] = ("\n".join(lines) + "\n").encode("utf-8")

    # 重算 RECORD：WHEEL 变了，它的哈希也要跟着变
    record_name = next((name for name in names if name.endswith(".dist-info/RECORD")), None)
    if record_name is None:
        raise SystemExit("wheel 里没有 RECORD")

    record_lines = []
    for name in names:
        if name == record_name:
            continue
        # RECORD 自己不写哈希
        record_lines.append(f"{name},{record_hash(entries[name])},{len(entries[name])}")
    record_lines.append(f"{record_name},,")
    entries[record_name] = ("\n".join(record_lines) + "\n").encode("utf-8")

    # 文件名：把平台段换掉
    stem = wheel_path.name[: -len(".whl")]
    parts = stem.split("-")
    if len(parts) != 5:
        raise SystemExit(f"文件名不符合 {{name}}-{{version}}-{{python}}-{{abi}}-{{platform}} : {wheel_path.name}")
    parts[4] = platform_tag
    new_path = wheel_path.with_name("-".join(parts) + ".whl")

    with zipfile.ZipFile(new_path, "w", zipfile.ZIP_DEFLATED) as archive:
        for name in names:
            archive.writestr(name, entries[name])

    if new_path != wheel_path:
        wheel_path.unlink()

    return new_path


def main() -> int:
    parser = argparse.ArgumentParser(description="改写 wheel 的平台标签")
    parser.add_argument("wheel", type=Path)
    parser.add_argument("--platform", default="win_amd64", help="win_amd64 / win_arm64")
    args = parser.parse_args()

    if not args.wheel.is_file():
        print(f"找不到 {args.wheel}", file=sys.stderr)
        return 1

    result = retag(args.wheel, args.platform)
    print(f"[retag] {args.wheel.name} -> {result.name}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
