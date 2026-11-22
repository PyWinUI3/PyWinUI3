using System.Text.Json.Nodes;
using PyWinUI.Host.Protocol;
using Xunit;

namespace PyWinUI.Host.Tests;

/// <summary>
/// Python → Host 的消息解析。覆盖合法形状，以及评审里点名的
/// 「解析异常穿透」「坏消息处置不一致」「JsonElement 脱离文档生命周期」。
/// </summary>
public sealed class InboundTests
{
    // ---------------------------------------------------------------
    // 合法消息
    // ---------------------------------------------------------------

    [Fact]
    public void Parse_Hello()
    {
        var msg = Assert.IsType<HelloMessage>(Inbound.Parse(
            """{"op":"hello","protocol":1,"client":"pywinui3/0.1","pid":1234}"""));

        Assert.Equal(1, msg.Protocol);
        Assert.Equal("pywinui3/0.1", msg.Client);
        Assert.Equal(1234, msg.Pid);
    }

    [Fact]
    public void Parse_Hello_PidIsOptional()
    {
        var msg = Assert.IsType<HelloMessage>(Inbound.Parse(
            """{"op":"hello","protocol":1,"client":"x"}"""));
        Assert.Null(msg.Pid);
    }

    [Fact]
    public void Parse_Render_KeepsTreeAndCopiesPayloadOutOfDocument()
    {
        var msg = Assert.IsType<RenderMessage>(Inbound.Parse("""
            {
              "op": "render", "rev": 5, "root": "root",
              "window": { "title": "中文标题", "width": 640, "resizable": true },
              "nodes": [
                { "id": "root", "type": "Column", "children": ["btn"] },
                {
                  "id": "btn", "type": "Button",
                  "props": { "text": "点我" },
                  "events": { "click": "on_click" }
                }
              ]
            }
            """));

        Assert.Equal(5, msg.Rev);
        Assert.Equal("root", msg.Root);
        Assert.Equal("中文标题", msg.Window.Title);
        Assert.Equal(640, msg.Window.Width);
        Assert.True(msg.Window.Resizable);
        Assert.Equal(2, msg.Nodes.Count);
        Assert.Equal(new[] { "btn" }, msg.Nodes[0].Children);

        var btn = msg.Nodes[1];
        Assert.Equal("点我", (string?)btn.Props["text"]);
        Assert.Equal("on_click", btn.Events!["click"]);
    }

    [Fact]
    public void Parse_Patch_AllFiveOps()
    {
        var msg = Assert.IsType<PatchMessage>(Inbound.Parse("""
            {
              "op": "patch", "rev": 6,
              "ops": [
                { "t": "set", "id": "a", "props": { "text": "新标题" } },
                { "t": "insert", "parent": "root", "index": 0,
                  "node": { "id": "b", "type": "TextBlock" } },
                { "t": "remove", "id": "c" },
                { "t": "move", "id": "b", "parent": "other", "index": 2 },
                { "t": "window", "props": { "title": "新窗口" } }
              ]
            }
            """));

        Assert.Equal(6, msg.Rev);
        Assert.Equal(PatchOpKind.Set, msg.Ops[0].Kind);
        Assert.Equal("a", msg.Ops[0].Id);
        Assert.Equal("新标题", (string?)msg.Ops[0].Props!["text"]);

        Assert.Equal(PatchOpKind.Insert, msg.Ops[1].Kind);
        Assert.Equal("root", msg.Ops[1].Parent);
        Assert.Equal(0, msg.Ops[1].Index);
        Assert.Equal("b", msg.Ops[1].Node!.Id);

        Assert.Equal(PatchOpKind.Remove, msg.Ops[2].Kind);
        Assert.Equal(PatchOpKind.Move, msg.Ops[3].Kind);
        Assert.Equal("other", msg.Ops[3].Parent);
        Assert.Equal(2, msg.Ops[3].Index);
        Assert.Equal(PatchOpKind.Window, msg.Ops[4].Kind);
        Assert.Equal("新窗口", (string?)msg.Ops[4].Props!["title"]);
    }

    [Fact]
    public void Parse_InvokeAndQuery_CopyArgs()
    {
        var invoke = Assert.IsType<InvokeMessage>(Inbound.Parse(
            """{"op":"invoke","id":"i1","method":"focus","args":{"force":true}}"""));
        Assert.Equal("i1", invoke.Id);
        Assert.Equal("focus", invoke.Method);
        Assert.True((bool?)invoke.Args!["force"]);

        var query = Assert.IsType<QueryMessage>(Inbound.Parse(
            """{"op":"query","id":"q1","node":"n1","key":"tabOrder"}"""));
        Assert.Equal("q1", query.Id);
        Assert.Equal("n1", query.Node);
        Assert.Equal("tabOrder", query.Key);
        Assert.Null(query.Args);
    }

