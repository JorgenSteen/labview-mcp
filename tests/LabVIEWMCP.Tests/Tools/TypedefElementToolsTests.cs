// =============================================================================
// TypedefElementToolsTests.cs - lvai_add_typedef_elements reads the cluster's element labels from
// lvai_describe_ctl's answer: the top cluster's members, or null when the top is no cluster.
// =============================================================================
using System.Text.Json.Nodes;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Tools;

public class TypedefElementToolsTests
{
    // The shape lvai_describe_ctl answered for a DQMH Module Data--cluster.ctl, 2026-11-14.
    private static JsonObject Described(string fields) =>
        (JsonObject)JsonNode.Parse($$"""{"isTypedef": true, "fields": {{fields}}}""")!;

    [Fact]
    public void The_top_cluster_members_are_the_elements()
    {
        var labels = TypedefElementTools.TopClusterLabels(Described(
            """[{"label":"Module Data","type":"Cluster","members":[{"label":"Status","type":"String"},{"label":"Flow Rate","type":"NumFloat64"}]}]"""));

        Assert.Equal(["Status", "Flow Rate"], labels);
    }

    [Fact]
    public void A_control_whose_top_is_no_cluster_has_no_elements()
    {
        Assert.Null(TypedefElementTools.TopClusterLabels(Described("""[{"label":"Gain","type":"NumFloat64"}]""")));
        Assert.Null(TypedefElementTools.TopClusterLabels(Described("[]")));
    }
}
