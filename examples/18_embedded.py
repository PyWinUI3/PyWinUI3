"""嵌入子窗口：把一扇窗口塞进另一扇窗口的客户区。

运行：
    python examples/18_embedded.py

要点：
- 传 ``parent=父窗口`` 后，本窗口降级成 WS_CHILD 子窗口，不再是独立顶层窗口
  （任务栏不会多出按钮，父窗口最小化/移动时它跟着走）
- 子窗口带自绘标题栏：白色底、按住可拖、最小化收进父窗口左下角（z 序置顶）、
  关闭按钮交给 Python 处理
- 关闭按钮默认直接关窗；用 ``on_close_request`` 可以接管，先问一句再决定
- ``layer`` 只有两档，对应子 HWND 唯一有效的两个 z 序位置：
  ``highest`` = HWND_TOP，``lowest`` = HWND_BOTTOM。
  TOPMOST / NOTOPMOST 对 WS_CHILD 窗口无效，所以不开放
- 两个子窗口故意重叠，用来直观看到 highest 压在 lowest 上面
- 父窗口关闭时子窗口一并关闭
"""

import pywinui3 as ui

app = ui.App()

# ---------------------------------------------------------------------------
# 父窗口
# ---------------------------------------------------------------------------
parent = ui.Window(app, title="父窗口 · 子窗口嵌在这里", width=900, height=640)
ui.Label(parent, "父窗口", font_size=22, weight="semibold")
ui.Label(
    parent,
    "下面两块是 WS_CHILD 子窗口，位置故意重叠以演示层级。拖动或缩放本窗口，它们会跟着走。",
    font_size=12,
    foreground="#8A8A8A",
)

# ---------------------------------------------------------------------------
# 子窗口 A：highest
# ---------------------------------------------------------------------------
front = ui.Window(
    app,
    title="子窗口 · highest",
    parent=parent,
    width=480,
    height=320,
    x=60,
    y=120,
    layer="highest",
)
ui.Label(front, "highest", font_size=18, weight="semibold")
ui.Label(front, "我压在 lowest 上面", font_size=12, foreground="#0F6CBD")

# ---------------------------------------------------------------------------
# 子窗口 B：lowest
# ---------------------------------------------------------------------------
back = ui.Window(
    app,
    title="子窗口 · lowest",
    parent=parent,
    width=480,
    height=320,
    x=300,
    y=240,
    layer="lowest",
)
ui.Label(back, "lowest", font_size=18, weight="semibold")
ui.Label(back, "和 highest 重叠的区域会被它盖住", font_size=12, foreground="#8A8A8A")


# ---------------------------------------------------------------------------
# 关闭按钮交给 Python：highest 接管，lowest 用默认行为（直接关）
# ---------------------------------------------------------------------------
def close_front(_event) -> None:
    print("[embedded] highest 的关闭请求交给了 Python", flush=True)
    front.close()


front.on_close_request(close_front)

print(f"[embedded] 会话: {list(app._windows.keys())}", flush=True)
print(f"[embedded] parent={parent._session} front={front._session} back={back._session}", flush=True)

parent.mainloop()
