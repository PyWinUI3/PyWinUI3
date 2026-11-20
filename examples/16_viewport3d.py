"""D3D11 三维视口：真 GPU 管线，带深度缓冲和着色器。

运行：
    python examples/16_viewport3d.py

与 15_win2d.py 的区别在于「谁来算」：
- Win2D 路线每帧由 Python 投影 + 排序 + 下发多边形，面数上千就掉帧
- 这里 Python 只给一次网格和相机，渲染循环全在宿主里跑，
  面数上限几十万，拖动旋转时不产生任何跨进程流量
"""

import math

import pywinui3 as ui

win = ui.Window(title="D3D11 三维视口", width=920, height=720)
col = ui.Column(win, padding=16, spacing=10)

ui.Label(col, "Viewport3D（D3D11）", font_size=20, weight="semibold")
ui.Label(
    col,
    "拖动旋转视角。几何和相机只下发一次，旋转由宿主自己推进。",
    font_size=12,
    foreground="#8A8A8A",
)

# ---------------------------------------------------------------------------
# 一个二十面体：面数适中，能看清光照和遮挡
# ---------------------------------------------------------------------------
_t = (1 + math.sqrt(5)) / 2
_vertices = [
    [-1, _t, 0], [1, _t, 0], [-1, -_t, 0], [1, -_t, 0],
    [0, -1, _t], [0, 1, _t], [0, -1, -_t], [0, 1, -_t],
    [_t, 0, -1], [_t, 0, 1], [-_t, 0, -1], [-_t, 0, 1],
]
_faces = [
    (0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11),
    (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
    (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9),
    (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1),
]
_indices = [i for face in _faces for i in face]

camera = {"yaw": 30.0, "pitch": 18.0, "distance": 4.0, "autoRotate": 12.0}

viewport = ui.Viewport3D(
    col,
    width=780,
    height=460,
    mesh={"vertices": _vertices, "indices": _indices, "color": "#4FC3F7"},
    camera=dict(camera),
)

status = ui.Label(
    col,
    f"yaw={camera['yaw']:.0f}°  pitch={camera['pitch']:.0f}°  distance={camera['distance']:.1f}",
    font_size=12,
    foreground="#0F6CBD",
)

# ---------------------------------------------------------------------------
# 拖动旋转：只改 camera 属性，宿主每帧自己插值
# ---------------------------------------------------------------------------
drag = {"active": False, "x": 0.0, "y": 0.0}


def on_press(event) -> None:
    data = event.data
    drag["active"] = True
    drag["x"] = data["x"]
    drag["y"] = data["y"]


def on_move(event) -> None:
    if not drag["active"]:
        return

    data = event.data
    dx = data["x"] - drag["x"]
    dy = data["y"] - drag["y"]
    drag["x"] = data["x"]
    drag["y"] = data["y"]

    camera["yaw"] -= dx * 0.4
    camera["pitch"] = max(-85.0, min(85.0, camera["pitch"] + dy * 0.4))

    viewport.camera = dict(camera)
    status.text = (
        f"yaw={camera['yaw']:.0f}°  pitch={camera['pitch']:.0f}°  "
        f"distance={camera['distance']:.1f}"
    )


def on_release(event) -> None:
    drag["active"] = False


viewport.bind("pointerPressed", on_press)
viewport.bind("pointerMoved", on_move)
viewport.bind("pointerReleased", on_release)

# ---------------------------------------------------------------------------
# 缩放与自转
# ---------------------------------------------------------------------------
ui.Separator(col)

row = ui.Row(col, spacing=12)
ui.Label(row, "距离：", font_size=12)
distance_slider = ui.Slider(row, minimum=1.8, maximum=8.0, value=camera["distance"], width=240)


def on_distance(event) -> None:
    camera["distance"] = float(event.data.get("value", 4.0))
    viewport.camera = dict(camera)


distance_slider.bind("change", on_distance)

row2 = ui.Row(col, spacing=12)
ui.Label(row2, "自转：", font_size=12)
auto_slider = ui.Slider(row2, minimum=0, maximum=120, value=camera["autoRotate"], width=240)


def on_auto(event) -> None:
    camera["autoRotate"] = float(event.data.get("value", 0.0))
    viewport.camera = dict(camera)


auto_slider.bind("change", on_auto)

ui.Label(
    col,
    "顶点数 12、面数 20。把面数换成几万也一样跑——渲染在宿主里，Python 不参与。",
    font_size=11,
    wrap=True,
    foreground="#8A8A8A",
)

win.mainloop()
