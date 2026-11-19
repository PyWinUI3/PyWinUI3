"""富文本与「子控件条目」两个能力的演示。

运行：
    python examples/12_rich_items.py

要点：
1. RichTextBlock 的 richText 接受 span 数组，逐段设置粗体 / 斜体 / 下划线 /
   字号 / 颜色，不必再退回纯文本。
2. ListBox 的 items 里可以写 children，把任意叶控件塞进列表项，
   不再局限于「图标 + 一行字」。
"""

import pywinui3 as ui

win = ui.Window(title="富文本与子控件条目", width=720, height=600)

col = ui.Column(win, padding=24, spacing=18)

# ---------------------------------------------------------------------------
# 1. 富文本
# ---------------------------------------------------------------------------
ui.Label(col, "富文本（RichTextBlock）", font_size=18, weight="semibold")

ui.RichTextBlock(
    col,
    rich_text=[
        {"text": "警告：", "bold": True, "color": "#C42B1C"},
        {"text": "磁盘剩余空间不足", "italic": True},
        {"text": "，当前仅剩 ", "size": 13},
        {"text": "2.1 GB", "bold": True, "underline": True, "color": "#0F7B0F"},
        {"text": "。请及时清理。", "size": 13, "color": "#5A5A5A"},
    ],
    wrap=True,
)

# 纯文本写法仍然有效，两者互不影响
ui.RichTextBlock(col, text="这一行用 text 属性，等价于单个无格式片段。", wrap=True)

# ---------------------------------------------------------------------------
# 2. 子控件条目
# ---------------------------------------------------------------------------
ui.Label(col, "动态条目（items.children）", font_size=18, weight="semibold")

ui.ListBox(
    col,
    height=260,
    items=[
        {
            "children": [
                {"type": "Icon", "props": {"glyph": "\uE8A5", "fontSize": 18}},
                {"type": "Label", "props": {"text": "年度报告.pdf", "width": 180}},
                {"type": "Label", "props": {"text": "2.4 MB", "opacity": 0.6}},
            ]
        },
        {
            "children": [
                {"type": "Icon", "props": {"glyph": "\uE8A5", "fontSize": 18}},
                {"type": "Label", "props": {"text": "会议纪要.docx", "width": 180}},
                {"type": "Label", "props": {"text": "128 KB", "opacity": 0.6}},
            ]
        },
        {
            "children": [
                {"type": "Icon", "props": {"glyph": "\uE8B7", "fontSize": 18}},
                {"type": "Label", "props": {"text": "素材库", "width": 180}},
                {"type": "Label", "props": {"text": "文件夹", "opacity": 0.6}},
            ]
        },
        # 纯字符串条目可以和子控件条目混排
        "这一项是纯文字，说明两种写法可以共存在同一个列表里",
    ],
)

ui.Label(
    col,
    "提示：children 里每个元素都是一个独立节点，可以绑事件，"
    "例如给某个 Label 加 click 回调。",
    font_size=12,
    wrap=True,
)

win.mainloop()
