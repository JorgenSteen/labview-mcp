// ============================================================================
// The composer refuses a shared uid something is nested inside, and names the property meant.
// ============================================================================
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Fakes;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// Session-7 report M9, 2026-11-03. A new loop reused a later Case Structure's uids: lvai_check_aixml
/// only warned `duplicateUid`, lvai_generate_vi went ahead, and the composer resolved uid_parent
/// through a map that keeps the LAST twin - so the loop's children went into the case structure and
/// the VI failed elsewhere. Main's composer wrote the fixture below without a word. And a refused
/// property named no near miss, where the check had already offered one. These run the bundled
/// interpreter and SKIP without it.
/// </summary>
public sealed class PylabviewDuplicateUidTests
{
    private static bool ComposerHere() => AixmlComposer.Locate(out _) is not null;

    private const string NestedTwins = """
        <VI _name="nested.vi" description="A For Loop and a node share a uid, and a node is nested inside it.">
          <Constant _name="n" type="int32" uid="4200" uid_parent="root" value="3" outputs="value:4200.value"/>
          <Structure _name="For Loop" count="4200.value" uid="4210" uid_parent="root">
            <Node _name="Increment" inputs="x:" outputs="x+1:" uid="4220" uid_parent="4210"/>
          </Structure>
          <Node _name="Decrement" inputs="x:" outputs="x-1:" uid="4210" uid_parent="root"/>
        </VI>
        """;

    private const string LeafTwins = """
        <VI _name="leaf.vi" description="Two leaf elements share a uid; nothing is nested in it.">
          <Control _name="v" type="double" uid="4200" uid_parent="root" value="0" outputs="value:4200.value"/>
          <Indicator _name="r" type="double" uid="4200" uid_parent="root" value="0" inputs="value:4200.value"/>
        </VI>
        """;

    private const string ClassNameSlip = """
        <VI _name="classname.vi" description="A property name with its space dropped.">
          <Node _name="Property Node" fields="read+ClassName" type="{LV.VI}" inputs="reference:,error in (no error):" outputs="ClassName:4210.c,reference out:,error out:" uid="4210" uid_parent="root"/>
          <Indicator _name="c" type="string" uid="4211" uid_parent="root" value="" inputs="value:4210.c"/>
        </VI>
        """;

    private static async Task<string> ComposeAsync(LvaiTestServer server, string xml, string name)
    {
        var path = server.TempPath(name + ".xml");
        await File.WriteAllTextAsync(path, xml);
        return await new AixmlTools(server.Connection).ConvertAixmlToViAsync(
            path, server.TempPath(name + ".vi"), checkExecutable: false, backend: "pylabview");
    }

    [Fact]
    public async Task A_shared_uid_something_is_nested_inside_is_refused_by_the_composer()
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();

        var composed = await ComposeAsync(server, NestedTwins, "nested");

        Assert.NotEqual(0, Res.Int(composed, "errorCode"));
        Assert.Contains("uid 4210 is used by more than one element", Res.Str(composed, "errorMessage"));
    }

    [Fact]
    public async Task A_shared_uid_nothing_is_nested_inside_still_composes()
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();

        Assert.Equal(0, Res.Int(await ComposeAsync(server, LeafTwins, "leaf"), "errorCode"));
    }

    [Fact]
    public async Task A_refused_property_name_comes_with_the_name_meant()
    {
        if (!ComposerHere()) return;
        await using var server = await LvaiTestServer.StartAsync();

        var composed = await ComposeAsync(server, ClassNameSlip, "classname");

        Assert.NotEqual(0, Res.Int(composed, "errorCode"));
        Assert.Contains("did you mean 'Class Name'?", Res.Str(composed, "errorMessage"));
    }

    [Fact]
    public void The_check_calls_the_nested_twin_an_error_and_the_leaf_twin_a_warning()
    {
        var nested = Assert.Single(AixmlCheck.Check(NestedTwins), f => f.Code.StartsWith("duplicate"));
        Assert.Equal(AixmlCheck.DuplicateParentUid, nested.Code);
        Assert.Equal(AixmlCheck.Severity.Error, nested.Severity);

        var leaf = Assert.Single(AixmlCheck.Check(LeafTwins), f => f.Code.StartsWith("duplicate"));
        Assert.Equal("duplicateUid", leaf.Code);
        Assert.Equal(AixmlCheck.Severity.Warning, leaf.Severity);
    }
}
