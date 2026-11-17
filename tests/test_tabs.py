"""标签元素：固定选项卡（PinnedTab）与标签页（TabPage）。

设计前提值得记住：PinnedTab 的「不可移动、不可关闭」不是靠事件拦截，
而是宿主把它放进 TabView.TabStripHeader —— 它根本不在 TabItems 里，
WinUI 内置的拖动与关闭机制碰不到它。因此这里测的是结构事实，不是策略。
"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))

import pywinui3 as ui  # noqa: E402
from pywinui3.extended import __all__ as EXTENDED_NAMES  # noqa: E402


class FakeHost:
    def __init__(self) -> None:
        self.sent: list[dict] = []

    def send(self, message: dict) -> None:
        self.sent.append(message)


class TabTestCase(unittest.TestCase):
    def setUp(self) -> None:
        self.app = ui.App()
        self.host = FakeHost()
        self.app._host = self.host  # type: ignore[assignment]
        self.app._rendered = True
        self.win = ui.Window(self.app, title="tabs", width=800, height=600)
        self.tabs = ui.TabView(self.win)
        self.host.sent.clear()

    def tearDown(self) -> None:
        self.app._host = None
        self.app._timers.clear()

    def settle(self) -> None:
        """把建树时压下的 insert 先落地，避免它混进被测消息序列。"""
        self.app._flush_patches()
        self.host.sent.clear()


class TestExports(unittest.TestCase):
    def test_both_names_exported(self):
        for name in ("PinnedTab", "TabPage"):
            self.assertIn(name, EXTENDED_NAMES)
            self.assertTrue(hasattr(ui, name), f"{name} 没有从包顶层导出")

    def test_protocol_type_names(self):
        self.assertEqual(ui.PinnedTab._type, "PinnedTab")
        self.assertEqual(ui.TabPage._type, "TabPage")


class TestConstruction(TabTestCase):
    def test_pinned_tab_is_not_closable(self):
        pinned = ui.PinnedTab(self.tabs, "首页")
        self.assertFalse(
            hasattr(ui.PinnedTab, "close"),
            "PinnedTab 是标签页但没有关闭按钮，不应提供 close()",
        )
        self.assertEqual(pinned._props["text"], "首页")

    def test_pinned_tab_keeps_other_props(self):
        pinned = ui.PinnedTab(self.tabs, "首页", icon="")
        self.assertEqual(pinned._props["icon"], "")

    def test_tab_page_supports_icon(self):
        page = ui.TabPage(self.tabs, "文档", icon="")
        self.assertEqual(page._props["icon"], "")

    def test_tab_width_props_pass_through(self):
        page = ui.TabPage(self.tabs, "很长的标签标题", min_width=120, max_width=200)
        self.assertEqual(page._props["minWidth"], 120)
        self.assertEqual(page._props["maxWidth"], 200)

    def test_tab_page_defaults_to_closable(self):
        page = ui.TabPage(self.tabs, "文档 1")
        self.assertTrue(page._props["closable"])
        self.assertEqual(page._props["text"], "文档 1")

    def test_tab_page_can_disable_closable(self):
        page = ui.TabPage(self.tabs, "固定页", closable=False)
        self.assertFalse(page._props["closable"])

    def test_tab_view_reorder_default_and_override(self):
        self.assertTrue(self.tabs._props["canReorderTabs"])
        another = ui.TabView(self.win, can_reorder_tabs=False)
        self.assertFalse(another._props["canReorderTabs"])

    def test_tab_width_mode_maps_to_camel_case(self):
        sized = ui.TabView(self.win, tab_width_mode="sizeToContent")
        self.assertEqual(sized._props["tabWidthMode"], "sizeToContent")

    def test_children_keep_insertion_order(self):
        pinned = ui.PinnedTab(self.tabs, "首页")
        page = ui.TabPage(self.tabs, "文档")
        self.assertEqual(self.tabs._children, [pinned, page])

    def test_helpers_return_children(self):
        page = self.tabs.add_page("A")
        pinned = self.tabs.add_pinned("P")
        self.assertIsInstance(page, ui.TabPage)
        self.assertIsInstance(pinned, ui.PinnedTab)
        self.assertEqual(self.tabs._children, [page, pinned])


class TestActions(TabTestCase):
    def test_select_sends_invoke(self):
        page = ui.TabPage(self.tabs, "A")
        self.settle()
        page.select()
        last = self.host.sent[-1]
        self.assertEqual(last["op"], "invoke")
        self.assertEqual(last["id"], page._id)
        self.assertEqual(last["method"], "select_tab")

    def test_pinned_select_sends_invoke(self):
        pinned = self.tabs.add_pinned("首页")
        self.settle()
        pinned.select()
        self.assertEqual(self.host.sent[-1]["method"], "select_tab")
        self.assertEqual(self.host.sent[-1]["id"], pinned._id)

    def test_close_removes_node(self):
        page = ui.TabPage(self.tabs, "A")
        self.settle()
        page.close()
        self.app._flush_patches()

        removes = [
            op
            for message in self.host.sent
            for op in message.get("ops", [])
            if op.get("t") == "remove"
        ]
        self.assertEqual(removes, [{"t": "remove", "id": page._id}])
        self.assertNotIn(page._id, self.app._nodes)
        self.assertNotIn(page, self.tabs._children)

    def test_close_is_idempotent(self):
        page = ui.TabPage(self.tabs, "A")
        self.settle()
        page.close()
        self.settle()
        page.close()
        self.app._flush_patches()
        self.assertEqual(self.host.sent, [])


class TestCloseRequested(TabTestCase):
    def _close_request(self, page: ui.TabPage) -> None:
        self.app._dispatch(
            {
                "t": "event",
                "id": self.tabs._id,
                "event": "tabCloseRequested",
                "data": {"value": page._props["text"], "nodeId": page._id},
            }
        )

    def test_close_request_destroys_matching_page(self):
        page = ui.TabPage(self.tabs, "A")
        self._close_request(page)
        self.assertNotIn(page._id, self.app._nodes)
        self.assertNotIn(page, self.tabs._children)

    def test_close_request_falls_back_to_text(self):
        page = ui.TabPage(self.tabs, "只给文字")
        self.app._dispatch(
            {
                "t": "event",
                "id": self.tabs._id,
                "event": "tabCloseRequested",
                "data": {"value": "只给文字"},
            }
        )
        self.assertNotIn(page._id, self.app._nodes)

    def test_close_request_keeps_pinned_tab(self):
        pinned = self.tabs.add_pinned("首页")
        page = ui.TabPage(self.tabs, "A")
        self._close_request(page)
        self.assertIn(pinned._id, self.app._nodes)
        self.assertIn(pinned, self.tabs._children)

    def test_user_handler_still_runs(self):
        page = ui.TabPage(self.tabs, "A")
        seen: list[str] = []
        self.tabs.bind("tabCloseRequested", lambda event: seen.append(str(event.value)))
        self._close_request(page)
        self.assertEqual(seen, ["A"])


class TestRenderSpec(TabTestCase):
    def _specs(self) -> dict[str, dict]:
        out: list[dict] = []
        self.win._collect_specs(out)
        return {spec["id"]: spec for spec in out}

    def test_tab_pages_render_as_children(self):
        pinned = self.tabs.add_pinned("首页")
        page = ui.TabPage(self.tabs, "A")
        ui.Label(page, "内容")  # TabPage 能直接放子控件

        specs = self._specs()
        tabs_spec = specs[self.tabs._id]
        self.assertEqual(tabs_spec["children"], [pinned._id, page._id])
        self.assertEqual(specs[page._id]["type"], "TabPage")
        self.assertEqual(len(specs[page._id]["children"]), 1)

    def test_pinned_tab_can_hold_children(self):
        pinned = self.tabs.add_pinned("首页")
        label = ui.Label(pinned, "固定页内容")
        specs = self._specs()
        self.assertEqual(specs[pinned._id]["children"], [label._id])
        self.assertEqual(specs[label._id]["type"], "Label")


if __name__ == "__main__":
    unittest.main()
