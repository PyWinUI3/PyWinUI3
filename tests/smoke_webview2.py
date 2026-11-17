"""WebView2 新能力真机冒烟：消息、加载事件、脚本配对、新窗口回执、下载生命周期。

需要系统安装 WebView2 Runtime。会短暂开一个离屏窗口，并起一个本地 HTTP
服务提供下载文件。

    python tests/smoke_webview2.py
"""

from __future__ import annotations

import http.server
import sys
import threading
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from smoke_render import Host, default_exe  # noqa: E402

FAILURES: list[str] = []
SEEN: list[dict] = []

HTML = """<!doctype html><html><head><meta charset="utf-8"><title>probe</title></head>
<body><h1>probe</h1><script>
window.chrome.webview.addEventListener('message', function (e) {
  window.chrome.webview.postMessage('pong:' + e.data);
});
window.chrome.webview.postMessage('hello');
</script></body></html>"""

PAYLOAD = b"pywinui3-download-probe" * 4096  # ~100KB


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self) -> None:  # noqa: N802
        if self.path.startswith("/file.bin"):
            self.send_response(200)
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("Content-Disposition", 'attachment; filename="probe.bin"')
            self.send_header("Content-Length", str(len(PAYLOAD)))
            self.end_headers()
            self.wfile.write(PAYLOAD)
        else:
            body = HTML.encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    def log_message(self, *_args: object) -> None:
        pass


def check(name: str, ok: bool, detail: str = "") -> None:
    print(f"  {'PASS' if ok else 'FAIL'}  {name}" + (f"  {detail}" if detail else ""))
    if not ok:
        FAILURES.append(name)


def events(name: str) -> list[dict]:
    return [m for m in SEEN if m.get("t") == "event" and m.get("event") == name]


def wait_for(host: Host, name: str, timeout: float = 15.0) -> dict | None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        found = events(name)
        if found:
            return found[-1]
        try:
            SEEN.append(host.recv(timeout=min(0.4, max(0.05, deadline - time.monotonic()))))
        except TimeoutError:
            continue
    return None


def wait_condition(host: Host, predicate, timeout: float = 15.0) -> bool:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return True
        try:
            SEEN.append(host.recv(timeout=min(0.4, max(0.05, deadline - time.monotonic()))))
        except TimeoutError:
            continue
    return predicate()


