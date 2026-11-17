using System.Text.Json.Nodes;
using PyWinUI.Host.Protocol;
using Xunit;

namespace PyWinUI.Host.Tests;

/// <summary>
/// Host → Python 的消息构造。重点锁两件事：
/// 1. 每条消息都是单行 JSON（分帧靠换行，消息体里绝不能有裸换行）；
/// 2. NaN / Infinity 必须被换成 null——否则 System.Text.Json 直接抛异常，
///    而 NumberBox 空值、Slider 中间态真的会产生非有限数。
/// </summary>
public sealed class OutboundTests
{
    private static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json)!.AsObject();

    // ---------------------------------------------------------------
    // 消息形状
    // ---------------------------------------------------------------

    [Fact]
    public void Ready_ContainsVersionHostOsAndCapabilities()
    {
        var json = ParseObject(Outbound.Ready(
            new OsInfo("10.0", 22631),
            new CapabilitiesInfo(true, false, true, "mica")));

        Assert.Equal("ready", (string?)json["t"]);
        Assert.Equal(Wire.Version, (int?)json["protocol"]);
        Assert.Equal(Wire.HostId, (string?)json["host"]);
        Assert.Equal("10.0", (string?)json["os"]!["version"]);
        Assert.Equal(22631, (int?)json["os"]!["build"]);
        Assert.Equal("mica", (string?)json["capabilities"]!["backdrop"]);
        Assert.True((bool?)json["capabilities"]!["mica"]);
        Assert.False((bool?)json["capabilities"]!["acrylic"]);
    }

    [Fact]
    public void Ack_CarriesRevision()
    {
        var json = ParseObject(Outbound.Ack(42));
        Assert.Equal("ack", (string?)json["t"]);
        Assert.Equal(42, (long?)json["rev"]);
    }

    [Fact]
    public void Event_OmitsEchoWhenFalseAndDataWhenNull()
    {
        var json = ParseObject(Outbound.Event("n1", "click", 7, null, echo: false));

        Assert.Equal("event", (string?)json["t"]);
        Assert.Equal("n1", (string?)json["id"]);
        Assert.Equal("click", (string?)json["event"]);
        Assert.Equal(7, (long?)json["rev"]);
        Assert.Null(json["echo"]);
        Assert.Null(json["data"]);
    }

    [Fact]
    public void Event_IncludesEchoOnlyWhenTrue()
    {
        var data = new JsonObject { ["x"] = 1.5 };
        var json = ParseObject(Outbound.Event("n1", "click", 8, data, echo: true));

        Assert.True((bool?)json["echo"]);
        Assert.Equal(1.5, (double?)json["data"]!["x"]);
    }

    [Fact]
    public void Result_SuccessHasNoErrorField()
    {
        var json = ParseObject(Outbound.Result("q1", true, JsonValue.Create("ok")));

        Assert.Equal("result", (string?)json["t"]);
        Assert.Equal("q1", (string?)json["id"]);
        Assert.True((bool?)json["ok"]);
        Assert.Equal("ok", (string?)json["value"]);
        Assert.Null(json["error"]);
    }

    [Fact]
    public void Result_FailureCarriesError()
    {
        var json = ParseObject(Outbound.Result("q1", false, null, "bad-key"));

        Assert.False((bool?)json["ok"]);
        Assert.Equal("bad-key", (string?)json["error"]);
        Assert.Null(json["value"]);
    }

    [Fact]
    public void Warning_MicroDone_Error_Bye_HaveExpectedTags()
    {
        Assert.Equal("warning", (string?)ParseObject(Outbound.Warning("注意", "n1"))["t"]);
        Assert.Equal("n1", (string?)ParseObject(Outbound.Warning("注意", "n1"))["id"]);

        var done = ParseObject(Outbound.MicroDone("m1", true));
        Assert.Equal("microDone", (string?)done["t"]);
        Assert.True((bool?)done["ok"]);
        Assert.Null(done["error"]);

        var failed = ParseObject(Outbound.MicroDone("m1", false, "超时"));
        Assert.False((bool?)failed["ok"]);
        Assert.Equal("超时", (string?)failed["error"]);

        var error = ParseObject(Outbound.Error("bad-protocol", "字段缺失", "n1"));
        Assert.Equal("error", (string?)error["t"]);
        Assert.Equal("bad-protocol", (string?)error["code"]);

        Assert.Equal(0, (int?)ParseObject(Outbound.Bye(0))["code"]);
    }

    [Fact]
    public void EveryMessage_IsSingleLine()
    {
        var messages = new[]
        {
            Outbound.Ready(new OsInfo("10", 1), new CapabilitiesInfo(false, false, false, "none")),
            Outbound.Ack(1),
            Outbound.Event("n", "e", 1, new JsonObject { ["a"] = "带\n换行" }, true),
            Outbound.Result("q", true, JsonValue.Create(1)),
            Outbound.Warning("w"),
            Outbound.MicroDone("m", true),
            Outbound.Error("code", "msg"),
            Outbound.Bye(0),
        };

        foreach (var message in messages)
        {
            Assert.DoesNotContain("\n", message);
            Assert.DoesNotContain("\r", message);
        }
    }

    // ---------------------------------------------------------------
    // NaN / Infinity 清洗：评审里两个真实踩过的坑
    // ---------------------------------------------------------------

    [Fact]
    public void Result_TopLevelNaN_BecomesNull()
    {
        // NumberBox 的空值就是 double.NaN，query("value") 直接回它。
        // Fix() 只遍历容器，顶层必须先单独判一次，否则序列化当场抛。
        var json = ParseObject(Outbound.Result("q1", true, JsonValue.Create(double.NaN)));
        Assert.Null(json["value"]);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Result_TopLevelNonFinite_BecomesNull(double value)
    {
        var json = ParseObject(Outbound.Result("q1", true, JsonValue.Create(value)));
        Assert.Null(json["value"]);
    }

    [Fact]
    public void Result_NestedNonFiniteInObjectAndArray_BecomesNull()
    {
        var payload = new JsonObject
        {
            ["good"] = 3.5,
            ["nan"] = JsonValue.Create(double.NaN),
            ["inf"] = JsonValue.Create(double.PositiveInfinity),
            ["nested"] = new JsonObject
            {
                ["negInf"] = JsonValue.Create(double.NegativeInfinity),
                ["keep"] = "yes",
            },
            ["array"] = new JsonArray(
                1,
                JsonValue.Create(double.NaN),
                new JsonObject { ["deep"] = JsonValue.Create(double.PositiveInfinity) }),
        };

        var json = ParseObject(Outbound.Result("q1", true, payload));
        var value = json["value"]!.AsObject();

        Assert.Equal(3.5, (double?)value["good"]);
        Assert.Null(value["nan"]);
        Assert.Null(value["inf"]);
        Assert.Null(value["nested"]!["negInf"]);
        Assert.Equal("yes", (string?)value["nested"]!["keep"]);

        var array = value["array"]!.AsArray();
        Assert.Equal(1, (int?)array[0]);
        Assert.Null(array[1]);
        Assert.Null(array[2]!["deep"]);
    }

    [Fact]
    public void Event_DataWithNaN_DoesNotBreakSerialization()
    {
        var data = new JsonObject
        {
            ["value"] = JsonValue.Create(double.NaN),
            ["label"] = "slider",
        };

        var json = ParseObject(Outbound.Event("n1", "valueChanged", 9, data, false));

        Assert.Null(json["data"]!["value"]);
        Assert.Equal("slider", (string?)json["data"]!["label"]);
    }

    [Fact]
    public void Sanitize_DoesNotMutateCallerPayload()
    {
        // DeepClone 是刻意的：调用方还要继续用这个 JsonObject。
        var payload = new JsonObject { ["nan"] = JsonValue.Create(double.NaN) };
        _ = Outbound.Result("q1", true, payload);

        Assert.NotNull(payload["nan"]);
        Assert.True(double.IsNaN(payload["nan"]!.GetValue<double>()));
    }
}
