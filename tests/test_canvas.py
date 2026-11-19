"""画布与图元：类型名、导出，以及 props 组装规则。

这里不构造真实控件（需要宿主进程），只验证「Python 侧会不会把参数
按协议要求塞进 props」。真正的绘制效果由 examples/14_canvas.py 验证。
"""

import unittest

import pywinui3 as ui


class TestCanvasExports(unittest.TestCase):
    def test_types(self):
        expected = {
            ui.Canvas: "Canvas",
            ui.Rect: "Rect",
            ui.Ellipse: "Ellipse",
            ui.Line: "Line",
            ui.Path: "Path",
            ui.Polygon: "Polygon",
            ui.Polyline: "Polyline",
        }
        for cls, type_name in expected.items():
            self.assertEqual(cls._type, type_name, f"{cls.__name__} 的 _type 不对")

    def test_exported_in_all(self):
        for name in ["Canvas", "Rect", "Ellipse", "Line", "Path", "Polygon", "Polyline"]:
            self.assertIn(name, ui.__all__)
            self.assertTrue(hasattr(ui, name))

    def test_shape_shares_fill_stroke(self):
        # 所有图元都应继承 _Shape，保证 fill / stroke / stroke_width 一致
        for cls in [ui.Rect, ui.Ellipse, ui.Line, ui.Path, ui.Polygon, ui.Polyline]:
            self.assertTrue(
                any(base.__name__ == "_Shape" for base in cls.__mro__),
                f"{cls.__name__} 没有继承 _Shape",
            )


class TestCamelizeProps(unittest.TestCase):
    """图元参数名到协议 key 的映射。"""

    def test_snake_to_camel(self):
        from pywinui3.widgets import camelize

        self.assertEqual(camelize("stroke_width"), "strokeWidth")
        self.assertEqual(camelize("radius_x"), "radiusX")
        self.assertEqual(camelize("line_cap"), "lineCap")
        self.assertEqual(camelize("line_join"), "lineJoin")
        self.assertEqual(camelize("stroke_dash_array"), "strokeDashArray")
        # 已经是单段的保持不变
        self.assertEqual(camelize("left"), "left")
        self.assertEqual(camelize("d"), "d")
        self.assertEqual(camelize("points"), "points")


if __name__ == "__main__":
    unittest.main()