def main() -> int:
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    port = server.server_address[1]
    base = f"http://127.0.0.1:{port}/"
    print(f"本地服务: {base}")

    event_names = [
        "navigationStarting", "navigationCompleted", "sourceChanged",
        "webMessage", "contentLoading", "domContentLoaded", "processFailed",
        "newWindowRequested", "downloadStarting", "downloadProgress",
        "downloadStateChanged", "scriptResult",
    ]

    host = Host(default_exe())
    try:
        print("\n[1] handshake + render")
        host.send({"op": "hello", "protocol": 1, "client": "smoke-webview2", "pid": 0})
        check("ready", host.recv().get("t") == "ready")

        host.send({
            "op": "render", "rev": 1,
            "window": {"title": "webview2-probe", "width": 800, "height": 500,
                       "x": -20000, "y": -20000, "center": False},
            "root": "n0",
            "nodes": [
                {"id": "n0", "type": "Column", "children": ["w1"]},
                {
                    "id": "w1", "type": "WebView2",
                    "props": {
                        "height": 400,
                        "source": base,
                        "isMuted": True,
                        "userAgent": "PyWinUI3-Smoke/1.0",
                    },
                    "events": {name: f"h{i}" for i, name in enumerate(event_names)},
                },
            ],
        })
        while True:
            message = host.recv(timeout=10)
            SEEN.append(message)
            if message.get("t") in ("ack", "error"):
                check("render ack", message.get("t") == "ack", str(message))
                break

        print("\n[1b] 设置属性（Core 就绪前缓存）")
        for key, expected in (("isMuted", True), ("userAgent", "PyWinUI3-Smoke/1.0")):
            qid = "q_" + key
            host.send({"op": "query", "id": qid, "node": "w1", "key": key})
            value = None
            deadline = time.monotonic() + 8
            while time.monotonic() < deadline:
                try:
                    message = host.recv(timeout=0.4)
                except TimeoutError:
                    continue
                SEEN.append(message)
                if message.get("t") == "result" and message.get("id") == qid:
                    value = message.get("value")
                    break
            check(f"query {key}", value == expected, str(value))

        print("\n[2] 加载生命周期 + 页面 → 宿主消息")
        required = (
            "webMessage", "contentLoading", "domContentLoaded", "navigationCompleted"
        )
        ok = wait_condition(
            host, lambda: all(events(name) for name in required), timeout=20
        )
        check("四个加载/消息事件都到齐", ok, str([n for n in required if not events(n)]))
        hello = [m for m in events("webMessage") if m.get("data", {}).get("data") == "hello"]
        check("页面 postMessage 内容正确", bool(hello), str(events("webMessage")[:1]))
        check("收到 domContentLoaded", bool(events("domContentLoaded")))
        check("收到 navigationCompleted", bool(events("navigationCompleted")))

        print("\n[3] 宿主 → 页面消息")
        host.send({"op": "invoke", "id": "w1", "method": "post_web_message",
                   "args": {"message": "ping"}})
        ok = wait_condition(
            host,
            lambda: any(m.get("data", {}).get("data") == "pong:ping" for m in events("webMessage")),
        )
        check("页面收到并回 pong", ok)

        print("\n[4] 脚本结果配对")
        host.send({"op": "invoke", "id": "w1", "method": "execute_script",
                   "args": {"script": "1+1", "requestId": "r1"}})
        result = wait_condition(
            host,
            lambda: any(m.get("data", {}).get("requestId") == "r1" for m in events("scriptResult")),
        )
        check("scriptResult 带回 requestId", bool(result))

        print("\n[5] 新窗口由 Python 回执决定")
        host.send({"op": "invoke", "id": "w1", "method": "execute_script",
                   "args": {"script": "window.open('about:blank')"}})
        new_window = None
        deadline = time.monotonic() + 12
        while time.monotonic() < deadline and new_window is None:
            if events("newWindowRequested"):
                new_window = events("newWindowRequested")[-1]
            else:
                try:
                    SEEN.append(host.recv(timeout=0.3))
                except TimeoutError:
                    pass
        check("收到 newWindowRequested", new_window is not None, str(new_window))
        if new_window:
            request_id = new_window.get("data", {}).get("requestId")
            check("带 requestId", bool(request_id), str(request_id))
            host.send({"op": "invoke", "id": "w1", "method": "respond_new_window",
                       "args": {"requestId": request_id, "allow": True, "url": base}})
            ok = wait_condition(
                host, lambda: len(events("navigationCompleted")) >= 2, timeout=12
            )
            check("允许后当前视图发生导航", ok)

        print("\n[6] 下载生命周期")
        host.send({"op": "invoke", "id": "w1", "method": "execute_script",
                   "args": {"script": "location.href='/file.bin'"}})
        starting = None
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline and starting is None:
            if events("downloadStarting"):
                starting = events("downloadStarting")[-1]
            else:
                try:
                    SEEN.append(host.recv(timeout=0.3))
                except TimeoutError:
                    pass
        check("收到 downloadStarting", starting is not None, str(starting))
        if starting:
            download_id = starting.get("data", {}).get("downloadId")
            check("带 downloadId 和 url", bool(download_id), str(starting.get("data")))
            out = str(Path(__file__).resolve().parent / "_probe_download.bin")
            host.send({"op": "invoke", "id": "w1", "method": "respond_download",
                       "args": {"downloadId": download_id, "allow": True, "filePath": out}})
            ok = wait_condition(
                host,
                lambda: any(
                    m.get("data", {}).get("state") in ("Completed", "Interrupted")
                    for m in events("downloadStateChanged")
                ),
                timeout=20,
            )
            check("收到下载终态事件", ok, str(events("downloadStateChanged")[-1:] if events("downloadStateChanged") else ""))
            check("产生进度事件", bool(events("downloadProgress")))
            path = Path(out)
            check("文件已落盘", path.is_file(), out)
            if path.is_file():
                path.unlink()

        print("\n[7] close")
        host.send({"op": "close", "code": 0})
        check(
            "收到 bye",
            wait_condition(host, lambda: any(m.get("t") == "bye" for m in SEEN), timeout=6),
        )
    finally:
        host.close()
        server.shutdown()

    print("\n" + "=" * 50)
    if FAILURES:
        print(f"FAILED: {FAILURES}")
        return 1
    print("ALL PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
