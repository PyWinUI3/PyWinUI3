# -*- coding: utf-8 -*-
"""自绘标题栏扩展：返回按钮 + 带图标输入框。

运行：
    python examples/19_titlebar.py

要点：
- ``return_button=True`` 在应用图标**左边**画一枚返回按钮。点击只发
  ``returnRequested``，宿主不自己关窗/切页，完全交给 ``on_return_request``。
- ``text_input_box=True`` 在标题文字**右边**画一枚带放大镜图标的输入框。
  输入发 ``textInputChanged``，回车发 ``textInputSubmitted``，都带 ``e.data["text"]``。
- 两个属性都能用 ``win.configure(...)`` 在运行期开关，不会清掉输入框里的文字。
- 标题栏在所有平台都是自绘的；Win11 上窗口圆角交给 DWM 按整窗绘制。
"""

from __future__ import annotations

import os
import subprocess
import sys

# ---- pythonw 自重启：隐藏黑色控制台窗口（原样抄录）----
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
            [pythonw, *child_args],
            cwd=os.getcwd(),
            env=env,
            close_fds=True,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=_DETACHED_PROCESS | _CREATE_NO_WINDOW,
        )
    except OSError:
        return False

    return True


def main() -> None:
    import pywinui3 as ui

    app = ui.App()

    # 两个属性默认都是 false，这里显式打开。
    # 拼错成 retrun_buttom 也能识别（早期示例里的写法）。
    win = ui.Window(
        app,
        title="标题栏扩展 · 返回 / 搜索",
        width=760,
        height=460,
        return_button=True,
        text_input_box=True,
    )

    col = ui.Column(win, padding=24, spacing=12)

    ui.Label(col, "自绘标题栏扩展", font_size=22, weight="semibold")
    ui.Label(
        col,
        "左上角是返回按钮（在应用图标左边），标题右侧是带放大镜的输入框。"
        "在输入框里打字或按回车，看下面的状态怎么变。",
        font_size=12,
        foreground="#8A8A8A",
    )

    status = ui.Label(col, "等待标题栏事件…", font_size=14, foreground="#0F6CBD")
    ui.Separator(col)

    state = {"return": True, "input": True}

    def on_return(_event) -> None:
        # 宿主什么都不会替你做，这里只改状态文字；
        # 真实应用里通常切回上一页，或者 win.close()。
        status.set_text("← 返回按钮被点击（示例里不关窗）")

    def on_changed(event) -> None:
        status.set_text("正在输入：" + event.data.get("text", ""))

    def on_submit(event) -> None:
        status.set_text("搜索：" + event.data.get("text", ""))

    # command= 走 tkinter 语义：包装成无参 lambda，回调不能带事件参数
    def toggle_return() -> None:
        state["return"] = not state["return"]
        win.configure(return_button=state["return"])
        btn_return.configure(text="隐藏返回按钮" if state["return"] else "显示返回按钮")

    def toggle_input() -> None:
        state["input"] = not state["input"]
        win.configure(text_input_box=state["input"])
        btn_input.configure(text="隐藏输入框" if state["input"] else "显示输入框")

    win.on_return_request(on_return)
    win.on_text_changed(on_changed)
    win.on_text_input(on_submit)

    row = ui.Row(col, spacing=10)
    btn_return = ui.Button(row, "隐藏返回按钮", command=toggle_return, width=132)
    btn_input = ui.Button(row, "隐藏输入框", command=toggle_input, width=116)

    ui.Label(
        col,
        "运行期开关只切可见性，输入框里的文字会保留。"
        "返回按钮和最大化按钮不同：没注册回调时按钮照常显示，只是没有动作。",
        font_size=12,
        foreground="#8A8A8A",
    )

    win.mainloop()


if __name__ == "__main__":
    if relaunch_with_pythonw():
        sys.exit(0)
    main()
