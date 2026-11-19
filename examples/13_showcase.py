"""能力总览：把 pywinui3 当前支持的所有控件和玩法集中在一个窗口里。

运行：
    python examples/13_showcase.py

左侧是分类导航，右侧是对应页面，底部是事件日志。
每切换一次页面就销毁旧控件、重建新的——顺带验证 destroy() 的回收路径。

这不是「最小示例」，而是回归用的视觉清单：
某个控件或属性失效时，跑一遍这个程序就能看出来。
"""

import os

import pywinui3 as ui

# ---------------------------------------------------------------------------
# 事件日志：所有回调都往这里写，方便观察事件是否真的回传了
# ---------------------------------------------------------------------------
log_lines: list[str] = []


def log(text: str) -> None:
    log_lines.append(text)
    if len(log_lines) > 6:
        log_lines.pop(0)
    log_view.items = list(reversed(log_lines))


# ---------------------------------------------------------------------------
# 窗口骨架
# ---------------------------------------------------------------------------
win = ui.Window(title="pywinui3 能力总览", width=1180, height=800)

root = ui.Row(win, spacing=0)

sidebar = ui.Column(root, width=210, padding=(12, 12, 6, 12), spacing=8)
ui.Label(sidebar, "能力总览", font_size=16, weight="semibold")
ui.Label(sidebar, f"协议 v{ui.PROTOCOL_VERSION}", font_size=11, foreground="#8A8A8A")

right = ui.Column(root, padding=(6, 12, 12, 12), spacing=10)

content_scroll = ui.Scroll(right, height=640)
page = ui.Column(content_scroll, spacing=14, padding=(4, 4, 12, 12))

ui.Separator(right)
ui.Label(right, "事件日志", font_size=12, weight="semibold", foreground="#5A5A5A")
log_view = ui.ListBox(right, items=[], height=92, font_size=12)


# ---------------------------------------------------------------------------
# 页面构建器（每个函数往父容器里塞控件）
# ---------------------------------------------------------------------------
def page_basic(parent: ui.Widget) -> None:
    ui.Label(parent, "文本与排版", font_size=20, weight="semibold")
    ui.Label(parent, "Label：一行普通文字，支持字号、颜色、粗细、换行。")
    ui.Label(parent, "28 号粗体", font_size=28, weight="bold")
    ui.Label(parent, "斜体 + 下划线 + 自定义颜色", italic=True, underline=True, foreground="#C42B1C")
    ui.Label(
        parent,
        "长文本自动换行：WinUI 3 的渲染器和 Python 侧只传字符串，"
        "换行、省略号、行高全部由原生控件负责，所以中文排版和系统其他应用完全一致。",
        wrap=True,
    )
    ui.Separator(parent)
    ui.Label(parent, "图标（Icon 节点，Segoe Fluent Icons）", font_size=16, weight="semibold")
    row = ui.Row(parent, spacing=16)
    for glyph, name in [
        ("\uE8A5", "文档"),
        ("\uE8B7", "文件夹"),
        ("\uEB9F", "图片"),
        ("\uE713", "设置"),
        ("\uE77B", "联系人"),
        ("\uE896", "下载"),
    ]:
        cell = ui.Column(row, spacing=4, h_align="center")
        ui.Icon(cell, glyph=glyph, font_size=24)
        ui.Label(cell, name, font_size=11, foreground="#8A8A8A")


def page_buttons(parent: ui.Widget) -> None:
    ui.Label(parent, "按钮家族", font_size=20, weight="semibold")
    row = ui.Row(parent, spacing=10)
    ui.Button(row, "普通按钮", command=lambda: log("Button click"))
    ui.Button(row, "强调按钮", variant="accent", command=lambda: log("Button(accent) click"))
    ui.HyperlinkButton(row, "超链接", command=lambda: log("HyperlinkButton click"))
    ui.ToggleButton(row, "可切换", command=lambda: log("ToggleButton click"))
    ui.RepeatButton(row, "长按重复", command=lambda: log("RepeatButton click"))

    row2 = ui.Row(parent, spacing=10)
    ui.SplitButton(row2, "分体按钮", command=lambda: log("SplitButton click"),
                   menu=[{"label": "选项 A", "value": "a"}, {"label": "选项 B", "value": "b"}])
    dropdown = ui.DropDownButton(row2, "下拉按钮")
    dropdown.context_menu = [{"label": "菜单项 1", "value": "1"}, {"label": "菜单项 2", "value": "2"}]
    toggle_split = ui.ToggleSplitButton(row2, "可切换分体")
    toggle_split.bind("change", lambda e: log(f"ToggleSplitButton checked={e.data.get('checked')}"))

    ui.Separator(parent)
    ui.Label(parent, "带图标的按钮与右键菜单", font_size=16, weight="semibold")
    row3 = ui.Row(parent, spacing=10)
    btn = ui.Button(row3, "带图标", icon="\uE8A5", command=lambda: log("icon 按钮"))
    btn.context_menu = [
        {"label": "复制", "glyph": "\uE8C8", "value": "copy"},
        {"label": "重命名", "value": "rename"},
        "-",
        {"label": "删除", "value": "delete"},
    ]
    ui.Button(row3, "点我弹窗", command=lambda: ui.dialog("对话框", "这是 dialog() 弹出的模态框"))


