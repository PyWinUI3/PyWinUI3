"""画布能力演示：Canvas 容器 + 常见图元 + 视图变换 + 指针拖动。

运行：
    python examples/14_canvas.py

这不是 SVG 编辑器，只验证画布层面的能力：定位、填充、描边、虚线、圆角、
路径、多边形、整体缩放，以及带坐标的指针事件能支撑拖动。
"""

import pywinui3 as ui

win = ui.Window(title="画布能力演示", width=900, height=700)

root = ui.Column(win, padding=16, spacing=12)

ui.Label(root, "画布（Canvas）", font_size=20, weight="semibold")
ui.Label(
    root,
    "图元用 left / top 绝对定位；下面那个蓝色方块可以用鼠标拖动。",
    font_size=12,
    foreground="#8A8A8A",
)

# ---------------------------------------------------------------------------
# 画布
# ---------------------------------------------------------------------------
canvas = ui.Canvas(root, width=850, height=420)

# 背景网格：用折线画几条，顺便验证 Polyline
for x in range(0, 850, 50):
    ui.Line(canvas, x1=x, y1=0, x2=x, y2=420, stroke="#E8E8E8", stroke_width=1)
for y in range(0, 420, 50):
    ui.Line(canvas, x1=0, y1=y, x2=850, y2=y, stroke="#E8E8E8", stroke_width=1)

# 矩形：圆角 + 虚线描边
ui.Rect(
    canvas,
    left=40,
    top=40,
    width=160,
    height=100,
    radius_x=12,
    radius_y=12,
    fill="#E8F0FE",
    stroke="#0F6CBD",
    stroke_width=2,
)
ui.Label(canvas, "圆角矩形", left=60, top=150, font_size=12, foreground="#0F6CBD")

# 椭圆
ui.Ellipse(
    canvas,
    left=240,
    top=40,
    width=140,
    height=100,
    fill="#FFF4CE",
    stroke="#9D5D00",
    stroke_width=2,
)
ui.Label(canvas, "椭圆", left=290, top=150, font_size=12, foreground="#9D5D00")

# 折线（不闭合）
ui.Polyline(
    canvas,
    points=[[420, 140], [470, 60], [520, 140], [570, 60], [620, 140]],
    stroke="#C42B1C",
    stroke_width=2,
    line_join="round",
)
ui.Label(canvas, "折线 Polyline", left=470, top=150, font_size=12, foreground="#C42B1C")

# 多边形（闭合）
ui.Polygon(
    canvas,
    points=[[680, 70], [720, 30], [760, 70], [740, 130], [700, 130]],
    fill="#DFF6DD",
    stroke="#0F7B0F",
    stroke_width=2,
)
ui.Label(canvas, "多边形", left=690, top=150, font_size=12, foreground="#0F7B0F")

# 路径：一条二次贝塞尔波形
ui.Path(
    canvas,
    d="M 40 300 C 140 200, 240 400, 340 300 S 540 200, 640 300",
    stroke="#5C2E91",
    stroke_width=3,
    line_cap="round",
)
ui.Label(canvas, "贝塞尔路径", left=40, top=320, font_size=12, foreground="#5C2E91")

# 可拖动的方块：z_index 抬高，保证盖在网格线和文字之上
box = ui.Rect(
    canvas,
    left=700,
    top=260,
    width=90,
    height=90,
    radius_x=8,
    radius_y=8,
    fill="#0F6CBD",
    stroke="#0B4F92",
    stroke_width=2,
    z_index=100,
)

state_label = ui.Label(root, "拖动方块试试（按住并移动鼠标）", font_size=12)

# ---------------------------------------------------------------------------
# 拖动：按下记录偏移，移动只改 left / top
# ---------------------------------------------------------------------------
pos = {"x": 700.0, "y": 260.0}
drag = {"active": False, "dx": 0.0, "dy": 0.0}

# 拖动范围限制在画布内：画布不裁切，拖出去会被父容器的其他控件盖住，
# 看起来就像「图元跑到别的控件底下」。
CANVAS_W, CANVAS_H = 850, 420
BOX_W, BOX_H = 90, 90


def on_press(event) -> None:
    data = event.data
    drag["active"] = True
    drag["dx"] = data["x"] - pos["x"]
    drag["dy"] = data["y"] - pos["y"]
    state_label.text = f"按下 ({data['x']:.0f}, {data['y']:.0f})"


def on_move(event) -> None:
    if not drag["active"]:
        return
    data = event.data
    pos["x"] = max(0.0, min(CANVAS_W - BOX_W, data["x"] - drag["dx"]))
    pos["y"] = max(0.0, min(CANVAS_H - BOX_H, data["y"] - drag["dy"]))
    box.left = pos["x"]
    box.top = pos["y"]
    state_label.text = f"拖动中 ({data['x']:.0f}, {data['y']:.0f}) → left={pos['x']:.0f}, top={pos['y']:.0f}"


def on_release(event) -> None:
    drag["active"] = False
    state_label.text = f"放下 ({event.data['x']:.0f}, {event.data['y']:.0f})，left={pos['x']:.0f}, top={pos['y']:.0f}"


box.bind("pointerPressed", on_press)
box.bind("pointerMoved", on_move)
box.bind("pointerReleased", on_release)

# ---------------------------------------------------------------------------
# 视图变换：一条属性控制整个画布的缩放和平移
# ---------------------------------------------------------------------------
ui.Separator(root)
scale_row = ui.Row(root, spacing=12)
ui.Label(scale_row, "缩放：", font_size=12)

view_state = {"scale": 1.0}


def apply_scale(value: float) -> None:
    view_state["scale"] = value
    canvas.view = {"scale": value, "offsetX": 0, "offsetY": 0}


slider = ui.Slider(scale_row, minimum=0.3, maximum=2.5, value=1.0, width=320)
slider.bind("change", lambda e: apply_scale(float(e.data.get("value", 1.0))))

scale_label = ui.Label(scale_row, "1.00x", font_size=12, foreground="#0F6CBD")
slider.bind("change", lambda e: setattr(scale_label, "text", f"{float(e.data.get('value', 1.0)):.2f}x"))

ui.Label(
    root,
    "缩放改的是画布容器的 RenderTransform，一条 patch 生效，跟图元数量无关。",
    font_size=11,
    wrap=True,
    foreground="#8A8A8A",
)

win.mainloop()
