// =============================================================================
// VisInMemory.cs - reads the lines of scripts/lvai_vis_modified.xml (one VI in memory
// per line, with its modification bitsets) and sorts each VI: service, LabVIEW, helper, other.
// =============================================================================
using System.Text.Json.Nodes;

namespace LabVIEWMcp.Infra;

/// <summary>One VI the helper found in memory.</summary>
/// <param name="FrontPanelMods">`Modifications:Front Panel Mods Bitset`; nonzero = unsaved panel edits.</param>
/// <param name="DiagramMods">`Modifications:Block Diagram Mods Bitset`; nonzero = unsaved diagram edits.</param>
/// <param name="ErrorCode">The open-by-name and property read's error; nonzero = the bits are not known.</param>
/// <param name="Origin">service / labview / helper / other - see <see cref="VisInMemory.OriginOf"/>.</param>
internal sealed record VisInMemoryEntry(long FrontPanelMods, long DiagramMods, int ErrorCode, string Name, string Origin)
{
    public bool Modified => FrontPanelMods != 0 || DiagramMods != 0;
}

/// <summary>What one run of the helper found.</summary>
internal sealed record VisInMemoryReading(IReadOnlyList<VisInMemoryEntry> Entries, IReadOnlyList<string> Unparsed)
{
    public int Count => Entries.Count;
    public IReadOnlyList<VisInMemoryEntry> Modified => [.. Entries.Where(e => e.Modified)];
    public IReadOnlyList<VisInMemoryEntry> Unreadable => [.. Entries.Where(e => e.ErrorCode != 0)];
    public IReadOnlyList<VisInMemoryEntry> Other => [.. Entries.Where(e => e.Origin == VisInMemory.OriginOther)];

    /// <summary>
    /// The user's "empty" rule applied to memory: nothing but NI's service, LabVIEW's own start
    /// window and labview-mcp's helpers, nothing modified, and every line readable.
    /// </summary>
    public bool OnlyServiceAndHelpers =>
        Other.Count == 0 && Modified.Count == 0 && Unreadable.Count == 0 && Unparsed.Count == 0;

    public JsonObject ToJson() => new()
    {
        ["count"] = Count,
        ["byOrigin"] = new JsonObject
        {
            [VisInMemory.OriginService] = Entries.Count(e => e.Origin == VisInMemory.OriginService),
            [VisInMemory.OriginLabView] = Entries.Count(e => e.Origin == VisInMemory.OriginLabView),
            [VisInMemory.OriginHelper] = Entries.Count(e => e.Origin == VisInMemory.OriginHelper),
            [VisInMemory.OriginOther] = Other.Count,
        },
        // Only the lines that matter, never all 400: the measured instance holds 419 VIs and a
        // full listing spilled the answer to a file in the skill's runs.
        ["modified"] = Lines(Modified),
        ["unreadable"] = Lines(Unreadable.Take(20)),
        ["other"] = new JsonArray([.. Other.Take(50).Select(e => (JsonNode)JsonValue.Create(e.Name)!)]),
        ["otherTruncated"] = Other.Count > 50 ? true : null,
        ["unparsedLines"] = Unparsed.Count > 0 ? Unparsed.Count : null,
        ["onlyServiceAndHelpers"] = OnlyServiceAndHelpers,
    };

    private static JsonArray Lines(IEnumerable<VisInMemoryEntry> entries) =>
        new([.. entries.Select(e => (JsonNode)new JsonObject
        {
            ["name"] = e.Name,
            ["origin"] = e.Origin,
            ["frontPanelMods"] = e.FrontPanelMods,
            ["diagramMods"] = e.DiagramMods,
            ["errorCode"] = e.ErrorCode,
        })]);
}

/// <summary>
/// Parsing and sorting the answer of `scripts/lvai_vis_modified.xml`, the skill's measured
/// `pa_vis_modified` helper. Each line is `fp TAB bd TAB error code TAB qualified name`.
///
/// WHAT THE HELPER SEES. `Application:All VIs In Memory` with no application reference reads the
/// instance the helper runs in. Measured three times on a production station (2026-10-07 twice,
/// 2026-10-08): 419 names, 341 from `LV AI gRPC Service.lvlibp`, 63 from `LV AI Core.lvlibp` and 15
/// from `GSW.lvlibp`, none modified - and docs/lvclass-creation.md measured the same instance at 404,
/// every one from the service library. An open project's VIs live in the project's own instance and
/// are NOT listed, and classes and libraries never are. So this is evidence about the default
/// instance only, and the answer says so.
/// </summary>
internal static class VisInMemory
{
    public const string OriginService = "service";
    public const string OriginLabView = "labview";
    public const string OriginHelper = "helper";
    public const string OriginOther = "other";

    public static VisInMemoryReading Parse(IEnumerable<string> lines, Func<string, bool> isHelper)
    {
        var entries = new List<VisInMemoryEntry>();
        var unparsed = new List<string>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('\t', 4);
            if (parts.Length == 4 &&
                long.TryParse(parts[0].Trim(), out var fp) &&
                long.TryParse(parts[1].Trim(), out var bd) &&
                int.TryParse(parts[2].Trim(), out var code))
            {
                var name = parts[3].Trim();
                entries.Add(new VisInMemoryEntry(fp, bd, code, name, OriginOf(name, isHelper)));
            }
            else
            {
                unparsed.Add(line);
            }
        }
        return new VisInMemoryReading(entries, unparsed);
    }

    /// <summary>
    /// service: NI's AI add-on (`LV AI …lvlibp:`), which hosts this very gRPC service.
    /// labview: LabVIEW's own Getting Started window library (`GSW.lvlibp:`), measured in every
    /// listing. helper: a labview-mcp helper or scratch VI - a name <paramref name="isHelper"/>
    /// knows, or one of the scratch names `LVMCP Validate…` / `LVMCP Convert…`. other: everything
    /// else, which is somebody's code.
    /// </summary>
    internal static string OriginOf(string name, Func<string, bool> isHelper)
    {
        if (name.StartsWith("LV AI ", StringComparison.Ordinal) && name.Contains(".lvlibp:", StringComparison.Ordinal))
            return OriginService;
        if (name.StartsWith("GSW.lvlibp:", StringComparison.Ordinal)) return OriginLabView;
        if (LabViewState.IsScratchTitle(name) || isHelper(name)) return OriginHelper;
        return OriginOther;
    }

    /// <summary>
    /// The helper names this server ships and generates: every `scripts\*.xml` as `&lt;name&gt;.vi`,
    /// and every `.vi` in the helper cache. A bare name only - a helper is never library-qualified.
    /// </summary>
    public static Func<string, bool> HelperNames(IEnumerable<string> scriptFiles, IEnumerable<string> cachedVis)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in scriptFiles)
            if (file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                names.Add(Path.GetFileNameWithoutExtension(file) + ".vi");
        foreach (var file in cachedVis)
            if (file.EndsWith(".vi", StringComparison.OrdinalIgnoreCase))
                names.Add(Path.GetFileName(file));
        return name => !name.Contains(':') && names.Contains(name);
    }
}