def page_inputs(parent: ui.Widget) -> None:
    ui.Label(parent, "输入与表单", font_size=20, weight="semibold")

    ui.Label(parent, "Entry / PasswordBox / AutoSuggestBox", font_size=16, weight="semibold")
    entry = ui.Entry(parent, text="可以输入文字", placeholder="占位提示")
    entry.bind("change", lambda e: log(f"Entry change: {e.data.get('text')}"))
    ui.PasswordBox(parent, placeholder="密码框")
    ui.AutoSuggestBox(parent, items=["北京", "上海", "广州", "深圳"], placeholder="输入城市名")

    ui.Label(parent, "Checkbox / Radio / Switch", font_size=16, weight="semibold")
    ui.Checkbox(parent, "已勾选的复选框", checked=True, command=lambda: log("Checkbox click"))
    radio_row = ui.Row(parent, spacing=16)
    ui.Radio(radio_row, "选项一", checked=True, group_name="demo")
    ui.Radio(radio_row, "选项二", group_name="demo")
    ui.Switch(parent, "开关", checked=True)

    ui.Label(parent, "Slider / NumberBox / RatingControl", font_size=16, weight="semibold")
    ui.Slider(parent, minimum=0, maximum=100, value=42)
    ui.NumberBox(parent, value=8, min=0, max=999)
    ui.RatingControl(parent, value=4)

    ui.Label(parent, "ColorPicker", font_size=16, weight="semibold")
    ui.ColorPicker(parent, color="#0F6CBD")


def page_datetime(parent: ui.Widget) -> None:
    ui.Label(parent, "日期与时间", font_size=20, weight="semibold")
    ui.Label(parent, "DatePicker / TimePicker / CalendarDatePicker", font_size=16, weight="semibold")
    ui.DatePicker(parent, date="2026-11-19")
    ui.TimePicker(parent, time="21:30")
    ui.CalendarDatePicker(parent, date="2026-11-19")

    ui.Label(parent, "CalendarView", font_size=16, weight="semibold")
    ui.CalendarView(parent, date="2026-11-19")

    ui.Label(parent, "ComboBox", font_size=16, weight="semibold")
    ui.ComboBox(parent, items=["选项一", "选项二", "选项三"], selected_index=0)


def page_lists(parent: ui.Widget) -> None:
    ui.Label(parent, "列表与数据", font_size=20, weight="semibold")

    ui.Label(parent, "ListBox：纯文本 / 带图标 / 子控件混排", font_size=16, weight="semibold")
    ui.ListBox(
        parent,
        height=170,
        items=[
            {"text": "文档条目（带图标）", "glyph": "\uE8A5"},
            "纯文本条目",
            {
                "children": [
                    {"type": "Icon", "props": {"glyph": "\uE8B7"}},
                    {"type": "Label", "props": {"text": "子控件条目", "width": 160}},
                    {"type": "Label", "props": {"text": "图标 + 两段文字", "opacity": 0.6}},
                ]
            },
            {
                "children": [
                    {"type": "Icon", "props": {"glyph": "\uEB9F"}},
                    {"type": "Label", "props": {"text": "另一种组合", "width": 160}},
                    {"type": "Button", "props": {"text": "按钮也能放进来"}},
                ]
            },
        ],
    )

    ui.Label(parent, "GridView / FlipView（图表条目）", font_size=16, weight="semibold")
    ui.GridView(parent, items=[{"text": f"格 {i}", "glyph": "\uE8A5"} for i in range(1, 7)])
    ui.FlipView(parent, items=["第一页", "第二页", "第三页"])

    ui.Label(parent, "TreeView（嵌套）", font_size=16, weight="semibold")
    ui.TreeView(
        parent,
        items=[
            {"label": "根节点", "children": ["子节点 1", {"label": "子节点 2", "children": ["叶子"]}]},
            {"label": "另一个根"},
        ],
        height=140,
    )

    ui.Label(parent, "RadioButtons / SelectorBar / ItemsView", font_size=16, weight="semibold")
    ui.RadioButtons(parent, items=["互斥选一", "第二个选项", "第三个选项"])
    ui.SelectorBar(parent, items=["标签一", "标签二", "标签三"])
    ui.ItemsView(parent, items=["项目 A", "项目 B", "项目 C"], height=100)


