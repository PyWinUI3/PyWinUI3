"""用真实鼠标拖拽验证：固定选项卡拖不动，普通标签页能重排。

会短暂打开一个 860x260 的可见窗口（约 6 秒），因为拖拽必须命中真实屏幕坐标。
适合手动跑，不进 CI。

    python tests/drag_pinned.py
"""

from __future__ import annotations

import ctypes
import sys
import time
from ctypes import wintypes
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "python"))

import pywinui3 as ui  # noqa: E402

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:  # noqa: BLE001
    ctypes.windll.user32.SetProcessDPIAware()

user32 = ctypes.WinDLL("user32", use_last_error=True)
user32.SetCursorPos.argtypes = [ctypes.c_int, ctypes.c_int]
user32.ClientToScreen.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.POINT)]
user32.GetClientRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
user32.GetDpiForWindow.argtypes = [wintypes.HWND]
user32.GetDpiForWindow.restype = wintypes.UINT
user32.SetWindowPos.argtypes = [
    wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int,
    ctypes.c_int, ctypes.c_int, wintypes.UINT,
]
user32.SetForegroundWindow.argtypes = [wintypes.HWND]

LEFTDOWN, LEFTUP = 0x0002, 0x0004
MOVE, ABSOLUTE = 0x0001, 0x8000


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [
        ("dx", wintypes.LONG),
        ("dy", wintypes.LONG),
        ("mouseData", wintypes.DWORD),
        ("dwFlags", wintypes.DWORD),
        ("time", wintypes.DWORD),
        ("dwExtraInfo", ctypes.c_size_t),
    ]


class INPUT(ctypes.Structure):
    _fields_ = [("type", wintypes.DWORD), ("mi", MOUSEINPUT)]


user32.SendInput.argtypes = [wintypes.UINT, ctypes.POINTER(INPUT), ctypes.c_int]
user32.GetSystemMetrics.argtypes = [ctypes.c_int]


def _absolute(x: int, y: int) -> tuple[int, int]:
    vx = user32.GetSystemMetrics(76)
    vy = user32.GetSystemMetrics(77)
    vw = user32.GetSystemMetrics(78) or 1920
    vh = user32.GetSystemMetrics(79) or 1080
    return int((x - vx) * 65535 / vw), int((y - vy) * 65535 / vh)


def _send(flags: int, x: int, y: int | None = None) -> None:
    ax, ay = _absolute(x, y) if y is not None else (0, 0)
    packet = INPUT(type=0, mi=MOUSEINPUT(
        dx=ax, dy=ay, mouseData=0, dwFlags=flags, time=0, dwExtraInfo=0))
    user32.SendInput(1, ctypes.byref(packet), ctypes.sizeof(INPUT))
FAILURES: list[str] = []


def check(name: str, ok: bool, detail: str = "") -> None:
    print(f"  {'PASS' if ok else 'FAIL'}  {name}" + (f"  {detail}" if detail else ""))
    if not ok:
        FAILURES.append(name)


def find_window(pid: int) -> int:
    found: list[tuple[int, int]] = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def callback(hwnd, _):
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value != pid:
            return True
        rect = wintypes.RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(rect))
        area = (rect.right - rect.left) * (rect.bottom - rect.top)
        if area > 200 * 200:
            found.append((area, hwnd))
        return True

    user32.EnumWindows(callback, 0)
    found.sort(reverse=True)
    return found[0][1] if found else 0


def client_origin(hwnd: int) -> tuple[int, int, float]:
    point = wintypes.POINT(0, 0)
    user32.ClientToScreen(hwnd, ctypes.byref(point))
    scale = user32.GetDpiForWindow(hwnd) / 96.0
    return point.x, point.y, scale


def drag(x1: int, y1: int, x2: int, y2: int, steps: int = 20, on_half=None) -> None:
    _send(MOVE | ABSOLUTE, x1, y1)
    time.sleep(0.2)
    _send(LEFTDOWN | ABSOLUTE, x1, y1)
    time.sleep(0.25)
    for i in range(1, steps + 1):
        _send(MOVE | ABSOLUTE,
              int(x1 + (x2 - x1) * i / steps),
              int(y1 + (y2 - y1) * i / steps))
        time.sleep(0.04)
        if on_half is not None and i == steps // 2:
            on_half()
    time.sleep(0.25)
    _send(LEFTUP | ABSOLUTE, x2, y2)
    time.sleep(0.8)


