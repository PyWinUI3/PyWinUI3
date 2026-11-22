"""控件层单元测试：定时器取消、折叠竞态、动画代次、补丁生成。

这些测试不需要宿主进程：用一个只记录 send() 的假 HostProcess 替代真实管道，
因此可以在任何机器上 2ms 跑完。
"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))

import pywinui3 as ui  # noqa: E402
from pywinui3.event import Event  # noqa: E402


class FakeHost:
    """只记录发出的消息，不启动进程。"""

    def __init__(self) -> None:
        self.sent: list[dict] = []

    def send(self, message: dict) -> None:
        self.sent.append(message)


class WidgetTestCase(unittest.TestCase):
    def setUp(self) -> None:
        self.app = ui.App()
        self.host = FakeHost()
        self.app._host = self.host  # type: ignore[assignment]
        self.app._rendered = True
        self.win = ui.Window(self.app, title="test", width=400, height=300)

    def tearDown(self) -> None:
        self.app._host = None
        self.app._timers.clear()


class TestAfterCancel(WidgetTestCase):
    def test_after_returns_token_and_runs(self):
        hits: list[str] = []
        token = self.app.after(0, lambda: hits.append("a"))
        self.assertIsInstance(token, str)
        self.app._run_timers()
        self.assertEqual(hits, ["a"])

    def test_after_cancel_prevents_callback(self):
        hits: list[str] = []
        token = self.app.after(1000, lambda: hits.append("x"))
        self.app.after_cancel(token)
        # 把 deadline 提前，确认不是「还没到时间」而是真的取消了
        self.assertNotIn(token, self.app._timers)
        self.app._run_timers()
        self.assertEqual(hits, [])

    def test_cancel_unknown_token_is_silent(self):
        self.app.after_cancel("nope")  # 不应抛异常

    def test_one_shot_only(self):
        hits: list[str] = []
        self.app.after(0, lambda: hits.append("once"))
        self.app._run_timers()
        self.app._run_timers()
        self.assertEqual(hits, ["once"])

    def test_callback_can_schedule_another(self):
        hits: list[str] = []

        def first() -> None:
            hits.append("first")
            self.app.after(0, lambda: hits.append("second"))

        self.app.after(0, first)
        self.app._run_timers()
        self.app._run_timers()
        self.assertEqual(hits, ["first", "second"])


class TestAnimateWidth(WidgetTestCase):
    def test_new_animation_supersedes_old(self):
        label = ui.Label(self.win, "x", width=10)
        label.animate_width(100, steps=3, interval=0)
        label.animate_width(200, steps=3, interval=0)

        # 手动把两条链的定时器都跑完（旧链应该自行退出）
        for _ in range(10):
            self.app._run_timers()

        self.assertEqual(float(label.width), 200.0)

    def test_snap_path_stops_previous_chain(self):
        label = ui.Label(self.win, "x", width=10)
        label.animate_width(1000, steps=5, interval=0)
        label.animate_width(50, steps=5, interval=0)
        for _ in range(10):
            self.app._run_timers()
        self.assertEqual(float(label.width), 50.0)


class TestListBoxAutoCollapse(WidgetTestCase):
    """折叠逻辑在宿主侧，Python 只负责下发参数与转发命令。"""

    def make_nav(self, **kwargs):
        return ui.ListBox(
            self.win,
            [{"text": "概览", "glyph": ""}, {"text": "设置", "glyph": ""}],
            width=200,
            auto_collapse=True,
            collapsed_width=56,
            **kwargs,
        )

    def invokes(self):
        return [m for m in self.host.sent if m.get("op") == "invoke"]

    def test_params_are_sent_to_host(self):
        nav = self.make_nav()
        props = nav._props
        self.assertTrue(props["autoCollapse"])
        self.assertEqual(props["collapsedWidth"], 56)
        self.assertEqual(props["expandedWidth"], 200)
        self.assertTrue(props["hoverExpand"])
        self.assertTrue(props["pinOnSelect"])
        self.assertFalse(props["open"])

    def test_default_collapsed_width_is_40(self):
        nav = ui.ListBox(self.win, ["a"], width=200, auto_collapse=True)
        self.assertEqual(nav._props["collapsedWidth"], 40)

    def test_initial_collapsed_uses_narrow_width(self):
        nav = self.make_nav()
        self.assertEqual(nav._props["width"], 56)
        self.assertFalse(nav.is_open)

    def test_expanded_init_keeps_width(self):
        nav = self.make_nav(expanded=True)
        self.assertEqual(nav._props["width"], 200)
        self.assertTrue(nav.is_open)

    def test_set_open_invokes_host(self):
        nav = self.make_nav()
        self.host.sent.clear()
        nav.set_open(True)
        self.assertTrue(nav.is_open)
        inv = self.invokes()
        self.assertEqual(len(inv), 1)
        self.assertEqual(inv[0]["method"], "set_open")
        self.assertEqual(inv[0]["args"], {"open": True})

    def test_release_invokes_host_and_clears_pin(self):
        nav = self.make_nav(expanded=True)
        nav._pinned = True
        self.host.sent.clear()
        nav.release()
        self.assertFalse(nav.is_open)
        self.assertFalse(nav.is_pinned)
        self.assertEqual(self.invokes()[0]["method"], "release")

    def test_open_changed_event_syncs_state(self):
        nav = self.make_nav()
        nav._sync_from_event(
            Event(widget=nav, name="openChanged", data={"open": True, "pinned": True}, echo=False)
        )
        self.assertTrue(nav.is_open)
        self.assertTrue(nav.is_pinned)

    def test_no_width_raises_on_open(self):
        nav = ui.ListBox(self.win, ["a"], auto_collapse=True, collapsed_width=56)
        with self.assertRaises(ValueError):
            nav.set_open(True)

    def test_hover_expand_false_is_forwarded(self):
        nav = self.make_nav(hover_expand=False)
        self.assertFalse(nav._props["hoverExpand"])

    def test_plain_listbox_has_no_auto_collapse(self):
        nav = ui.ListBox(self.win, ["a"])
        self.assertFalse(nav._props["autoCollapse"])
        nav.set_open(True)  # 空操作
        self.assertEqual(self.invokes(), [])


class TestPatchGeneration(WidgetTestCase):
    def settle(self) -> None:
        """把「创建控件」产生的 insert 落地并清空记录，隔离出被测操作。

        合并窗口开启后，只 clear 假 host 的 sent 是不够的：insert 还压在
        pending 里，下一次 flush 会跟着被测操作一起冒出来。
        """
        self.app._flush_patches()
        self.host.sent.clear()

    def flush(self) -> list[dict]:
        """合并窗口是异步的，断言消息形状前先手动落地。"""
        self.app._flush_patches()
        return self.host.sent

    def test_configure_emits_single_set_patch(self):
        label = ui.Label(self.win, "hi")
        self.settle()
        label.configure(text="bye", font_size=18)
        sent = self.flush()
        self.assertEqual(len(sent), 1)
        patch = sent[0]
        self.assertEqual(patch["op"], "patch")
        self.assertEqual(patch["ops"][0]["props"], {"text": "bye", "fontSize": 18})

    def test_bind_emits_events_map(self):
        entry = ui.Entry(self.win)
        self.settle()
        entry.bind("change", lambda e: None)
        ops = self.flush()[-1]["ops"]
        self.assertEqual(ops[0]["id"], entry._id)
        self.assertEqual(len(ops[0]["events"]), 1)

    def test_unbind_emits_empty_events_map(self):
        entry = ui.Entry(self.win)
        entry.bind("change", lambda e: None)
        self.settle()
        entry.unbind("change")
        ops = self.flush()[-1]["ops"]
        self.assertEqual(ops[0]["events"], {})

    def test_same_frame_writes_merge_into_one_patch(self):
        label = ui.Label(self.win, "hi")
        self.settle()
        label.text = "bye"          # 三次赋值落在同一个 16ms 窗口
        label.font_size = 18
        label.text = "final"
        sent = self.flush()
        self.assertEqual(len(sent), 1, "同帧多次赋值必须只发一条 patch")
        self.assertEqual(len(sent[0]["ops"]), 1)
        self.assertEqual(sent[0]["ops"][0]["props"], {"text": "final", "fontSize": 18})

    def test_structural_op_blocks_merge_across_it(self):
        label = ui.Label(self.win, "hi")
        self.settle()
        label.text = "before"
        label.destroy()             # remove 会清空合并索引
        label.text = "after"
        sent = self.flush()
        kinds = [op["t"] for op in sent[0]["ops"]]
        self.assertEqual(kinds, ["set", "remove", "set"])

    def test_invoke_flushes_pending_patch_first(self):
        label = ui.Label(self.win, "hi")
        self.settle()
        label.text = "x"
        self.app._send_invoke(label._id, "focus")
        self.assertEqual([m["op"] for m in self.host.sent], ["patch", "invoke"])


class TestDestroyCleanup(WidgetTestCase):
    """回归：destroy 曾只发 remove，不注销 handler、不解绑变量。"""

    def settle(self) -> None:
        self.app._flush_patches()
        self.host.sent.clear()

    def test_destroy_unregisters_handlers(self):
        btn = ui.Button(self.win, "x")
        btn.bind("click", lambda e: None)
        btn.bind("pointerEntered", lambda e: None)
        ids = list(btn._handler_ids.values())
        self.assertTrue(all(h in self.app._handlers for h in ids))

        btn.destroy()

        self.assertFalse(any(h in self.app._handlers for h in ids))
        self.assertEqual(btn._handler_ids, {})
        self.assertEqual(btn._events, {})

    def test_destroy_detaches_variable_and_stops_patches(self):
        var = ui.StringVar("a")
        entry = ui.Entry(self.win, variable=var)
        self.assertIn(entry, var._widgets)

        entry.destroy()
        self.assertNotIn(entry, var._widgets)

        self.settle()
        before = len(self.host.sent)
        var.set("b")  # 不应再向已移除节点发 patch
        self.settle()
        self.assertEqual(len(self.host.sent), before)

    def test_destroy_recurses_to_children(self):
        row = ui.Row(self.win)
        child = ui.Button(row, "x")
        child.bind("click", lambda e: None)
        handler_ids = list(child._handler_ids.values())

        row.destroy()

        self.assertNotIn(child._id, self.app._nodes)
        self.assertFalse(any(h in self.app._handlers for h in handler_ids))


if __name__ == "__main__":
    unittest.main()
