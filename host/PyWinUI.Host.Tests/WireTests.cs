using System.Text.Json;
using PyWinUI.Host.Protocol;
using Xunit;

namespace PyWinUI.Host.Tests;

/// <summary>
/// 协议常量与 JSON 口径。两端序列化必须完全一致，这里锁住那些
/// 「改一个设置就静默破坏兼容性」的细节。
/// </summary>
public sealed class WireTests
{
    [Fact]
    public void Ready_UsesStableProtocolIdentity()
    {
        Assert.Equal(1, Wire.Version);
        Assert.Equal("PyWinUI.Host/0.2.1", Wire.HostId);
        Assert.True(Wire.MaxLineBytes > 0);
    }

    [Fact]
    public void Json_CamelCasesAndIgnoresNulls()
    {
        var json = JsonSerializer.Serialize(
            new { NodeId = "n1", Missing = (string?)null, Flag = true },
            Wire.Json);

        Assert.Equal("{\"nodeId\":\"n1\",\"flag\":true}", json);
    }

    [Fact]
    public void Json_DoesNotEscapeNonAscii()
    {
        // 中文必须是裸字符，不能变成 \u4E2D：既省带宽也让管道日志可读。
        var json = JsonSerializer.Serialize(new { Text = "中文标签" }, Wire.Json);
        Assert.Contains("中文标签", json);
        Assert.DoesNotContain("\\u", json);
    }

    [Fact]
    public void Json_IsCaseInsensitiveOnRead()
    {
        var value = JsonSerializer.Deserialize<Probe>("{\"NodeId\":\"n1\"}", Wire.Json);
        Assert.NotNull(value);
        Assert.Equal("n1", value.NodeId);
    }

    private sealed record Probe(string? NodeId);

    // ---------------------------------------------------------------
    // 错误分级：这是评审里「坏消息处置两端相反」的回归锁。
    // 只有 version-mismatch 致命；bad-json / bad-protocol / bad-op / bad-micro
    // 一律非致命，逐行分帧下跳过坏行即可。
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("version-mismatch", true)]
    [InlineData("bad-json", false)]
    [InlineData("bad-protocol", false)]
    [InlineData("bad-op", false)]
    [InlineData("bad-micro", false)]
    [InlineData("whatever", false)]
    public void IsFatal_OnlyVersionMismatchKillsSession(string code, bool expected)
    {
        Assert.Equal(expected, new ProtocolException(code, "msg").IsFatal);
    }

    [Fact]
    public void ProtocolException_CarriesCodeAndOptionalNode()
    {
        var inner = new InvalidOperationException("boom");
        var ex = new ProtocolException("bad-json", "消息不是合法 JSON", "n1", inner);

        Assert.Equal("bad-json", ex.Code);
        Assert.Equal("n1", ex.NodeId);
        Assert.Same(inner, ex.InnerException);
        Assert.Contains("合法 JSON", ex.Message);
    }

    [Fact]
    public void ProtocolException_NodeIdIsOptional()
    {
        Assert.Null(new ProtocolException("bad-protocol", "x").NodeId);
    }
}
