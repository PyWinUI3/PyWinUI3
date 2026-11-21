"""多窗口：一个 App 里开两扇窗，各自 hide/show/close。

运行：
    python examples/17_multiwindow.py

要点：
- 第一扇 Window 是 main 会话，之后每扇自动分配 w1 / w2 ...
- 每扇窗的控件树独立，事件按 session 路由，互不干扰
- hide() 只隐藏窗口、保留会话；show() 恢复；close() 关掉这一扇
- main 窗口关闭会退出整个应用，和单窗口时行为一致
"""

import pywinui3 as ui

app = ui.App()

# ---------------------------------------------------------------------------
# 主窗口（session = main）
# ---------------------------------------------------------------------------
main = ui.Window(app, title="多窗口演示 · 主窗口", width=560, height=420)
ui.Label(main, "主窗口", font_size=20, weight="semibold")
ui.Label(main, "副窗口最小化到后台后，用下面的按钮唤回。", font_size=12, foreground="#8A8A8A")

log = ui.Label(main, "操作：等待中…", font_size=12, foreground="#0F6CBD")


btn_row = ui.Row(main, spacing=8)
btn_row2 = ui.Row(main, spacing=8)
# 副窗口还没创建，按钮回调先留空，等 second 建好再 bind

# ---------------------------------------------------------------------------
# 副窗口（session = w1，自动分配）
# ---------------------------------------------------------------------------
second = ui.Window(app, title="副窗口", width=480, height=320)
ui.Label(second, f"副窗口（session={second._session}）", font_size=18, weight="semibold")
ui.Label(second, "这扇窗有自己独立的控件树和事件循环。", font_size=12, foreground="#8A8A8A")


def hide_second() -> None:
    second.hide()
    log.text = "副窗口已隐藏（会话 w1 仍在）"


def show_second() -> None:
    second.show()
    log.text = "副窗口已显示并激活"


def close_second() -> None:
    second.close()
    log.text = "副窗口已关闭，会话 w1 已移除"


ui.Button(btn_row, "隐藏副窗口", command=hide_second)
ui.Button(btn_row2, "显示副窗口", command=show_second)
ui.Button(btn_row2, "关闭副窗口", command=close_second)

ui.Button(second, "隐藏自己", command=hide_second)
ui.Button(second, "关闭自己", command=close_second)

print(f"[multi] 会话: {list(app._windows.keys())}", flush=True)
print(f"[multi] main={main._session}, second={second._session}", flush=True)

main.mainloop()
