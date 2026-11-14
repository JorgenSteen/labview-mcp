// ============================================================================================
// TypedefElementTools.cs - lvai_add_typedef_elements: append elements to the cluster of a cluster
// typedef .ctl that already exists (a DQMH Module Data--cluster.ctl, any cluster typedef).
// ============================================================================================
using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// ELEMENTS FOR A CLUSTER TYPEDEF (field report D6: no tool added an element to a DQMH module's
/// Module Data cluster). NI's own route for a class's private data - Message Maker's
/// <c>Add Member Data to Private Data Control.vi</c> - opens its target with LVClass.Open and
/// refuses a .ctl (Error 74, measured 2026-11-14). Its chain after that step is generic, and
/// <c>scripts/lvai_add_cluster_elements.xml</c> runs it on the .ctl opened with Open VI
/// Reference: Move (duplicate) each carrier control into the first cluster, CtlVIApplyChanges,
/// Save.Instrument in place. Every caller of the typedef follows the saved file.
/// </summary>
[McpServerToolType]
internal sealed class TypedefElementTools(LvaiConnection connection)
{
    internal const string Helper = "lvai_add_cluster_elements";

    [McpServerTool(Name = "lvai_add_typedef_elements", Destructive = true, OpenWorld = true,
        Title = "Append elements to a cluster typedef")]
    [Description("""
        Typedef: APPENDS elements to the cluster of a cluster typedef .ctl that already exists - a
        DQMH module's `Module Data--cluster.ctl`, any cluster typedef - and saves it; every VI using
        the typedef follows. ADDS ONLY; a name the cluster already has is refused before LabVIEW.
        NEEDS A PROJECT OPEN AND ACTIVE (pass projectPath and this call opens it). Verified from the
        saved file: the old elements first and unchanged, the new ones after them. The callers
        follow IN MEMORY and are left modified; with projectPath, the VIs in the project folder this
        call left unsaved are saved (`callersSaved`) - DQMH tools refuse a project with unsaved VIs.
        A class private data control is lvai_add_class_field's job.
        """)]
    public async Task<string> AddTypedefElementsAsync(
        [Description("Absolute path to the cluster typedef .ctl")] string ctlPath,
        [Description("""
            Elements as `<type>.<name>`, comma separated, e.g. `double.Flow Rate,int32.Count` - the
            lvai_add_class_field format; no cluster, array or enum.
            """)]
        string fields,
        [Description("The .lvproj to open (and make active) first; it is not edited")]
        string? projectPath = null,
        [Description("Local budget in seconds, per step")] int timeoutSeconds = 300,
        [Description(AixmlBackend.RouteParameterDescription)] string? backend = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(() => BackendRoute.WithBackendAsync(backend, "lvai_add_typedef_elements", async () =>
        {
            if (string.IsNullOrWhiteSpace(ctlPath) || !File.Exists(ctlPath))
                return Json.Error("ctlMissing", $"No file at '{ctlPath}'.", new { ctlPath });
            var ctl = Path.GetFullPath(ctlPath);
            if (!ctl.EndsWith(".ctl", StringComparison.OrdinalIgnoreCase))
                return Json.Error("notACtl", $"'{ctl}' is not a .ctl.", new { ctlPath = ctl });

            List<LvClass.Field> parsed;
            try { parsed = LvClass.ParseFields(fields); }
            catch (ArgumentException e) { return Json.Error("badArguments", e.Message, new { fields }); }
            if (parsed.Count == 0)
                return Json.Error("badArguments", "`fields` names no element, so this call asks for nothing.");
            if (parsed.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1)
                is { } twice)
                return Json.Error("badArguments", $"`fields` names '{twice.Key}' more than once.");

            // ASK THE FILE: a cluster at the top, and which names it already has.
            var (before, readFailure) = await ElementsAsync(ctl, timeoutSeconds, ct);
            if (readFailure is not null) return readFailure;
            if (before is null)
                return Json.Error("notAClusterTypedef",
                    $"'{Path.GetFileName(ctl)}' does not hold a cluster at its top - nothing to append to.",
                    new { ctlPath = ctl });
            var taken = parsed.Where(f => before.Contains(f.Name, StringComparer.OrdinalIgnoreCase))
                              .Select(f => f.Name).ToArray();
            if (taken.Length > 0)
                return Json.Error("elementAlreadyPresent",
                    $"'{Path.GetFileName(ctl)}' already has {string.Join(", ", taken.Select(t => $"'{t}'"))}. " +
                    "LabVIEW would add a second one of that name; this refuses instead.",
                    new JsonObject
                    {
                        ["taken"] = new JsonArray([.. taken.Select(t => (JsonNode)t)]),
                        ["elements"] = new JsonArray([.. before.Select(t => (JsonNode)t)]),
                    });

            var steps = new JsonArray();
            var helpers = new HousekeepingHelpers(connection);
            var (helperVi, generated, failure) = await helpers.EnsureAsync(Helper, timeoutSeconds, ct);
            if (failure is not null) return failure;

            var carrierVi = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "carriers",
                $"{Path.GetFileNameWithoutExtension(ctl).Replace(' ', '_')}-add-{DateTime.UtcNow:yyyyMMddHHmmss}.vi");
            Directory.CreateDirectory(Path.GetDirectoryName(carrierVi)!);
            var carrierAixml = Path.ChangeExtension(carrierVi, ".xml");
            await File.WriteAllTextAsync(carrierAixml,
                LvClass.CarrierAixml(Path.GetFileNameWithoutExtension(ctl), parsed), ct);
            var carrier = await new BulkTools(connection).GenerateViAsync(
                carrierAixml, carrierVi, openVI: false, measurePane: false,
                timeoutSeconds: timeoutSeconds, ct: ct, measureDiagram: false);
            steps.Add(new JsonObject { ["step"] = "carrier", ["answer"] = Parse(carrier) });
            if (!File.Exists(carrierVi))
                return Json.Error("carrierNotGenerated",
                    "The carrier VI could not be generated, so there are no controls to append.",
                    new JsonObject { ["carrierViPath"] = carrierVi, ["steps"] = steps });

            if (projectPath is { Length: > 0 })
            {
                var (opened, openFailure) = await helpers.OpenProjectAsync(projectPath, timeoutSeconds, ct);
                steps.Add(new JsonObject { ["step"] = "openProject", ["answer"] = opened?.DeepClone() });
                if (openFailure is not null) return openFailure;
            }

            // WHAT WAS UNSAVED BEFORE, so only this call's own changes get saved afterwards.
            var scripts = StatusTools.ScriptsDirectory();
            var dqmh = new DqmhTools(connection);
            List<string>? dirtyBefore = null;
            if (projectPath is { Length: > 0 } && scripts is not null)
                (dirtyBefore, _) = await DqmhHeadless.ProjectDirtyAsync(dqmh, scripts,
                    Path.GetFullPath(projectPath), timeoutSeconds, ct);

            var stamp = File.GetLastWriteTimeUtc(ctl);
            var (values, helperError) = await helpers.RunAsync(helperVi!, new()
            {
                ["ctl path"] = ctl,
                ["carrier path"] = carrierVi,
            }, timeoutSeconds, ct);
            var error = values is null ? helperError ?? "the helper returned no values" : DqmhTools.Failed(values);
            steps.Add(new JsonObject
            {
                ["step"] = "append",
                ["clustersFound"] = int.TryParse(values is null ? null : DqmhTools.Scalar(values, "Clusters Found"), out var n) ? n : null,
                ["error"] = error,
            });

            // THE CALLERS FOLLOW IN MEMORY ONLY - measured 2026-11-14: a DQMH Main.vi's Data Cluster
            // constant read cluster{string.Status,double.Flow Rate} in LabVIEW and the old type on
            // disk - and an unsaved VI in the project makes every DQMH tool refuse the next call.
            JsonArray? callersSaved = null;
            if (dirtyBefore is not null && scripts is not null && error is null)
            {
                var (dirtyAfter, _) = await DqmhHeadless.ProjectDirtyAsync(dqmh, scripts,
                    Path.GetFullPath(projectPath!), timeoutSeconds, ct);
                var ours = (dirtyAfter ?? []).Except(dirtyBefore, StringComparer.Ordinal).ToList();
                var saved = await DqmhHeadless.SaveNamedAsync(dqmh, scripts, ours, timeoutSeconds, ct);
                callersSaved = new JsonArray([.. saved.Select(n => (JsonNode)n)]);
            }

            var (after, _) = await ElementsAsync(ctl, timeoutSeconds, ct);
            var expected = before.Concat(parsed.Select(f => f.Name)).ToList();
            var verified = after is not null && after.Count == expected.Count
                           && after.Zip(expected).All(p => string.Equals(p.First, p.Second, StringComparison.Ordinal));
            return Json.Document(new JsonObject
            {
                ["ok"] = verified && error is null,
                ["verified"] = verified,
                ["ctlPath"] = ctl,
                ["saved"] = File.GetLastWriteTimeUtc(ctl) != stamp,
                ["elementsBefore"] = new JsonArray([.. before.Select(t => (JsonNode)t)]),
                ["elementsAfter"] = after is null ? null : new JsonArray([.. after.Select(t => (JsonNode)t)]),
                ["error"] = error,
                ["callersSaved"] = callersSaved,
                ["unsavedBefore"] = dirtyBefore is null ? null : new JsonArray([.. dirtyBefore.Select(n => (JsonNode)n)]),
                ["helperViPath"] = helperVi,
                ["helperGenerated"] = generated,
                ["carrierViPath"] = carrierVi,
                ["steps"] = steps,
                ["note"] = verified
                    ? "Verified from the saved .ctl: the old elements first and unchanged, the new ones " +
                      "appended. " + (callersSaved is null
                          ? "The callers follow in memory only and are left modified - save them, or pass " +
                            "projectPath next time. "
                          : "") +
                      "Run lvai_exec_state over the typedef's callers if any of them bundles the cluster by position."
                    : "The saved .ctl does not show exactly the old elements followed by the new ones - " +
                      "compare elementsBefore and elementsAfter, and read `error`.",
            });
        }));

    /// <summary>
    /// The labels of the TOP cluster's elements, read from the file through pylabview - null when
    /// the control's top is not a cluster.
    /// </summary>
    internal static async Task<(List<string>? Labels, string? Failure)> ElementsAsync(
        string ctl, int timeoutSeconds, CancellationToken ct)
    {
        var outDirectory = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "ctl", Path.GetRandomFileName());
        try
        {
            var (mainXml, failure) = await CtlTools.ExtractAsync(ctl, outDirectory, timeoutSeconds, ct);
            if (failure is not null) return (null, failure);
            var described = CtlTools.Describe(XDocument.Load(mainXml!).Root!, ctl);
            return (TopClusterLabels(described), null);
        }
        finally
        {
            try { Directory.Delete(outDirectory, recursive: true); } catch { /* best effort */ }
        }
    }

    internal static List<string>? TopClusterLabels(JsonObject described) =>
        described["fields"] is JsonArray { Count: 1 } top
        && top[0]?["members"] is JsonArray members
            ? [.. members.Select(m => m?["label"]?.GetValue<string>() ?? "")]
            : null;

    private static JsonNode? Parse(string answer)
    {
        try { return JsonNode.Parse(answer); }
        catch (System.Text.Json.JsonException) { return JsonValue.Create(answer); }
    }
}
