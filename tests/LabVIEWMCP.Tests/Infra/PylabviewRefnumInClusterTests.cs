using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// A refnum that holds a type - <c>ref{UserEvent}{bool.stop}</c>, <c>ref{Queue}{string.cmd}</c> - composed
/// as a top-level control but was refused as "unreadable type" INSIDE a cluster or an array until
/// 2026-10-28: the element splitter read one brace group after <c>ref</c> and took the payload group for
/// a malformed label. The actor helper loop's events typedef, <c>cluster{ref{UserEvent}{bool.stop}.stop}</c>,
/// is exactly that shape, so lvai_create_typedef ("carrierRefused"), lvai_placeholder_subvi and
/// lvai_add_class_method's pre-check all went to NI's service for it. Measured after the fix: all four
/// shapes below composed for LabVIEW 2020 and read execState 1 there. These run the bundled interpreter
/// and SKIP without it, as the other composer tests do.
/// </summary>
public sealed class PylabviewRefnumInClusterTests
{
    private static bool ComposerHere() => AixmlComposer.Locate(out _) is not null;

    private static string Probe(string type) => $"""
        <VI _name="RefInCluster.vi" description="A refnum that holds a type, inside a cluster or an array.">
          <Control _name="Events in" conIdx="5" connection="required" type="{type}" value="" outputs="value:4201.v" uid="4201" uid_parent="root"/>
          <Indicator _name="Events out" conIdx="6" connection="recommended" type="{type}" value="" inputs="value:4201.v" uid="4202" uid_parent="root"/>
        </VI>
        """;

    private static async Task<XElement> ReadAsync(string vi)
    {
        var setup = AixmlComposer.Locate(out _);
        Assert.NotNull(setup);
        var output = Path.Combine(Path.GetTempPath(), "reader-" + Guid.NewGuid().ToString("n")[..8] + ".xml");
        try
        {
            var result = await AixmlComposer.ReadAsync(setup, vi, output, 120, default);
            Assert.True(result.Written, result.Message);
            return XElement.Load(output);
        }
        finally
        {
            try { File.Delete(output); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("cluster{ref{UserEvent}{bool.stop}.stop}")]
    [InlineData("cluster{ref{UserEvent}{bool.stop}.stop,ref{UserEvent}{int32.tick}.tick,double.rate}")]
    [InlineData("array{ref{UserEvent}{bool.stop}}")]
    [InlineData("cluster{ref{Queue}{string.cmd}.cmds,bool.flag}")]
    public async Task A_refnum_holding_a_type_composes_inside_a_cluster_or_array_and_reads_back_unchanged(string type)
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("refincluster.xml");
        var vi = server.TempPath("refincluster.vi");
        await File.WriteAllTextAsync(xml, Probe(type));

        var composed = await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, vi, checkExecutable: false, backend: "pylabview");
        Assert.Equal(0, Res.Int(composed, "errorCode"));

        var read = await ReadAsync(vi);
        Assert.Equal(type, (string?)read.Descendants("Control").Single().Attribute("type"));
        Assert.Equal(type, (string?)read.Descendants("Indicator").Single().Attribute("type"));
    }

    [Fact]
    public async Task A_second_brace_group_after_a_type_that_is_not_a_refnum_is_still_refused()
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("refincluster-bad.xml");
        await File.WriteAllTextAsync(xml, Probe("cluster{array{double}{int32}.bad}"));

        var composed = await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, server.TempPath("refincluster-bad.vi"), checkExecutable: false, backend: "pylabview");

        Assert.NotEqual(0, Res.Int(composed, "errorCode"));
        Assert.Contains("unreadable type", Res.Str(composed, "errorMessage"));
    }
}
