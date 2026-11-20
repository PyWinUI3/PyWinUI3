using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Windows.Foundation;
using Windows.UI;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// Win2D 立即模式画布。
///
/// 与 XAML 路线的 <c>Canvas</c> 是两种东西：
/// - <c>Canvas</c>：每个图元一个 XAML 元素，能单独绑事件、单独改属性，
///   适合"图元数量不多、要逐个交互"的编辑器场景。
/// - <c>Canvas2D</c>：整块图元数组一次性下发，宿主在 GPU 画布上重绘，
///   图元再多也只花一条 patch，适合"几千图元、要满帧"的渲染场景。
///   代价是图元本身不是 UIElement，命中测试要自己在 Python 侧算。
///
/// 图元数组挂在 ConditionalWeakTable 上而不是控件 Tag：
/// 控件被销毁时条目会自动释放，不会像字典那样留一堆死引用。
/// </summary>
internal static class Canvas2DSupport
{
    private sealed class Holder
    {
        public JsonArray? Shapes;

        /// <summary>id → 数组下标。全量设置时置空，第一次增量更新时惰性构建。</summary>
        public Dictionary<string, int>? Index;
    }

    private static readonly ConditionalWeakTable<CanvasControl, Holder> Store = new();

    public static CanvasControl Create()
    {
        var control = new CanvasControl
        {
            ClearColor = Colors.Transparent,
        };

        control.Draw += OnDraw;
        return control;
    }

    public static void SetShapes(CanvasControl control, JsonArray shapes)
    {
        Store.AddOrUpdate(control, new Holder { Shapes = shapes, Index = null });
        control.Invalidate();
    }

    /// <summary>
    /// 按 id 增量更新图元：只覆盖传进来的字段，其余保持不变。
    ///
    /// 走 invoke 而不是属性：属性会被 16ms 合并窗口按节点合并，
    /// 同一帧内多次增量会被压成最后一次，前面的更新直接丢掉。
    ///
    /// 全量重发 N 个图元 vs 增量只发变化的几个，差距在图元多时是几十倍。
    /// </summary>
    public static void UpdateShapes(CanvasControl control, JsonArray updates)
    {
        if (!Store.TryGetValue(control, out var holder) || holder.Shapes is null)
        {
            return;
        }

        holder.Index ??= BuildIndex(holder.Shapes);

        var changed = false;

        foreach (var item in updates)
        {
            if (item is not JsonObject patch)
            {
                continue;
            }

            var id = ValueParsers.AsString(patch["id"]);
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            if (!holder.Index.TryGetValue(id, out var slot))
            {
                continue;
            }

            if (holder.Shapes[slot] is not JsonObject shape)
            {
                continue;
            }

            // JsonNode 只能有一个父节点，必须深拷贝才能挂到目标对象下
            foreach (var pair in patch)
            {
                if (pair.Key == "id")
                {
                    continue;
                }

                shape[pair.Key] = pair.Value?.DeepClone();
            }

            changed = true;
        }

        if (changed)
        {
            control.Invalidate();
        }
    }

    private static Dictionary<string, int> BuildIndex(JsonArray shapes)
    {
        var index = new Dictionary<string, int>(shapes.Count);

        for (var i = 0; i < shapes.Count; i++)
        {
            if (shapes[i] is JsonObject obj
                && ValueParsers.AsString(obj["id"]) is { Length: > 0 } id)
            {
                index[id] = i;
            }
        }

        return index;
    }