    [Fact]
    public void Parse_Micro_CopiesProgramOutOfDocument()
    {
        var msg = Assert.IsType<MicroMessage>(Inbound.Parse("""
            {
              "op": "micro", "id": "m1", "node": "n1", "notify": true,
              "program": [
                { "i": "set", "props": { "opacity": 0.5 } },
                { "i": "wait", "ms": 16 }
              ]
            }
            """));

        Assert.Equal("m1", msg.Id);
        Assert.Equal("n1", msg.Node);
        Assert.Equal("main", msg.Session);
        Assert.True(msg.Notify);
        Assert.Equal(2, msg.Program.Count);
        Assert.Equal("set", (string?)msg.Program[0]!["i"]);
        Assert.Equal(0.5, (double?)msg.Program[0]!["props"]!["opacity"]);
        Assert.Equal(16, (int?)msg.Program[1]!["ms"]);
    }

    [Fact]
    public void Parse_Micro_PreservesSession()
    {
        // 回归：ParseMicro 曾漏解析 session，导致非 main 窗口的动画
        // 全被路由到 main 的 MicroRuntime（找不到节点或写错节点）。
        var msg = Assert.IsType<MicroMessage>(Inbound.Parse("""
            {
              "op": "micro", "id": "m2", "node": "n9", "session": "w1",
              "program": [ { "i": "set", "props": { "opacity": 0.5 } } ]
            }
            """));

        Assert.Equal("w1", msg.Session);
        Assert.Equal("n9", msg.Node);
    }

    [Fact]
    public void Parse_DialogAndClose_UseDefaults()
    {
        var dialog = Assert.IsType<DialogMessage>(Inbound.Parse("""{"op":"dialog"}"""));
        Assert.Equal("提示", dialog.Title);
        Assert.Equal("", dialog.Body);
        Assert.Null(dialog.Kind);

        var close = Assert.IsType<CloseMessage>(Inbound.Parse("""{"op":"close"}"""));
        Assert.Equal(0, close.Code);
    }

    // ---------------------------------------------------------------
    // 文件对话框（自绘，不用原生 Picker）
    // ---------------------------------------------------------------

    [Fact]
    public void Parse_FileDialog_AllFields()
    {
        var msg = Assert.IsType<FileDialogMessage>(Inbound.Parse("""
            {
              "op": "fileDialog", "id": "f1", "mode": "save",
              "title": "保存文件", "initialDir": "D:\\data",
              "filter": "*.txt;*.py", "defaultName": "out.txt"
            }
            """));

        Assert.Equal("f1", msg.Id);
        Assert.Equal("save", msg.Mode);
        Assert.Equal("保存文件", msg.Title);
        Assert.Equal(@"D:\data", msg.InitialDir);
        Assert.Equal("*.txt;*.py", msg.Filter);
        Assert.Equal("out.txt", msg.DefaultName);
    }

    [Theory]
    [InlineData("open", "open")]
    [InlineData("openMultiple", "openMultiple")]
    [InlineData("OPENMULTIPLE", "openMultiple")]
    [InlineData("folder", "folder")]
    public void Parse_FileDialog_NormalizesMode(string raw, string expected)
    {
        var msg = Assert.IsType<FileDialogMessage>(Inbound.Parse(
            $"{{\"op\":\"fileDialog\",\"id\":\"f\",\"mode\":\"{raw}\"}}"));
        Assert.Equal(expected, msg.Mode);
    }

    [Fact]
    public void Parse_FileDialog_OptionalFieldsAreNull()
    {
        var msg = Assert.IsType<FileDialogMessage>(Inbound.Parse(
            """{"op":"fileDialog","id":"f","mode":"open"}"""));
        Assert.Null(msg.Title);
        Assert.Null(msg.InitialDir);
        Assert.Null(msg.Filter);
        Assert.Null(msg.DefaultName);
    }

    [Fact]
    public void Parse_FileDialog_InvalidModeThrowsBadProtocol()
    {
        var ex = Assert.Throws<ProtocolException>(() => Inbound.Parse(
            """{"op":"fileDialog","id":"f","mode":"bogus"}"""));
        Assert.Equal("bad-protocol", ex.Code);
        Assert.False(ex.IsFatal);
    }

    [Fact]
    public void Parse_FileDialog_MissingIdThrowsBadProtocol()
    {
        var ex = Assert.Throws<ProtocolException>(() => Inbound.Parse(
            """{"op":"fileDialog","mode":"open"}"""));
        Assert.Equal("bad-protocol", ex.Code);
    }

    [Fact]
    public void Parse_UnknownOp_BecomesNonFatalUnknownMessage()
    {
        // 新增 op 不算不兼容变更：旧端遇到新消息忽略即可，不能终止会话。
        var msg = Assert.IsType<UnknownMessage>(Inbound.Parse("""{"op":"futureThing"}"""));
        Assert.Equal("futureThing", msg.Op);
    }

