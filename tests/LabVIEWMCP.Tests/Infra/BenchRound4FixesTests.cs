// =============================================================================
// BenchRound4FixesTests - what CLD benchmark round 4 (2026-11-19) found in the ANSWERS:
// one mistake repeated 110 times filled a 43 KB check answer, and a slow generate call
// said nothing about which step took the time. Both are answer-shape fixes, pinned here.
// =============================================================================
using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Tools;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

public sealed class BenchRound4FixesTests
{
    private static List<AixmlCheck.Finding> Tunnels(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new AixmlCheck.Finding(
            AixmlCheck.Severity.Error, "tunnelIdNotDirection", $"Tunnel _id=\"T{i}\": the rule, at length.", $"t{i}"))];

    [Fact]
    public void A_code_found_many_times_is_written_three_times_and_folded_once()
    {
        var summary = AixmlCheck.Summarise(Tunnels(110));

        Assert.Equal(110, summary["errors"]!.GetValue<int>());
        var findings = summary["findings"]!.AsArray();
        Assert.Equal(AixmlCheck.FullFindingsPerCode + 1, findings.Count);
        var folded = findings[^1]!.AsObject();
        Assert.Equal("tunnelIdNotDirection", folded["code"]!.GetValue<string>());
        Assert.Equal(107, folded["repeated"]!.GetValue<int>());
        var uids = folded["uids"]!.AsArray().Select(u => u!.GetValue<string>()).ToList();
        Assert.Equal(107, uids.Count);
        Assert.Equal("t4", uids[0]);
        Assert.Equal("t110", uids[^1]);
    }

    [Fact]
    public void The_folded_answer_of_110_findings_stays_small()
    {
        // 110 whole findings of the real rule's length were 43 KB in the benchmark run
        Assert.True(AixmlCheck.Summarise(Tunnels(110)).ToJsonString().Length < 4000);
    }

    [Fact]
    public void Up_to_three_findings_of_a_code_are_left_as_they_were()
    {
        var findings = AixmlCheck.Summarise(Tunnels(3))["findings"]!.AsArray();

        Assert.Equal(3, findings.Count);
        Assert.All(findings, f => Assert.Null(f!["repeated"]));
    }

    [Fact]
    public void Folding_is_per_code_and_per_severity_and_keeps_the_order()
    {
        var mixed = Tunnels(5);
        mixed.Insert(1, new AixmlCheck.Finding(AixmlCheck.Severity.Warning, "other", "one of a kind", "x"));
        mixed.Add(new AixmlCheck.Finding(AixmlCheck.Severity.Warning, "tunnelIdNotDirection", "as a warning", "w"));

        var codes = AixmlCheck.Collapsed(mixed)
            .Select(f => $"{f!["severity"]}:{f["code"]}:{f["repeated"]?.ToString() ?? "-"}").ToList();

        Assert.Equal(
        [
            "error:tunnelIdNotDirection:-", "warning:other:-", "error:tunnelIdNotDirection:-",
            "error:tunnelIdNotDirection:-", "error:tunnelIdNotDirection:2", "warning:tunnelIdNotDirection:-",
        ], codes);
    }

    [Fact]
    public void Step_costs_are_the_differences_of_the_stamps_and_a_repeated_name_is_summed()
    {
        var steps = new JsonArray
        {
            new JsonObject { ["step"] = "check", ["atMs"] = 40 },
            new JsonObject { ["step"] = "convert", ["atMs"] = 2040 },
            new JsonObject { ["step"] = "unstamped" },
            new JsonObject { ["step"] = "cleanUpDiagram", ["atMs"] = 30040 },
            new JsonObject { ["step"] = "convert", ["atMs"] = 30540 },
        };

        var cost = BulkTools.StepMs(steps)!;

        Assert.Equal(40, cost["check"]!.GetValue<long>());
        Assert.Equal(2500, cost["convert"]!.GetValue<long>());
        Assert.Equal(28000, cost["cleanUpDiagram"]!.GetValue<long>());
        Assert.Null(cost["unstamped"]);
    }

    [Fact]
    public void Steps_without_a_stamp_give_no_cost_object()
    {
        Assert.Null(BulkTools.StepMs([new JsonObject { ["step"] = "convert" }]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_compact_generate_answer_carries_the_step_costs_with_or_without_its_steps(bool keepSteps)
    {
        var full = new JsonObject
        {
            ["ok"] = true,
            ["steps"] = new JsonArray
            {
                new JsonObject { ["step"] = "convert", ["errorCode"] = 0, ["atMs"] = 1500, ["answer"] = new JsonObject { ["errorCode"] = 0 } },
                new JsonObject { ["step"] = "cleanUpDiagram", ["atMs"] = 9500 },
            },
        };

        var compact = BulkTools.CompactGenerate(full, keepSteps);

        Assert.Equal(1500, compact["stepMs"]!["convert"]!.GetValue<long>());
        Assert.Equal(8000, compact["stepMs"]!["cleanUpDiagram"]!.GetValue<long>());
        Assert.Equal(keepSteps, compact["steps"] is not null);
    }
}
