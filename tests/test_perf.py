"""性能回归护栏。

这些断言不测「功能对不对」，而是给热路径钉钉子 —— 它们存在的意义是：
谁把 16ms 合并窗口去掉、把动画改回逐帧 patch、或者在每条消息上重新
加一次同步磁盘写，CI 就立刻变红。

最后两条读 C# 源码做静态断言。宿主没有可跑的 C# 单测，这是目前唯一能
把「别再加回来」变成自动失败的办法，宁可粗糙也不要没有。
"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "python"))

import pywinui3 as ui  # noqa: E402
from pywinui3 import app as app_module  # noqa: E402

HOST_DIR = ROOT / "host" / "PyWinUI.Host"


class FakeHost:
    def __init__(self) -> None:
        self.sent: list[dict] = []

    def send(self, message: dict) -> None:
        self.sent.append(message)


class PerfTestCase(unittest.TestCase):
    def setUp(self) -> None:
        self.app = ui.App()
        self.host = FakeHost()
        self.app._host = self.host  # type: ignore[assignment]
        self.app._rendered = True
        self.win = ui.Window(self.app, title="perf", width=800, height=600)
        self.host.sent.clear()

    def tearDown(self) -> None:
        self.app._host = None
        self.app._timers.clear()

    def settle(self) -> None:
        """把建树时压下的 insert 先落到假宿主并清空记录。

        否则后面断言「一条 patch」时，队列里还躺着窗口/控件自己的 insert。
        """
        self.app._flush_patches()
        self.host.sent.clear()


class TestMergeWindow(PerfTestCase):
    def test_window_is_at_most_one_frame(self):
        self.assertGreater(app_module.MERGE_WINDOW_MS, 0)
        self.assertLessEqual(app_module.MERGE_WINDOW_MS, 16)

    def test_writes_inside_window_are_not_sent_immediately(self):
        label = ui.Label(self.win, "0")
        self.settle()
        for i in range(100):
            label.text = str(i)
        self.assertEqual(self.host.sent, [], "合并窗口内的赋值不应立刻发消息")

    def test_only_one_flush_timer_for_many_writes(self):
        label = ui.Label(self.win, "0")
        for i in range(100):
            label.text = str(i)
        self.assertEqual(len(self.app._timers), 1, "100 次赋值只应挂一个 flush 定时器")

    def test_live_ui_one_second_message_count(self):
        """复刻 examples/03_live.py 的 tick()：20 帧 × 5 次赋值。

        旧实现是 100 条 patch；合并后每帧一条，共 20 条。
        """
        clock = ui.Label(self.win, "12:00:00")
        percent = ui.Label(self.win, "0%")
        progress = ui.ProgressBar(self.win, value=0)
        swatch = ui.Button(self.win, "s")
        self.settle()

        for i in range(20):
            clock.text = f"12:00:{i:02d}"
            clock.foreground = "#0F6E56"
            progress.value = i * 2
            percent.text = f"{i * 2}%"
            swatch.background = "#3C3489"
            self.app._flush_patches()  # 一帧结束，窗口到期

        self.assertEqual(len(self.host.sent), 20, "每帧应只发一条 patch")
        self.assertTrue(all(msg["op"] == "patch" for msg in self.host.sent))
        self.assertTrue(
            all(len(msg["ops"]) <= 4 for msg in self.host.sent),
            "4 个节点各自合并成一个 set，一帧不该超过 4 个 op",
        )

    def test_same_node_props_collapse_into_one_op(self):
        label = ui.Label(self.win, "a")
        self.settle()
        label.text = "b"
        label.font_size = 18
        label.foreground = "#123456"
        self.app._flush_patches()
        self.assertEqual(len(self.host.sent), 1)
        self.assertEqual(len(self.host.sent[0]["ops"]), 1)
        self.assertEqual(
            self.host.sent[0]["ops"][0]["props"],
            {"text": "b", "fontSize": 18, "foreground": "#123456"},
        )

    def test_flush_releases_pending_state(self):
        label = ui.Label(self.win, "a")
        self.settle()
        label.text = "b"
        self.app._flush_patches()
        self.assertEqual(self.app._pending, [])
        self.assertEqual(self.app._pending_index, {})
        self.assertIsNone(self.app._flush_token)

        label.text = "c"
        self.app._flush_patches()
        self.assertEqual(len(self.host.sent), 2, "flush 之后的新写入必须另起一条 patch")


class TestAnimationCost(PerfTestCase):
    def test_animate_width_is_a_single_message(self):
        panel = ui.Column(self.win, width=200)
        self.settle()
        panel.animate_width(56)
        self.app._flush_patches()

        self.assertEqual(len(self.host.sent), 1, "整段补间只应发一条消息")
        message = self.host.sent[0]
        self.assertEqual(message["op"], "micro")
        self.assertEqual(message["node"], panel._id)
        self.assertEqual(message["program"][0]["i"], "animate")
        self.assertEqual(message["program"][0]["prop"], "width")
        self.assertEqual(message["program"][0]["to"], 56.0)

    def test_animate_width_does_not_queue_a_patch(self):
        panel = ui.Column(self.win, width=200)
        self.host.sent.clear()
        panel.animate_width(56)
        self.assertEqual(self.app._pending, [], "动画不应在合并窗口里压一条 width patch")
        self.host.sent.clear()
        self.app._flush_patches()
        self.assertEqual(self.host.sent, [])


@unittest.skipUnless((HOST_DIR / "Session.cs").is_file(), "没有宿源码（已安装 wheel）")
class TestHostHotPath(unittest.TestCase):
    """静态守卫：逐条报文必须走 Trace，patch 不得再逐条 ack。"""

    def setUp(self) -> None:
        self.session = (HOST_DIR / "Session.cs").read_text(encoding="utf-8")

    def test_per_message_logging_uses_trace(self):
        self.assertIn(
            'HostLog.Trace("<< "',
            self.session,
            "入站报文必须走 HostLog.Trace，否则每条消息都会同步落盘",
        )
        self.assertIn(
            'HostLog.Trace(">> "',
            self.session,
            "出站报文必须走 HostLog.Trace，否则每条消息都会同步落盘",
        )
        self.assertNotIn(
            'HostLog.Write("<< "',
            self.session,
            "入站报文又回到 HostLog.Write 了",
        )
        self.assertNotIn(
            'HostLog.Write(">> "',
            self.session,
            "出站报文又回到 HostLog.Write 了",
        )

    def test_patch_does_not_ack_every_message(self):
        self.assertNotIn(
            "Outbound.Ack(patch.Rev)",
            self.session,
            "patch 又逐条回 ack 了：Python 侧根本不读，纯浪费一半出站流量",
        )


if __name__ == "__main__":
    unittest.main()
