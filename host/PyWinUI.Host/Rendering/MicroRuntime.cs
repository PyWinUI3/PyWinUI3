using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using PyWinUI.Host.Protocol;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// 微代码运行时：把 Python 发来的一小段指令在**宿主 UI 线程内**解释执行。
///
/// 动机：以前 Python 做一次 20 步宽度动画要发 20 条 patch、收 20 条 ack，
/// 每条还各写一次日志。现在整段补间用一条 micro 消息发过来，宿主本地
/// 按真实经过时间推进，全程零往返、零回执。
///
/// 安全边界（这是"微代码"而不是"远程执行 C#"）：
/// - 不做动态编译、不用反射、不访问文件系统 / 网络 / 进程；
/// - 指令只有 set / animate / wait / emit 四种，固定语义；
/// - set 复用 FormRenderer 的属性应用链（带回声抑制），与一次普通 patch 等价；
/// - animate 只能改 width / height / opacity 三个数值属性；
/// - 指令数、键数、时长、步数、数值范围全部有上限，NaN / Infinity 直接拒绝；
/// - 每条消息都绑定一个目标节点，节点不存在就拒绝，不能跨节点乱写；
/// - 同一节点的新程序会顶掉旧程序（代次），不会两个动画同时改一个 Width。
///
/// 全部 await 都跑在 UI 同步上下文上（Program.cs 装了
/// DispatcherQueueSynchronizationContext），因此可以直接碰 XAML 对象。
/// </summary>
internal sealed class MicroRuntime
{
    public const int MaxOps = 64;
    public const int MaxDurationMs = 5000;
    public const int MaxSetKeys = 32;
    public const int MaxSteps = 1000;

    private static readonly HashSet<string> AnimProps =
        new(StringComparer.Ordinal) { "width", "height", "opacity" };

    private readonly FormRenderer _renderer;
    private readonly Action<string, string, JsonObject?, bool> _emit;
    private readonly Action<string> _send;
    private readonly Dictionary<string, int> _generation = new(StringComparer.Ordinal);

    public MicroRuntime(
        FormRenderer renderer,
        Action<string, string, JsonObject?, bool> emit,
        Action<string> send)
    {
        _renderer = renderer;
        _emit = emit;
        _send = send;
    }

    /// <summary>从 UI 线程调用；执行是异步的，异常全部在内部兜住。</summary>
    public void Run(MicroMessage message) => _ = ExecuteAsync(message);

