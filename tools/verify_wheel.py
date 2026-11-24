"""校验 wheel 的平台标签与内部宿主 exe 的真实架构是否一致。

为什么要做：build_wheel.ps1 用 retag_wheel.py 把 py3-none-any 改成
win_amd64 / win_arm64，但标签是"手写"的——如果构建脚本传错 Platform、
或复制产物时串了目录，就会出现「标签写 arm64、里面装的却是 x64 exe」
这种最坑的错误：pip 装得上，启动才炸，而且很难定位。

本脚本从两个层面校验：
1. 标签层：wheel 文件名与 .dist-info/WHEEL 的 Tag 都是期望平台；
2. 二进制层：直接读 PyWinUI.Host.exe 的 PE 头 Machine 字段，
   确认真是 AMD64(0x8664) / ARM64(0xAA64)。

用法：
    python tools/verify_wheel.py dist/pywinui3-*-win_amd64.whl --platform win_amd64
    python tools/verify_wheel.py dist/pywinui3-*-win_arm64.whl --platform win_arm64
"""

from __future__ import annotations

import argparse
import struct
import sys
import zipfile
from pathlib import Path

# PE COFF Machine 字段
IMAGE_FILE_MACHINE_AMD64 = 0x8664
IMAGE_FILE_MACHINE_ARM64 = 0xAA64
MACHINE_BY_TAG = {
    "win_amd64": IMAGE_FILE_MACHINE_AMD64,
    "win_arm64": IMAGE_FILE_MACHINE_ARM64,
}
TAG_BY_MACHINE = {v: k for k, v in MACHINE_BY_TAG.items()}

HOST_EXE = "PyWinUI.Host.exe"


def pe_machine(data: bytes) -> int:
    """从一个 PE 文件的字节里读出 COFF Machine 字段。"""
    if len(data) < 0x40 or data[:2] != b"MZ":
        raise ValueError("不是有效的 PE 文件（缺 MZ 头）")
    (pe_offset,) = struct.unpack_from("<I", data, 0x3C)
    if pe_offset + 6 > len(data) or data[pe_offset : pe_offset + 4] != b"PE\0\0":
        raise ValueError("不是有效的 PE 文件（缺 PE\\0\\0 签名）")
    (machine,) = struct.unpack_from("<H", data, pe_offset + 4)
    return machine


def verify(wheel_path: Path, platform_tag: str) -> list[str]:
    """返回问题列表；空列表表示通过。"""
    problems: list[str] = []

    if not wheel_path.is_file():
        return [f"找不到 wheel：{wheel_path}"]

    # ---- 1. 文件名标签 ----
    if f"-{platform_tag}.whl" not in wheel_path.name:
        problems.append(
            f"文件名平台标签不符：{wheel_path.name}（期望含 -{platform_tag}.whl）"
        )

    try:
        with zipfile.ZipFile(wheel_path) as archive:
            names = archive.namelist()

            # ---- 2. WHEEL 元数据标签 ----
            wheel_meta = next(
                (n for n in names if n.endswith(".dist-info/WHEEL")), None
            )
            if wheel_meta is None:
                problems.append("wheel 里没有 .dist-info/WHEEL")
            else:
                meta = archive.read(wheel_meta).decode("utf-8", "replace")
                tags = [ln[5:] for ln in meta.splitlines() if ln.startswith("Tag: ")]
                if not any(t.endswith(f"-{platform_tag}") for t in tags):
                    problems.append(
                        f"WHEEL 元数据 Tag 不含 {platform_tag}：{tags}"
                    )
                if "Root-Is-Purelib: false" not in meta:
                    problems.append("WHEEL 元数据 Root-Is-Purelib 应为 false（含宿主 exe）")

            # ---- 3. 宿主 exe 存在且架构正确 ----
            exe_entries = [n for n in names if n.endswith("/" + HOST_EXE) or n == HOST_EXE]
            if not exe_entries:
                problems.append(f"wheel 里没有 {HOST_EXE}")
            else:
                for exe_entry in exe_entries:
                    data = archive.read(exe_entry)
                    try:
                        machine = pe_machine(data)
                    except ValueError as exc:
                        problems.append(f"{exe_entry}: {exc}")
                        continue
                    actual_tag = TAG_BY_MACHINE.get(machine)
                    expected = MACHINE_BY_TAG[platform_tag]
                    print(
                        f"[verify] {exe_entry}: PE Machine=0x{machine:04X} "
                        f"({actual_tag or 'unknown'})"
                    )
                    if machine != expected:
                        problems.append(
                            f"{exe_entry} 架构错误：Machine=0x{machine:04X} "
                            f"({actual_tag or 'unknown'})，期望 {platform_tag} "
                            f"(0x{expected:04X})"
                        )
    except zipfile.BadZipFile:
        problems.append(f"不是有效的 wheel（zip 损坏）：{wheel_path}")

    return problems


def main() -> int:
    parser = argparse.ArgumentParser(description="校验 wheel 标签与宿主 exe 架构一致")
    parser.add_argument("wheel", type=Path)
    parser.add_argument(
        "--platform", required=True, choices=sorted(MACHINE_BY_TAG), help="期望平台标签"
    )
    args = parser.parse_args()

    problems = verify(args.wheel, args.platform)
    if problems:
        for p in problems:
            print(f"[verify] FAIL: {p}", file=sys.stderr)
        return 1

    print(f"[verify] OK: {args.wheel.name} 标签与 exe 架构一致（{args.platform}）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
