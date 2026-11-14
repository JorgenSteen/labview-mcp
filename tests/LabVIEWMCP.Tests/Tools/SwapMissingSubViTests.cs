// =============================================================================
// SwapMissingSubViTests.cs - lvai_swap_subvis on a subVI node whose FILE IS MISSING (field
// report N17): what a 1055 means, where the stand-in goes, and how sockets left are counted.
// =============================================================================
using LabVIEWMcp.Tests.Support;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

/// <summary>
/// Reproduced 2026-11-14 on LabVIEW 2026: with Caller.vi's Sub.vi moved away, the helper read the
/// node's name as '' and answered 1055 from To More Specific Class, and the tool said
/// noActiveProject although the project was active. A copy of the target at the linked path made
/// the swap land. The export lines below are the reader's, from that run.
/// </summary>
public class SwapMissingSubViTests
{
    private const string MissingExport = """
        <VI _name="Caller.vi" description="Fixture caller: calls Sub.vi once.">
          <Call uid="4202" uid_parent="root" target="Sub.vi" inputs="error in:4200.value,text:4201.value" outputs="error out:4202.error out,length:4202.length" />
        </VI>
        <?lvmcp calllink uid="4202" tp="1" path="|sub|Sub.vi"?>
        """;

    [Fact]
    public void A_nameless_node_is_a_missing_subVI_whatever_the_project()
    {
        Assert.Equal("missingSubVi", SwapTools.Kind1055(true, [""]));
        Assert.Equal("missingSubVi", SwapTools.Kind1055(null, ["Other.vi", ""]));
    }

    [Fact]
    public void Only_an_inactive_or_unknown_project_is_noActiveProject()
    {
        Assert.Equal("invalidReference", SwapTools.Kind1055(true, ["Sub.vi"]));
        Assert.Equal(SwapTools.NoActiveProject, SwapTools.Kind1055(false, ["Sub.vi"]));
        Assert.Equal(SwapTools.NoActiveProject, SwapTools.Kind1055(null, []));
    }

    [Fact]
    public void A_socket_that_is_a_substring_of_the_target_is_not_left()
    {
        Assert.Equal(0, SwapTools.SocketsLeft(["NewSub.vi"], ["Sub.vi"]));
        Assert.Equal(1, SwapTools.SocketsLeft(["Sub.vi", "NewSub.vi"], ["Sub.vi"]));
        Assert.Equal(1, SwapTools.SocketsLeft(["Pump.lvclass:LVMCP Acc W1.vi"], ["LVMCP Acc W1.vi"]));
    }

    [Fact]
    public void The_stand_in_goes_to_the_relative_path_the_caller_links()
    {
        using var tree = new TempTree("swap-standin");
        var target = tree.Write(@"new\NewSub.vi", "x");

        var plan = SwapTools.StandInPlan(MissingExport, tree.PathOf("Caller.vi"),
            [new SwapTools.NodeSwap("Sub.vi", target)]);

        var item = Assert.Single(plan);
        Assert.Null(item.Problem);
        Assert.Equal(tree.PathOf(@"sub\Sub.vi"), item.LinkedPath);
    }

    [Fact]
    public void A_file_already_at_the_linked_path_is_not_overwritten()
    {
        using var tree = new TempTree("swap-standin");
        var target = tree.Write(@"new\NewSub.vi", "x");
        tree.Write(@"sub\Sub.vi", "the real one");

        var item = Assert.Single(SwapTools.StandInPlan(MissingExport, tree.PathOf("Caller.vi"),
            [new SwapTools.NodeSwap("Sub.vi", target)]));

        Assert.Contains("already at the linked path", item.Problem);
    }

    [Theory]
    [InlineData("<?lvmcp calllink uid=\"4202\" tp=\"0\" path=\"C|x|Sub.vi\"?>", "not a plain relative path")]
    [InlineData("<?lvmcp calllink uid=\"4202\" tp=\"1\" path=\"||x|Sub.vi\"?>", "not a plain relative path")]
    [InlineData("<?lvmcp calllink uid=\"9\" tp=\"1\" path=\"|sub|Sub.vi\"?>", "no Call of that name")]
    public void An_unusable_link_is_a_reason_not_a_guess(string link, string reason)
    {
        using var tree = new TempTree("swap-standin");
        var target = tree.Write(@"new\NewSub.vi", "x");
        var xml = MissingExport.Replace("<?lvmcp calllink uid=\"4202\" tp=\"1\" path=\"|sub|Sub.vi\"?>", link);

        var item = Assert.Single(SwapTools.StandInPlan(xml, tree.PathOf("Caller.vi"),
            [new SwapTools.NodeSwap("Sub.vi", target)]));

        Assert.Contains(reason, item.Problem);
    }
}
