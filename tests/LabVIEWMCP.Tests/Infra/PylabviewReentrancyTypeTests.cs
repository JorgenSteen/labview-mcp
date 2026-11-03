// ============================================================================
// Execution:Reentrancy Type composes, and the name check knows it (session-7 report M4).
// ============================================================================
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// A DQMH cloneable module needs every member reentrant, and on LabVIEW 2025 there was no route to
/// set the TYPE: the composer refused `Execution:Reentrancy Type` ("not in the harvested table") and
/// NI's ValidateAIXML is Unimplemented there. The table carries the property under its short name
/// `Exec.ReentrancyType` (code 648, harvested from SetUpClassControls.vi), and LabVIEW 2026's
/// resource\lvserver.rsc pairs the two names under id 0x288 - so the pair is an alias in
/// parts/property-names.json. Measured on LabVIEW 2026 2026-11-03: written 0 on a VI whose default
/// was 1, saved, read back 0 after a fresh load. These run the bundled interpreter and SKIP
/// without it.
/// </summary>
public sealed class PylabviewReentrancyTypeTests
{
    private static bool ComposerHere() => AixmlComposer.Locate(out _) is not null;

    private const string Probe = """
        <VI _name="reentrancy.vi" description="Writes and reads the reentrancy of a VI.">
          <Control _name="target" type="path" uid="4200" uid_parent="root" value="" outputs="value:4200.value"/>
          <Control _name="reentrancy type" type="uint16{Share clones between instances,Preallocate clone for each instance}" uid="4202" uid_parent="root" value="0" outputs="value:4202.value"/>
          <Node _name="Open VI Reference" inputs="vi path:4200.value,error in (no error):" outputs="vi reference:4210.vi,error out:4210.err" uid="4210" uid_parent="root"/>
          <Node _name="Property Node" fields="write+Execution\3AReentrancy Type" type="{LV.VI}" inputs="reference:4210.vi,error in (no error):4210.err,Execution\3AReentrancy Type:4202.value" outputs="reference out:,error out:4212.err" uid="4212" uid_parent="root"/>
          <Node _name="Property Node" fields="read+Execution\3AReentrancy Type" type="{LV.VI}" inputs="reference:4210.vi,error in (no error):4212.err" outputs="Execution\3AReentrancy Type:4214.rt,reference out:,error out:4214.err" uid="4214" uid_parent="root"/>
          <Node _name="Close Reference" inputs="reference:4210.vi,error in (no error):4214.err" outputs="error out:4215.err" uid="4215" uid_parent="root"/>
          <Indicator _name="type after" inputs="value:4214.rt" type="uint16{Share clones between instances,Preallocate clone for each instance}" uid="4303" uid_parent="root" value="0"/>
          <Indicator _name="error out" inputs="value:4215.err" type="cluster{bool.status,int32.code,string.source}" uid="4304" uid_parent="root" value="[false,0,]"/>
        </VI>
        """;

    [Fact]
    public async Task Execution_Reentrancy_Type_composes_for_write_and_read()
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("reentrancy.xml");
        await File.WriteAllTextAsync(xml, Probe);

        var composed = await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, server.TempPath("reentrancy.vi"), checkExecutable: false, backend: "pylabview");

        Assert.Equal(0, Res.Int(composed, "errorCode"));
    }

    [Fact]
    public void The_name_check_knows_Execution_Reentrancy_Type()
    {
        Assert.DoesNotContain(AixmlCheck.Check(Probe),
            f => f.Code is "propertyNameNotKnown" or "propertyNameUnknown");
    }
}
