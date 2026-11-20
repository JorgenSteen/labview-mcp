// =============================================================================
// HelperPrewarm - composes the pylabview backend's helper VIs in the background after a
// server start, so the first tool call after a new build does not pay for composing them.
// Measured 2026-11-19: with an empty helper cache the first lvai_generate_vis spent 31.7 s
// of 59.7 s in the first VI's cleanUpDiagram; with the helpers in place the same call took 14.4 s.
// =============================================================================
using Microsoft.Extensions.Logging;
using LabVIEWMcp.Tools;

namespace LabVIEWMcp.Infra;

/// <summary>
/// Composes helper VIs ahead of their first use. Needs NO LabVIEW: on the pylabview backend a
/// helper is composed by our own composer (<see cref="ComposedHelpers"/>), and a composed helper
/// is stale after every new build because its AIXML is newer - which is why the first build of a
/// benchmark round answered a 95 s generate call.
///
/// WHAT IT COMPOSES: the helpers every generate needs (<see cref="Core"/>), and every helper this
/// station has composed before - a `.vi` in the helper folder whose AIXML still ships. So the
/// list follows what the station really uses and nothing is composed that never ran here.
/// Lane copies (`name N.vi`, lvai_layout_diagrams' workers) are left to their first use.
///
/// WHAT IT COSTS: one compose at a time out of the worker pool's four slots, after
/// <see cref="StartDelay"/>. A tool call that needs a helper while it is being composed waits for
/// that one compose (<see cref="ComposedHelpers"/> takes a lock per helper) instead of composing
/// it a second time. A helper composed here for the LabVIEW release in use now is rebuilt on
/// first use when another release is in use then - the stamp decides, as before.
///
/// pylabview backend only; <see cref="Variable"/>=0 switches it off.
/// </summary>
internal static class HelperPrewarm
{
    public const string Variable = "LABVIEWMCP_HELPER_PREWARM";

    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(3);

    private const int ComposeTimeoutSeconds = 120;

    /// <summary>What every lvai_generate_vi runs: the layout engine's two helpers.</summary>
    internal static readonly string[] Core =
        [DiagramLayoutTools.DumpHelper, DiagramLayoutTools.ApplyHelper];

    /// <summary>Where the run stands, for pylv_status. Null until a run has begun.</summary>
    internal sealed record Progress(string State, int Targets, int Composed, int AlreadyCurrent, int Failed);

    private static volatile Progress? _progress;

    public static Progress? Current => _progress;

    /// <summary>
    /// The helper AIXML files to compose: <see cref="Core"/> and every shipped helper with a
    /// composed VI of its own name in <paramref name="helperFolder"/>, as file names, core first.
    /// </summary>
    internal static List<string> Targets(string scriptsDirectory, string helperFolder)
    {
        var targets = new List<string>();
        void Add(string fileName)
        {
            if (File.Exists(Path.Combine(scriptsDirectory, fileName))
                && !targets.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                targets.Add(fileName);
        }
        foreach (var core in Core) Add(core);
        if (Directory.Exists(helperFolder))
            foreach (var vi in Directory.EnumerateFiles(helperFolder, "*.vi").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                Add(Path.ChangeExtension(Path.GetFileName(vi), ".xml"));
        return targets;
    }

    public static async Task RunAsync(ILogger log, CancellationToken ct = default)
    {
        try
        {
            if (Environment.GetEnvironmentVariable(Variable) == "0") return;
            await Task.Delay(StartDelay, ct);
            if (AixmlBackend.ServerDefault().Name != AixmlBackend.PyLabview) return;
            if (StatusTools.ScriptsDirectory() is not { } scripts) return;

            var targets = Targets(scripts, ComposedHelpers.Folder);
            int composed = 0, current = 0, failed = 0;
            _progress = new Progress("running", targets.Count, 0, 0, 0);
            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                var (vi, problem, made) = await ComposedHelpers.EnsureReportingAsync(target, ComposeTimeoutSeconds, ct);
                if (vi is null)
                {
                    failed++;
                    log.LogInformation("helper pre-warm: {Helper} not composed: {Problem}", target, problem);
                }
                else if (made) composed++;
                else current++;
                _progress = new Progress("running", targets.Count, composed, current, failed);
            }
            _progress = new Progress("done", targets.Count, composed, current, failed);
            log.LogInformation("helper pre-warm: {Targets} helper(s), {Composed} composed, {Current} already current, {Failed} failed",
                               targets.Count, composed, current, failed);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            // a warm-up must never take the server down: the helpers are composed on first use
            _progress = new Progress("failed: " + e.Message, 0, 0, 0, 0);
            log.LogInformation("helper pre-warm stopped: {Message}", e.Message);
        }
    }
}
