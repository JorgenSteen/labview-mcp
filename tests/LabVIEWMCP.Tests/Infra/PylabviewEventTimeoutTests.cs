using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// An Event Structure's Timeout terminal is wired from AIXML through `timeoutin`, a net as a Case
/// Structure's `selectin` reads one. NI's AIXML has no way to say it, so a composed Event Structure
/// waited for ever (-1), and the only route - a scripting helper that sets the value after
/// generation - failed on LabVIEW 2020 at a VI Server refnum class and is reset by any regeneration
/// (B4/B5, 2026-10-28). The reader writes the attribute back, where it used to drop the wire.
/// SKIPS where the bundled interpreter is missing, as the other composer tests do.
/// </summary>
public sealed class PylabviewEventTimeoutTests
{
    private static bool ComposerHere() => AixmlComposer.Locate(out _) is not null;

    private const string Probe = """
        <VI _name="Timeout Probe.vi" description="An Event Structure whose Timeout frame stops the loop.">
          <Control _name="timeout ms" type="int32" value="200" outputs="value:4201.tmo" uid="4201" uid_parent="root"/>
          <Structure _name="While Loop" uid="4210" uid_parent="root">
            <Tunnel _id="In1" inputs="value:4201.tmo" outputs="value:4211.tmo" uid="4211" uid_parent="4210"/>
            <Structure _name="Event Structure" timeoutin="4211.tmo" uid="4220" uid_parent="4210">
              <CaseFrame selector=" Timeout " uid="4221" uid_parent="4220">
                <Constant _name="stop now" type="bool" value="true" outputs="value:4222.value" uid="4222" uid_parent="4221"/>
                <Tunnel _id="Out1" inputs="value:4222.value" uid="4223" uid_parent="4221"/>
              </CaseFrame>
              <Tunnel _id="Out1" outputs="value:4224.stop" uid="4224" uid_parent="4220"/>
            </Structure>
            <Condition inputs="value:4224.stop" uid="4225" uid_parent="4210" value="stop"/>
          </Structure>
        </VI>
        """;

    [Fact]
    public async Task A_wired_event_timeout_composes_and_reads_back_as_timeoutin()
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("timeout.xml");
        var vi = server.TempPath("timeout.vi");
        await File.WriteAllTextAsync(xml, Probe);

        var composed = await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, vi, checkExecutable: false, backend: "pylabview");
        Assert.Equal(0, Res.Int(composed, "errorCode"));

        var setup = AixmlComposer.Locate(out _)!;
        var output = server.TempPath("timeout-back.xml");
        var read = await AixmlComposer.ReadAsync(setup, vi, output, 120, default);
        Assert.True(read.Written, read.Message);
        var back = XElement.Load(output);
        var events = back.Descendants("Structure").Single(s => (string?)s.Attribute("_name") == "Event Structure");
        var net = (string?)events.Attribute("timeoutin");
        Assert.False(string.IsNullOrEmpty(net), "the Timeout terminal's wire was dropped");
        // the net comes out of the loop's input tunnel, whose outer side reads the control
        var source = back.Descendants("Tunnel").Single(t => ((string?)t.Attribute("outputs") ?? "").EndsWith(":" + net));
        var control = back.Descendants("Control").Single(c => (string?)c.Attribute("_name") == "timeout ms");
        var controlNet = ((string)control.Attribute("outputs")!).Split(':', 2)[1];
        Assert.Contains(":" + controlNet, (string?)source.Attribute("inputs") ?? "");
    }
}
