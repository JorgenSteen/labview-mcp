// =============================================================================
// FrameCaptions.cs - which rendered diagram PNG shows which structure frame:
// scripts/aixml-pylabview/frames.py lists a VI's hidden frames in the order
// LabVIEW's Print VI To HTML writes <base>d1..dN.png, and this pairs them up.
// =============================================================================
using System.Text.Json.Nodes;

namespace LabVIEWMcp.Infra;

/// <summary>
/// WHY. A render answers `<base>d.png` plus one PNG per hidden frame, named only by a number, so
/// on a large VI the only check that sees a clipped comment or an undrawn wire produced thousands
/// of pictures nobody could map to a frame - a field project's main VI gave about 2 700 in three
/// minutes (2026-11). The order LabVIEW writes them in is measured (frames.py's header); the
/// captions come from the file, and a count that does not match the PNGs is reported, never
/// paired up by guess.
/// </summary>
internal static class FrameCaptions
{
    internal const string ScriptFileName = "frames.py";

    /// <summary>The hidden frames of <paramref name="viPath"/> in print order, or a problem text.</summary>
    internal static async Task<(JsonArray? Frames, string? NamedBy, string? Problem)> ReadAsync(
        string viPath, int timeoutSeconds, CancellationToken ct)
    {
        var setup = AixmlComposer.Locate(out var missing);
        if (setup is null) return (null, null, "no captions: the pylabview bundle is not provisioned (" + missing + ")");
        var script = Path.Combine(setup.ComposerDirectory, ScriptFileName);
        if (!File.Exists(script)) return (null, null, $"no captions: {ScriptFileName} is missing beside the composer");
        try
        {
            var run = await PyLabview.RunAsync(setup.Bundle, script, [Path.GetFullPath(viPath)], timeoutSeconds, ct);
            if (run.ExitCode != 0 || JsonNode.Parse(run.StdOut) is not JsonObject answer)
                return (null, null, "no captions: the frame reader failed - " +
                                    (run.StdErr.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "no output"));
            return (answer["frames"] as JsonArray, answer["namedBy"]?.GetValue<string>(),
                    answer["readerError"]?.GetValue<string>() is { } e ? "frames named by index: the AIXML reader refused the VI (" + e + ")" : null);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return (null, null, "no captions: " + e.Message);
        }
    }

    /// <summary>
    /// The captions added to one render entry: `frames` pairs every PNG with its caption (the first
    /// is the top-level diagram), and with <paramref name="filter"/> (substrings, `|`-separated, any
    /// case) `diagrams` keeps the top-level diagram and the matching frames only.
    /// </summary>
    internal static void Apply(JsonObject entry, IReadOnlyList<string> diagrams, JsonArray? frames, string? problem,
                               string? filter)
    {
        if (diagrams.Count == 0) return;
        if (frames is null)
        {
            entry["captions"] = problem ?? "no captions";
            return;
        }
        if (frames.Count != diagrams.Count - 1)
        {
            entry["captions"] = $"not paired: {diagrams.Count - 1} frame PNG(s) and {frames.Count} hidden frame(s) " +
                                "in the file - LabVIEW's print order may differ for a structure kind not measured yet.";
            return;
        }
        var wanted = (filter ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var paired = new JsonArray { new JsonObject { ["image"] = diagrams[0], ["caption"] = "top-level diagram (each structure's visible frame)" } };
        var kept = new List<string> { diagrams[0] };
        for (var i = 0; i < frames.Count; i++)
        {
            var caption = frames[i]?["caption"]?.GetValue<string>() ?? "";
            paired.Add(new JsonObject { ["image"] = diagrams[i + 1], ["caption"] = caption });
            if (wanted.Length == 0 || wanted.Any(w => caption.Contains(w, StringComparison.OrdinalIgnoreCase)))
                kept.Add(diagrams[i + 1]);
        }
        if (problem is not null) entry["captions"] = problem;
        if (wanted.Length > 0)
        {
            entry["diagrams"] = new JsonArray([.. kept.Select(d => (JsonNode)d)]);
            entry["framesMatchingFilter"] = kept.Count - 1;
            paired = new JsonArray([.. paired.Where(p => kept.Contains(p!["image"]!.GetValue<string>())).Select(p => p!.DeepClone())]);
        }
        entry["frames"] = paired;
    }
}
