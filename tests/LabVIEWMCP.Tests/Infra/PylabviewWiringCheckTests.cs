using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

/// <summary>
/// The pylabview backend has no NI validation, so a wiring fault NI's ValidateAIXML refuses - an
/// unwired REQUIRED subVI input, a string into a numeric indicator or subVI input - used to compose,
/// write a VI and surface only as execState 0 (2026-10-29, docs/non-grpc-gaps.md 4b). The composer
/// now refuses those by name. Only wires whose SOURCE type is exact are judged: a primitive's output
/// is typed from its donor part, and judging it refused six working documents (an Equal? comparing
/// aggregates feeds a boolean indicator while its donor gave an array of booleans), so the
/// aggregate case is pinned here as the control. SKIPS where the bundled interpreter is missing, as
/// the other composer tests do.
/// </summary>
public sealed class PylabviewWiringCheckTests
{
    private const string Callee = """
        <VI _name="Wiring Callee.vi" description="A callee with one required input.">
          <Control _name="n" conIdx="0" connection="required" outputs="value:4200.value" type="double" uid="4200" uid_parent="root" value="0"/>
          <Control _name="opt" conIdx="1" connection="recommended" outputs="value:4201.value" type="double" uid="4201" uid_parent="root" value="0"/>
          <Node _name="Add" inputs="x:4200.value,y:4201.value" outputs="x+y:4220.x+y" uid="4220" uid_parent="root"/>
          <Indicator _name="r" conIdx="3" connection="recommended" inputs="value:4220.x+y" type="double" uid="4225" uid_parent="root" value="0"/>
        </VI>
        """;

    private static string Caller(string type, string value, string inputs) => $"""
        <VI _name="Wiring Caller.vi" description="Calls the callee.">
          <Constant _name="k" outputs="value:4200.value" type="{type}" uid="4200" uid_parent="root" value="{value}"/>
          <Call _name="Wiring Callee.vi" target="Wiring Callee.vi" inputs="{inputs}" outputs="r:4220.r" uid="4220" uid_parent="root"/>
          <Indicator _name="out" conIdx="3" connection="recommended" inputs="value:4220.r" type="double" uid="4225" uid_parent="root" value="0"/>
        </VI>
        """;

    private static string Indicator(string sourceType, string sourceValue) => $"""
        <VI _name="Wiring Indicator.vi" description="A control straight into an indicator.">
          <Control _name="a" conIdx="0" connection="recommended" outputs="value:4200.value" type="{sourceType}" uid="4200" uid_parent="root" value="{sourceValue}"/>
          <Indicator _name="sum" conIdx="3" connection="recommended" inputs="value:4200.value" type="double" uid="4225" uid_parent="root" value="0"/>
        </VI>
        """;

    private const string AggregateCompare = """
        <VI _name="Wiring Aggregate.vi" description="Equal? on two arrays in Compare Aggregates mode.">
          <Control _name="x" conIdx="0" connection="recommended" outputs="value:4200.value" type="array{double}" uid="4200" uid_parent="root" value="[]"/>
          <Control _name="y" conIdx="1" connection="recommended" outputs="value:4205.value" type="array{double}" uid="4205" uid_parent="root" value="[]"/>
          <Node _name="Equal?" aggregate="true" inputs="x:4200.value,y:4205.value" outputs="x = y?:4300.x = y?" uid="4300" uid_parent="root"/>
          <Indicator _name="same" conIdx="3" connection="recommended" inputs="value:4300.x = y?" type="bool" uid="4400" uid_parent="root" value="false"/>
        </VI>
        """;

    public static TheoryData<string, string, string?> Cases => new()
    {
        // document, file name, the refusal it must carry (null: it must compose)
        { Caller("double", "1", "n:4200.value"), "Wiring Caller.vi", null },
        { Caller("int32", "3", "n:4200.value"), "Wiring Caller.vi", null },   // a coercion dot, not a fault
        { Caller("double", "1", "opt:4200.value"), "Wiring Caller.vi", "required input 'n' is not wired" },
        { Caller("string", "x", "n:4200.value"), "Wiring Caller.vi", "input 'n': a string wire" },
        { Indicator("double", "1"), "Wiring Indicator.vi", null },
        { Indicator("string", ""), "Wiring Indicator.vi", "Indicator 'sum': a string wire" },
        { Indicator("bool", "false"), "Wiring Indicator.vi", "Indicator 'sum': a boolean wire" },
        { AggregateCompare, "Wiring Aggregate.vi", null },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_wire_LabVIEW_would_break_is_refused_by_name_and_a_good_one_composes(
        string document, string fileName, string? refusal)
    {
        var setup = AixmlComposer.Locate(out _);
        if (setup is null) return;
        var dir = Path.Combine(Path.GetTempPath(), "wirecheck-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var calleeXml = Path.Combine(dir, "callee.xml");
            await File.WriteAllTextAsync(calleeXml, Callee);
            var callee = await AixmlComposer.ComposeAsync(setup, calleeXml, Path.Combine(dir, "Wiring Callee.vi"), [], 300, default);
            Assert.True(callee.Written, callee.Message);

            var xml = Path.Combine(dir, "probe.xml");
            var vi = Path.Combine(dir, fileName);
            await File.WriteAllTextAsync(xml, document);
            var composed = await AixmlComposer.ComposeAsync(setup, xml, vi, [], 300, default);

            if (refusal is null)
            {
                Assert.True(composed.Written, composed.Message);
            }
            else
            {
                Assert.True(composed.Refused, "composed although LabVIEW would break it: " + composed.Message);
                Assert.Contains("wiring:", composed.Message);
                Assert.Contains(refusal, composed.Message);
                Assert.False(File.Exists(vi));
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
