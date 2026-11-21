# PyWinUI3

用 tkinter 的写法，画出真正的 WinUI 3 界面。

Python 进程只描述控件树，一个独立的 C# 进程负责渲染窗口，两端通过 stdin/stdout 交换
JSON Lines。`mainloop()` 的语义与 tkinter 一致；宿主崩溃不会带走 Python 解释器。

| | |
|---|---|
| **平台** | Windows 10 19041 (2004) 及以上 |
| **Python** | 3.10+，仅标准库，零第三方依赖 |
| **协议** | [docs/protocol.md](docs/protocol.md) 是唯一权威定义 |
| **能力** | 58 种控件、query/result 同步读回、micro 微代码、键盘/焦点/拖放/窗口状态事件 |
| **版本 / 许可** | 0.2.2 · GPL-3.0-only |

```python
import pywinui3 as ui

win = ui.Window(title="Hello", width=640, height=400)
ui.Label(win, "你好，WinUI 3", font_size=28)
ui.Button(win, "点我", command=lambda: print("clicked"))
win.mainloop()
```

---

## 目录

1. [快速开始](#1-快速开始)
2. [工作原理](#2-工作原理)
3. [项目结构](#3-项目结构)
4. [控件速查](#4-控件速查)
5. [基础用法](#5-基础用法)
6. [进阶用法](#6-进阶用法)
7. [示例](#7-示例)
8. [已知限制](#8-已知限制)
9. [开发与验证](#9-开发与验证)
10. [许可](#10-许可)

---

## 1. 快速开始

### 1.1 前置条件

| 需要 | 说明 |
|---|---|
| .NET SDK **8.0** | 只在构建宿主时需要；只装 Runtime 不够 |
| Python **3.10+** | 无第三方依赖 |
| Windows 10 **19041+** | 宿主基于 WinUI 3 / Windows App SDK 1.8 |
| WebView2 Runtime | **仅使用 `ui.WebView2` 时需要**；没装会自动降级，见 [6.3](#63-webview2-与运行时降级) |

工具链已在 Windows 10 21H2 / .NET 8.0.427 / Windows App SDK 1.8 上端到端验证。

### 1.2 安装

**从 release 安装**（推荐；仅 Windows，需要 Python 3.10+）：

```powershell
pip install https://github.com/SystenGames/PyWinUI3/releases/download/v0.2.4/pywinui3-0.2.4-py3-none-win_amd64.whl
```

这是当前已发布的版本；发布新版本后把两处版本号一起换成新标签。包**没有发布到 PyPI**，只能从这里装。

wheel 的平台标签是 `win_amd64`，在 Linux / macOS 上 pip 会直接拒绝安装——
而不是装成功之后一启动才崩。

**从源码构建**：顺序是**先构建宿主，再装 Python 包**。`tools/build_host.ps1` 执行
`dotnet build -c Release`，把产物复制进 `python/pywinui3/runtime/PyWinUI.Host/`；
打包时由 `python/MANIFEST.in` 递归收进该目录，`pyproject.toml` 的 `include-package-data`
一并带上。

```powershell
./tools/build_host.ps1          # 构建 Release 宿主 + 复制进 runtime/
pip install .                   # 安装 wheel，宿主随包一起安装
```

要打一个可直接分发的 wheel（会自动修正平台标签）：

```powershell
./tools/build_wheel.ps1                 # x64
./tools/build_wheel.ps1 -Platform ARM64 # ARM64
```

开发时可用可编辑安装，此时不必跑构建脚本，Python 侧直接从源码树查找宿主：

```powershell
dotnet build host/PyWinUI.Host/PyWinUI.Host.csproj -c Release
pip install -e python
```

产物 `PyWinUI.Host.exe` 就是渲染宿主。工程预设了 `WindowsPackageType=None` 与
`WindowsAppSDKSelfContained=true`，因此它是**免安装的裸 exe**（约 138 MB，自带完整
Windows App SDK 运行时），目标机器无需预装 Windows App Runtime。Python 侧会自动在源码树
或包内 runtime 目录定位它，也可以用环境变量 `PYWINUI3_HOST` 直接指定路径。

> Python 包名是 **`pywinui3`**（带 3，用于和同名项目区分），导入写作 `import pywinui3 as ui`。
> C# 宿主的程序集名仍是 `PyWinUI.Host`，与包名无关。

### 1.3 运行示例

```powershell
python examples/01_hello.py
```

想肉眼验收全部控件与事件，运行 `examples/02_controls.py`：操作任意控件都会弹窗回报
「哪个控件、什么事件、当前值」。

---

## 2. 工作原理

Python 桌面 UI 的选择有限：tkinter 老旧，PyQt 授权重，Web 套壳要拖一个浏览器。
WinUI 3 在 Windows 上是原生感最好的方案，却只有 C# / C++ 能用。PyWinUI3 把两件事拆开：

- **Python 负责描述界面**——控件树、属性、回调，都是普通 Python 对象；
- **C# 负责渲染**——独立进程，接收界面描述并绘制。

```
┌─ Python 进程 ────────────────────────────────┐
│  控件树 API    Patch 引擎    mainloop        │
└──────────────────┬───────────────────────────┘
                   │  JSON Lines / stdin-stdout
┌──────────────────┴───────────────────────────┐
│  协议解析    节点表    渲染器    事件桥       │  C# 宿主进程
└──────────────────┬───────────────────────────┘
                   │
              WinUI 3 / Windows App SDK
```

界面用「表单」描述：一棵扁平节点列表，父子关系由 `children` 字段表达。首帧发全量
`render`，之后只发增量 `patch`：

```json
{"op":"patch","rev":2,"ops":[
  {"t":"set","id":"n1","props":{"text":"Changed"}},
  {"t":"insert","parent":"n0","index":1,"node":{"id":"n3","type":"Label"}}
]}
```

节点有稳定 id，宿主维护 `id → 控件实例` 的映射，补丁按 id 定位；事件按
`(nodeId, eventName)` 回传并分发到 Python 回调。完整定义见 [docs/protocol.md](docs/protocol.md)。

### 为什么是两个进程，而不是 pythonnet 进程内加载

这是整个设计里最关键的取舍。WinUI 3 的 `Application.Start()` 会把调用线程整个吃掉去跑
消息泵：用 pythonnet 在 Python 进程内起 CLR 再起 WinUI，Python 主线程就被这个泵占了，
`mainloop()` 只能靠 hack 模拟，GIL 与 XAML 回调线程交织后的死锁极难排查；而且 UI 层一个
异常会直接带走 Python 解释器。

拆成两个进程后，Python 侧只依赖标准库，`mainloop()` 天然就是「主线程阻塞在事件队列上」，
宿主崩溃可被检测、可恢复。代价是跨进程往返——本地管道单程 0.1–0.5 ms，对 UI 可忽略。

### 为什么用 stdin/stdout，而不是命名管道或 TCP

Python 标准库没有 Windows 命名管道的实现，用 `ctypes` 调 Win32 API 太重；TCP 会占端口，
还可能被防火墙拦截。stdin/stdout 零依赖，句柄生命周期天然绑定进程，对端一死立刻读到
EOF，连心跳包都省了——LSP 用的也是这套。

---

## 3. 项目结构

```
host/PyWinUI.Host/            C# WinUI 3 渲染宿主
  Protocol/                   消息类型与解析（纯逻辑，可独立单测）
  Transport/                  stdin/stdout 行分帧
  Rendering/                  控件工厂、属性映射、增量补丁、WebView2、微代码
  Interop/                    事件回传
  Session.cs                  消息循环与 invoke 分发
host/PyWinUI.Host.Tests/      xUnit 协议层单测（纯 net8.0，不依赖桌面会话）
python/pywinui3/              Python 库（包名带 3）
  protocol.py / transport.py  协议编解码、宿主进程与管道
  app.py                      生命周期、事件循环、16ms patch 合并窗口
  window.py / widgets.py / extended.py / layout.py / variables.py
  runtime/PyWinUI.Host/       构建脚本复制进来的宿主产物（随 wheel 打包）
python/MANIFEST.in            递归包含 runtime/
.github/workflows/ci.yml      Python 单测 + 宿主编译 + C# 协议层单测
docs/protocol.md              协议权威定义
examples/                     10 个可运行示例（01~10）
tests/                        单测 + 端到端 smoke + 拖拽回归
tools/build_host.ps1          构建 Release 宿主 + 复制进 runtime/
```

---

## 4. 控件速查

**窗口与布局**

| 类 | 说明 |
|---|---|
| `App` | 宿主进程与事件循环 |
| `Window` | 顶层窗口，同时是控件树根 |
| `Column` / `Row` | 垂直 / 水平堆叠 |
| `Grid` | 二维网格，子控件用 `.grid(row=, column=)` 定位 |
| `Scroll` | 纵向滚动区，**必须给 `height`**（或放进 Grid 的 `"*"` 行） |
| `Frame` | `Column` 的别名，照顾 tkinter 习惯 |

**基础控件**

| 分组 | 类 |
|---|---|
| 文本 / 输入 | `Label` `Entry` `Checkbox` `Radio` `Switch` |
| 按钮 | `Button` `HyperlinkButton` `ToggleButton` `SplitButton` `RepeatButton` |
| 列表 / 选择 | `ComboBox` `ListBox` `Slider` |
| 进度 | `ProgressBar` `ProgressRing` |
| 内容 | `Image` `Media` `WebView2` `Separator` |

**扩展控件**（共 43 种，均为 WinUI 3 / Windows App SDK 1.8 内置类型，含 2 类标签元素）

| 分组 | 类 |
|---|---|
| 表单输入 | `NumberBox` `PasswordBox` `RichTextBlock` `RichEditBox` `AutoSuggestBox` `DatePicker` `TimePicker` `CalendarDatePicker` `CalendarView` `RatingControl` `ColorPicker` |
| 按钮 / 头像 | `DropDownButton` `ToggleSplitButton` `PersonPicture` |
| 数据 / 导航 | `GridView` `FlipView` `Pivot` `TabView` `TreeView` `NavigationView` `MenuBar` `CommandBar` `BreadcrumbBar` `ItemsView` `RadioButtons` `SelectorBar` |
| 标签元素（`TabView` 子节点） | `PinnedTab` `TabPage` |
| 信息 / 容器 | `InfoBar` `Expander` `BorderBox` `Viewbox` `RelativePanel` `TwoPaneView` `RefreshContainer` |
| 画布 / 图元 | `Canvas` `Rect` `Ellipse` `Line` `Path` `Polygon` `Polyline` `Icon` |

**变量与工具**：`StringVar` `IntVar` `DoubleVar` `BooleanVar`、`Event`、`dialog()`、
`PROTOCOL_VERSION`、各异常类。`Widget` 是所有控件的基类。

---

## 5. 基础用法

### 5.1 窗口

```python
win = ui.Window(
    title="工具",
    width=900, height=600,
    x=120, y=80,               # 位置（逻辑像素）；给了就不居中
    icon=r"C:\icons\app.ico",  # 标题栏和任务栏图标
    always_on_top=True,
    minimizable=True, maximizable=True,
)

win.always_on_top = False       # 运行中改也行，走 window 补丁
win.width = 1000
win.on_resize(lambda e: print(e.data["width"], e.data["height"]))
win.on_close(lambda e: print("要关了"))   # 关闭前触发，首版不能取消
```

`Window(layout="grid")` 让窗口根节点直接成为 Grid，这是实现「侧栏 + 内容区」整屏布局的
关键，见 [6.4](#64-可收缩侧栏与宽度动画)。

### 5.2 布局与滚动

整窗滚动默认开启：内容比窗口高时自动出现滚动条，无需自己包一层。

```python
win = ui.Window(title="长表单")          # scroll=True 是默认值
col = ui.Column(win, padding=20, spacing=10)
for i in range(50):
    ui.Label(col, f"第 {i} 行")          # 排到窗口外面也能滚到
```

只想让某一块滚动，用 `Scroll`——它必须有一个确定高度：

```python
box = ui.Scroll(col, height=200, spacing=4)
for line in lines:
    ui.Label(box, line)
```

`Window(scroll=False)` 关闭整窗滚动（超出部分被裁切）；运行中 `win.scroll = False`
也能切换。

### 5.3 事件、变量与弹窗

`command=` 无参，与 tkinter 一致；`bind()` 的回调收到 `Event` 对象：

```python
entry.bind("change", lambda e: print(e.text))
entry.bind("submit", lambda e: print("回车了", e.text))
```

`StringVar` / `IntVar` / `DoubleVar` / `BooleanVar` 支持双向绑定：

```python
name = ui.StringVar("world")
ui.Entry(win, textvariable=name)
name.trace(lambda v: print("变成", v))
```

```python
ui.dialog("控件事件", "Button 被点击了")
```

`dialog()` 是模态的，会阻塞窗口交互，适合演示和调试；生产界面请用界面内的状态文字。
第四个参数 `kind` 可选 `info` / `warn` / `error` / `success`，图标、颜色与按钮文字各不相同。

### 5.4 读取运行时状态

界面的「设定值」与「真实状态」不是一回事：`width=200` 是设定值，布局完成后可能只有 184；
`web.source` 是写进去的目标，浏览器里的地址可能已经跳转。`query()` 直接问宿主：

```python
btn = ui.Button(win, "确定")

print(btn.actual_width, btn.actual_height)   # 真实尺寸，读前会强制完成布局
print(btn.is_loaded, btn.is_focused, btn.control_type)
print(btn.query("text"))                     # 任意键都能读，见下

print(win.state, win.is_maximized, win.is_active)
print(win.window_bounds)                     # {"x","y","width","height"}
```

常用键：`actualWidth` / `bounds` / `isLoaded` / `isVisible` / `isEnabled` / `text` /
`value` / `checked` / `selectedIndex` / `selectedItems` / `selectionStart` /
`selectionLength` / `selectedText` / `itemCount` / `date` / `time` / `color` /
`currentUrl` / `canGoBack` / `canGoForward` / `position` / `duration` / `isPlaying` /
`verticalOffset` / `controlType` / `dpiScale`（完整清单见
[协议 3.7](docs/protocol.md#37-query-键清单)）。

各控件也封装了常用读取：`entry.selection`、`entry.selected_text`、`web.can_go_back`、
`web.browser_url`、`media.duration`、`media.is_playing`、`scroll.vertical_offset`、
`list.selected_item`。

窗口动作与状态事件：

```python
win.maximize(); win.restore(); win.minimize(); win.activate()
win.on_state_changed(lambda e: print(e.data["state"]))
win.on_activate(lambda e: print("焦点", e.data["active"]))
```

`query()` 是**同步**的（默认 2 秒超时），会阻塞主线程直到宿主回 `result`。等待期间到达的
事件不会丢，会暂存到内部队列、下一轮事件循环照常派发，因此在回调里调用也安全。读不到值
会抛 `UiError`，不会假装读到 `None`。

事件面同时覆盖了键盘、焦点、加载、尺寸、拖放与指针：

```python
entry.on_key_down(lambda e: print(e.data["key"], e.data["ctrl"]))
entry.on_loaded(lambda e: print("布局完成", e.data["width"]))
entry.on_got_focus(lambda: print("聚焦"))
entry.enable_drop()
entry.on_drop(lambda e: print("放下了"))
```

---

## 6. 进阶用法

### 6.1 按钮、图标与右键菜单

```python
ui.Button(col, "普通")
ui.Button(col, "保存", icon="\uE74E")            # 图标 + 文字
ui.Button(col, "强调色", variant="accent")        # 系统强调色样式
ui.Button(col, "图标按钮", icon="\uE713")         # 只有图标
ui.Button(col, "自定义", background="#0F6E56")    # 任意颜色
```

自定义背景色会连同 hover / 按下两个状态一起覆盖——只设 `Background` 的话，鼠标移上去会
变回系统默认色。`icon` 用 Segoe Fluent Icons 码点，WinUI 自带该字体，无需额外资源文件。

悬停提示与右键菜单对**所有控件**都可用，也可在构造时直接传 `tooltip=`：

```python
btn = ui.Button(col, "删除")
btn.set_tooltip("从列表里移除这一项")

btn.set_menu([
    {"label": "移到废纸篓", "glyph": "\uE74D", "value": "trash"},
    {"label": "彻底删除", "glyph": "\uE711", "value": "purge"},
    "-",                                              # 分隔线
    {"label": "撤销", "value": "undo", "enabled": False},
])
btn.on_menu(lambda e: print("点了", e.data["value"]))
```

三种特殊按钮：

```python
bold = ui.ToggleButton(col, "加粗", icon="\uE8DD")     # 双态：按下保持，再按弹回
bold.value = True                                    # 程序里改状态
bold.bind("change", lambda e: print(e.data["checked"]))

save = ui.SplitButton(                               # 左边执行，右边弹菜单
    col, "保存", icon="\uE74E",
    items=[
        {"label": "另存为…", "value": "save-as"},
        {"label": "导出为 PDF", "value": "export"},
        "-",
        {"label": "导出为图片", "value": "export-image"},
    ],
)
save.bind("click", lambda e: print("左半边：默认动作"))
save.on_menu(lambda e: print("右半边：", e.data["value"]))

ui.RepeatButton(col, "+", repeat_interval=80)        # 按住连续触发
```

`ToggleButton` 与 `Checkbox` 都是开/关：前者当模式开关（工具栏加粗、视图切换），后者当
表单选项。`RepeatButton` 的 `click` 在按住期间不断回传，回调里别做重活。

### 6.2 进度、图片、媒体与列表

**进度指示**

```python
ui.ProgressBar(col, value=45)                     # 确定进度
ui.ProgressBar(col, indeterminate=True)           # 不确定态，光条来回跑
bar.indeterminate = True                          # 运行中切换也行

ui.ProgressRing(col, size=40)                     # 不确定态加载圈
ring.active = False                               # 停下
```

**图片**：`src` 除本地路径和 URL 外，还接受 `data:image/png;base64,...`——程序里算出来的
图（截图、图表）可直接贴上去，不必先写临时文件。

```python
ui.Image(col, src="data:image/png;base64," + b64, height=120)
img.on_error(lambda e: print("加载失败", e.data["error"]))
```

**媒体播放器**

```python
player = ui.Media(col, src=r"C:\videos\demo.mp4", height=360)
player.play()
player.pause()
player.stop()
player.seek(30)              # 跳到第 30 秒；也可 player.position = 30
player.volume = 0.6
player.loop = True
player.muted = False
player.playback_rate = 1.5   # 0.25–4

player.bind("mediaOpened", lambda e: print("时长", e.data["duration"], "秒"))
player.bind("mediaEnded", lambda e: print("放完了"))
player.bind("mediaFailed", lambda e: print("失败", e.data["error"]))
```

本地路径与 http(s) URL 都支持。`show_controls=True`（默认）使用系统自带的播放/暂停/进度条；
关掉后可用自己的按钮通过 `play()` / `pause()` 控制。

**单选框**：同一个 `group` 自动互斥，无需手动取消其它项。

```python
ui.Radio(col, "选项甲", group="g1", checked=True)
ui.Radio(col, "选项乙", group="g1")
ui.Radio(col, "选项丙", group="g1")
```

**列表项图标**：`items` 既可传字符串，也可传带图标的对象，可混用。

```python
ui.ListBox(col, [
    {"text": "文档", "glyph": "\uE8A5"},
    {"text": "图片", "glyph": "\uEB9F"},
    "没有图标的普通项",
])
```

### 6.3 WebView2 与运行时降级

```python
web = ui.WebView2(col, source="https://www.example.com", height=360)
web.navigate("https://www.bing.com")   # 等价于 web.source = "..."
web.reload()
web.go_back()
web.go_forward()

print(web.source)        # 最后一次写进去的目标地址
print(web.current_url)   # 浏览器当前的真实地址（含页面内跳转、重定向）
web.on_navigate(lambda e: print(e.data["url"], e.data["isSuccess"]))
web.navigate_to_string("<h1>Hello</h1>")   # 直接渲染一段 HTML
```

`source` 与 `current_url` 不是一回事：前者是设定的目标，后者是宿主从导航事件回报的**实际
地址**。页面里点链接、服务端重定向、JS 改 `location.hash` 之后两者会不同。宿主订阅了
`navigationStarting` / `navigationCompleted` / `sourceChanged` 三类事件；`on_navigate(fn)`
是绑 `navigationCompleted` 的快捷方式，`data` 里还有 `isSuccess` 与 `httpStatusCode`。
`current_url` 的同步不占用用户回调，照样可以 `web.bind("sourceChanged", fn)`。

`navigate_to_string()` 直接渲染 HTML 字符串，不需要文件或本地服务器；这类页面没有真实
地址，`current_url` 通常是 `about:blank`。

**页面 ↔ Python 双向通信**：页面 `window.chrome.webview.postMessage(x)` 会触发 `webMessage`
事件；Python 用 `post_message()` 往页面发消息。字符串与结构化值都支持（要求
`isWebMessageEnabled=True`，默认开启）。

```python
web.on_message(lambda e: print("页面说：", e.data["data"]))
web.post_message({"type": "ping", "seq": 1})
rid = web.execute_script("document.title")          # scriptResult 会带回 rid
```

**新窗口与下载**都由 Python 回执决定：

```python
def on_open(e):
    web.respond_new_window(e.data["requestId"], allow=True)   # 在当前视图打开

web.on_new_window_request(on_open)

def on_download(e):
    did = e.data["downloadId"]
    web.respond_download(did, allow=True, file_path=r"D:\out\a.bin")

web.on_download_starting(on_download)
web.on_download_progress(lambda e: print(e.data["bytesReceived"]))
web.on_download_state_changed(lambda e: print(e.data["state"]))
```

还有 `contentLoading` / `domContentLoaded` / `processFailed` 事件、`download_action()`
（暂停/恢复/取消）、`open_devtools()`，以及 `muted` / `user_agent` / `script_enabled` /
`context_menus_enabled` / `web_message_enabled` / `zoom_control_enabled` 等设置（Core 就绪前
自动缓存）。完整语义见 [协议 3.5.5](docs/protocol.md#355-webview2-扩展能力)。
`permissionRequested` 暂未接；WinUI 3 不暴露程序化 `zoomFactor`。

**运行时降级**：控件由 WinUI 3 的 `WebView2` 承载，背后是系统安装的 Edge WebView2
Runtime；本项目**不打包、也不替你安装**运行时。宿主在建节点时探测：

- 装了 → 正常渲染网页，支持 `https://` / `file:///` / `http://localhost`；
- 没装 → 该控件自动换成灰色矩形，居中显示「加载失败」，下方一行
  `该控件无法正常加载：Webview2 Runtime未找到。`，其余界面照常工作，不白屏、不崩溃，
  此时该控件的方法调用是安全的空操作。

不想卸载运行时也能验证降级：启动前设 `PYWINUI3_WEBVIEW2_FALLBACK=1`，或直接跑
`examples/09_webview2.py`。探测规则见 [协议 3.5.3](docs/protocol.md#353-webview2-的运行时降级)。

### 6.4 可收缩侧栏与宽度动画

`ListBox(auto_collapse=True)` 让列表自己当侧栏：鼠标移进去展开、点中某项后钉住、点内容区
才收起。宽度以 100 步指数缓出变化，总时长 320 ms，最后一步 snap 到目标值。

```python
win = ui.Window(title="工具", layout="grid", scroll=False,
                rows=["*"], columns=["auto", "*"])   # auto 列跟着侧栏宽度走

nav = ui.ListBox(win, [
    {"text": "概览", "glyph": "\uE80F"},
    {"text": "任务", "glyph": "\uE9D5"},
], width=200, auto_collapse=True, collapsed_width=40)
nav.grid(row=0, column=0)

body = ui.Scroll(win, padding=20)      # 右侧内容区，独立滚动
body.grid(row=0, column=1)
body.bind("pointerPressed", lambda e: nav.release())   # 点内容区收起
```

`layout="grid"` 是关键：窗口根节点直接就是 Grid，`*` 行列才拿得到真实剩余高度。普通
`Grid(win)` 会被窗口的 StackPanel 包一层，高度变成无限，里面的东西撑不满也滚不动。

宽度必须显式给定，且大于 `collapsed_width`——折叠只改宽度，不给宽度就没有「展开态」。
不想要悬停行为就用 `hover_expand=False`，改用 `nav.toggle()` / `nav.set_open(True/False)`。

这套动画任意控件都能单独调：

```python
panel.animate_width(56)                       # 一条 micro，宿主本地跑完缓出
panel.animate_width(200, steps=12, interval=24)
```

`animate_width()` 早期实现是每步发一条 `patch`（20 步就是 20 次往返 + 20 条 ack）；现在只发
**一条 `micro` 消息**，把整段补间交给宿主本地跑完，见 [6.7](#67-性能设计与微代码)。

### 6.5 扩展控件

第 4 章列出的扩展控件（含 2 类 `TabView` 标签元素，见 [6.6](#66-标签页))用法与基础控件完全
一致——构造传属性、赋值发 patch、`bind()` 挂事件、`query()` 读状态。几组典型写法：

```python
# 表单输入：数值、日期、评分、颜色、建议列表
num = ui.NumberBox(col, value=42, min=0, max=100, step_frequency=2, header="数量")
num.bind("change", lambda e: print(e.data["value"]))

ui.DatePicker(col, date="2026-11-11", header="开始日期")
ui.RatingControl(col, value=4)
ui.ColorPicker(col, color="#FF0078D4")

suggest = ui.AutoSuggestBox(col, items=["Alpha", "Beta", "Gamma"], placeholder="搜索")
suggest.bind("querySubmitted", lambda e: print("提交", e.data["text"]))
```

```python
# 数据 / 导航：多选网格、树、原生导航侧栏
grid_view = ui.GridView(col, items=["一", "二", "三"],
                       selection_mode="multiple", height=200)
grid_view.bind("itemClick", lambda e: print(e.data["value"]))
print(grid_view.selected_items)     # 所有选中项，问宿主要

ui.TreeView(col, items=[{"label": "根", "children": ["子1", "子2"]}], height=220)
ui.NavigationView(col, items=["首页", "设置"], pane_display_mode="left")
```

`TreeView` 的 `items` 用 `{"label": ..., "children": [...]}` 表达层级；`MenuBar` /
`CommandBar` 的条目点击绑在父控件上，事件名是 `click`，用 `e.data["index"]` 区分点的是哪一项。

```python
# 信息 / 容器：提示条、折叠面板、带背景的盒子
info = ui.InfoBar(col, title="成功", message="已保存", severity="success")
info.hide()

exp = ui.Expander(col, header="高级选项", is_expanded=True)
ui.Checkbox(exp, "启用实验特性")

box = ui.BorderBox(col, background="#FFF0F0F0", padding=12)
ui.Label(box, "BorderBox 里的子控件")
```

`BorderBox` / `Viewbox` / `Expander` / `NavigationView` / `TwoPaneView` /
`RefreshContainer` 都能直接放子控件，和 `Column` 一样。`RelativePanel` 也开放了容器能力，
但 WinUI 的附加对齐属性（`AlignLeftWithPanel` 等）暂未映射，见第 8 章。

输入控件的增强能力：

```python
entry = ui.Entry(col, text="abcdef")
entry.select_range(1, 3)
print(entry.selection, entry.selected_text)
entry.copy(); entry.clear(); entry.paste()
entry.set_spellcheck(False)
entry.configure(accepts_return=True, is_spell_check_enabled=False)
```

### 6.6 标签页

`TabView` 有两类专用子节点，控件总数因此从 56 增至 58：

- **`PinnedTab`（固定选项卡）**：和 `TabPage` 一样是真正的 `TabViewItem`，同样有图标、
  同样能放子控件，区别只在 `IsClosable=false`、`CanDrag=false`，以及按下时会临时关闭
  整个标签条的重排。由于标签条内部重排没有可取消的公开事件，宿主额外记录每个固定标签的
  规范顺序，一旦顺序被改动就复位：结果是**不可关闭、拖不动，且始终排在普通标签之前**。
  属性 `text` / `icon` / `minWidth` / `maxWidth`；方法 `select()`。
- **`TabPage`（标签页）**：渲染成可关闭的 `TabViewItem` 放进 `TabItems`，可拖动重排、
  可关闭。`Content` 预置一层 StackPanel，**能放子控件**。属性 `text` / `closable` /
  `icon` / `minWidth` / `maxWidth`；方法 `close()` / `select()`。

两类标签的 `icon` 既可以是 Segoe 字形（如 `"\uE80F"`），也可以是本地图片路径
（`.png` / `.jpg` / `.ico` / `.bmp` / `.svg`）；SVG 走 `SvgImageSource`，栅格化为 32 px
后显示，高 DPI 下依然清晰。`min_width` / `max_width` 直接约束宽度，标题超出时显示省略号，
但 `query("text")` 读回的仍是完整标题。

```python
tabs = ui.TabView(col, can_reorder_tabs=True, tab_width_mode="sizeToContent")

# 固定选项卡：没有 ×，也拖不动，但同样是标签页、同样能放内容
home = tabs.add_pinned("首页", icon="\uE80F")
ui.Label(home, "固定页也能放子控件")

page = tabs.add_page(
    "一个会很长很长很长很长很长的标签标题",
    icon="\uE8A5",
    min_width=120, max_width=200,   # 超宽自动省略成 "一个会很长…"
)
ui.Label(page, "这里是标签内容")        # TabPage 能放子控件
ui.Button(page, "确定")
page.select()                          # 选中并回 tabSelected

tabs.add_page("预览", icon="\uE8B7")
tabs.bind("tabCloseRequested", lambda e: print("关了", e.data["value"]))
```

`add_page()` / `add_pinned()` 是便捷方法；`items=[...]` 仍是快捷写法，生成普通可关闭
标签页、不能放子控件，**不要与 `TabPage` 子节点混用**，否则两组标签会同时出现。`TabView` 的
`tab_width_mode` 支持 `equal`（默认）/ `sizeToContent` / `compact`。`select_tab` 对两类标签
都能真正切换选中项并回一条 `tabSelected`；用户点关闭按钮时宿主回 `tabCloseRequested`
（`data.value` 是标签文字，`data.nodeId` 是被关页的节点 id），默认内部钩子据此销毁对应
`TabPage` 节点，用户仍可 `bind()` 收通知，二者不冲突。

诚实限制：拖动重排由 WinUI 内置完成，宿主不回传重排事件，Python 侧 `children` 顺序不会
自动同步；`TabPage` 构造时不支持 `selected=True`（请用 `select()`）；子标签一律追加，忽略
协议 `insert` 的 `index`；固定标签被拖到别处时只在拖动结束那一刻归位，过程中会看到短暂位移。

### 6.7 性能设计与微代码

跨进程往返单程 0.1–0.5 ms，单看不贵，但「每帧 / 每步一条」会把它放大。这版做了几件事：

- **`ack` 只在 `render` 成功后回一条**。`patch` 不再逐条回 ack（Python 侧本来就忽略），
  `invoke` / `dialog` / `close` 也不发 ack。
- **16 ms patch 合并窗口**。同帧对同一节点的多次属性赋值按节点合并，窗口结束合成一条
  `patch` 发出；`query()` / `invoke` / `dialog` / `close` / `micro` 之前强制 flush，保证同步
  读回看到最新状态。结构类 `insert` / `remove` / `move` 不参与合并，也不跨越它们合并。
- **几何读取节流**。`actualWidth` / `actualHeight` / `bounds` 的 `UpdateLayout()` 在 16 ms
  内最多跑一次，连读多个几何值不再重复全量布局。
- **`pointerMoved` 按 16 ms 节流**。鼠标移动能到 120Hz 以上，而一次「改路径 + 重绘」在宿主
  侧要几毫秒，全量转发会把 UI 线程打满。节流对齐 60fps，松手位置由 `pointerReleased` 补上。
- **定时器精度提到 1 ms**。Windows 默认约 15.6 ms，`after(16)` 实际会落到 ~31 ms，
  动画只有半帧率。进入 `mainloop` 时调 `timeBeginPeriod(1)`，退出时还原。
- **日志默认零磁盘 IO**。`HostLog.Write` 只记异常 / 生命周期 / 告警，使用长驻
  StreamWriter；逐条报文 `<<` / `>>` 走 `HostLog.Trace`，默认不落盘，设
  `PYWINUI3_TRACE=1` 才写 `%TEMP%/pywinui3-host-<pid>.log`（按进程分开）。
- **`animate_width()` 一条消息跑完整段补间**（见 [6.4](#64-可收缩侧栏与宽度动画)），不再
  每步一次往返。

`micro` 把这件事推到极致：一段连续动作在宿主本地跑完，只有一条入站消息。

```python
panel = ui.Column(col, width=100)

panel.micro([
    {"i": "set",     "props": {"opacity": 0.0}},
    {"i": "animate", "prop": "width", "to": 240.0, "ms": 300, "steps": 20},
    {"i": "animate", "prop": "opacity", "to": 1.0, "ms": 200, "steps": 12},
    {"i": "wait",    "ms": 100},
    {"i": "emit",    "event": "revealed", "data": {"ok": True}},
], notify=True)

panel.animate_width(200, steps=12, interval=16)   # 底层就是一条 micro
```

`App.micro(node_id, program, notify=False)` 返回请求 id；`notify=True` 时宿主跑完回一条
`microDone`（`id` / `ok` / `error`），缺省不回。`program` 的指令白名单只有四种：

| `i` | 字段 | 语义 |
|---|---|---|
| `set` | `props` | 走与 patch 相同的属性链、带回声抑制，不许改 `events` |
| `animate` | `prop`（width/height/opacity）+ `to` / `ms` / `steps` / `divisor` | 闭式解指数缓出，最后一步 snap |
| `wait` | `ms` | 等待 |
| `emit` | `event` + 可选 `data` | 主动回一条普通事件 |

**micro 不是任意代码执行**：宿主按固定语义解释，不动态编译、不反射、不访问文件 / 网络 /
进程。限额：`program` ≤ 64 条、单条 `set` ≤ 32 个属性、动画 ≤ 5000 ms / ≤ 1000 步、数值
绝对值 ≤ 1e6 且拒绝 NaN / Infinity；目标节点必须存在；同一节点的新程序顶掉旧程序（代次）。
形状校验失败回非终止的 `bad-micro`。

告警走 `t:"warning"`（宿主对同一条 1 秒去重后才写日志），Python 侧默认打到 stderr，可用
`App(on_warning=...)` 接管；未知 `op` 也是回一条 warning 后忽略，不终止会话。

### 6.8 画布与图元

`Canvas` 是绝对定位容器，子元素用 `left` / `top` 定位：

```python
canvas = ui.Canvas(win, width=800, height=600)

ui.Rect(canvas, left=40, top=40, width=160, height=100,
        radius_x=12, radius_y=12, fill="#E8F0FE", stroke="#0F6CBD", stroke_width=2)
ui.Ellipse(canvas, left=240, top=40, width=140, height=100, fill="#FFF4CE")
ui.Line(canvas, x1=0, y1=0, x2=200, y2=120, stroke="#C42B1C")
ui.Polygon(canvas, points=[[680, 70], [720, 30], [760, 70]], fill="#DFF6DD")
ui.Path(canvas, d="M 40 300 C 140 200, 240 400, 340 300", stroke="#5C2E91", stroke_width=3)
ui.Label(canvas, "文字也能放", left=60, top=150)
```

`Path` 的 `d` 支持完整的 SVG 路径命令（`M L H V C S Q T A Z`，小写为相对坐标），
宿主自己解析，不依赖 XAML。

**视图缩放平移**走 `view`，一条属性生效，开销与图元数量无关：

```python
canvas.view = {"scale": 1.5, "offsetX": 40, "offsetY": -20}
```

**单个图元的变换**：

```python
box.transform = {"rotate": 15, "scaleX": 1.2, "scaleY": 1.2}
```

**拖动**靠带坐标的指针事件，坐标相对父容器：

```python
drag = {"dx": 0.0, "dy": 0.0}

def on_press(e):
    drag["dx"] = e.data["x"] - box.left
    drag["dy"] = e.data["y"] - box.top

def on_move(e):
    box.left = e.data["x"] - drag["dx"]
    box.top = e.data["y"] - drag["dy"]

box.bind("pointerPressed", on_press)
box.bind("pointerMoved", on_move)      # 绑了 moved，按下会自动捕获指针
box.bind("pointerReleased", lambda e: None)
```

**性能边界**：每个图元都是真实 XAML 元素。几百个图元、拖动单个对象完全流畅；
上千个图元同帧全量改属性会掉到 10 fps 左右。缩放和平移务必用 `view`，
不要逐个改 `left` / `top`。

完整可运行示例见 `examples/14_canvas.py`。

### 6.9 Win2D 画布（大量图元）

`Canvas2D` 把图元数组一次性交给 GPU 绘制，适合"几千个图元、只读或整体刷新"的场景：

```python
canvas = ui.Canvas2D(win, width=860, height=520, shapes=[
    {"type": "line", "x1": 0, "y1": 0, "x2": 200, "y2": 120, "stroke": "#4FC3F7"},
    {"type": "rect", "x": 240, "y": 40, "w": 120, "h": 80, "radius": 8, "fill": "#DFF6DD"},
    {"type": "ellipse", "cx": 500, "cy": 90, "rx": 50, "ry": 40, "fill": "#FFF4CE"},
    {"type": "path", "d": "M 0 200 C 80 120, 160 280, 240 200", "stroke": "#C42B1C"},
    {"type": "text", "x": 20, "y": 300, "text": "Win2D", "size": 18, "color": "#0F6CBD"},
])
```

图元带 `id` 时可以增量更新，**只发变化的字段**：

```python
canvas.shapes = [{"id": "p1", "type": "ellipse", "cx": 10, "cy": 10, "rx": 3, "ry": 3}]
canvas.update_shapes([{"id": "p1", "cx": 40, "cy": 60}])   # 只改位置
```

`update_shapes` 走 invoke 通道，不参与 16ms 属性合并，同一帧多次调用不会丢更新。

**与 `Canvas` 的分工**

| | `Canvas`（XAML） | `Canvas2D`（Win2D） |
|---|---|---|
| 每图元一个 UIElement | 是 | 否 |
| 单独绑事件 / 点选 | 能 | 不能（要自己算命中） |
| 改单个图元 | 一条 patch，约 7ms | 增量更新，约 2ms |
| 几千图元静态渲染 | 掉帧 | 流畅 |
| 适用 | 交互编辑 | 大量只读渲染 |

依赖已内嵌：`Microsoft.Graphics.Win2D` 的两个 DLL 随构建进输出目录，
目标机器不需要额外安装。实测 800 个动态图元约 52fps（Python 每帧生成约 2ms）。

完整示例见 `examples/15_win2d.py`。

### 6.11 多窗口与窗口控制

一个 `App` 可以有多扇窗。第一扇是 `main` 会话，之后每扇自动分配 `w1` / `w2` ...：

```python
app = ui.App()
main = ui.Window(app, title="主窗口")
second = ui.Window(app, title="副窗口")   # session = w1

second.hide()     # 隐藏但保留会话
second.show()     # 显示并激活
second.focus()    # 只激活
second.close()    # 只关这一扇，main 不受影响

main.mainloop()   # 启动时会把所有已创建窗口都渲染出来
```

每扇窗有独立的控件树，事件按会话路由。main 窗口关闭会退出整个应用，
和单窗口时行为一致。

运行时再新建窗口也可以：构造 `Window(app, session="custom")` 后调 `win.render()`
把控件树发给宿主，宿主会为新会话建一扇窗。

协议层为向后兼容，main 会话的报文不写 `session` 字段，
与旧版宿主完全一致。完整示例见 `examples/17_multiwindow.py`。

### 6.12 托盘图标

```python
tray = ui.TrayIcon("app", icon="C:/path/app.ico", tooltip="我的应用")
tray.on_click(lambda e: print("点击", e.name))
tray.on_right_click(lambda e: print("右键"))
tray.update(tooltip="新提示")
tray.destroy()
```

托盘回调走 Win32 消息，宿主复用主窗口句柄挂 WndProc 子类接收，
不需要额外的消息循环。退出时会自动清理所有图标，不留僵尸。

### 6.13 多窗口并发弹窗与文件对话框

对话框按会话串行：同一扇窗同时只能开一个，不同窗口可以并发弹窗。

```python
app.dialog("提示", "主窗口的消息", session="main")
app.dialog("提示", "副窗口的消息", session="w1")
```

文件对话框现在优先走系统 `IFileDialog` COM 接口，**支持自定义标题和初始目录**：

```python
paths = app.file_dialog("open", title="请选择文件", initial_dir="D:/docs")
```

COM 调用不可用时自动回退到 WinRT picker（标题会被忽略）。

### 6.10 D3D11 三维视口

`Viewport3D` 走真 GPU 管线：有深度缓冲（正确遮挡）、顶点/像素着色器（真实光照），
面数上限几十万，且**渲染循环完全在宿主里跑**，旋转不产生跨进程流量。

```python
viewport = ui.Viewport3D(
    win, width=780, height=460,
    mesh={"vertices": [[x, y, z], ...], "indices": [0, 1, 2, ...], "color": "#4FC3F7"},
    camera={"yaw": 30, "pitch": 18, "distance": 4.0, "autoRotate": 12.0},
)
```

| 字段 | 说明 |
|---|---|
| `vertices` | 顶点数组 `[[x, y, z], ...]` |
| `indices` | 三角形索引，每 3 个一组 |
| `color` | 基础色，光照后自动明暗 |
| `camera.yaw` / `pitch` | 绕原点的角度（度） |
| `camera.distance` | 相机距离 |
| `camera.autoRotate` | 每秒自转角度，0 表示不自转 |

法线由宿主按面法线自动累加，不用自己传。拖动旋转只需要改 `camera` 属性。

**三种画布的分工**

| | `Canvas`（XAML） | `Canvas2D`（Win2D） | `Viewport3D`（D3D11） |
|---|---|---|---|
| 图元/面数上限 | 几百 | 几千 | 几十万 |
| 每帧谁在算 | 宿主重绘 XAML | Python 投影后下发 | 宿主 GPU |
| 单独绑事件 | 能 | 不能 | 不能 |
| 适用 | SVG 编辑 | 大量 2D 图元 | 真 3D |

依赖：`Vortice.Direct3D11` / `Vortice.DXGI`，约 1.5 MB，随 exe 内嵌；
原生 D3D 由系统提供。着色器运行时编译（`d3dcompiler_47.dll`）。

完整示例见 `examples/16_viewport3d.py`。

---

## 7. 示例

| 文件 | 内容 |
|---|---|
| `examples/01_hello.py` | 最小示例：文字 + 按钮 + 回调改界面 |
| `examples/02_controls.py` | 控件总览：所有控件摆一屏，操作任一个都弹窗回报「哪个控件、什么事件、当前值」 |
| `examples/03_live.py` | 实时渲染：时钟 / 进度条 / 色块每 50 ms 变一次，还能运行中动态插入控件 |
| `examples/04_window_toolkit.py` | 窗口图标 / 位置 / 置顶、按钮图标、data URI 图片、媒体控制、提示与右键菜单 |
| `examples/05_scroll.py` | 整窗滚动 + 局部滚动区 |
| `examples/06_buttons.py` | ToggleButton / SplitButton / RepeatButton |
| `examples/07_sidebar.py` | Grid 布局 + 可折叠 ListBox 侧边栏（宽度动画） |
| `examples/08_load_fail.py` | 加载 → 失败：加载圈 0.5 秒保底 + 失败错误界面 |
| `examples/09_webview2.py` | WebView2：当前网址 / 导航事件 / HTML 字符串 / 缺运行时的灰色降级页 |
| `examples/10_extended_controls.py` | 扩展控件总览 + query 读回 + 键盘/焦点/窗口状态事件 |

`02_controls.py` 用于肉眼验收；`03_live.py` 用来确认「实时」这件事——全程没有手动刷新调用。

---

## 8. 已知限制

- **`pack()` 只实现语义子集**。WinUI 没有 tkinter 的几何管理器，`pack` 的 `side` 不参与
  布局计算，只把 `padx/pady` 翻译成 margin、`fill` 翻译成对齐。新代码建议直接用
  `Column` / `Row` / `Grid`。
- **`place()` 不实现**。WinUI 没有绝对定位，调用会抛 `NotImplementedError`。
- **Windows 10 上部分视觉效果降级**。Mica 背景、控件圆角需要 Windows 11，宿主按能力位
  自动回落（Acrylic 或纯色）。
- **单窗口**。一个宿主进程对应一个窗口，多窗口（`Toplevel`）尚未支持。
- **窗口没有尺寸上限**。`AppWindow` 不提供 `maxWidth` / `maxHeight`，只能靠 Win32 子类化
  拦 `WM_GETMINMAXINFO`，代价与收益不成比例。
- **关闭事件不可取消**。`closing` 只是通知，拦不住关窗；需要「确认退出」请在界面里做。
- **启动至少 0.5 秒**。加载圈有保底显示时间，否则在快机器上只闪一两帧，看起来像界面抖动。
- **回调在主线程执行**。回调里阻塞会卡住整个 UI，与 tkinter 行为一致。
- **读回是同步阻塞的**。`query()` 默认等 2 秒，在主线程上阻塞；回调里连续读几百次会明显
  卡顿。等待期间的事件不丢，但定时器会顺延。读几何值时宿主会强制 `UpdateLayout()`，控件
  特别多时单次读取可能较慢。
- **窗口状态有事件顺序问题**。`sizeChanged` 比 presenter 状态更新早，在 `on_resize()`
  回调里读 `win.state` 可能还是旧值；要精确判断请在 `on_state_changed()` 里读，或在
  `maximize()` 之后单独 `query()`。
- **拖放只回报「发生了」**。`drop` 等事件给出 `{accepted}`，不解析 `DataView` 里的文件 /
  文本内容，也没有 `DragStarting`。
- **画布只覆盖基础几何图元**。支持 `Rect` / `Ellipse` / `Line` / `Path` / `Polygon` /
  `Polyline`，但没有渐变填充、`pattern`、`filter`、`mask`、`clipPath`；`d` 只解析几何，
  文本沿路径、`<use>` 引用、SMIL 动画都不支持。
- **富文本只覆盖 Run 级样式**。`RichTextBlock` 的 `richText` 支持粗体 / 斜体 / 下划线 /
  字号 / 颜色分段，但没有超链接、内联图片、多段落和列表；`RichEditBox` 仍是纯文本。
- **`RelativePanel` 只有容器能力**。WinUI 的 `AlignLeftWithPanel` / `AlignRightWith` 等附加
  属性未映射到协议，子节点目前按默认位置叠放；需要精确布局请用 `Grid`。
- **动态条目的子控件只在 `ListBox` / `ComboBox` 支持**。这两者的 `items` 元素可以写
  `children` 放叶控件（见 [3.5.1](docs/protocol.md#351-items-的三种形态)）；`TreeView` /
  `MenuBar` / `CommandBar` / `TabView` / `ItemsView` / `BreadcrumbBar` 的条目仍由宿主按字符串
  生成。（`TabPage` / `PinnedTab` 是例外，见 [6.6](#66-标签页)。）
- **`CalendarView` 多选只读第一个日期**，`selectedDatesChanged` 回报单值；`ItemsView` 用默认
  模板显示字符串，复杂 ItemTemplate 未开放。
- **没有 `DataGrid` 和 `InkCanvas`**。WinUI 3 不内置这两个控件；前者可用 `GridView` /
  `ItemsView` 近似，后者需要自己接 `SwapChainPanel` + DirectX。
- **WebView2 依赖系统运行时**。只覆盖「运行时未安装」这一种失败；运行时存在但初始化失败
  （安装损坏）时该区域仍可能空白。`navigate_to_string()` 的页面没有真实地址。
  `WebResourceRequested` 拦截、`addHostObjectToScript`、证书 / HTTP 认证、完整多窗口尚未
  实现；`permissionRequested` 未接；WinUI 3 不暴露程序化 `zoomFactor`。

---

## 9. 开发与验证

```powershell
# 宿主（Release）
dotnet build host/PyWinUI.Host/PyWinUI.Host.csproj -c Release

# Python 单测 + C# 协议层单测
$env:PYTHONPATH = "python"
python -m unittest discover -s tests -p "test_*.py"
dotnet test host/PyWinUI.Host.Tests/PyWinUI.Host.Tests.csproj -c Release

# 端到端 smoke（需要真实桌面会话）
python tests/smoke_render.py
python tests/smoke_events.py
python tests/smoke_tabs.py
python tests/drag_pinned.py      # 真实鼠标拖拽：固定标签拖不动、普通标签能重排
python tests/smoke_badline.py    # 坏 JSON / 错误字段之后会话仍可用
python tests/smoke_webview2.py   # 真机：消息 / 新窗口回执 / 下载生命周期
```

CI（`.github/workflows/ci.yml`）跑 Python 单测、宿主 `dotnet build -c Release` 与 C# 协议层
`dotnet test`。协议层测试项目是纯 net8.0，直接链接宿主的 `Protocol/*.cs`，不需要桌面会话，
也不需要拉起整套 WinUI 运行时。

> **不要删 `App.xaml`。** 它看起来只是放资源，实际上 XAML 编译器靠它生成
> `IXamlMetadataProvider` 与 `XamlTypeInfo`。少了它，`XamlControlsResources` 在构造时会抛
> 「Cannot find a resource with the given key」，进程随后以 `0xC000027B`（stowed exception）
> 直接退出。纯代码工程走不通这条路。

改协议时请同步 `docs/protocol.md`、C# 的 `Protocol/`、`PropertyApplier.cs` /
`ExtendedProperties.cs`、`EventBridge.cs` / `ExtendedEvents.cs`、`Session.cs`、
`MicroRuntime.cs`（微代码）、`HostLog.cs`（日志分级）、`ExtendedControls.cs` /
`ExtendedInvoke.cs` / `FormRenderer.cs`（扩展控件的工厂 / 命令 / 挂载），以及 Python 的
`protocol.py`、`app.py`（含 16 ms patch 合并窗口）、`widgets.py` / `extended.py`，再跑一遍
单测与 smoke。`tests/test_extended.py` 覆盖新增控件、query 消息形状与事件快捷绑定，不需要
宿主进程。

扩展能力的实现刻意按「工厂 / 属性 / 事件 / 命令 / 读回」拆成五个独立文件
（`ExtendedControls.cs`、`ExtendedProperties.cs`、`ExtendedEvents.cs`、
`ExtendedInvoke.cs`、`QueryExecutor.cs`），再通过各主 switch 的 `default` 或属性循环前置
调用接进来；基础控件的既有分支基本没动，回归面更小。

---

## 10. 许可

**GPL-3.0-only**（GNU General Public License v3.0）。完整条款见仓库根目录 [LICENSE](LICENSE)。

选择强著佐权（copyleft）而非 MIT：任何人都可以自由使用、修改和分发本项目，但只要分发
基于本项目的衍生作品（包括打包进商业软件），就必须在相同协议下公开对应的完整源代码，
且不得附加额外限制。这保留了原作者对其代码传播方式的控制权，也避免代码被闭源产品直接使用。

> 如需将本库链接进闭源程序，请通过 GitHub Issues 联系获取商业授权。