    private async Task ExecuteAsync(MicroMessage message)
    {
        string? error = null;
        var superseded = false;

        try
        {
            if (message.Program.Count is 0 or > MaxOps)
            {
                throw new InvalidOperationException($"micro program 指令数必须在 1..{MaxOps}");
            }

            if (!_renderer.TryGetElement(message.Node, out var element))
            {
                throw new InvalidOperationException($"micro 的目标节点不存在: {message.Node}");
            }

            CompactRegistry();

            var generation = _generation.TryGetValue(message.Node, out var previous)
                ? previous + 1
                : 1;
            _generation[message.Node] = generation;

            foreach (var node in message.Program)
            {
                if (_generation[message.Node] != generation)
                {
                    // 同一个控件上来了更新的程序，旧的立刻退出，别再写属性
                    superseded = true;
                    break;
                }

                if (node is not JsonObject op)
                {
                    throw new InvalidOperationException("micro 指令必须是对象");
                }

                switch ((string?)op["i"])
                {
                    case "set":
                        ApplySet(message.Node, op);
                        break;
                    case "animate":
                        await AnimateAsync(element, op, message.Node, generation);
                        break;
                    case "wait":
                        await Task.Delay((int)Clamp(Number(op, "ms", 16), 0, MaxDurationMs));
                        break;
                    case "emit":
                        ApplyEmit(message.Node, op);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"未知 micro 指令 \"{(string?)op["i"]}\"");
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            HostLog.Write("[micro] 执行失败: " + ex.Message);
        }
        finally
        {
            if (message.Notify)
            {
                // 被新程序顶掉时也必须回执：否则调用方等的是一个永远不来的
                // microDone。ok=false 让「没跑完」这件事可见，而不是静默消失。
                if (superseded)
                {
                    error = "已被同一节点上的新 micro 程序取代";
                }

                _send(Outbound.MicroDone(message.Id, error is null, error));
            }
        }
    }

    private void ApplySet(string nodeId, JsonObject op)
    {
        if (op["props"] is not JsonObject props || props.Count == 0)
        {
            throw new InvalidOperationException("set 指令需要非空的 props 对象");
        }

        if (props.Count > MaxSetKeys)
        {
            throw new InvalidOperationException($"set 指令最多 {MaxSetKeys} 个属性");
        }

        if (props.ContainsKey("events"))
        {
            // 事件绑定只能走 patch 的 set.events，微代码不许偷偷改订阅
            throw new InvalidOperationException("micro set 不允许修改 events");
        }

        _renderer.ApplyMicroProps(nodeId, props);
    }

    private void ApplyEmit(string nodeId, JsonObject op)
    {
        var name = (string?)op["event"] ?? "microStep";
        if (name.Length is 0 or > 64)
        {
            throw new InvalidOperationException("emit 的 event 名长度必须在 1..64");
        }

        _emit(nodeId, name, op["data"] as JsonObject, false);
    }

    /// <summary>
    /// 指数缓出补间。用 Stopwatch 按真实经过时间换算步数，一次唤醒补齐多步，
    /// 避免 Task.Delay(3) 被 Windows 15.6ms 定时器粒度吞掉。
    /// 曲线：value(n) = to - (to - start) * (1 - 1/divisor)^n，最后 snap 到 to。
    /// </summary>
    private async Task AnimateAsync(
        FrameworkElement element,
        JsonObject op,
        string nodeId,
        int generation)
    {
        var prop = (string?)op["prop"] ?? "";
        if (!AnimProps.Contains(prop))
        {
            throw new InvalidOperationException(
                $"micro animate 只允许 {string.Join("/", AnimProps)}，收到 \"{prop}\"");
        }

        var to = Number(op, "to", 0);
        var duration = Clamp(Number(op, "ms", 320), 1, MaxDurationMs);
        var steps = (int)Clamp(Number(op, "steps", 100), 1, MaxSteps);
        var divisor = Clamp(Number(op, "divisor", 15), 1.01, 100);
        var stepMs = Math.Max(0.1, duration / steps);

        var start = ReadNumber(element, prop);
        var clock = Stopwatch.StartNew();
        var lastStep = 0;

        while (lastStep < steps)
        {
            await Task.Delay(16);

            if (_generation[nodeId] != generation)
            {
                return;
            }

            var step = Math.Min(steps, (int)(clock.Elapsed.TotalMilliseconds / stepMs));
            if (step <= lastStep)
            {
                continue;
            }

            lastStep = step;
            var value = to - (to - start) * Math.Pow(1.0 - 1.0 / divisor, step);
            WriteNumber(element, prop, Math.Round(value, 2));
        }

        if (_generation[nodeId] != generation)
        {
            return;
        }

        WriteNumber(element, prop, to);
    }

    /// <summary>
    /// 代次表只在跑过动画的节点上留一行。控件被销毁后这里不会自动收到通知，
    /// 所以攒到一定规模时顺手清掉指向已删节点的条目，避免长期运行慢慢鼓起来。
    /// </summary>
    private void CompactRegistry()
    {
        if (_generation.Count <= 256)
        {
            return;
        }

        var stale = new List<string>();
        foreach (var pair in _generation)
        {
            if (!_renderer.TryGetElement(pair.Key, out _))
            {
                stale.Add(pair.Key);
            }
        }

        foreach (var id in stale)
        {
            _generation.Remove(id);
        }
    }

    private static double Number(JsonObject op, string key, double fallback)
    {
        var node = op[key];
        if (node is null)
        {
            return fallback;
        }

        if (node is not JsonValue value || !value.TryGetValue<double>(out var number))
        {
            throw new InvalidOperationException($"micro 字段 \"{key}\" 必须是数字");
        }

        if (double.IsNaN(number) || double.IsInfinity(number) || Math.Abs(number) > 1_000_000)
        {
            throw new InvalidOperationException($"micro 字段 \"{key}\" 超出允许范围");
        }

        return number;
    }

    private static double Clamp(double value, double min, double max) =>
        Math.Min(max, Math.Max(min, value));

    private static double ReadNumber(FrameworkElement element, string prop) => prop switch
    {
        "width" => double.IsNaN(element.Width) ? element.ActualWidth : element.Width,
        "height" => double.IsNaN(element.Height) ? element.ActualHeight : element.Height,
        "opacity" => element.Opacity,
        _ => 0,
    };

    private static void WriteNumber(FrameworkElement element, string prop, double value)
    {
        switch (prop)
        {
            case "width": element.Width = Math.Max(0, value); break;
            case "height": element.Height = Math.Max(0, value); break;
            case "opacity": element.Opacity = Math.Clamp(value, 0, 1); break;
        }
    }
}