    private static void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        try
        {
            DrawCore(sender, args);
        }
        catch (Exception ex)
        {
            // 绘制异常绝不能让宿主进程挂掉：一帧画不出来比整个应用消失好得多
            HostLog.Write(ex, "Canvas2D.Draw");
        }
    }

    private static void DrawCore(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (!Store.TryGetValue(sender, out var holder) || holder.Shapes is null)
        {
            return;
        }

        var session = args.DrawingSession;

        foreach (var node in holder.Shapes)
        {
            if (node is not JsonObject shape)
            {
                continue;
            }

            var type = ValueParsers.AsString(shape["type"]) ?? "";
            var stroke = ValueParsers.AsColor(shape["stroke"]);
            var fill = ValueParsers.AsColor(shape["fill"]);
            var strokeWidth = (float)(ValueParsers.AsDouble(shape["strokeWidth"]) ?? 1);

            switch (type)
            {
                case "line":
                {
                    if (stroke is not { } lineColor)
                    {
                        break;
                    }

                    session.DrawLine(
                        (float)(ValueParsers.AsDouble(shape["x1"]) ?? 0),
                        (float)(ValueParsers.AsDouble(shape["y1"]) ?? 0),
                        (float)(ValueParsers.AsDouble(shape["x2"]) ?? 0),
                        (float)(ValueParsers.AsDouble(shape["y2"]) ?? 0),
                        lineColor,
                        strokeWidth);
                    break;
                }

                case "rect":
                {
                    var x = (float)(ValueParsers.AsDouble(shape["x"]) ?? 0);
                    var y = (float)(ValueParsers.AsDouble(shape["y"]) ?? 0);
                    var w = (float)(ValueParsers.AsDouble(shape["w"]) ?? 0);
                    var h = (float)(ValueParsers.AsDouble(shape["h"]) ?? 0);
                    var radius = (float)(ValueParsers.AsDouble(shape["radius"]) ?? 0);
                    var rect = new Rect(x, y, w, h);

                    if (fill is { } rectFill)
                    {
                        if (radius > 0)
                        {
                            using var rounded = CanvasGeometry.CreateRoundedRectangle(
                                sender, x, y, w, h, radius, radius);
                            session.FillGeometry(rounded, rectFill);
                        }
                        else
                        {
                            session.FillRectangle(rect, rectFill);
                        }
                    }

                    if (stroke is { } rectStroke)
                    {
                        if (radius > 0)
                        {
                            using var rounded = CanvasGeometry.CreateRoundedRectangle(
                                sender, x, y, w, h, radius, radius);
                            session.DrawGeometry(rounded, rectStroke, strokeWidth);
                        }
                        else
                        {
                            session.DrawRectangle(rect, rectStroke, strokeWidth);
                        }
                    }

                    break;
                }

                case "ellipse":
                {
                    var cx = (float)(ValueParsers.AsDouble(shape["cx"]) ?? 0);
                    var cy = (float)(ValueParsers.AsDouble(shape["cy"]) ?? 0);
                    var rx = (float)(ValueParsers.AsDouble(shape["rx"]) ?? 0);
                    var ry = (float)(ValueParsers.AsDouble(shape["ry"]) ?? 0);

                    if (fill is { } ellipseFill)
                    {
                        session.FillEllipse(cx, cy, rx, ry, ellipseFill);
                    }

                    if (stroke is { } ellipseStroke)
                    {
                        session.DrawEllipse(cx, cy, rx, ry, ellipseStroke, strokeWidth);
                    }

                    break;
                }

                case "path":
                {
                    var d = ValueParsers.AsString(shape["d"]);
                    if (string.IsNullOrWhiteSpace(d))
                    {
                        break;
                    }

                    using var geometry = BuildGeometry(sender, d);
                    if (geometry is null)
                    {
                        break;
                    }

                    if (fill is { } pathFill)
                    {
                        session.FillGeometry(geometry, pathFill);
                    }

                    if (stroke is { } pathStroke)
                    {
                        session.DrawGeometry(geometry, pathStroke, strokeWidth);
                    }

                    break;
                }

                case "text":
                {
                    var text = ValueParsers.AsString(shape["text"]) ?? "";
                    if (text.Length == 0)
                    {
                        break;
                    }

                    var color = stroke ?? fill ?? Colors.Black;
                    var size = (float)(ValueParsers.AsDouble(shape["size"]) ?? 14);
                    using var format = new CanvasTextFormat
                    {
                        FontSize = size,
                        WordWrapping = CanvasWordWrapping.NoWrap,
                    };

                    session.DrawText(
                        text,
                        (float)(ValueParsers.AsDouble(shape["x"]) ?? 0),
                        (float)(ValueParsers.AsDouble(shape["y"]) ?? 0),
                        color,
                        format);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// 用 CanvasPathBuilder 构建几何。命令集与 XAML 路线一致，
    /// 复用同一个词法器，避免两套解析逻辑走出不同结果。
    /// </summary>
    private static CanvasGeometry? BuildGeometry(ICanvasResourceCreator creator, string d)
    {
        var tokens = ExtendedProperties.TokenizePath(d);
        if (tokens.Count == 0)
        {
            return null;
        }

        var builder = new CanvasPathBuilder(creator);
        var figureOpen = false;
        var index = 0;
        var command = 'M';

        double curX = 0, curY = 0;
        double startX = 0, startY = 0;
        double ctrlX = 0, ctrlY = 0;
        char previous = '\0';

        bool HasNumber() => index < tokens.Count && tokens[index] is double;

        double ReadNumber()
        {
            var value = (double)tokens[index];
            index++;
            return value;
        }

        void EnsureFigure()
        {
            if (figureOpen)
            {
                return;
            }

            builder.BeginFigure(new System.Numerics.Vector2((float)curX, (float)curY));
            figureOpen = true;
        }

        while (index < tokens.Count)
        {
            if (tokens[index] is char explicitCommand)
            {
                command = explicitCommand;
                index++;
            }

            var relative = char.IsLower(command);
            var upper = char.ToUpperInvariant(command);

            switch (upper)
            {
                case 'M':
                {
                    if (!HasNumber())
                    {
                        goto done;
                    }

                    var x = ReadNumber();
                    var y = ReadNumber();
                    if (relative)
                    {
                        x += curX;
                        y += curY;
                    }

                    if (figureOpen)
                    {
                        builder.EndFigure(CanvasFigureLoop.Open);
                        figureOpen = false;
                    }

                    curX = x;
                    curY = y;
                    startX = x;
                    startY = y;
                    builder.BeginFigure(new System.Numerics.Vector2((float)x, (float)y));
                    figureOpen = true;
                    command = relative ? 'l' : 'L';
                    previous = 'M';
                    break;
                }

                case 'L':
                    while (HasNumber())
                    {
                        var x = ReadNumber();
                        var y = ReadNumber();
                        if (relative)
                        {
                            x += curX;
                            y += curY;
                        }

                        curX = x;
                        curY = y;
                        EnsureFigure();
                        builder.AddLine(new System.Numerics.Vector2((float)x, (float)y));
                    }

                    previous = command;
                    break;

                case 'H':
                    while (HasNumber())
                    {
                        var x = ReadNumber();
                        if (relative)
                        {
                            x += curX;
                        }

                        curX = x;
                        EnsureFigure();
                        builder.AddLine(new System.Numerics.Vector2((float)curX, (float)curY));
                    }

                    previous = command;
                    break;

                case 'V':
                    while (HasNumber())
                    {
                        var y = ReadNumber();
                        if (relative)
                        {
                            y += curY;
                        }

                        curY = y;
                        EnsureFigure();
                        builder.AddLine(new System.Numerics.Vector2((float)curX, (float)curY));
                    }

                    previous = command;
                    break;

                case 'C':
                    while (HasNumber())
                    {
                        var x1 = ReadNumber();
                        var y1 = ReadNumber();
                        var x2 = ReadNumber();
                        var y2 = ReadNumber();
                        var x = ReadNumber();
                        var y = ReadNumber();
                        if (relative)
                        {
                            x1 += curX;
                            y1 += curY;
                            x2 += curX;
                            y2 += curY;
                            x += curX;
                            y += curY;
                        }

                        ctrlX = x2;
                        ctrlY = y2;
                        curX = x;
                        curY = y;
                        EnsureFigure();
                        builder.AddCubicBezier(
                            new System.Numerics.Vector2((float)x1, (float)y1),
                            new System.Numerics.Vector2((float)x2, (float)y2),
                            new System.Numerics.Vector2((float)x, (float)y));
                    }

                    previous = command;
                    break;

                case 'S':
                    while (HasNumber())
                    {
                        var reflect = previous is 'C' or 'c' or 'S' or 's';
                        var x1 = reflect ? 2 * curX - ctrlX : curX;
                        var y1 = reflect ? 2 * curY - ctrlY : curY;

                        var x2 = ReadNumber();
                        var y2 = ReadNumber();
                        var x = ReadNumber();
                        var y = ReadNumber();
                        if (relative)
                        {
                            x2 += curX;
                            y2 += curY;
                            x += curX;
                            y += curY;
                        }

                        ctrlX = x2;
                        ctrlY = y2;
                        curX = x;
                        curY = y;
                        EnsureFigure();
                        builder.AddCubicBezier(
                            new System.Numerics.Vector2((float)x1, (float)y1),
                            new System.Numerics.Vector2((float)x2, (float)y2),
                            new System.Numerics.Vector2((float)x, (float)y));
                    }

                    previous = command;
                    break;

                case 'Q':
                    while (HasNumber())
                    {
                        var cx = ReadNumber();
                        var cy = ReadNumber();
                        var x = ReadNumber();
                        var y = ReadNumber();
                        if (relative)
                        {
                            cx += curX;
                            cy += curY;
                            x += curX;
                            y += curY;
                        }

                        ctrlX = cx;
                        ctrlY = cy;
                        curX = x;
                        curY = y;
                        EnsureFigure();
                        builder.AddQuadraticBezier(
                            new System.Numerics.Vector2((float)cx, (float)cy),
                            new System.Numerics.Vector2((float)x, (float)y));
                    }

                    previous = command;
                    break;

                case 'T':
                    while (HasNumber())
                    {
                        var reflect = previous is 'Q' or 'q' or 'T' or 't';
                        var cx = reflect ? 2 * curX - ctrlX : curX;
                        var cy = reflect ? 2 * curY - ctrlY : curY;

                        var x = ReadNumber();
                        var y = ReadNumber();
                        if (relative)
                        {
                            x += curX;
                            y += curY;
                        }

                        ctrlX = cx;
                        ctrlY = cy;
                        curX = x;
                        curY = y;
                        EnsureFigure();
                        builder.AddQuadraticBezier(
                            new System.Numerics.Vector2((float)cx, (float)cy),
                            new System.Numerics.Vector2((float)x, (float)y));
                    }

                    previous = command;
                    break;

                case 'A':
                    while (HasNumber())
                    {
                        var rx = ReadNumber();
                        var ry = ReadNumber();
                        var rotation = ReadNumber();
                        var largeArc = ReadNumber() != 0;
                        var sweep = ReadNumber() != 0;
                        var x = ReadNumber();
                        var y = ReadNumber();
                        if (relative)
                        {
                            x += curX;
                            y += curY;
                        }

                        curX = x;
                        curY = y;
                        EnsureFigure();

                        if (rx <= 0 || ry <= 0)
                        {
                            builder.AddLine(new System.Numerics.Vector2((float)x, (float)y));
                            continue;
                        }

                        builder.AddArc(
                            new System.Numerics.Vector2((float)x, (float)y),
                            (float)rx,
                            (float)ry,
                            (float)rotation,
                            sweep ? CanvasSweepDirection.Clockwise : CanvasSweepDirection.CounterClockwise,
                            largeArc ? CanvasArcSize.Large : CanvasArcSize.Small);
                    }

                    previous = command;
                    break;

                case 'Z':
                    if (figureOpen)
                    {
                        builder.EndFigure(CanvasFigureLoop.Closed);
                        figureOpen = false;
                    }

                    curX = startX;
                    curY = startY;
                    previous = command;
                    break;

                default:
                    goto done;
            }
        }

        done:
        if (figureOpen)
        {
            builder.EndFigure(CanvasFigureLoop.Open);
        }

        try
        {
            return CanvasGeometry.CreatePath(builder);
        }
        catch
        {
            return null;
        }
    }
}