app = ui.App()
win = ui.Window(title="tab-drag-test", width=860, height=260, x=100, y=100,
                center=False, background="#F3F3F3")
tabs = ui.TabView(win, height=200, can_reorder_tabs=True, tab_width_mode="sizeToContent")

p1 = tabs.add_pinned("固定甲", icon="\uE80F")
p2 = tabs.add_pinned("固定乙", icon="\uE897")
page_a = tabs.add_page("页面甲", icon="\uE8A5")
page_b = tabs.add_page("页面乙", icon="\uE8B7")

state: dict[str, object] = {}


def points(rects: list[dict], text: str) -> tuple[float, float, float]:
    for rect in rects:
        if rect["text"] == text:
            return rect["x"] + rect["w"] / 2, rect["y"] + rect["h"] / 2, rect["w"]
    raise KeyError(text)


def run_tests() -> None:
    try:
        pid = app._host.pid
        hwnd = 0
        for _ in range(20):
            hwnd = find_window(pid)
            if hwnd:
                break
            time.sleep(0.2)

        # 拖拽必须落到最前面的窗口上，否则鼠标打的是别的程序
        user32.SetWindowPos(hwnd, wintypes.HWND(-1), 0, 0, 0, 0, 0x0002 | 0x0001)
        user32.SetForegroundWindow(hwnd)
        time.sleep(1.0)
        fg = user32.GetForegroundWindow()
        print("  前台窗口:", fg, "目标:", hwnd, "一致" if fg == hwnd else "不一致")
        ox, oy, scale = client_origin(hwnd)
        print(f"  客户区原点=({ox},{oy}) 缩放={scale}")

        before = app.query(tabs._id, "tabOrder")
        state["before"] = before
        print(f"  初始顺序: {before}")

        rects = app.query(tabs._id, "tabRects") or []
        for rect in rects:
            print("  rect", rect)

        def screen(text: str) -> tuple[int, int]:
            x, y, _ = points(rects, text)
            return int(ox + x * scale), int(oy + y * scale)

        # 先验证输入注入本身有效：点一下“页面甲”，选中项应变成它
        cx, cy = screen("页面甲")
        _send(MOVE | ABSOLUTE, cx, cy)
        time.sleep(0.2)
        _send(LEFTDOWN | ABSOLUTE, cx, cy)
        time.sleep(0.1)
        _send(LEFTUP | ABSOLUTE, cx, cy)
        time.sleep(0.5)
        print("  点击后 selectedTab:", app.query(tabs._id, "selectedTab"))

        # 1) 拖固定标签到页面区域：应当完全不动
        sx, sy = screen("固定甲")
        tx, ty = screen("页面乙")
        mid: dict[str, object] = {}

        def during() -> None:
            mid["order"] = app.query(tabs._id, "tabOrder")

        drag(sx, sy, tx, ty, on_half=during)
        check("拖动过程中固定标签就没动", mid.get("order") == before, str(mid.get("order")))
        after_pinned = app.query(tabs._id, "tabOrder")
        state["pinned"] = after_pinned
        check("固定标签拖进页面区后顺序不变", after_pinned == before, f"{after_pinned}")

        # 2) 固定标签之间互相拖：也不应改变顺序
        sx, sy = screen("固定乙")
        tx, ty = screen("固定甲")
        drag(sx, sy, max(0, tx - 30), ty)
        after_pinned2 = app.query(tabs._id, "tabOrder")
        state["pinned2"] = after_pinned2
        check("固定标签之间也不能重排", after_pinned2 == before, f"{after_pinned2}")

        # 3) 正向对照：普通标签拖到最前，固定块应归位、页面顺序变化
        sx, sy = screen("页面乙")
        tx, ty = screen("页面甲")
        drag(sx, sy, max(0, tx - 30), ty)
        after_pages = app.query(tabs._id, "tabOrder")
        state["pages"] = after_pages
        check("普通标签仍可重排", after_pages != before, f"{after_pages}")
        check(
            "固定标签仍在最前",
            after_pages.startswith("固定甲,固定乙"),
            f"{after_pages}",
        )
    except Exception as exc:  # noqa: BLE001
        state["error"] = exc
    finally:
        win.quit()


app.after(2200, run_tests)
app.after(20000, win.quit)
win.mainloop()

print("状态:", {k: v for k, v in state.items() if k != "error"})
if "error" in state:
    raise SystemExit(f"测试异常: {state['error']}")
if FAILURES:
    raise SystemExit(f"FAILED: {FAILURES}")
print("ALL PASSED")
