"""Win2D 立即模式画布演示：大量图元 + 每帧重绘。

运行：
    python examples/15_win2d.py

与 14_canvas.py 的 XAML 画布对比：
- XAML 路线每个图元是一个 UIElement，能单独交互，但几千个就会掉帧
- Win2D 路线把图元数组一次性交给 GPU，几千个也能满帧，
  代价是图元不能单独绑事件

这里用两层叠加：背景 5000 条静态线（只画一次），
前景 800 个运动圆点（每帧重绘）。分层是为了避免每帧重复序列化背景。
"""

import math
import random
import time

import pywinui3 as ui

W, H = 860, 520

win = ui.Window(title="Win2D 画布", width=920, height=720)

col = ui.Column(win, padding=16, spacing=10)

ui.Label(col, "Win2D 立即模式画布", font_size=20, weight="semibold")
ui.Label(
    col,
    "背景 5000 条线 + 前景 800 个运动圆点，两者都是每帧整体重绘。",
    font_size=12,
    foreground="#8A8A8A",
)

# 用 Canvas 叠两层：Canvas 支持绝对定位，天然可以重叠
stage = ui.Canvas(col, width=W, height=H)

# ---------------------------------------------------------------------------
# 背景层：5000 条随机线，只发一次
# ---------------------------------------------------------------------------
random.seed(20261119)
t0 = time.perf_counter()
background = [
    {
        "type": "line",
        "x1": random.uniform(0, W),
        "y1": random.uniform(0, H),
        "x2": random.uniform(0, W),
        "y2": random.uniform(0, H),
        "stroke": "#24405C",
        "strokeWidth": 1,
    }
    for _ in range(5000)
]
gen_ms = (time.perf_counter() - t0) * 1000

bg_canvas = ui.Canvas2D(stage, left=0, top=0, width=W, height=H, shapes=background)

# ---------------------------------------------------------------------------
# 前景层：800 个绕圈运动的小点，每帧重算
# ---------------------------------------------------------------------------
COUNT = 800
particles = [
    {
        "phase": random.uniform(0, math.tau),
        "radius": random.uniform(30, 230),
        "speed": random.uniform(0.4, 1.6),
    }
    for _ in range(COUNT)
]

fg_canvas = ui.Canvas2D(stage, left=0, top=0, width=W, height=H, shapes=[], z_index=10)

# 帧率与耗时统计
stats = {"frames": 0, "window_start": time.perf_counter(), "gen_ms": 0.0, "t": 0.0}

status = ui.Label(
    col,
    f"背景生成 {gen_ms:.0f} ms / 5000 条线；动画启动中…",
    font_size=12,
    foreground="#0F6CBD",
)


def tick() -> None:
    stats["t"] += 0.025
    angle_base = stats["t"]
    cx, cy = W / 2, H / 2

    frame_start = time.perf_counter()

    shapes = []
    for p in particles:
        angle = angle_base * p["speed"] + p["phase"]
        x = cx + math.cos(angle) * p["radius"]
        y = cy + math.sin(angle) * p["radius"] * 0.62
        shapes.append(
            {
                "type": "ellipse",
                "cx": x,
                "cy": y,
                "rx": 2.6,
                "ry": 2.6,
                "fill": "#4FC3F7",
            }
        )

    fg_canvas.shapes = shapes
    stats["gen_ms"] = (time.perf_counter() - frame_start) * 1000

    stats["frames"] += 1
    now = time.perf_counter()
    if now - stats["window_start"] >= 1.0:
        fps = stats["frames"] / (now - stats["window_start"])
        status.text = (
            f"背景 5000 条线（一次 {gen_ms:.0f} ms）｜"
            f"前景 {COUNT} 点 @ {fps:.0f} fps｜"
            f"Python 每帧生成 {stats['gen_ms']:.1f} ms"
        )
        print(
            f"[win2d] {fps:5.1f} fps  每帧生成 {stats['gen_ms']:.2f} ms  "
            f"({COUNT} 个图元)",
            flush=True,
        )
        stats["frames"] = 0
        stats["window_start"] = now

    win.after(16, tick)


win.after(600, tick)
win.mainloop()
