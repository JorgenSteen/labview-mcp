// ============================================================================
// lvai_dqmh_place_handler puts a handler into the frame's module-data wire (session-7 report M1).
// ============================================================================
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// Session-7 report M1, 2026-11-03: a handler with `Module Data in` / `Module Data out` - the class
/// travels in the module data, as in every PSU/HAL frame - was refused `namesDoNotMatch` ("Handler
/// input 'Module Data in' is not an argument of the message"), and nothing wired the frame's
/// module-data pass-through. The names are now kept out of the argument check, and a handler with
/// BOTH goes in series into that wire through the user's own measured splice helper. The fixture
/// names are the report's: frame Configure Instrument Address, arguments Module ID and
/// instrumentAddress.
/// </summary>
public sealed class DqmhModuleDataTests
{
    private static readonly string[] Args = ["input cluster", "Module ID", "instrumentAddress"];

    [Fact]
    public void The_reported_handler_is_no_longer_refused_and_its_module_data_is_spliced()
    {
        string[] inputs = ["error in", "instrumentAddress", "Module Data in"];
        string[] outputs = ["error out", "Module Data out"];
        var moduleData = DqmhFrameTools.ModuleDataPlan(inputs, outputs,
            DqmhFrameTools.DefaultModuleDataIn, DqmhFrameTools.DefaultModuleDataOut);

        var plan = DqmhFrameTools.Plan("Configure Instrument Address", inputs, outputs, Args, [], moduleData.Names);

        Assert.True(moduleData.Splice);
        Assert.Empty(plan.Problems);
        Assert.Equal(["instrumentAddress"], plan.Arguments);
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("Module Data"));
        // the old call, without the names, still refuses - which is what the report saw
        var old = DqmhFrameTools.Plan("Configure Instrument Address", inputs, outputs, Args, []);
        Assert.Contains(old.Problems, p => p.Contains("'Module Data in' is not an argument"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_handler_with_only_one_module_data_terminal_splices_nothing(bool hasIn, bool hasOut)
    {
        var moduleData = new DqmhFrameTools.ModuleData("Module Data in", "Module Data out", hasIn, hasOut);

        Assert.False(moduleData.Splice);
        Assert.Contains("both", moduleData.Describe()["note"]!.GetValue<string>());
    }

    [Fact]
    public void Other_names_can_be_given()
    {
        var moduleData = DqmhFrameTools.ModuleDataPlan(["error in", "Data in"], ["error out", "Data out"],
                                                       "Data in", "Data out");
        Assert.True(moduleData.Splice);
        Assert.Equal(["Data in", "Data out"], moduleData.Names);
    }

    private static List<LvValuesXml.Value> Answered(string spliced, int sources, int sinks, int code = 0) =>
    [
        DqmhTools.ToValue("spliced", "bool", spliced == "true"),
        DqmhTools.ToValue("sources", "int32", sources),
        DqmhTools.ToValue("sinks", "int32", sinks),
        DqmhTools.ToValue("handler in found", "int32", 1),
        DqmhTools.ToValue("handler out found", "int32", 1),
        DqmhTools.ToValue("code", "int32", code),
        DqmhTools.ToValue("source", "string", ""),
    ];

    [Fact]
    public void Spliced_only_when_the_helper_says_so_without_an_error()
    {
        Assert.True(DqmhFrameTools.ModuleDataAnswer(Answered("true", 1, 1), null)["spliced"]!.GetValue<bool>());

        var two = DqmhFrameTools.ModuleDataAnswer(Answered("false", 2, 2), null);
        Assert.False(two["spliced"]!.GetValue<bool>());
        Assert.Equal(2, two["sources"]!.GetValue<int>());
        Assert.Contains("exactly one wire", two["problem"]!.GetValue<string>());

        Assert.False(DqmhFrameTools.ModuleDataAnswer(Answered("true", 1, 1, code: 1055), null)["spliced"]!.GetValue<bool>());
        Assert.False(DqmhFrameTools.ModuleDataAnswer(null, "Error 1003")["spliced"]!.GetValue<bool>());
    }

    [Fact]
    public void The_shipped_splice_helper_is_the_measured_one_in_the_safe_uid_range()
    {
        var helper = System.Xml.Linq.XElement.Load(RepoTree.Path("scripts", "lvdqmh_splice_module_data.xml"));
        var uids = helper.DescendantsAndSelf().Select(e => (string?)e.Attribute("uid")).OfType<string>()
                         .Select(int.Parse).ToList();
        Assert.True(uids.Min() >= AixmlCheck.SafeUidBase, $"lowest uid {uids.Min()}");
        Assert.Equal(uids.Count, uids.Distinct().Count());
        foreach (var control in new[] { "Main VI Path", "Frame Name", "In Name", "Out Name", "Dry Run" })
            Assert.Contains(helper.Elements("Control"), c => (string?)c.Attribute("_name") == control);
        foreach (var indicator in new[] { "spliced", "sources", "sinks", "handler in found", "handler out found", "code" })
            Assert.Contains(helper.Elements("Indicator"), i => (string?)i.Attribute("_name") == indicator);
        Assert.Empty(AixmlCheck.Check(helper.ToString()).Where(f => f.Severity == AixmlCheck.Severity.Error));
    }
}
