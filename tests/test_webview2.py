"""WebView2 新增能力的 Python 侧形状测试（纯内存，不需要运行时）。"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))

import pywinui3 as ui  # noqa: E402


class FakeHost:
    def __init__(self) -> None:
        self.sent: list[dict] = []

    def send(self, message: dict) -> None:
        self.sent.append(message)


class WebViewTestCase(unittest.TestCase):
    def setUp(self) -> None:
        self.app = ui.App()
        self.host = FakeHost()
        self.app._host = self.host  # type: ignore[assignment]
        self.app._rendered = True
        self.win = ui.Window(self.app, title="webview", width=800, height=600)
        self.web = ui.WebView2(self.win, source="https://example.com")
        self.app._flush_patches()
        self.host.sent.clear()

    def tearDown(self) -> None:
        self.app._host = None
        self.app._timers.clear()

    def last_invoke(self) -> tuple[str, dict]:
        messages = [m for m in self.host.sent if m.get("op") == "invoke"]
        self.assertTrue(messages, "没有发出 invoke")
        return messages[-1]["method"], messages[-1].get("args", {})


class TestMessaging(WebViewTestCase):
    def test_post_message_shape(self):
        self.web.post_message("hello")
        method, args = self.last_invoke()
        self.assertEqual(method, "post_web_message")
        self.assertEqual(args, {"message": "hello"})

    def test_on_message_binds_web_message(self):
        token = self.web.on_message(lambda _e: None)
        self.assertIsInstance(token, str)
        self.app._flush_patches()
        payloads = [m for m in self.host.sent if m.get("op") == "patch"]
        self.assertTrue(
            any(
                "webMessage" in op.get("events", {})
                for m in payloads
                for op in m.get("ops", [])
            ),
            "webMessage 没有出现在事件订阅里",
        )

    def test_execute_script_returns_request_id(self):
        rid = self.web.execute_script("1+1")
        method, args = self.last_invoke()
        self.assertEqual(method, "execute_script")
        self.assertEqual(args["requestId"], rid)

    def test_execute_script_accepts_custom_id(self):
        self.web.execute_script("1+1", request_id="mine")
        self.assertEqual(self.last_invoke()[1]["requestId"], "mine")


class TestLifecycleEvents(WebViewTestCase):
    def test_event_helpers_bind_expected_names(self):
        expected = {
            self.web.on_content_loading: "contentLoading",
            self.web.on_dom_content_loaded: "domContentLoaded",
            self.web.on_process_failed: "processFailed",
            self.web.on_new_window_request: "newWindowRequested",
            self.web.on_download_starting: "downloadStarting",
            self.web.on_download_progress: "downloadProgress",
            self.web.on_download_state_changed: "downloadStateChanged",
            self.web.on_script_result: "scriptResult",
        }
        for fn, name in expected.items():
            fn(lambda _e: None)
        self.app._flush_patches()
        events = {}
        for message in self.host.sent:
            for op in message.get("ops", []):
                events.update(op.get("events", {}))
        for name in expected.values():
            self.assertIn(name, events)


class TestReplies(WebViewTestCase):
    def test_respond_new_window(self):
        self.web.respond_new_window("w1", True, "https://example.org")
        method, args = self.last_invoke()
        self.assertEqual(method, "respond_new_window")
        self.assertEqual(args["requestId"], "w1")
        self.assertTrue(args["allow"])
        self.assertEqual(args["url"], "https://example.org")

    def test_respond_new_window_deny_omits_url(self):
        self.web.respond_new_window("w2", False)
        self.assertEqual(
            self.last_invoke()[1], {"requestId": "w2", "allow": False}
        )

    def test_respond_download(self):
        self.web.respond_download("d1", True, r"C:\tmp\a.bin")
        method, args = self.last_invoke()
        self.assertEqual(method, "respond_download")
        self.assertEqual(args["downloadId"], "d1")
        self.assertEqual(args["filePath"], r"C:\tmp\a.bin")

    def test_download_actions(self):
        for method_name, action in (
            ("cancel_download", "cancel"),
            ("pause_download", "pause"),
            ("resume_download", "resume"),
        ):
            self.host.sent.clear()
            getattr(self.web, method_name)("d1")
            method, args = self.last_invoke()
            self.assertEqual(method, "download_action")
            self.assertEqual(args, {"downloadId": "d1", "action": action})

    def test_bad_download_action_rejected(self):
        with self.assertRaises(ValueError):
            self.web.download_action("d1", "delete")

    def test_open_devtools(self):
        self.web.open_devtools()
        self.assertEqual(self.last_invoke()[0], "open_devtools")


class TestSettings(unittest.TestCase):
    def test_snake_case_settings_map_to_protocol_keys(self):
        app = ui.App()
        app._host = FakeHost()  # type: ignore[assignment]
        app._rendered = True
        win = ui.Window(app, title="webview", width=400, height=300)
        web = ui.WebView2(
            win,
            muted=True,
            user_agent="PyWinUI3/1.0",
            script_enabled=False,
            context_menus_enabled=False,
            web_message_enabled=True,
            zoom_control_enabled=False,
        )
        self.assertEqual(web._props["isMuted"], True)
        self.assertEqual(web._props["userAgent"], "PyWinUI3/1.0")
        self.assertFalse(web._props["isScriptEnabled"])
        self.assertFalse(web._props["areDefaultContextMenusEnabled"])
        self.assertTrue(web._props["isWebMessageEnabled"])
        self.assertFalse(web._props["isZoomControlEnabled"])
        app._host = None
        app._timers.clear()


if __name__ == "__main__":
    unittest.main()
