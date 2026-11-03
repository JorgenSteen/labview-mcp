// ============================================================================
// lvai_exec_state reads the active project's copy and says which copy it read (session-7 report M5).
// ============================================================================
using System.Xml.Linq;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// Session-7 report M5, 2026-11-03: after typedef edits made in the project's instance, the ActiveX
/// route reported unchanged Delacor framework VIs broken (Get Errors 74/1073) until LabVIEW was
/// restarted. The helper opened its VI with no application reference - a SECOND copy in LabVIEW's
/// main instance - while the IDE held the edited one in the project's. It now opens the VI in
/// `Project:Active Project` -> `Application` when a project is active, as lvbd_coercion_dots has
/// since 2026-10-13, and answers `instance`.
/// </summary>
public sealed class ExecStateInstanceTests
{
    private const string Vi = @"C:\fixtures\caller.vi";

    private static LabViewCom.RunResult Ran(string? instance) =>
        new(1, true, true, false, 40, [],
        [
            new("exec state", "0", null),
            new("linker errors xml", "", null),
            new("code", "0", null),
            new("source", "", null),
            new("vi path", Vi, null),
            .. instance is null ? Array.Empty<LabViewCom.Reading>() : [new LabViewCom.Reading("instance", instance, null)],
        ]);

    [Theory]
    [InlineData("project", "project")]
    [InlineData("own", "own")]
    [InlineData(null, null)]        // a helper built before 2026-11-03 has no such indicator
    [InlineData("nonsense", null)]
    public void The_helper_s_instance_answer_is_carried_into_the_reading(string? answered, string? expected)
    {
        var (reading, why) = ExecStateTools.FromHelperRun(Ran(answered), Vi);

        Assert.Null(why);
        Assert.Equal(expected, reading!.Instance);
    }

    [Fact]
    public void The_helper_opens_through_the_active_project_and_falls_back_to_its_own_instance()
    {
        var helper = XElement.Load(RepoTree.Path("scripts", "lvai_exec_state.xml"));
        var opens = helper.Descendants("Node").Where(n => (string?)n.Attribute("_name") == "Open VI Reference").ToList();
        Assert.Equal(2, opens.Count);
        Assert.Single(opens, n => ((string)n.Attribute("inputs")!).StartsWith("application reference (local):"));
        Assert.Single(opens, n => !((string)n.Attribute("inputs")!).Contains("application reference"));
        Assert.Contains(helper.Descendants("Node"),
            n => ((string?)n.Attribute("fields") ?? "").Contains("Project\\3AActive Project"));
        Assert.Contains(helper.Elements("Indicator"), i => (string?)i.Attribute("_name") == "instance");
        // and the read-back list asks for it
        Assert.Contains(ExecStateTools.InstanceReadBack, ExecStateTools.ComReadBack);
    }
}