def page_nav(parent: ui.Widget) -> None:
    ui.Label(parent, "导航与结构", font_size=20, weight="semibold")

    ui.Label(parent, "Pivot", font_size=16, weight="semibold")
    ui.Pivot(parent, items=["概览", "详情", "设置"], height=140)

    ui.Label(parent, "TabView（含可关闭标签与固定标签）", font_size=16, weight="semibold")
    tabs = ui.TabView(parent, items=["标签一", "标签二"])
    page1 = ui.TabPage(tabs, "带内容的标签")
    ui.Label(page1, "这是 TabPage 里的内容，可以放任意子控件。")
    pinned = ui.PinnedTab(tabs, "固定标签")
    ui.Label(pinned, "固定标签不可关闭、不可拖动。")

    ui.Label(parent, "NavigationView / MenuBar / CommandBar / BreadcrumbBar",
             font_size=16, weight="semibold")
    ui.NavigationView(parent, items=["主页", "收藏", "设置"], height=160)
    ui.MenuBar(parent, items=["文件", "编辑", "查看"])
    ui.CommandBar(parent, items=["新建", "保存", "删除"])
    ui.BreadcrumbBar(parent, items=["根目录", "子目录", "当前项"])

    ui.Label(parent, "Expander / InfoBar", font_size=16, weight="semibold")
    expander = ui.Expander(parent, header="点我展开", is_expanded=True)
    ui.Label(expander, "Expander 内部的内容区，默认展开。")
    ui.InfoBar(parent, title="提示", message="这是 InfoBar，severity 可选 info/success/warning/error。",
               severity="info")
    ui.InfoBar(parent, title="成功", message="操作已完成。", severity="success")
    ui.InfoBar(parent, title="警告", message="磁盘空间不足。", severity="warning")
    ui.InfoBar(parent, title="错误", message="保存失败。", severity="error")


def page_richtext(parent: ui.Widget) -> None:
    ui.Label(parent, "富文本与编辑", font_size=20, weight="semibold")

    ui.Label(parent, "RichTextBlock：richText 分段格式", font_size=16, weight="semibold")
    ui.RichTextBlock(
        parent,
        rich_text=[
            {"text": "警告：", "bold": True, "color": "#C42B1C"},
            {"text": "磁盘剩余空间不足", "italic": True},
            {"text": "，当前仅剩 ", "size": 13},
            {"text": "2.1 GB", "bold": True, "underline": True, "color": "#0F7B0F", "size": 13},
            {"text": "。请及时清理。", "size": 13, "color": "#5A5A5A"},
        ],
        wrap=True,
    )
    ui.RichTextBlock(parent, text="这一行用 text 属性，等价于单个无格式片段。", wrap=True)

    ui.Label(parent, "RichEditBox：可编辑纯文本（富文本写入暂未开放）", font_size=16, weight="semibold")
    ui.RichEditBox(parent, text="可以在这里输入多行文字。")


