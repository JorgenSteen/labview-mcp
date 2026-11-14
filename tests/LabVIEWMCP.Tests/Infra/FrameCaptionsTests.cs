// =============================================================================
// FrameCaptionsTests.cs - frames.py's print order on the VI whose render was
// measured, and how captions are paired with PNGs and filtered.
// =============================================================================
using System.Text.Json.Nodes;
using LabVIEWMcp.Infra;
using Xunit;

namespace LabVIEWMcp.Tests.Infra;

public class FrameCaptionsTests
{
    /// <summary>
    /// Fixtures/frames/Frame Order 2.vi rendered on LabVIEW 2026 (2026-11-14) as d1 = "S2 frame q"
    /// (a case nested in S1's VISIBLE frame A), d2 = "S1 frame B", d3 = "S3 frame n" - each frame's
    /// subdiagram label names it, so the PNGs were read, not inferred. frames.py must list the same
    /// order from the file. Skips where the pylabview bundle is missing.
    /// </summary>
    [Fact]
    public async Task The_frames_come_in_the_measured_print_order()
    {
        if (AixmlComposer.Locate(out _) is null) return;
        var vi = Path.Combine(LabVIEWMcp.Tests.Support.RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "frames", "Frame Order 2.vi");

        var (frames, namedBy, problem) = await FrameCaptions.ReadAsync(vi, 120, default);

        Assert.Null(problem);
        Assert.Equal("reader", namedBy);
        var captions = frames!.Select(f => f!["caption"]!.GetValue<string>()).ToArray();
        Assert.Equal(3, captions.Length);
        Assert.EndsWith("(S2 frame q)", captions[0]);
        Assert.StartsWith("Case Structure \"A\" (S1 frame A) > ", captions[0]);
        Assert.EndsWith("(S1 frame B)", captions[1]);
        Assert.EndsWith("(S3 frame n)", captions[2]);
    }

    /// <summary>Fixtures/frames/Frame Order 3.vi: a stacked sequence, rendered d1 = frame one, d2 = the
    /// Default of the case inside frame one, d3 = frame two (2026-11-14).</summary>
    [Fact]
    public async Task A_stacked_sequence_comes_in_the_measured_print_order()
    {
        if (AixmlComposer.Locate(out _) is null) return;
        var vi = Path.Combine(LabVIEWMcp.Tests.Support.RepoTree.Root, "tests", "LabVIEWMCP.Tests", "Fixtures", "frames", "Frame Order 3.vi");

        var (frames, _, problem) = await FrameCaptions.ReadAsync(vi, 120, default);

        Assert.Null(problem);
        Assert.Equal(["Stacked Sequence SEQ frame one",
                      "Stacked Sequence SEQ frame one > Case Structure Default (CASE frame default)",
                      "Stacked Sequence SEQ frame two"],
                     frames!.Select(f => f!["caption"]!.GetValue<string>()));
    }

    private static JsonArray Frames(params string[] captions) =>
        new([.. captions.Select(c => (JsonNode)new JsonObject { ["caption"] = c })]);

    [Fact]
    public void Each_png_gets_its_frame_and_the_filter_keeps_the_top_level_and_the_matches()
    {
        string[] pngs = ["xd.png", "xd1.png", "xd2.png", "xd3.png"];
        var entry = new JsonObject();

        FrameCaptions.Apply(entry, pngs, Frames("Case Structure \"Init\"", "Case Structure \"Run\"", "Case Structure \"Stop\""),
                            null, "init|STOP");

        Assert.Equal(["xd.png", "xd1.png", "xd3.png"], entry["diagrams"]!.AsArray().Select(d => d!.GetValue<string>()));
        Assert.Equal(2, entry["framesMatchingFilter"]!.GetValue<int>());
        Assert.Equal("Case Structure \"Stop\"", entry["frames"]![2]!["caption"]!.GetValue<string>());
    }

    /// <summary>A count that does not match the PNGs is said, never paired up by guess.</summary>
    [Fact]
    public void A_frame_count_that_does_not_match_the_pngs_is_not_paired()
    {
        var entry = new JsonObject();

        FrameCaptions.Apply(entry, ["xd.png", "xd1.png"], Frames("a", "b"), null, null);

        Assert.Null(entry["frames"]);
        Assert.Contains("not paired", entry["captions"]!.GetValue<string>());
    }
}
