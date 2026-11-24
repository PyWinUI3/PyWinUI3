# PyWinUI3 协议定义

协议版本：`1`

本文是 Python 客户端与 C# 宿主之间的**唯一契约**。两端实现都必须以本文为准；任何字段的
增删都要先改这里，再改两端。

---

## 目录

1. [传输层](#1-传输层)
2. [消息类型](#2-消息类型)
3. [表单结构](#3-表单结构)
4. [事件](#4-事件)
5. [错误](#5-错误)
6. [完整示例](#6-完整示例)
7. [版本演进规则](#7-版本演进规则)

---

## 1. 传输层

### 1.1 通道

宿主进程由 Python 用 `subprocess` 启动，通信走**标准输入 / 标准输出**：

```
Python ──stdin──▶ 宿主 ──▶ WinUI 3
Python ◀─stdout── 宿主
Python ◀─stderr── 宿主的日志与诊断信息
```

选择 stdin/stdout 而非命名管道或 TCP 的理由：

- Python 标准库原生支持 `subprocess.PIPE`，不需要 `pywin32` 或任何第三方包；
- Windows 命名管道在 Python 标准库里没有对应实现，用 `ctypes` 自己调 Win32 API 太重；
- TCP 会占端口，且可能被防火墙拦截；
- 管道句柄的生命周期天然绑定进程，对端一死立刻读到 EOF，不需要额外心跳；
- 这也是 LSP 采用的方式，久经验证。

### 1.2 分帧

JSON Lines：**一行一条消息**，行尾 `\n`。

- 编码一律 UTF-8，**不带 BOM**；
- 消息体内不得出现裸换行（由 JSON 序列化器保证）；
- 单行长度上限 8 MiB，超限视为协议错误；
- 读到 EOF 即视为对端退出。

### 1.3 时序

```
Python                          Host
  │                              │
  ├── spawn(exe --stdio) ───────▶│
  │                              │  初始化 WinUI 3
  ├── hello ────────────────────▶│
  │                              │  校验版本
  │◀──────────────────── ready ──┤
  ├── render ───────────────────▶│  建树、显示窗口
  │◀───────────────────── ack ───┤  render 成功，回唯一一条 ack
  │                              │
  │◀─────────────────── event ───┤  用户点击
  ├── patch ────────────────────▶│  回调里改界面（不回 ack）
  │                              │
  ├── close ────────────────────▶│
  │◀────────────────────── bye ───┤  进程退出
```

握手必须在 **10 秒**内完成，否则 Python 侧杀掉宿主进程并抛 `HostStartError`。

### 1.4 宿主诊断日志

宿主的文件日志按轻重分级，避免正常运行被逐条报文的磁盘写拖累：

| 入口 | 记录内容 | 默认磁盘 IO |
|---|---|---|
| `HostLog.Write` | 关键日志：异常、进程生命周期、`warning` 告警 | 有，使用长驻 `StreamWriter`（不逐条开关文件） |
| `HostLog.Trace` | 逐条报文 `<<` / `>>` | 无 |

逐条报文走 `HostLog.Trace`，默认零磁盘 IO；设环境变量 `PYWINUI3_TRACE=1` 才把它们落盘。
日志路径是 `%TEMP%/pywinui3-host-<pid>.log`——按进程分开，同时开多个 App 不会互相清空。

---

## 2. 消息类型

所有消息都是单行 JSON 对象。方向用 `op`（Python → Host）与 `t`（Host → Python）区分；
两个字段名刻意不同，抓日志时一眼就能分辨请求与响应。

### 2.1 Python → Host

#### `hello` — 握手

```json
{ "op": "hello", "protocol": 1, "client": "pywinui3/1.0.0-rc1", "pid": 12345 }
```

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `protocol` | int | 是 | 客户端协议版本 |
| `client` | string | 是 | 客户端标识，格式 `名称/版本` |
| `pid` | int | 否 | Python 进程 pid，便于排查孤儿进程 |

宿主回 `ready`；版本不兼容则回 `error` 后退出。

#### `render` — 全量渲染

```json
{
  "op": "render",
  "rev": 1,
  "window": { "title": "Demo", "width": 900, "height": 600 },
  "root": "n0",
  "nodes": [
    { "id": "n0", "type": "Column", "props": { "padding": 24, "spacing": 12 },
      "children": ["n1", "n2"] },
    { "id": "n1", "type": "Label", "props": { "text": "Hello" } },
    { "id": "n2", "type": "Button", "props": { "text": "点击" }, "events": { "click": "h1" } }
  ]
}
```

- `nodes` 是**扁平列表**，父子关系由 `children` 表达，不是嵌套结构——扁平结构让增量更新
  不必重新序列化整棵子树；
- `root` 指向根节点 id；
- `nodes` 里所有 id 必须唯一；
- 首次 `render` 创建窗口；后续 `render` 视为整棵树重建（用于热重载）。

#### `patch` — 增量更新

```json
{
  "op": "patch",
  "rev": 2,
  "ops": [
    { "t": "set",    "id": "n1", "props": { "text": "Changed" } },
    { "t": "insert", "parent": "n0", "index": 1,
      "node": { "id": "n3", "type": "Label", "props": { "text": "新行" } } },
    { "t": "remove", "id": "n4" },
    { "t": "move",   "id": "n2", "parent": "n0", "index": 0 },
    { "t": "window", "props": { "title": "新标题" } }
  ]
}
```

| `t` | 语义 | 必需字段 | 可选字段 |
|---|---|---|---|
| `set` | 设置已有节点的属性 | `id`, `props` | `events` |
| `insert` | 插入新节点到指定父节点的指定位置 | `parent`, `index`, `node` | — |
| `remove` | 移除节点及其子树 | `id` | — |
| `move` | 把已有节点移到新父节点的新位置 | `id`, `parent`, `index` | — |
| `window` | 更新窗口属性 | `props` | — |

`set` 带上 `events` 时，该节点的事件绑定被**整体替换**为给定映射（传空对象即解绑全部）。
之所以让事件走 `set` 而不是单独的 `bind` 操作：事件映射本身就是节点声明的一部分，用一个
操作维护它比维护两套路径更不容易出错。

```json
{ "t": "set", "id": "n2", "props": {}, "events": { "click": "h7" } }
```

`ops` **按数组顺序依次应用**，同一条 `patch` 里的操作可以前后依赖（例如先 `insert` 再对插入
的节点 `set`）。

不用 RFC 6902 JSON Patch 的原因：UI 树的增删必须带父节点与兄弟序号，语义化操作能一对一
映射到 `Panel.Children.Insert/RemoveAt`，比通用 JSON 路径补丁更好实现、更好调试。

Python 侧默认开启 **16 ms 合并窗口**：同一帧里对同一节点的多次属性赋值按节点合并，窗口
结束时合成一条 `patch` 发出，而不是每次赋值发一条。合并只针对属性类 `set`；结构类 op
（`insert` / `remove` / `move`）不参与合并，也不跨越它们合并（结构变更立即落地，前后的
属性改动不会被并到一起）。在 `query()` / `invoke` / `dialog` / `close` / `micro` 之前 Python
侧强制 flush 待合并的改动，保证同步读回能看到最新状态。这是发送侧优化，`patch` 的线上
形状不变。

#### `invoke` — 调用控件方法

```json
{ "op": "invoke", "id": "n2", "method": "focus", "args": {} }
```

支持的方法：

| 方法 | 适用 | `args` |
|---|---|---|
| `focus` / `select_all` | 通用 | — |
| `scroll_to` | 列表类 | `{"index": 0}` |
| `play` / `pause` / `stop` | `Media` | — |
| `seek` | `Media` | `{"seconds": 12.5}` |
| `navigate` / `reload` / `go_back` / `go_forward` | `WebView2` | `navigate` 用 `{"url": "..."}` |
| `navigate_to_string` | `WebView2` | `{"html": "<h1>Hi</h1>"}`；会等 Core 就绪，见 [3.5.4](#354-webview2-的导航事件与当前网址) |
| `execute_script` | `WebView2` | `{"script": "...", "requestId": "r1"}`；结果以 `scriptResult` 回推并带回 `requestId` |
| `post_web_message` | `WebView2` | `{"message": ...}` |
| `open_devtools` | `WebView2` | — |
| `respond_new_window` | `WebView2` | `{"requestId": "...", "allow": true, "url": "..."}` |
| `respond_download` | `WebView2` | `{"downloadId": "...", "allow": true, "filePath": "..."}` |
| `download_action` | `WebView2` | `{"downloadId": "...", "action": "cancel\|pause\|resume"}` |
| `select_range` | `Entry` | `{"start": 0, "length": 3}` |
| `clear` / `copy` / `cut` / `paste` | `Entry` | — |
| `show` / `hide` | `InfoBar` / `Expander` / `NavigationView` | — |
| `scroll_into_view` | 通用 | — |
| `minimize` / `maximize` / `restore` / `activate` | 窗口级（见 [3.1](#31-window)） | — |
| `select_tab` | `TabView` | 切换到指定标签，两类标签都回一条 `tabSelected`，见 [3.3](#33-控件类型) |

#### `query` — 读取运行时值

```json
{ "op": "query", "id": "q1", "node": "n2", "key": "actualWidth" }
```

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `id` | string | 是 | 请求 id，回包原样带回，用于并发配对 |
| `node` | string | 是 | 目标节点 id；窗口级键传根节点 id 即可 |
| `key` | string | 是 | 要读的键，清单见 [3.7](#37-query-键清单) |
| `args` | object | 否 | 预留给带参数的查询 |

`invoke` 是单向命令、没有返回值，事件又只在「发生变化」时推送，于是 `ActualWidth`、
窗口是否最大化、文本框选区这类「随时问一句」的值一直读不到。`query` 补的正是这半边：宿主
在 UI 线程同步读取，回一条 `t="result"`。

Python 侧 `App.query()` 是阻塞式的：等到配对结果才返回，等待期间收到的事件暂存到内部队列，
事件循环下一轮照常派发，因此在事件回调里调用也安全。

#### `dialog` — 弹出模态对话框

```json
{ "op": "dialog", "title": "控件事件", "body": "Button · click", "kind": "info" }
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `title` | 否 | 标题，缺省 `"提示"` |
| `body` | 否 | 正文，纯文本 |
| `kind` | 否 | `info` / `warn` / `error` / `success`，默认 `info` |

宿主用 `ContentDialog` 模态显示，用户点按钮后关闭；`dialog` 不回 `ack`。`kind` 会真正影响
外观：`info` 蓝色信息图标、按钮「知道了」；`warn` 橙色警告图标、「我知道了」；`error`
红色错误图标、「关闭」；`success` 绿色对勾、「好的」。窗口刚切换完内容时宿主会短暂等待
`XamlRoot` 就绪（最多 500 ms），避免「启动后立刻弹提示」被丢弃。

同一时刻只能有一个对话框：若上一个还没关，新请求被丢弃并回 `{"t":"error","code":"busy"}`。
这是 WinUI 的硬性限制，不是设计选择。

对话框会阻塞窗口交互，适合演示与调试，不适合当通知用——生产场景请用界面内的状态文字。

#### `fileDialog` — 文件 / 文件夹选择

带请求 id，宿主回一条 `fileDialogResult` 配对——`invoke` 只能单向做事，而文件选择
必须把用户选中的路径带回来。

```json
{
  "op": "fileDialog",
  "id": "fd1",
  "mode": "openMultiple",
  "title": "请选择文件",
  "initialDir": "D:/docs",
  "filter": "文本 (*.txt)|*.txt",
  "defaultName": "report.txt"
}
```

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `id` | string | 是 | 请求 id，回包原样带回 |
| `mode` | string | 是 | `open` / `openMultiple` / `save` / `folder`，其他值回 `bad-protocol` |
| `title` / `initialDir` / `filter` / `defaultName` | string | 否 | 对话框外观参数 |

宿主优先用系统 `IFileDialog` COM（支持自定义标题和初始目录），不可用时回退 WinRT
picker（标题被忽略）。用户取消时回 `ok=false`，`paths` 为空。

#### `windowAction` — 单扇窗口的控制

只影响一个会话窗口；和 `close` 不同，它不结束整个宿主。

```json
{ "op": "windowAction", "session": "w1", "action": "hide" }
```

`action` 取 `hide` / `show` / `focus` / `close` / `minimize` / `maximize` / `restore` /
`activate` / `start_move`。每个会话串行处理。

#### `tray` — 系统托盘图标

```json
{ "op": "tray", "action": "create", "id": "app",
  "spec": { "icon": "C:/app.ico", "tooltip": "我的应用" } }
```

| 字段 | 说明 |
|---|---|
| `action` | `create` / `update` / `destroy` |
| `id` | 图标 id，后续 `update` / `destroy` 用它定位 |
| `spec` | 可选，图标路径、提示文字等 |

托盘点击通过 `trayEvent` 回传。宿主复用主窗口句柄挂 WndProc 子类接收，不需要额外消息循环；
进程退出时会自动清理所有图标。

#### `close` — 关闭

```json
{ "op": "close", "code": 0 }
```

宿主关闭窗口、回 `bye`、退出进程。

#### `micro` — 微代码注入

```json
{
  "op": "micro",
  "id": "m1",
  "node": "n2",
  "program": [
    { "i": "set",     "props": { "opacity": 0.0 } },
    { "i": "animate", "prop": "opacity", "to": 1.0, "ms": 300, "steps": 20 },
    { "i": "wait",    "ms": 100 },
    { "i": "emit",    "event": "revealed", "data": { "ok": true } }
  ],
  "notify": true
}
```

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `id` | string | 是 | 请求 id；`notify=true` 时随 `microDone` 原样带回 |
| `node` | string | 是 | 目标节点 id，节点必须存在 |
| `program` | array | 是 | 指令数组，≤ 64 条 |
| `notify` | bool | 否 | 缺省 `false`；为 `true` 时程序结束回一条 `microDone` |

`micro` 让一段补间 / 连续动作在宿主本地一次性跑完，省掉每步一次跨进程往返
（`Widget.animate_width()` 就是用它实现的）。它**不是可执行 C# 代码**：宿主按下面固定语义
逐条解释，不动态编译、不反射、不访问文件 / 网络 / 进程。

`program` 与 `patch` 的 `ops` 同构，用 `i` 区分指令种类；差别是 `set` 不带 `id`，作用对象
统一由消息级 `node` 指定。指令白名单只有四种：

| `i` | 携带字段 | 语义 |
|---|---|---|
| `set` | `props`(object) | 走与 `patch` 相同的属性应用链，带回声抑制；**不许改 `events`** |
| `animate` | `prop` + `to` / `ms` / `steps` / `divisor`(number) | `prop` 只允许 `width` / `height` / `opacity`；闭式解指数缓出，最后一步 snap 到 `to` |
| `wait` | `ms`(number) | 等待指定毫秒 |
| `emit` | `event`(string) + 可选 `data` | 主动回一条普通 `event`（不是 `echo`） |

限额（形状校验失败回非终止的 `bad-micro`，见第 5 章）：

| 约束 | 上限 |
|---|---|
| `program` 指令数 | ≤ 64 |
| 单条 `set` 的 `props` 属性数 | ≤ 32 |
| 单条 `animate` 的 `ms` | ≤ 5000 |
| 单条 `animate` 的 `steps` | ≤ 1000 |
| 所有数值 | 绝对值 ≤ 1e6，且拒绝 `NaN` / `Infinity` |

目标节点必须存在；同一节点收到新的 `micro` 程序时，旧程序按代次作废、不会两段动画叠加。
`notify=true` 时程序结束（或校验失败）回一条 `microDone`（见 [2.2](#22-host--python)），缺省
不回，避免每帧一条回包。被新程序顶掉的旧请求同样回一条 `ok=false` 的 `microDone`（`error`
说明被取代），否则等回执的调用方会一直挂到超时。

#### 未知 `op` 与字段校验

`op` 不在已知集合（`hello` / `render` / `patch` / `invoke` / `query` / `dialog` /
`fileDialog` / `windowAction` / `tray` / `close` / `micro`）里时，宿主解析成 `UnknownMessage`，回一条 `warning` 后**忽略，不终止会话**——
新版本 Python 往旧宿主发新 `op` 不会把连接打断。字段缺失 / 类型错误回 `bad-protocol`，但
**同样不终止会话**：消息是逐行分帧的，坏一行不影响下一行，跳过并继续即可。旧实现把它当
致命错误，结果是一个字段写错整窗消失。现在只有 `version-mismatch` 还会终止——两端对协议
的理解根本不同，继续跑只会产生更难查的错。

### 2.2 Host → Python

#### `ready` — 就绪

```json
{
  "t": "ready",
  "protocol": 1,
  "host": "PyWinUI.Host/1.0.0-rc1",
  "os": { "version": "10.0.19044", "build": 19044 },
  "capabilities": {
    "mica": false,
    "acrylic": true,
    "roundedCorners": false,
    "backdrop": "acrylic"
  }
}
```

`capabilities` 让 Python 侧知道哪些视觉效果可用。Windows 10 上没有 Mica，宿主会据此把
`backdrop: "auto"` 降级成 `acrylic`。

#### `ack` — 渲染完成

```json
{ "t": "ack", "rev": 1 }
```

`ack` **只在 `render` 成功后回一条**，`rev` 对应那次 `render` 的 `rev`。`patch` 不再逐条回
`ack`（Python 侧本来就忽略逐条 ack），`invoke` / `dialog` / `close` / `micro` 也不发 `ack`；
`query` 走自己的 `result`，`micro` 带 `notify=true` 时走 `microDone`。Python 侧不再靠 ack
做逐条流控，只把它当作「首帧（或热重载）已落地」的信号。

#### `event` — 控件事件

```json
{ "t": "event", "id": "n2", "event": "click", "rev": 2, "data": { "x": 120, "y": 40 } }
```

`echo: true` 表示这次变化是程序自己 `set` 引起的、不是用户输入：

```json
{ "t": "event", "id": "n5", "event": "change", "rev": 3,
  "data": { "text": "hi" }, "echo": true }
```

Python 侧据此抑制双向绑定回环。

#### `result` — `query` 的应答

```json
{ "t": "result", "id": "q1", "ok": true, "value": 128.0 }
```

```json
{ "t": "result", "id": "q2", "ok": false, "error": "未知的 query 键 \"foo\"" }
```

`value` 可以是任意 JSON 值（数字、字符串、布尔、数组、对象、null）。`ok=false` 时 Python
侧抛 `UiError`，**不会**静默把「读不到」当成「读到空值」。迟到（调用方已超时）的 result
会被忽略。

若读到 `NaN` / `Infinity`（典型来源是 `NumberBox` 空值的 `Value`），宿主会在序列化前把它
归一化成 `null`，结果是 `ok=true, value=null`，而不是 `ok=false`。这个清洗对事件载荷同样
生效，避免一个非有限值把整条消息打废。

#### `microDone` — micro 程序结束

```json
{ "t": "microDone", "id": "m1", "ok": true }
{ "t": "microDone", "id": "m2", "ok": false, "error": "program 超过 64 条指令" }
```

**仅当 `micro` 请求带 `notify=true` 时回**。程序正常跑完 `ok=true`；形状校验失败或目标节点
不存在时 `ok=false` 并在 `error` 里给原因。没有 `notify` 的请求不发这条，避免每帧一条回包。

#### `fileDialogResult` — 文件对话框应答

```json
{ "t": "fileDialogResult", "id": "fd1", "ok": true,
  "paths": ["D:/docs/a.txt"], "session": null }
```

`ok=false` 表示用户取消（`error` 给出原因）；`paths` 在 `openMultiple` 下可以是多项，
其他模式恒为 1 项或空。

#### `windowClosed` — 某个会话窗口关闭

```json
{ "t": "windowClosed", "session": "w1", "code": 0 }
```

只影响那一扇窗，宿主继续运行；`main` 会话关闭则整个宿主退出并回 `bye`。

#### `trayEvent` — 托盘图标被点击

```json
{ "t": "trayEvent", "id": "app", "event": "rightClick" }
```

`event` 取 `click` / `doubleClick` / `rightClick` / `middleClick`。

#### `warning` — 告警

```json
{ "t": "warning", "message": "未知 op \"foo\"，已忽略", "id": "n7" }
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `message` | 是 | 告警文本 |
| `id` | 否 | 相关节点 / 请求 id，便于定位 |

宿主对**同一条告警做 1 秒去重**：去重之后才写日志，所以短时间内重复的告警既不会刷屏、
也不会反复落盘。Python 侧默认把 `warning` 打到 stderr，可用 `App(on_warning=...)` 接管。
未知 `op` 走这条通道（见 [2.1](#21-python--host)）。

#### `error` — 渲染或解析错误

```json
{ "t": "error", "code": "unknown-type", "message": "未知控件类型 Foo", "id": "n7" }
```

错误**不终止会话**（协议级错误除外），Python 侧把 `id` 对应的控件操作抛成 `UiError`。

#### `bye` — 退出

```json
{ "t": "bye", "code": 0 }
```

窗口被用户点关闭、或收到 `close` 之后发出，随后进程退出。

---

## 3. 表单结构

### 3.1 Window

| 字段 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `title` | string | `"PyWinUI3"` | 窗口标题 |
| `width` | number | 800 | 逻辑像素 |
| `height` | number | 600 | 逻辑像素 |
| `minWidth` | number | 320 | |
| `minHeight` | number | 240 | |
| `resizable` | bool | `true` | |
| `center` | bool | `true` | 启动时居中；与 `x`/`y` 同时给时坐标优先 |
| `x` / `y` | number | — | 窗口左上角（逻辑像素） |
| `icon` | string | — | 图标路径（`.ico` / `.png`），相对路径按工作目录解析 |
| `alwaysOnTop` | bool | `false` | 置顶 |
| `minimizable` | bool | `true` | 是否显示最小化按钮 |
| `maximizable` | bool | `true` | 是否显示最大化按钮 |
| `maximized` | bool | `false` | 启动即最大化 |
| `scroll` | bool | `true` | 窗口内容可滚动；关掉后超出部分被裁切 |
| `theme` | string | `"system"` | `system` / `light` / `dark` |
| `backdrop` | string | `"auto"` | `auto` / `mica` / `acrylic` / `none` |
| `parent` | string | — | 父窗口的会话 id。给了就嵌成 WS_CHILD 子窗口，不再是独立顶层窗口 |
| `layer` | string | `highest` | 嵌入子窗口的 z 序：`highest` / `lowest`。TOPMOST 对 WS_CHILD 无效，不开放 |
| `maximizeRequestable` | bool | `false` | 是否启用标题栏最大化按钮并把它交给 Python 回调。**默认禁用**，见下方说明 |

窗口尺寸上限（`maxWidth` / `maxHeight`）没有对应字段：`AppWindow` 不提供这个能力，只能靠
Win32 子类化拦 `WM_GETMINMAXINFO`，代价与收益不成比例。

`backdrop: "auto"` 由宿主按能力挑：有 Mica 用 Mica，否则 Acrylic，再否则纯色。

`parent` / `layer` 只对**顶层父窗口**有效——父必须是顶层窗口，不能是另一个子窗口。
子窗口的层级只有两档，对应子 HWND 唯一有效的两个 z 序位置：`highest` = `HWND_TOP`，
`lowest` = `HWND_BOTTOM`。

`maximizeRequestable` 默认为 `false`：宿主不自己决定要不要最大化，标题栏上的最大化按钮
**禁用**（灰着、不响应点击和双击）。Python 侧调用 `win.on_maximize_request(cb)` 时，会通过
`patch` 的 `window` 操作把这一位置成 `true`；之后点按钮，宿主发 `maximizeRequested` 事件
（带 `data.maximized` 表示当前状态），由回调自己决定调 `maximize()` 还是 `restore()`。
`on_maximize_request(None)` 会把它改回 `false`。

### 3.2 Node

```json
{
  "id": "n1",
  "type": "Label",
  "props": { "text": "Hello", "fontSize": 20 },
  "children": ["n2"],
  "events": { "click": "h1" }
}
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `id` | 是 | 稳定标识，一次会话内唯一，由 Python 侧生成（`n1`, `n2`…） |
| `type` | 是 | 控件类型，见 [3.3](#33-控件类型) |
| `props` | 否 | 属性字典，缺省为空对象 |
| `children` | 否 | 子节点 id 列表，顺序即渲染顺序 |
| `events` | 否 | 事件名 → handler id |

### 3.3 控件类型

基础控件：

| `type` | 映射到 | 可含子节点 |
|---|---|---|
| `Window` | 窗口内容根（通常由 `root` 承担，一般不显式出现） | 是（1 个） |
| `Column` | `StackPanel`（垂直） | 是 |
| `Row` | `StackPanel`（水平） | 是 |
| `Grid` | `Grid` | 是 |
| `Scroll` | `ScrollViewer`（内含 `StackPanel`） | 是 |
| `Label` | `TextBlock` | 否 |
| `Button` | `Button` | 否 |
| `HyperlinkButton` | `HyperlinkButton` | 否 |
| `ToggleButton` | `ToggleButton` | 否 |
| `SplitButton` | `SplitButton` | 否 |
| `RepeatButton` | `RepeatButton` | 否 |
| `Entry` | `TextBox` | 否 |
| `Checkbox` | `CheckBox` | 否 |
| `Radio` | `RadioButton` | 否 |
| `Switch` | `ToggleSwitch` | 否 |
| `Slider` | `Slider` | 否 |
| `ProgressBar` | `ProgressBar` | 否 |
| `ProgressRing` | `ProgressRing` | 否 |
| `ComboBox` | `ComboBox` | 否 |
| `ListBox` | `ListView` | 否 |
| `Image` | `Image` | 否 |
| `Media` | `MediaPlayerElement` | 否 |
| `WebView2` | `WebView2`；缺运行时时降级成灰色 `Border`，见 [3.5.3](#353-webview2-的运行时降级) | 否 |
| `Separator` | `Border`（1 px 线） | 否 |

扩展控件（全部是 WinUI 3 / Windows App SDK 1.8 内置类型）：

| `type` | 映射到 | 可含子节点 |
|---|---|---|
| `NumberBox` | `NumberBox` | 否 |
| `PasswordBox` | `PasswordBox` | 否 |
| `RichTextBlock` | `RichTextBlock`（`text` 纯文本 / `richText` 分段格式） | 否 |
| `RichEditBox` | `RichEditBox` | 否 |
| `Icon` | `FontIcon`（`glyph` + 通用 `fontSize` / `foreground`） | 否 |
| `Canvas` | `Canvas`（子元素用 `left` / `top` 绝对定位，不裁切） | 是 |
| `Rect` | `Rectangle` | 否 |
| `Ellipse` | `Ellipse` | 否 |
| `Line` | `Line` | 否 |
| `Path` | `Path`（`d` 用 SVG 路径语法） | 否 |
| `Polygon` | `Polygon` | 否 |
| `Polyline` | `Polyline` | 否 |
| `AutoSuggestBox` | `AutoSuggestBox` | 否 |
| `DatePicker` | `DatePicker` | 否 |
| `TimePicker` | `TimePicker` | 否 |
| `CalendarDatePicker` | `CalendarDatePicker` | 否 |
| `CalendarView` | `CalendarView` | 否 |
| `RatingControl` | `RatingControl` | 否 |
| `ColorPicker` | `ColorPicker` | 否 |
| `DropDownButton` | `DropDownButton` | 否 |
| `ToggleSplitButton` | `ToggleSplitButton` | 否 |
| `PersonPicture` | `PersonPicture` | 否 |
| `GridView` | `GridView` | 否 |
| `FlipView` | `FlipView` | 否 |
| `Pivot` | `Pivot`（条目转成 `PivotItem`） | 否 |
| `TabView` | `TabView`（条目转成 `TabViewItem`） | 否 |
| `TreeView` | `TreeView`（嵌套 dict 转成 `TreeViewNode`） | 否 |
| `NavigationView` | `NavigationView`（条目转成 `NavigationViewItem`） | 是（右侧内容区） |
| `MenuBar` | `MenuBar`（条目转成 `MenuBarItem`） | 否 |
| `CommandBar` | `CommandBar`（条目转成 `AppBarButton`） | 否 |
| `BreadcrumbBar` | `BreadcrumbBar` | 否 |
| `ItemsView` | `ItemsView` | 否 |
| `RadioButtons` | `RadioButtons` | 否 |
| `SelectorBar` | `SelectorBar`（条目转成 `SelectorBarItem`） | 否 |
| `InfoBar` | `InfoBar` | 否 |
| `Expander` | `Expander` | 是 |
| `BorderBox` | `Border` + 内层 `StackPanel` | 是 |
| `Viewbox` | `Viewbox` + 内层 `StackPanel` | 是 |
| `RelativePanel` | `RelativePanel`（未开放附加对齐属性） | 是 |
| `TwoPaneView` | `TwoPaneView`（子控件进 Pane1） | 是 |
| `RefreshContainer` | `RefreshContainer` + 内层 `StackPanel` | 是 |
| `PinnedTab` | `TabViewItem`（`IsClosable=false`，Content 预置 `StackPanel`） | 是 |
| `TabPage` | `TabViewItem`（`IsClosable=true`，Content 预置 `StackPanel`） | 是 |

`BorderBox` 单独命名，是为了不和 `Separator` 复用的 `Border` 混淆。这些容器创建时都预置
一层内层 `StackPanel`，所以子节点挂载规则与 `Column` / `Scroll` 完全一致，渲染器不需要为
它们开特例分支。

#### 富文本：`richText`

`RichTextBlock` 除了 `text` 纯文本，还接受 `richText` 数组做分段格式：

```json
"richText": [
  { "text": "警告：", "bold": true, "color": "#C42B1C" },
  { "text": "剩余空间不足", "italic": true },
  { "text": "2.1 GB", "bold": true, "underline": true, "size": 18 }
]
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `text` | 是 | 片段文字 |
| `bold` / `italic` / `underline` | 否 | 字形开关 |
| `size` | 否 | 字号（DIP），不填沿用控件字号 |
| `color` | 否 | 颜色，格式同 `foreground` |

数组里也可以混入裸字符串，按无格式片段处理。所有片段渲染进同一个 `Paragraph`。

`text` 与 `richText` 同时出现时，后应用的覆盖先应用的——协议里保持 `text` 在前、
`richText` 在后的书写顺序即可。

> `RichEditBox` 暂不支持 `richText`：它的富文本要经 `ITextRange` 逐段设置
> `CharacterFormat`，与用户的撤销栈交织容易出问题，当前按纯文本降级并打一条告警。

#### 画布与图元

`Canvas` 是绝对定位容器，子元素用 `left` / `top` 定位，超出边界不裁切
（等同 SVG 的 `overflow: visible`）。

```json
{
  "id": "n10",
  "type": "Canvas",
  "props": {
    "width": 800,
    "height": 600,
    "view": { "scale": 1.5, "offsetX": 40, "offsetY": -20 }
  }
}
```

**图元属性**

| 属性 | 适用 | 说明 |
|---|---|---|
| `left` / `top` | 任意子节点 | 相对父 `Canvas` 左上角的偏移 |
| `fill` | 图元 | 填充色，格式同 `foreground`；不填则无填充 |
| `stroke` | 图元 | 描边色；不填则无描边 |
| `strokeWidth` | 图元 | 描边宽度，默认 `1` |
| `strokeDashArray` | 图元 | 虚线样式，如 `[4, 2]` |
| `lineCap` | 图元 | `flat` / `round` / `square` |
| `lineJoin` | 图元 | `miter` / `bevel` / `round` |
| `d` | `Path` | SVG 路径数据，支持 `M L H V C S Q T A Z`（大小写均可） |
| `points` | `Polygon` / `Polyline` | 顶点数组 `[[x, y], ...]` |
| `x1` / `y1` / `x2` / `y2` | `Line` | 两个端点 |
| `radiusX` / `radiusY` | `Rect` | 圆角半径 |
| `transform` | 任意节点 | 组合变换，见下 |
| `view` | `Canvas` | 视图缩放与平移，见下 |

**`d` 的解析**

宿主自己实现了一套 SVG 路径解析，**不依赖** XAML 的字符串解析（WinUI 3 的
`XamlReader` 不接受 `Data="M0,0 L10,10"` 这种写法）。支持命令：

| 命令 | 含义 | 命令 | 含义 |
|---|---|---|---|
| `M` / `m` | 起点 | `S` / `s` | 平滑三次贝塞尔 |
| `L` / `l` | 直线 | `Q` / `q` | 二次贝塞尔 |
| `H` / `h` | 水平线 | `T` / `t` | 平滑二次贝塞尔 |
| `V` / `v` | 垂直线 | `A` / `a` | 圆弧 |
| `C` / `c` | 三次贝塞尔 | `Z` / `z` | 闭合 |

小写为相对坐标。支持隐式重复参数（`"L 1 2 3 4"` 等于两段直线）、省略分隔符
（`"M1-2"`）。解析失败会打一条告警，保留已经解析出来的部分，不中断渲染。

**`transform`（单个图元的变换）**

```json
"transform": { "rotate": 15, "scaleX": 1, "scaleY": 1, "translateX": 0, "translateY": 0,
               "originX": 0.5, "originY": 0.5 }
```

| 字段 | 默认 | 说明 |
|---|---|---|
| `rotate` | 0 | 旋转角度（度） |
| `scaleX` / `scaleY` | 1 | 缩放 |
| `translateX` / `translateY` | 0 | 平移 |
| `originX` / `originY` | 0.5 | 变换原点，按元素宽高的比例 |

`rotate` 是它的简写形式（等价于只给旋转、原点居中）。

**`view`（整个画布的视图）**

```json
"view": { "scale": 1.5, "offsetX": 40, "offsetY": -20 }
```

作用于画布容器的 `RenderTransform`，**一条属性生效，开销与图元数量无关**。
缩放和平移必须走这里，不要逐个改图元的 `left` / `top`——那会让开销随图元数线性增长。

**指针事件**

画布交互依赖带坐标的指针事件：

| 事件 | 负载 |
|---|---|
| `pointerPressed` | `{x, y, isLeftButtonPressed, isRightButtonPressed}` |
| `pointerMoved` | 同上 |
| `pointerReleased` | 同上 |
| `pointerCanceled` | 同上 |
| `pointerCaptureLost` | 同上 |
| `pointerEntered` / `pointerExited` | 同上 |

`x` / `y` 相对**父容器**而不是图元自身——图元在拖动过程中自己会移动，用相对自身的
坐标会一直漂。

同一个节点同时绑了 `pointerPressed` 和 `pointerMoved` 时，按下会自动捕获指针，
鼠标移出图元也会继续收到 `pointerMoved`，松开时自动释放。这是拖动手势的前提。

`pointerMoved` 在宿主侧按 16 ms 节流（对齐 60fps），避免事件洪泛打满 UI 线程；
拖动结束位置由 `pointerReleased` 补上，不受节流影响。

#### Win2D 画布：`Canvas2D`

`Canvas2D` 把整块图元数组交给 GPU 绘制，与 `Canvas` 是两条不同的路线：

- `Canvas` 的每个图元是独立 XAML 元素，能单独绑事件、单独改属性
- `Canvas2D` 的图元只是数据，命中测试要自己在 Python 侧算

```json
{
  "id": "n20",
  "type": "Canvas2D",
  "props": {
    "width": 860,
    "height": 520,
    "shapes": [
      { "id": "p1", "type": "line", "x1": 0, "y1": 0, "x2": 200, "y2": 120, "stroke": "#4FC3F7" },
      { "id": "p2", "type": "rect", "x": 240, "y": 40, "w": 120, "h": 80, "radius": 8, "fill": "#DFF6DD" }
    ]
  }
}
```

**图元类型**

| `type` | 字段 |
|---|---|
| `line` | `x1` `y1` `x2` `y2` |
| `rect` | `x` `y` `w` `h` `radius`（圆角，可选） |
| `ellipse` | `cx` `cy` `rx` `ry` |
| `path` | `d`（语法同 `Path`） |
| `polygon` | `points`（`[[x, y], ...]`，至少 3 点，自动闭合） |
| `text` | `x` `y` `text` `size` `color` |

所有图元都支持 `fill` / `stroke` / `strokeWidth`；`text` 用 `color`。
`id` 可选，但要支持增量更新就必须给。

**全量替换**：给 `shapes` 赋新数组，整块重绘。

**增量更新**（`invoke`，不是属性）：

```json
{ "op": "invoke", "id": "n20", "method": "update_shapes",
  "args": { "updates": [ { "id": "p1", "cx": 40, "cy": 60 } ] } }
```

只覆盖传进来的字段，其余保持不变；按 `id` 定位，找不到的条目静默跳过。
走 invoke 而非属性，是为了绕开 16ms 的属性合并窗口——
否则同一帧内的多次增量会被压成最后一次，前面的更新丢失。

#### 标签页：`PinnedTab` 与 `TabPage`

两者都是 `TabView` 的专用子节点，控件总数因此从 56 增至 58：

- **`PinnedTab`（固定选项卡）** 和 `TabPage` 一样是 `TabViewItem`，同样进 `TabItems`、同样
  有图标、同样能放子控件，只是 `IsClosable=false`、`CanDrag=false`。标签条内部重排没有可
  取消的公开事件，所以宿主在固定标签按下时临时关闭整个 `TabView` 的 `CanReorderTabs`，
  松开（或捕获丢失 / 取消）后由定时器恢复；同时记录每个固定标签的规范顺序，一旦被改动
  就复位。结果是**不可关闭、拖不动，且始终排在普通标签之前**。属性 `text` / `icon` /
  `minWidth` / `maxWidth`；方法 `select()`。
- **`TabPage`（标签页）** 渲染成 `TabViewItem`（`IsClosable=true`）放进 `TabItems`；
  `TabView.CanReorderTabs=true`，所以可拖动重排、可关闭。`Content` 预置一层 `StackPanel`，
  **能放子控件**。属性 `text` / `closable` / `icon` / `minWidth` / `maxWidth`；方法 `close()` /
  `select()`。

两类标签的 `minWidth` / `maxWidth` 直接作用在 `TabViewItem` 上；宽度被限制后标题自动用
`…` 省略（`Header` 是开了 `TextTrimming=CharacterEllipsis` 的 `TextBlock`）；`query("text")`
读回的仍是完整标题，不含省略号。

`TabView` 新增 `canReorderTabs`（默认 `true`，Python 侧 `can_reorder_tabs`）控制能否拖动
重排；`tabWidthMode`（`equal` / `sizeToContent` / `compact`，默认 `equal`）控制标签宽度——
只有两三个标签时建议用 `sizeToContent`，否则每个标签平分整条宽度、关闭按钮漂得很远。另有
`add_page()` / `add_pinned()` 便捷方法。`items=[...]` 仍是快捷写法，生成普通可关闭标签页、
不能放子控件；不要把它和 `TabPage` 子节点混用，否则两组标签会同时出现。子标签一律追加，
忽略协议 `insert` 的 `index`。

诚实限制：标签拖动重排由 WinUI 内置完成，宿主**不回传重排事件**，Python 侧 `children`
顺序不会自动同步；`TabPage` 构造时不支持 `selected=True`（请用 `select()`）；固定标签被拖到
别处后只在拖动结束时归位，过程中会看到短暂的位置变化。

未知 `type` 不崩溃：回一条 `error`，该节点渲染成占位 `TextBlock` 显示 `[未知控件: Foo]`。

`Scroll` 在宿主侧是 `ScrollViewer` 套一层 `StackPanel`：节点模型允许一个节点有多个子节点，
而 `ScrollViewer.Content` 只收一个元素，里层那层 StackPanel 就是子节点的宿主，`spacing` 也
作用在它身上。注意 `ScrollViewer` 只有在**父容器给出有界高度**时才会滚动——放在 `Column` /
`Row` 里必须显式给 `height`，否则会被测量成无限高。整窗滚动不需要它，`window.scroll` 默认
就是开的。

### 3.4 通用属性

所有控件都接受：

| 属性 | 类型 | 说明 |
|---|---|---|
| `width` / `height` | number | 固定尺寸，缺省为自动 |
| `minWidth` / `minHeight` / `maxWidth` / `maxHeight` | number | |
| `margin` | number / `[l,t,r,b]` / `{l,t,r,b}` | 外边距 |
| `padding` | 同上 | 内边距（容器类） |
| `hAlign` | `left`/`center`/`right`/`stretch` | 水平对齐 |
| `vAlign` | `top`/`center`/`bottom`/`stretch` | 垂直对齐 |
| `visible` / `enabled` | bool | |
| `opacity` | number 0–1 | |
| `background` / `foreground` | color | 见 [3.6](#36-值类型约定) |
| `borderColor` / `borderWidth` | color / number | |
| `cornerRadius` | number / `[tl,tr,br,bl]` | Win10 上不生效，宿主静默忽略 |
| `fontSize` / `fontFamily` | number / string | |
| `weight` | `normal`/`medium`/`semibold`/`bold` | |
| `italic` | bool | |
| `tooltip` | string | 鼠标悬停提示，空串清除 |
| `contextMenu` | array | 右键菜单，见 [3.5.2](#352-contextmenu-与-menu-的菜单项) |
| `name` / `tag` | string | XAML `Name` / `Tag`，调试与 UI 自动化用 |
| `tabIndex` / `isTabStop` | int / bool | Tab 键顺序与是否参与 |
| `allowDrop` / `isHitTestVisible` | bool | 拖放 / 命中测试 |
| `flowDirection` | `ltr`/`rtl` | 布局方向 |
| `selectionStart` / `selectionLength` | int | 仅 `TextBox`，文本选区 |
| `isSpellCheckEnabled` / `acceptsReturn` | bool | 仅 `TextBox` |
| `decodePixelWidth` / `decodePixelHeight` | int | 仅 `Image`，见下 |
| `isEditable` / `isTextSearchEnabled` | bool | 仅 `ComboBox` |
| `selectionMode` | `single`/`multiple`/`extended`/`none` | `ListViewBase` / `CalendarView` |
| `isFullWindow` | bool | 仅 `Media`，全屏播放 |
| `verticalScrollBarVisibility` / `horizontalScrollBarVisibility` | `auto`/`visible`/`hidden`/`disabled` | 仅 `Scroll` |

`decodePixelWidth/Height` 必须在 `BitmapImage.UriSource` 赋值之前落地才有效；宿主会在应用
一批 props 之前先把这两个值收集到控件上，所以写入顺序无关。

### 3.5 各控件专有属性

| 控件 | 属性 |
|---|---|
| `Column` / `Row` | `spacing`(number), `padding` |
| `Grid` | `rows`([`auto`/`*`/数字]), `columns` 同上, `rowGap`, `columnGap`, `padding` |
| `Scroll` | `spacing`, `padding`, `height`（**必须给一个有界高度，否则不会滚**） |
| `Grid` 子节点 | `row`, `column`, `rowSpan`, `columnSpan` |
| `Label` | `text`, `wrap`(bool), `maxLines`(number), `textAlign` |
| `Button` | `text`, `icon`(glyph), `variant`(`normal`/`accent`) |
| `HyperlinkButton` | `text`, `icon`(glyph), `navigateUri`(string) |
| `ToggleButton` | `text`, `icon`(glyph), `checked`(bool) |
| `SplitButton` | `text`, `icon`(glyph), `menu`（下拉菜单，格式见 [3.5.2](#352-contextmenu-与-menu-的菜单项)） |
| `RepeatButton` | `text`, `icon`(glyph), `repeatDelay`(ms), `repeatInterval`(ms) |
| `Entry` | `text`, `placeholder`, `readOnly`(bool), `multiline`(bool), `password`(bool), `maxLength`(number) |
| `Checkbox` | `text`, `checked`(bool) |
| `Radio` | `text`, `checked`(bool), `groupName` |
| `Switch` | `header`, `on`(bool) |
| `Slider` | `min`, `max`, `value`, `stepFrequency`, `orientation` |
| `ProgressBar` | `value`, `min`, `max`, `indeterminate`(bool) |
| `ProgressRing` | `active`(bool), `size`(number), `foreground` |
| `Media` | `src`, `autoPlay`(bool), `showControls`(bool), `volume`(0–1), `position`(秒), `loop`(bool), `muted`(bool), `playbackRate`(0.25–4), `stretch` |
| `ComboBox` | `items`（见 [3.5.1](#351-items-的两种形态)）, `selectedIndex`(number), `placeholder` |
| `ListBox` | `items`（见 [3.5.1](#351-items-的两种形态)）, `selectedIndex`(number) |
| `Image` | `src`（路径 / URL / data URI）, `stretch`(`fill`/`uniform`/`uniformToFill`/`none`) |
| `WebView2` | `source`(绝对 URL)；`isMuted` / `userAgent` / `isScriptEnabled` / `areDefaultContextMenusEnabled` / `isWebMessageEnabled` / `isZoomControlEnabled`（Core 就绪前先缓存，见 [3.5.5](#355-webview2-扩展能力)）；缺运行时时静默忽略，见 [3.5.3](#353-webview2-的运行时降级) |
| `Separator` | `color`, `thickness` |

扩展控件沿用上表里的同名属性：所有带 `items` 的控件都吃 `items` + `selectedIndex`，
`NumberBox` / `RatingControl` 吃 `value` / `min` / `max`，`AutoSuggestBox` / `RichEditBox` /
`NumberBox` 吃 `placeholder`，`DropDownButton` / `ToggleSplitButton` 吃 `text`（按钮文字），
`InfoBar` 吃 `text`（等价 `message`），`Viewbox` 吃 `stretch`，`ColorPicker` 吃 `color`，
`ToggleSplitButton` 吃 `checked`。下表是扩展控件新增的专有 key：

| 属性 | 类型 | 适用 |
|---|---|---|
| `date` | `"YYYY-MM-DD"` | `DatePicker` / `CalendarDatePicker` / `CalendarView` |
| `time` | `"HH:MM"` | `TimePicker` |
| `severity` | `info`/`success`/`warning`/`error` | `InfoBar` |
| `title` / `message` | string | `InfoBar` |
| `isOpen` / `isClosable` | bool | `InfoBar` |
| `isExpanded` / `expandDirection` | bool / `down`/`up` | `Expander` |
| `displayName` / `initials` | string | `PersonPicture` |
| `paneDisplayMode` | `left`/`top`/`compact`/`minimal`/`auto` | `NavigationView` |
| `isPaneOpen` / `isBackEnabled` / `isSettingsVisible` | bool | `NavigationView` |
| `openPaneLength` / `compactPaneLength` | number | `NavigationView` |
| `spin` | `compact`/`inline`/`hidden` | `NumberBox` |
| `isClearEnabled` / `maxRating` | bool / int | `RatingControl` |
| `queryText` | string | `AutoSuggestBox`（等价 `text`） |
| `content` | string | `DropDownButton` / `ToggleSplitButton` |
| `maxColumns` | int | `RadioButtons` |
| `isAddTabButtonVisible` | bool | `TabView` |
| `canReorderTabs` | bool | `TabView`（默认 `true`，Python 侧 `can_reorder_tabs`） |
| `tabWidthMode` | `equal`/`sizeToContent`/`compact` | `TabView`（默认 `equal`） |
| `text` | string | `PinnedTab` / `TabPage`（标签文字） |
| `icon` | string | `PinnedTab` / `TabPage`（Segoe 字形，或 `.png` / `.jpg` / `.ico` / `.bmp` / `.svg` 本地路径） |
| `closable` | bool | `TabPage`（对应 `TabViewItem.IsClosable`） |
| `minWidth` / `maxWidth` | number | `PinnedTab` / `TabPage`（宽度约束，超出显示省略号） |
| `panePriority` | `pane1`/`pane2` | `TwoPaneView` |
| `isMoreButtonVisible` | bool | `ColorPicker` |
| `header` | string | `Expander` / `NumberBox` / 日期时间控件 / 输入框 / `ComboBox` |

`TreeView` 的 `items` 支持嵌套：
`{"label": "父节点", "children": ["子1", {"label": "子2", "children": [...]}]}`。

#### 3.5.1 `items` 的三种形态

纯文本：

```json
"items": ["选项 A", "选项 B"]
```

带图标（数组元素换成对象）：

```json
"items": [
  { "text": "文档", "glyph": "\uE8A5" },
  { "text": "图片", "glyph": "\uEB9F" }
]
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `text` | 是 | 显示文字，同时作为事件的 `value` 回传 |
| `glyph` | 否 | Segoe Fluent Icons 的码点，如 `"\uE8A5"` |

两种形态可以混用——数组里的裸字符串按纯文本项处理。图标用的是 WinUI 自带的 Segoe Fluent
Icons 字体，不需要额外资源文件。常用码点：

| 码点 | 图标 | 码点 | 图标 |
|---|---|---|---|
| `\uE8A5` | 文档 | `\uE8B7` | 文件夹 |
| `\uEB9F` | 图片 | `\uE713` | 设置 |
| `\uE77B` | 联系人 | `\uE8F1` | 库 |
| `\uE896` | 下载 | `\uE72D` | 分享 |

完整列表见微软文档 "Segoe Fluent Icons font"。

带子控件（数组元素写 `children`，每个子元素是一个节点描述）：

```json
"items": [
  {
    "children": [
      { "type": "Icon", "props": { "glyph": "\uE8A5" } },
      { "type": "Label", "props": { "text": "年度报告.pdf", "width": 180 } },
      { "type": "Label", "props": { "text": "2.4 MB", "opacity": 0.6 } }
    ]
  },
  "纯文字项可以和它混排"
]
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `children` | 是 | 节点描述数组，每项 `{ "type": ..., "props": {...} }`，与 render 里的节点同格式 |

子元素由宿主建好并注册进节点表，id 形如 `{列表id}#{条目序号}.{子控件序号}`，
**因此可以正常绑事件**（`click` 等照常回传）。

布局规则：只有一个子控件时直接作为条目；多个则包一层横向 `StackPanel`（间距 8）排开。
需要纵向或更复杂的内部结构时，把内容拆成多个叶子控件自行控制宽度，或改用
`TabPage` 之类真正的容器。

限制：`children` 里的元素**只创建自身**，不递归处理它 `props` 里的 `children`——
嵌套容器请用常规的子节点层级，而不是塞进列表项。

列表重新赋值时，上一轮创建的子节点会从节点表移除，反复刷新不会累积。

#### 3.5.2 `contextMenu` 与 `menu` 的菜单项

右键菜单（`contextMenu`）和 `SplitButton` 的下拉菜单（`menu`）用同一套格式，任何控件都能挂
右键菜单：

```json
"contextMenu": [
  { "label": "复制", "glyph": "\uE8C8", "value": "copy" },
  "-",
  { "label": "删除", "value": "delete", "enabled": false }
]
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `label` | 是 | 菜单文字 |
| `glyph` | 否 | Segoe Fluent Icons 码点 |
| `value` | 否 | 点击时回传的值，缺省用 `label` |
| `enabled` | 否 | 缺省 `true` |

分隔线写成字符串 `"-"` 或 `{"separator": true}`。菜单项点击后统一回传 `menu` 事件，`data`
为 `{ "value": "...", "label": "..." }`。之所以用「一个事件 + value」而不是每条菜单一个
事件名：菜单项属于数据，让 Python 侧按 `value` 分派，比动态注册一堆事件好维护。

`Image` 的 `src` 除路径与 URL 外还支持 `data:image/png;base64,...`；真正开始解码前不会阻塞
UI，解码失败回 `imageFailed`。

#### 3.5.3 `WebView2` 的运行时降级

宿主在**创建节点时**（`render` / `insert` 的建树阶段）探测 WebView2 Runtime，也就是窗口
启动这一刻，而不是等控件自己去初始化炸掉：

```
探测 = 注册表 EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5} 的 pv
       （HKLM / HKCU × 32 / 64 视图，Evergreen 安装）
    ∨  EdgeWebView\Application\<版本>\msedgewebview2.exe 存在
       （固定版本 Fixed Version 运行时，不写注册表）
```

探测结果在整个宿主进程里缓存；设环境变量 `PYWINUI3_WEBVIEW2_FALLBACK=1` 可强制判为
「缺失」，用于演示和自动化验证。

- **探测到运行时** → 建 `WebView2` 控件，`source` 写进 `WebView2.Source`。
- **没探测到运行时** → 不建控件，换成一块灰色 `Border`：居中是「加载失败」图标和标题，
  下方一行 `该控件无法正常加载：Webview2 Runtime未找到。`，同时回一条 `warning`。节点
  本身仍是合法节点，`source` 被静默忽略，`navigate` / `reload` / `go_back` / `go_forward`
  因类型不匹配静默跳过；窗口其余部分照常渲染，不会白屏、不会崩溃。

降级占位用 `Border` 而不是 `Panel`，所以它**不是容器**：给它塞 `children` 会收到
`bad-protocol`「不是容器」错误，而不是把提示文字冲掉。

边界：本节只覆盖「运行时根本没装」。运行时存在但初始化失败（安装损坏、权限问题）目前
只在日志里留下 WebView2 的异常，该区域显示为空白，暂不自动替换成降级页。

#### 3.5.4 `WebView2` 的导航事件与当前网址

`invoke` 没有返回值，Python 不能「问」宿主当前网址，所以由宿主主动推送。`WebView2` 节点
**订阅以下事件后**宿主才去挂对应监听器——不订阅就不拉起 `CoreWebView2`，避免为了读地址
白白初始化一个浏览器。

| 事件 | 来源 | `data` |
|---|---|---|
| `navigationStarting` | `WebView2.NavigationStarting` | `{ "url": "https://..." }` |
| `navigationCompleted` | `WebView2.NavigationCompleted` | `{ "url": "...", "isSuccess": true, "httpStatusCode": 200 }` |
| `sourceChanged` | `CoreWebView2.SourceChanged` | `{ "url": "..." }` |

`navigationCompleted` 的事件参数不带 URI，地址从 `CoreWebView2.Source` 取（回落用
`WebView2.Source`）；`sourceChanged` 覆盖页面内 JS 改地址（`location.hash` /
`history.pushState`）这类没走 `NavigationStarting` 的变化。`CoreWebView2` 是异步初始化的，
订阅会等 `CoreWebView2Initialized`，并在就绪时补报一次当前地址（`about:blank` 不算，跳过）。
Core 回调不保证在 UI 线程，事件统一经 `DispatcherQueue` 调度后再 `emit`。

Python 侧 `WebView2` 构造时常驻订阅 `sourceChanged`，把收到的 `url` 记进 `current_url`；同步
写在 `_sync_from_event` 而不是用户回调里，所以用户重新 `bind("sourceChanged", ...)` 覆盖掉
内部空回调也不会影响 `current_url`。`source` 是最后一次写入的目标，`current_url` 是浏览器
回报的实际地址，页面内跳转或服务端重定向后两者会不同。

降级占位是 `Border`，三个事件都静默跳过、不报错；此时没有浏览器，`current_url` 保持最后
一次设置的 `source`。

`navigate_to_string` 先 `await EnsureCoreWebView2Async()` 再调 `NavigateToString(html)`：冷
启动时 Core 还没好，直接调会抛「valid CoreWebView2 is not present」。这类页面没有真实地址，
`current_url` 通常是 `about:blank`。

#### 3.5.5 `WebView2` 扩展能力

这些能力挂在 `CoreWebView2` 上（不在 XAML 控件上）。宿主在建节点时统一挂一次事件，只有
Python 真的 `bind()` 了对应事件名才往管道推；设置属性如果来得比 CoreWebView2 初始化早，
会先缓存，初始化完成后补写（`query` 在 Core 未就绪时读回的是缓存值）。

**双向消息**：页面里 `window.chrome.webview.postMessage(x)` → `webMessage` 事件；Python
`post_web_message(message)` → 页面 `window.chrome.webview` 的 `message` 事件。字符串走
`PostWebMessageAsString`，其它值按 JSON 发。要求 `isWebMessageEnabled=true`（默认）。

**新窗口（Python 回执决定）**：页面 `window.open` 时宿主取 deferral，回 `newWindowRequested`
带 `requestId`；Python 必须调用 `respond_new_window`：

- `allow=true` → 在**当前视图**导航过去（可用 `url` 覆盖目标）；
- `allow=false` → 丢弃这次开窗；
- 没订阅 `newWindowRequested` 时直接按拒绝处理；
- 8 秒没回执自动按拒绝处理，deferral 不会永远挂着。

当前模型是「一个节点一个 WebView2」，不会真的创建第二个浏览器窗口。

**下载（`DownloadOperation` 生命周期）**：`DownloadStarting` 时同样取 deferral，回
`downloadStarting` 带 `downloadId`；Python 用 `respond_download` 决定 `allow`（false = 取消）
与 `filePath`（覆盖默认保存路径），8 秒不回执按默认允许。回执后宿主挂
`BytesReceivedChanged` / `StateChanged`，回 `downloadProgress`（100 ms 节流）和
`downloadStateChanged`；`completed` / `interrupted` 之后清掉记录。下载过程中可用
`download_action` 执行 `cancel` / `pause` / `resume`。

**脚本结果配对**：`execute_script` 的 `requestId` 会原样出现在 `scriptResult` 里，并发执行
多段脚本不会混淆；不传时行为与以前一致。

**诚实限制**：

- `permissionRequested` 暂未接（需要异步 deferral + 超时策略，本期不做）；
- WinUI 3 的 WebView2 投影没有暴露 `CoreWebView2Controller`，因此**不提供程序化
  `zoomFactor`**，只有 `isZoomControlEnabled` 控制用户 Ctrl+滚轮缩放；
- `WebResourceRequested` 拦截、`addHostObjectToScript`、证书 / HTTP 认证、完整多窗口仍未
  实现（见第 8 章）。

### 3.6 值类型约定

**颜色**接受四种写法：

```
"#FF5500"          十六进制，支持 #RGB / #RRGGBB / #AARRGGBB
"red"              140 个 CSS 具名颜色
{ "a": 255, "r": 255, "g": 85, "b": 0 }   显式通道
"accent"           系统强调色
```

**尺寸**一律是逻辑像素（DIP），由 WinUI 的 DPI 缩放自动处理，Python 侧无需关心显示器 DPI。

**枚举值**全部小写 camelCase，例如 `"semibold"`、`"uniformToFill"`、`"stretch"`。收到无法
识别的枚举值时用默认值并记一条 `error`，不崩溃。

### 3.7 `query` 键清单

窗口级（与 `node` 指向哪个控件无关，通常传根节点 id）：

| key | 返回 | 说明 |
|---|---|---|
| `windowState` | `normal`/`maximized`/`minimized` | |
| `isMaximized` / `isMinimized` / `isActive` | bool | `isActive` 由 `Window.Activated` 维护 |
| `windowBounds` | `{x,y,width,height}` | 屏幕坐标，逻辑像素 |
| `dpiScale` | number | `XamlRoot.RasterizationScale` |
| `windowTitle` | string | |

通用控件键：

| key | 返回 |
|---|---|
| `actualWidth` / `actualHeight` / `bounds` | number / number / object；读取会触发 `UpdateLayout()`，但 16 ms 内最多一次，连读多个几何值不再重复全量布局，也不会在首帧返回 0 |
| `width` / `height` / `opacity` | number（设定值，可能为 NaN；序列化前会归一化为 null） |
| `isLoaded` / `isVisible` / `isEnabled` / `isFocused` | bool |
| `name` / `tag` / `controlType` | string |
| `text` / `password` | string（自动按控件类型取，按钮也能读文字） |
| `selectionStart` / `selectionLength` / `selectedText` | int / int / string |
| `isSpellCheckEnabled` / `acceptsReturn` / `isReadOnly` / `maxLength` | bool / bool / bool / int |
| `value` / `checked` | number / bool |
| `selectedIndex` / `selectedItem` / `selectedItems` / `selectedCount` / `itemCount` | int / string / array / int / int |
| `selectedTab` | string：`TabView` 当前选中标签的标题 |
| `tabOrder` / `tabRects` | string / array：`TabView` 的标签顺序（逗号分隔标题）、每个标签的 `text/pinned/x/y/w/h`（相对窗口内容的逻辑像素） |
| `date` / `time` / `color` | string（`color` 为 `#AARRGGBB`） |
| `isOpen` / `isExpanded` / `severity` / `isPaneOpen` | bool / bool / string / bool |
| `currentUrl` / `canGoBack` / `canGoForward` / `webTitle` | WebView2 运行时状态 |
| `isMuted` / `userAgent` / `isScriptEnabled` / `areDefaultContextMenusEnabled` / `isWebMessageEnabled` / `isZoomControlEnabled` | WebView2 设置（Core 未就绪时返回缓存值） |
| `position` / `duration` / `isPlaying` / `volume` / `playbackRate` / `isFullWindow` | Media 播放状态 |
| `verticalOffset` / `viewportHeight` / `extentHeight` | ScrollViewer 滚动状态 |
| `imageSource` | string 或 null |

读不到的键（节点不存在、键名拼错、控件类型不匹配）回 `ok=false` 并带 `error`，不静默返回
null。这些读取全部在 UI 线程同步完成，因此 `query` 里不能放异步操作；WebView2 的
`execute_script` 走 `invoke` + `scriptResult` 事件。

---

## 4. 事件

### 4.1 事件名

| 事件 | 触发控件 | `data` |
|---|---|---|
| `click` | Button, HyperlinkButton, ToggleButton, RepeatButton, SplitButton（左半边）, Checkbox | `{}` |
| `change` | Entry, Checkbox, Switch, ToggleButton, Slider, ComboBox, ListBox | 见下 |
| `submit` | Entry（回车） | `{ "text": "..." }` |
| `focus` / `blur` | 所有交互控件 | `{}` |
| `selectionChanged` | ComboBox, ListBox | `{ "index": 0, "value": "..." }` |
| `menu` | 挂了 `contextMenu` 的任意控件 | `{ "value": "...", "label": "..." }` |
| `pointerEntered` / `pointerExited` | 任意节点 | `{}`，鼠标进出元素边界 |
| `pointerPressed` | 任意节点 | `{}`，按下（含子元素冒泡） |
| `imageFailed` | Image | `{ "error": "...", "src": "..." }` |
| `navigationStarting` | WebView2（订阅后才挂监听） | `{ "url": "..." }` |
| `navigationCompleted` | WebView2 | `{ "url": "...", "isSuccess": true, "httpStatusCode": 200 }` |
| `sourceChanged` | WebView2 | `{ "url": "..." }`，含 JS 改地址，见 [3.5.4](#354-webview2-的导航事件与当前网址) |
| `closing` | Window | `{}`，关闭前触发，首版不支持取消 |
| `sizeChanged` | Window（根节点）/ 任意控件（非根节点） | `{ "width": 640.0, "height": 480.0 }` |
| `keyDown` / `keyUp` | 任意节点 | `{ "key": "A", "keyCode": 65, "ctrl": false, "shift": false, "alt": false, "windows": false }` |
| `gotFocus` / `lostFocus` | 任意节点 | `{}`，`focus`/`blur` 的别名 |
| `textChanging` | TextBox | `{ "text": "..." }` |
| `loaded` / `unloaded` | 任意节点 | `loaded` 带 `{width,height}`，`unloaded` 为 `{}` |
| `dragEnter` / `dragOver` / `dragLeave` / `drop` | 任意节点（先 `allowDrop=true`） | `{ "accepted": true }` |
| `tapped` / `doubleTapped` / `rightTapped` | 任意节点 | `{ "x": 12.0, "y": 8.0 }` |
| `pointerMoved` / `pointerReleased` / `pointerWheelChanged` | 任意节点 | `{x,y}`，滚轮额外带 `delta` |
| `contextRequested` | 任意节点 | `{}` |
| `valueChanged` | NumberBox, RatingControl, Slider | `{ "value": 3.0 }` |
| `colorChanged` | ColorPicker | `{ "color": "#FF0078D4" }` |
| `dateChanged` / `timeChanged` / `selectedDatesChanged` | 日期时间控件 | `{ "date": "..." }` / `{ "time": "..." }` |
| `querySubmitted` / `suggestionChosen` | AutoSuggestBox | `{ "text": "...", "value": "..." }` |
| `itemClick` | GridView, ListViewBase, BreadcrumbBar, TreeView | `{ "index": 0, "value": "..." }` |
| `expanding` / `collapsed` | Expander | `{ "expanded": true }` |
| `closeButtonClick` | InfoBar | `{}` |
| `tabCloseRequested` | TabView | `{ "value": "...", "nodeId": "n7" }`（`nodeId` 指向被关的那一页） |
| `tabSelected` | `PinnedTab` / `TabPage`（含 `select_tab`） | `{ "value": "..." }` |
| `paneOpening` / `paneClosed` / `displayModeChanged` | NavigationView | `{}` / `{ "mode": "..." }` |
| `refreshRequested` | RefreshContainer | `{}` |
| `modeChanged` | TwoPaneView | `{ "mode": "..." }` |
| `scriptResult` | WebView2（`execute_script` 的异步回执） | `{ "result": "...", "requestId": "r1" }` 或 `{ "error": "...", "requestId": "r1" }` |
| `webMessage` | WebView2（页面 `postMessage`） | `{ "data": ..., "raw": "...", "text": "..." }` |
| `contentLoading` | WebView2 | `{ "url": "...", "isErrorPage": false, "navigationId": 1 }` |
| `domContentLoaded` | WebView2 | `{ "url": "...", "navigationId": 1 }` |
| `processFailed` | WebView2 | `{ "kind": "...", "reason": "...", "exitCode": 0 }` |
| `newWindowRequested` | WebView2（`window.open`，需回执） | `{ "requestId": "w1", "url": "...", "name": "", "isUserInitiated": true }` |
| `downloadStarting` | WebView2（需回执） | `{ "downloadId": "d1", "url": "...", "suggestedFilePath": "...", "totalBytes": 94208 }` |
| `downloadProgress` | WebView2（100 ms 节流） | `{ "downloadId": "...", "bytesReceived": 4096, "totalBytes": 94208, "state": "InProgress" }` |
| `downloadStateChanged` | WebView2 | `{ "downloadId": "...", "state": "Completed", "interruptReason": "None", "filePath": "..." }` |
| `windowActivated` | Window | `{ "active": true }` |
| `windowStateChanged` | Window | `{ "state": "maximized" }` |
| `click`（菜单条目） | MenuBar / CommandBar（绑在父控件上） | `{ "index": 0, "value": "..." }` |

`tabSelected` 的 `data.value` 是标签文字（`text`）：用户点选任意标签页（`PinnedTab` /
`TabPage` 都一样）或 Python 侧调用 `select_tab` 都会回这一条。绑定了 `tabSelected` 的标签在
选中时由 `TabView.SelectionChanged` 触发；没绑定的话 `select_tab` 会补发一条，保证「调了就有
一条回执」。

用户点 `TabPage` 的关闭按钮时宿主回 `tabCloseRequested`：`data.value` 是标签文字，
`data.nodeId` 是被关标签页自己的节点 id（同名标签不会关错）。Python 侧 `TabView` 有默认内部
钩子据此销毁对应 `TabPage` 节点，用户仍可 `bind("tabCloseRequested", fn)` 收通知——二者互不
冲突。

`change` 事件同样覆盖扩展控件：`NumberBox` / `RatingControl` 带 `value`，日期时间控件带
`date` / `time`，`AutoSuggestBox` 带 `text`，`ColorPicker` 带 `color`，`Expander` 带
`expanded`，列表类带 `index` / `value`。`selectionChanged` 也覆盖 `GridView` / `FlipView` /
`Pivot` / `NavigationView` / `RadioButtons`。

窗口事件挂在根节点上：Python 侧照常 `win.bind("closing", fn)`，宿主会自动把处理器挂到
`Window` 而不是根元素。`sizeChanged` 在根节点上走窗口级、在其他节点上走控件级——同一事件名，
按节点是否为 root 分流。

`change` 的 `data` 按控件类型：

- Entry → `{ "text": "..." }`
- Checkbox / Switch / ToggleButton → `{ "checked": true }`
- Slider / ProgressBar → `{ "value": 42.0 }`
- ComboBox / ListBox → `{ "index": 1, "value": "..." }`

### 4.2 窗口标题栏事件

| 事件名 | 触发时机 | `data` |
|---|---|---|
| `closeRequested` | 用户点标题栏关闭按钮 | `null` |
| `maximizeRequested` | 用户点标题栏最大化/还原按钮，或双击标题栏 | `{"maximized": bool}` 表示当前是否最大化 |

这两条事件**只发给对应会话的根节点**，且不受节点的 `events` 映射影响——Python 侧由
`Window.on_close_request()` / `Window.on_maximize_request()` 专门接管：

- `closeRequested`：没注册回调时默认直接关窗；注册后可以拦下来询问用户。
- `maximizeRequested`：没注册回调时按钮是**禁用**的，根本收不到事件；
  注册后由回调自己决定最大化还是还原（见 3.1 节的 `maximizeRequestable`）。

### 4.3 回声抑制

Python 侧 `entry.configure(text="x")` 会触发宿主的 TextChanged，宿主无法区分「用户输入」与
「程序赋值」。

解决方式：宿主在**应用 `set` 操作期间**给该控件打标记，期间产生的事件带上 `echo: true`。
Python 侧收到 `echo` 事件时更新内部变量值，但**不触发**用户回调。

```
Python: entry.set("x")
   → patch {set: {text: "x"}}
Host:   打 echo 标记 → 赋值 → TextChanged 触发 → 事件带 echo:true → 清标记
Python: 收到 echo 事件 → 同步变量 → 不调用 on_change
```

---

## 5. 错误

| `code` | 含义 | 是否终止 |
|---|---|---|
| `bad-json` | 行不是合法 JSON | 否 |
| `bad-protocol` | 缺少必需字段 / 类型不对 | 否 |
| `version-mismatch` | 协议版本不兼容 | **是** |
| `unknown-type` | 未知控件类型 | 否 |
| `unknown-node` | 引用了不存在的节点 id | 否 |
| `unknown-session` | 引用了不存在的会话 id | 否 |
| `bad-property` | 属性值无法解析 | 否 |
| `bad-op` | 未知的 patch 操作 | 否 |
| `bad-micro` | `micro` 形状校验失败 | 否 |
| `internal` | 宿主内部异常 | 视情况 |

非终止错误会在 Python 侧以 `log` 回调或 `UiError` 形式暴露。`bad-micro` 专指 `micro` 的形状
校验失败：指令不在 `set` / `animate` / `wait` / `emit` 白名单、`program` 超 64 条、单条
`set` 超 32 个属性、动画 `ms>5000` 或 `steps>1000`、数值绝对值超 1e6 或为 `NaN` /
`Infinity`、目标节点不存在，都归这一类，不终止会话。

未知 `op` 不进这张表：它回 `warning` 并忽略。`bad-json` / `bad-protocol` 都只影响当前这一行：
宿主回 `error` 后继续读下一行，会话不断。**唯一的终止条件是 `version-mismatch`**——两端对
协议的理解根本不同，继续跑只会产生更难查的错。

C# 侧的 `Protocol/` 目录有 xUnit 单测（`host/PyWinUI.Host.Tests/`）锁住上述错误分级、JSON
口径（camelCase / 忽略 null / 中文不转义）、`NaN` / `Infinity` 清洗与坏行不致命的语义。测试
项目是纯 net8.0，直接链接宿主的 `Protocol/*.cs`，不需要桌面会话；CI 会运行它。

---

## 6. 完整示例

Python 侧：

```python
win = Window(title="Demo", width=640, height=480)
col = Column(win, padding=24, spacing=12)
label = Label(col, "你好", font_size=28)
Button(col, "点我", command=lambda: label.configure(text="点了"))
win.mainloop()
```

对应线上流量：

```
→ {"op":"hello","protocol":1,"client":"pywinui3/1.0.0-rc1","pid":8123}
← {"t":"ready","protocol":1,"host":"PyWinUI.Host/1.0.0-rc1","os":{"version":"10.0.19044","build":19044},"capabilities":{"mica":false,"acrylic":true,"roundedCorners":false,"backdrop":"acrylic"}}
→ {"op":"render","rev":1,"window":{"title":"Demo","width":640,"height":480,"backdrop":"auto"},"root":"n0","nodes":[{"id":"n0","type":"Column","props":{"padding":24,"spacing":12},"children":["n1","n2"]},{"id":"n1","type":"Label","props":{"text":"你好","fontSize":28}},{"id":"n2","type":"Button","props":{"text":"点我"},"events":{"click":"h1"}}]}
← {"t":"ack","rev":1}
← {"t":"event","id":"n2","event":"click","rev":1,"data":{}}
→ {"op":"patch","rev":2,"ops":[{"t":"set","id":"n1","props":{"text":"点了"}}]}
```

`ack` 只在 `render` 成功后回一条；点击回调里发出的 `patch`（以及任何后续 `patch`）都不再回
`ack`，Python 侧也不等待它。

---

## 7. 版本演进规则

- 协议版本是单调整数，**不兼容变更**才 +1；
- 新增可选字段**不算**不兼容变更，旧端忽略即可；
- 新增控件类型、新增事件名、新增 `op` / `t` 消息类型**不算**不兼容变更：旧端永远不会发出
  它；新端往旧宿主发未知 `op` 时，宿主回 `warning` 后忽略、不终止会话（见
  [2.1](#21-python--host)），协议版本因此仍为 1；
- 重命名字段、改变字段语义、删除字段**算**不兼容变更；
- `query` / `result`、`micro` / `microDone`、`warning` 都属于「新增消息类型」。旧宿主收到
  未知 `op` 现在回 `warning` 并忽略；字段缺失 / 类型错误回 `bad-protocol` 但同样不终止会话，
  只有版本不匹配才终止。功能上仍要求宿主同步更新（`App.query()` / `App.micro()` 需要宿主
  真的实现了对应语义），否则调用拿不到结果，只是不会把连接打断。
