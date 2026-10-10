// =============================================================================
// RpcDialogTests.cs - a timed-out call names LabVIEW's possible dialogs in its error,
// and every other answer stays exactly as it was.
// =============================================================================
using System.Text.Json.Nodes;
using Grpc.Core;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

[Collection("RpcDialogProbe")]
public class RpcDialogTests
{
    private static async Task<JsonObject> WithProbe(JsonArray? dialogs, Func<Task<string>> body)
    {
        var saved = Rpc.DialogProbe;
        Rpc.DialogProbe = () => dialogs?.DeepClone() as JsonArray;
        try { return Res.Obj(await Rpc.GuardAsync(body)); }
        finally { Rpc.DialogProbe = saved; }
    }

    private static JsonArray OneDialog() =>
        [new JsonObject { ["title"] = "Find the VI Named 'x.vi'", ["class"] = "LVDChild", ["visible"] = false }];

    [Fact]
    public async Task A_deadline_names_the_dialog_that_may_block_the_service()
    {
        var o = await WithProbe(OneDialog(), () => throw new RpcException(new Status(StatusCode.DeadlineExceeded, "late")));
        Assert.Equal("Find the VI Named 'x.vi'", o["possibleDialogs"]![0]!["title"]!.GetValue<string>());
        Assert.Contains("nothing was pressed", o["dialogNote"]!.GetValue<string>());
        Assert.Equal("rpc", o["errorKind"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_local_timeout_names_it_too()
    {
        var o = await WithProbe(OneDialog(), () => throw new OperationCanceledException());
        Assert.Equal("cancelled", o["errorKind"]!.GetValue<string>());
        Assert.NotNull(o["possibleDialogs"]);
    }

    [Fact]
    public async Task Other_failures_and_no_dialog_leave_the_answer_as_it_was()
    {
        var other = await WithProbe(OneDialog(), () => throw new RpcException(new Status(StatusCode.Internal, "boom")));
        Assert.Null(other["possibleDialogs"]);

        var none = await WithProbe(null, () => throw new RpcException(new Status(StatusCode.DeadlineExceeded, "late")));
        Assert.Null(none["possibleDialogs"]);
        Assert.Null(none["dialogNote"]);
    }

    [Fact]
    public async Task A_probe_that_throws_does_not_change_the_error()
    {
        var saved = Rpc.DialogProbe;
        Rpc.DialogProbe = () => throw new InvalidOperationException("no windows");
        try
        {
            var o = Res.Obj(await Rpc.GuardAsync(() => throw new OperationCanceledException()));
            Assert.Equal("cancelled", o["errorKind"]!.GetValue<string>());
        }
        finally { Rpc.DialogProbe = saved; }
    }
}
