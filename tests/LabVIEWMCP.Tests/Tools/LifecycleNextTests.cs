// =============================================================================
// LifecycleNextTests.cs - the advice a `starting` answer of lvai_ensure_labview
// gives: per backend, and when the LabVIEW it started is gone.
// =============================================================================
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

public sealed class LifecycleNextTests
{
    /// <summary>
    /// Measured 2026-11-13 on the pylabview backend: the first call of a cold start answered
    /// "its AI gRPC service is not answering yet" while `lastError` named the start-up splash.
    /// </summary>
    [Fact]
    public void On_pylabview_the_advice_is_about_ActiveX_not_the_gRPC_service()
    {
        var next = LifecycleTools.NextAfterStarting(stillRunning: 1, overCom: true);

        Assert.Contains("ActiveX", next);
        Assert.Contains("CALL THIS TOOL AGAIN", next);
        Assert.DoesNotContain("gRPC service is not answering", next);
        Assert.DoesNotContain("Nigel", next);
    }

    [Fact]
    public void On_grpc_the_advice_is_about_the_service()
    {
        var next = LifecycleTools.NextAfterStarting(stillRunning: 1, overCom: false);

        Assert.Contains("gRPC service is not answering yet", next);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void With_no_LabVIEW_left_the_advice_says_so_on_both_backends(bool overCom) =>
        Assert.StartsWith("No LabVIEW process is running any more",
                          LifecycleTools.NextAfterStarting(stillRunning: 0, overCom));
}