    // ---------------------------------------------------------------
    // 坏消息：必须抛 ProtocolException 且非致命，不能让 JsonException 穿透，
    // 更不能终止会话（逐行分帧，跳过坏行继续）。
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ broken }")]
    [InlineData("")]
    public void Parse_BadJson_ThrowsNonFatalBadJson(string line)
    {
        var ex = Assert.Throws<ProtocolException>(() => Inbound.Parse(line));
        Assert.Equal("bad-json", ex.Code);
        Assert.False(ex.IsFatal);
        // JsonDocument.Parse 实际抛的是 JsonReaderException（JsonException 子类），
        // 用 IsAssignableFrom 而不是 IsType，别把文档注释写死在基类上。
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(ex.InnerException);
    }

    [Fact]
    public void Parse_NonObjectRoot_ThrowsBadProtocol()
    {
        var ex = Assert.Throws<ProtocolException>(() => Inbound.Parse("[1,2,3]"));
        Assert.Equal("bad-protocol", ex.Code);
        Assert.False(ex.IsFatal);
    }

    [Theory]
    [InlineData("""{"client":"x"}""")]                       // 缺 op
    [InlineData("""{"op":"hello","client":"x"}""")]          // 缺 protocol
    [InlineData("""{"op":"hello","protocol":"1","client":"x"}""")] // 类型错
    [InlineData("""{"op":"query","id":"q","node":"n"}""")]   // 缺 key
    public void Parse_MissingOrWrongField_ThrowsBadProtocol(string line)
    {
        var ex = Assert.Throws<ProtocolException>(() => Inbound.Parse(line));
        Assert.Equal("bad-protocol", ex.Code);
        Assert.False(ex.IsFatal);
    }

    [Fact]
    public void Parse_ChildrenWithNonString_ThrowsBadProtocol()
    {
        var ex = Assert.Throws<ProtocolException>(() => Inbound.Parse("""
            {
              "op":"render","rev":1,"root":"r",
              "window":{},
              "nodes":[{"id":"r","type":"Column","children":[123]}]
            }
            """));
        Assert.Equal("bad-protocol", ex.Code);
    }

    [Fact]
    public void Parse_EventWithNonStringHandler_ThrowsBadProtocol()
    {
        var ex = Assert.Throws<ProtocolException>(() => Inbound.Parse("""
            {
              "op":"render","rev":1,"root":"r",
              "window":{},
              "nodes":[{"id":"r","type":"Button","events":{"click":123}}]
            }
            """));
        Assert.Equal("bad-protocol", ex.Code);
    }

    [Fact]
    public void Parse_UnknownPatchOp_ThrowsBadOp()
    {
        var ex = Assert.Throws<ProtocolException>(() => Inbound.Parse(
            """{"op":"patch","rev":1,"ops":[{"t":"teleport","id":"a"}]}"""));
        Assert.Equal("bad-op", ex.Code);
        Assert.False(ex.IsFatal);
    }

    [Fact]
    public void Parse_MicroWithEmptyProgram_ThrowsBadMicro()
    {
        var ex = Assert.Throws<ProtocolException>(() => Inbound.Parse(
            """{"op":"micro","id":"m1","node":"n1","program":[]}"""));
        Assert.Equal("bad-micro", ex.Code);
        Assert.Equal("m1", ex.NodeId);
        Assert.False(ex.IsFatal);
    }

    [Fact]
    public void Parse_MicroWithTooManyOps_ThrowsBadMicro()
    {
        var ops = string.Join(",", Enumerable.Repeat("""{"i":"wait","ms":1}""", 65));
        var ex = Assert.Throws<ProtocolException>(() => Inbound.Parse(
            $"{{\"op\":\"micro\",\"id\":\"m1\",\"node\":\"n1\",\"program\":[{ops}]}}"));
        Assert.Equal("bad-micro", ex.Code);
    }

    [Fact]
    public void Parse_MicroWithMissingInstruction_ThrowsBadMicro()
    {
        var ex = Assert.Throws<ProtocolException>(() => Inbound.Parse(
            """{"op":"micro","id":"m1","node":"n1","program":[{"ms":16}]}"""));
        Assert.Equal("bad-micro", ex.Code);
    }

    [Fact]
    public void Parse_ArgsAreIndependentJsonObjects()
    {
        // ToJsonObject 必须深拷贝：直接持有 JsonElement 会在 using(doc) 结束后失效。
        var msg = Assert.IsType<InvokeMessage>(Inbound.Parse(
            """{"op":"invoke","id":"i","method":"m","args":{"nested":{"v":7}}}"""));

        var args = msg.Args!;
        args["added"] = "later"; // 原文档已释放，这里不能抛
        Assert.Equal("later", (string?)args["added"]);
        Assert.Equal(7, (int?)args["nested"]!["v"]);
    }
}
