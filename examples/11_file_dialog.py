# -*- coding: utf-8 -*-
"""文件对话框 + 自绘调色框交互演示。

点四个按钮分别打开：单个文件、多选文件、保存路径、选择文件夹；
调色框是 C# 侧用基础控件自绘的（非原生 ColorPicker）。

结果写到 examples 目录下的 file_dialog_result.txt（pythonw 没有控制台）。
"""

from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path

# ---- pythonw 自重启：隐藏黑色控制台窗口（用户约定，原样抄录）----
_GUARD_ENV = "PYTHONW_RELAUNCHED"
_DETACHED_PROCESS = 0x00000008
_CREATE_NO_WINDOW = 0x08000000


def is_running_as_pythonw() -> bool:
    if not sys.platform.startswith("win"):
        return True
    exe = os.path.basename(sys.executable or "").lower()
    return exe.startswith("pythonw")


def find_pythonw() -> str | None:
    if not sys.executable:
        return None
    exe_dir = os.path.dirname(os.path.abspath(sys.executable))
    candidate = os.path.join(exe_dir, "pythonw.exe")
    if os.path.isfile(candidate):
        return candidate
    try:
        for name in os.listdir(exe_dir):
            if name.lower() == "pythonw.exe":
                path = os.path.join(exe_dir, name)
                if os.path.isfile(path):
                    return path
    except OSError:
        pass
    return None


def _build_child_args(argv: list[str] | None) -> list[str] | None:
    if argv is not None:
        return list(argv)
    if sys.argv and sys.argv[0] and os.path.isfile(sys.argv[0]):
        return list(sys.argv)
    main_file = getattr(sys.modules.get("__main__"), "__file__", None)
    if main_file and os.path.isfile(main_file):
        return [main_file] + list(sys.argv[1:])
    return None


def relaunch_with_pythonw(argv: list[str] | None = None) -> bool:
    if not sys.platform.startswith("win"):
        return False
    if getattr(sys, "frozen", False):
        return False
    if os.environ.get(_GUARD_ENV) == "1":
        return False
    if is_running_as_pythonw():
        return False
    pythonw = find_pythonw()
    if not pythonw:
        return False
    child_args = _build_child_args(argv)
    if not child_args:
        return False
    env = os.environ.copy()
    env[_GUARD_ENV] = "1"
    try:
        subprocess.Popen(
            [pythonw, *child_args], cwd=os.getcwd(), env=env, close_fds=True,
            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            creationflags=_DETACHED_PROCESS | _CREATE_NO_WINDOW,
        )
    except OSError:
        return False
    return True


# ----------------------------------------------------------------------

RESULT_FILE = Path(__file__).with_name("file_dialog_result.txt")


def log(text: str) -> None:
    with RESULT_FILE.open("a", encoding="utf-8") as f:
        f.write(text + "\n")


def main() -> None:
    import pywinui3 as ui

    RESULT_FILE.write_text("=== 文件对话框 / 自绘调色框演示 ===\n", encoding="utf-8")

    win = ui.Window(title="文件对话框演示（自绘）", width=620, height=520)
    col = ui.Column(win, padding=24, spacing=12)

    ui.Label(col, "自绘调色框（点色块弹出「编辑颜色」独立窗口）", font_size=16, weight="bold")
    picker = ui.ColorPicker(col, color="#FF0078D4")
    picker.bind("colorChanged", lambda e: log(f"colorChanged -> {e.data.get('color')}"))

    ui.Label(col, "文件选择（点按钮打开自绘弹窗）", font_size=16, weight="bold")

    def on_open(_e):
        paths = ui.file_dialog("open", title="选一个文件", filter="*.*")
        log(f"open      -> {paths}")

    def on_multi(_e):
        paths = ui.file_dialog("openMultiple", title="选多个文件", filter="*.py;*.txt;*.md")
        log(f"multiple  -> {paths}")

    def on_save(_e):
        paths = ui.file_dialog("save", title="保存文件", default_name="output.txt",
                               filter="*.txt;*.md")
        log(f"save      -> {paths}")

    def on_folder(_e):
        paths = ui.file_dialog("folder", title="选一个文件夹")
        log(f"folder    -> {paths}")

    ui.Button(col, "① 打开单个文件", command=on_open)
    ui.Button(col, "② 打开多个文件", command=on_multi)
    ui.Button(col, "③ 保存路径", command=on_save)
    ui.Button(col, "④ 选择文件夹", command=on_folder)

    log("窗口已打开，点按钮测试。结果实时写入本文件。")
    win.mainloop()
    log("窗口已关闭。")


if __name__ == "__main__":
    if relaunch_with_pythonw():
        sys.exit(0)
    main()