def page_layout(parent: ui.Widget) -> None:
    ui.Label(parent, "布局与容器", font_size=20, weight="semibold")

    ui.Label(parent, "Column（纵向）", font_size=16, weight="semibold")
    col = ui.Column(parent, spacing=6, padding=10)
    for i in range(1, 4):
        ui.Label(col, f"Column 子项 {i}")

    ui.Label(parent, "Row（横向）", font_size=16, weight="semibold")
    row = ui.Row(parent, spacing=10, padding=10)
    for i in range(1, 4):
        ui.Button(row, f"按钮 {i}", command=lambda n=i: log(f"Row 按钮 {n}"))

    ui.Label(parent, "Grid（行列）", font_size=16, weight="semibold")
    grid = ui.Grid(parent, rows=["auto", "auto"], columns=["*", "*", "*"], spacing=8, padding=10)
    for r in range(2):
        for c in range(3):
            ui.Label(grid, f"格 {r},{c}", h_align="center").grid(row=r, column=c)

    ui.Label(parent, "BorderBox / Viewbox", font_size=16, weight="semibold")
    box = ui.BorderBox(parent, padding=14, border_width=1, corner_radius=8)
    ui.Label(box, "BorderBox 内的一行文字，带边框和圆角。")
    ui.Viewbox(parent, max_width=320)
    ui.Label(parent, "（Viewbox 已创建，可包内容做等比缩放）")

    ui.Label(parent, "TwoPaneView / RelativePanel / RefreshContainer", font_size=16, weight="semibold")
    ui.Label(parent, "以下三个容器只验证可创建、可挂子控件：", font_size=12, foreground="#8A8A8A")
    two = ui.TwoPaneView(parent)
    ui.Label(two, "TwoPaneView（子控件进 Pane1）")
    rel = ui.RelativePanel(parent)
    ui.Label(rel, "RelativePanel（附加属性未开放，按默认叠放）")
    refresh = ui.RefreshContainer(parent)
    ui.Label(refresh, "RefreshContainer 内的内容")


def page_binding(parent: ui.Widget) -> None:
    ui.Label(parent, "数据绑定与运行时读取", font_size=20, weight="semibold")

    name_var = ui.StringVar(value="初始值")
    ui.Label(parent, "StringVar 双向绑定（改下面输入框，上面标签跟着变）",
             font_size=16, weight="semibold")
    ui.Entry(parent, textvariable=name_var)
    # Label 不支持 textvariable，用 trace 手动同步（setattr 会下发 patch）
    name_shown = ui.Label(parent, text=name_var.get(), font_size=18, foreground="#0F6CBD")
    name_var.trace(lambda v: setattr(name_shown, "text", v))

    count_var = ui.IntVar(value=0)
    ui.Label(parent, "IntVar：点按钮自增", font_size=16, weight="semibold")

    def bump() -> None:
        count_var.set(count_var.get() + 1)
        log(f"IntVar -> {count_var.get()}")

    ui.Button(parent, "加一", command=bump)
    count_shown = ui.Label(parent, text=str(count_var.get()), font_size=20)
    count_var.trace(lambda v: setattr(count_shown, "text", str(v)))

    ui.Label(parent, "运行时读取（query）", font_size=16, weight="semibold")
    probe = ui.Label(parent, "这个标签的尺寸可以被读出来")

    def read_size() -> None:
        width = probe.query("actualWidth")
        height = probe.query("actualHeight")
        log(f"query: {width} x {height}")

    ui.Button(parent, "读取上面标签的尺寸", command=read_size)

    ui.Label(parent, "BooleanVar 联动 Switch", font_size=16, weight="semibold")
    flag = ui.BooleanVar(value=False)
    ui.Switch(parent, textvariable=flag)
    ui.Label(parent, "（BooleanVar 绑定在 Switch 上，值变化会回写到变量）")


# ---------------------------------------------------------------------------
# 导航
# ---------------------------------------------------------------------------
PAGES = {
    "基础文本": page_basic,
    "按钮家族": page_buttons,
    "输入与表单": page_inputs,
    "日期与时间": page_datetime,
    "列表与数据": page_lists,
    "导航与结构": page_nav,
    "富文本": page_richtext,
    "布局容器": page_layout,
    "绑定与读取": page_binding,
}

current_page = ui.Label(page, "…", font_size=12, foreground="#8A8A8A")


def show(name: str) -> None:
    """切页：销毁旧子控件再重建。"""
    for child in list(page.children):
        child.destroy()

    builder = PAGES[name]
    builder(page)
    log(f"切换到「{name}」")


nav = ui.ListBox(sidebar, items=list(PAGES.keys()), height=520, selected_index=0)
nav.bind("selectionChanged", lambda e: show(e.data.get("value") or list(PAGES)[0]))

# 开发时可以指定初始页，省得每次手动点：PYWINUI3_SHOWCASE_START="布局容器"
_initial = os.environ.get("PYWINUI3_SHOWCASE_START") or list(PAGES)[0]
if _initial in PAGES:
    nav.selected_index = list(PAGES).index(_initial)
show(_initial)
log("程序已启动")

win.mainloop()
