// =============================================================================
// PylabviewBarePropertyNameTests.cs - a Property Node field with no read+/write+ prefix
// composes as a read on the pylabview backend, as the AIXML reference says it reads.
// =============================================================================
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Measured in the first CLD benchmark round (2026-11-17): `fields="Disabled"` was refused as
/// "LV.Boolean declares no property ''" because every composer site split the field on "+".
/// The reference has always said a field without `write+` reads. Runs the bundled interpreter and
/// SKIPS without it, as the other composer tests do.
/// </summary>
public sealed class PylabviewBarePropertyNameTests
{
    private static bool ComposerHere() => AixmlComposer.Locate(out _) is not null;

    private static string Probe(string field) => $"""
        <VI _name="BareProp.vi" description="Reads a boolean's Disabled property through a bound reference.">
          <Control _name="Go" outputs="value:" type="bool" uid="4200" uid_parent="root" value="false"/>
          <Node _name="VI Server Reference" element="Go" outputs="Go:4201.Go" uid="4201" uid_parent="root"/>
          <Node _name="Property Node" fields="{field}" type="{"{"}LV.Boolean{"}"}"
                inputs="reference:4201.Go,error in (no error):" outputs="Disabled:,reference out:,error out:" uid="4202" uid_parent="root"/>
        </VI>
        """;

    [Theory]
    [InlineData("Disabled")]
    [InlineData("read+Disabled")]
    public async Task A_bare_property_name_composes_as_a_read(string field)
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();
        var xml = server.TempPath("bareprop.xml");
        await File.WriteAllTextAsync(xml, Probe(field));

        var composed = await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            xml, server.TempPath("bareprop.vi"), checkExecutable: false, backend: "pylabview");

        Assert.True(Res.Int(composed, "errorCode") == 0, Res.Str(composed, "errorMessage"));
    }
}
