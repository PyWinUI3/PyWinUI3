using PyWinUI.Host.Protocol;
using Xunit;

namespace PyWinUI.Host.Tests;

/// <summary>
/// 协议数据模型。锁住解析器依赖的缺省值语义：
/// 属性缺省交给渲染层决定，不在协议层预设默认值。
/// </summary>
public sealed class FormModelTests
{
    [Fact]
    public void NodeSpec_DefaultsAreEmptyButUsable()
    {
        var node = new NodeSpec();

        Assert.Equal("", node.Id);
        Assert.Equal("", node.Type);
        Assert.NotNull(node.Props);
        Assert.Empty(node.Props);
        Assert.Null(node.Children);
        Assert.Null(node.Events);
    }

    [Fact]
    public void WindowSpec_AllPropertiesDefaultToNull()
    {
        var spec = new WindowSpec();

        Assert.Null(spec.Title);
        Assert.Null(spec.Width);
        Assert.Null(spec.Resizable);
        Assert.Null(spec.Scroll);
        Assert.Null(spec.Theme);
        Assert.Null(spec.Backdrop);
    }

    [Fact]
    public void PatchOp_UsesInitializerValues()
    {
        var op = new PatchOp
        {
            Kind = PatchOpKind.Move,
            Id = "n1",
            Parent = "p1",
            Index = 3,
        };

        Assert.Equal(PatchOpKind.Move, op.Kind);
        Assert.Equal("n1", op.Id);
        Assert.Equal("p1", op.Parent);
        Assert.Equal(3, op.Index);
        Assert.Null(op.Props);
        Assert.Null(op.Node);
    }
}
