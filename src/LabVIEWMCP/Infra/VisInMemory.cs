// =============================================================================
// VisInMemory.cs - reads the lines of scripts/lvai_vis_modified*.xml (one VI in memory
// per line, with its modification bitsets and path) and sorts each VI: service, LabVIEW,
// helper, installation, other.
// =============================================================================
using System.Text.Json.Nodes;

namespace LabVIEWMcp.Infra;

/// <summary>One VI the helper found in memory.</summary>
/// <param name="FrontPanelMods">`Modifications:Front Panel Mods Bitset`; nonzero = unsaved panel edits.</param>
/// <param name="DiagramMods">`Modifications:Block Diagram Mods Bitset`; nonzero = unsaved diagram edits.</param>
/// <param name="ErrorCode">The open-by-name and property read's error; nonzero = the bits are not known.</param>
/// <param name="Origin">service / labview / helper / installation / other - see <see cref="VisInMemory.OriginOf"/>.</param>
/// <param name="Path">The VI's path as the helper read it (`VI Path`); null from a line without one.</param>
internal sealed record VisInMemoryEntry(long FrontPanelMods, long DiagramMods, int ErrorCode, string Name, string Origin,
                                        string? Path = null)
{
    public bool Modified => FrontPanelMods != 0 || DiagramMods != 0;
}

/// <summary>A library, class or other owner in memory, known by its loaded members.</summary>
/// <param name="Kind">library / class / packedLibrary / xcontrol / statechart / owner.</param>
/// <param name="Folders">Where its members with a path were loaded from.</param>
internal sealed record OwnerInMemory(string Name, string Kind, int MembersInMemory, string Origin,
                                     IReadOnlyList<string> Folders)
{
    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["kind"] = Kind,
        ["membersInMemory"] = MembersInMemory,
        ["folders"] = new JsonArray([.. Folders.Take(5).Select(f => (JsonNode)f)]),
        ["foldersTruncated"] = Folders.Count > 5 ? true : null,
    };
}

/// <summary>What one run of the helper found.</summary>
internal sealed record VisInMemoryReading(IReadOnlyList<VisInMemoryEntry> Entries, IReadOnlyList<string> Unparsed)
{
    public int Count => Entries.Count;
    public IReadOnlyList<VisInMemoryEntry> Modified => [.. Entries.Where(e => e.Modified)];
    public IReadOnlyList<VisInMemoryEntry> Unreadable => [.. Entries.Where(e => e.ErrorCode != 0)];
    public IReadOnlyList<VisInMemoryEntry> Other => [.. Entries.Where(e => e.Origin == VisInMemory.OriginOther)];

    /// <summary>
    /// Unreadable lines that still leave the answer unknown: NOT those of NI's packed libraries.
    /// Measured 2026-10-10 on a fresh LabVIEW 2026 Q3: 20 of the 419 lines, every one a `.ctl` of
    /// `LV AI gRPC Service.lvlibp` or `LV AI Core.lvlibp`, answered error 1035 and nothing else
    /// did. A packed library is built, read-only code that cannot hold anybody's unsaved edits, so
    /// requiring those lines to be readable made the empty rule fail on every LabVIEW.
    /// </summary>
    public IReadOnlyList<VisInMemoryEntry> UnreadableThatMatters =>
        [.. Unreadable.Where(e => e.Origin is not (VisInMemory.OriginService or VisInMemory.OriginLabView))];

    /// <summary>
    /// The user's "empty" rule applied to memory: nothing but NI's service, LabVIEW's own start
    /// window and labview-mcp's helpers, nothing modified, and every line readable outside NI's
    /// packed libraries (<see cref="UnreadableThatMatters"/>).
    /// </summary>
    public bool OnlyServiceAndHelpers =>
        Other.Count == 0 && Installation.Count == 0 && Modified.Count == 0 &&
        UnreadableThatMatters.Count == 0 && Unparsed.Count == 0;

    /// <summary>
    /// Code under the LabVIEW installation (vi.lib, user.lib, ...). Not the user's, but loaded, so
    /// it still counts against <see cref="OnlyServiceAndHelpers"/> - the fork, which read no path,
    /// counted it as `other`, and "empty" stays as strict as it was.
    /// </summary>
    public IReadOnlyList<VisInMemoryEntry> Installation => [.. Entries.Where(e => e.Origin == VisInMemory.OriginInstallation)];

    /// <summary>
    /// The libraries, classes and other owners IN MEMORY, read off their loaded members'
    /// qualified names (`Lib.lvlib:Class.lvclass:Method.vi` makes `Lib.lvlib` and
    /// `Lib.lvlib:Class.lvclass` both owners). LabVIEW lists no owner itself - All VIs In Memory
    /// names VIs only - but a class loads with every member, so a loaded class always shows here;
    /// a library none of whose members is loaded does not. `folders` are where the members were
    /// loaded FROM: the question a field project could not answer was whether a class in memory
    /// was the copy from this tree or another one (2026-11).
    /// </summary>
    public IReadOnlyList<OwnerInMemory> Owners => _owners ??= ComputeOwners();

    private IReadOnlyList<OwnerInMemory>? _owners;

    private IReadOnlyList<OwnerInMemory> ComputeOwners()
    {
        var owners = new Dictionary<string, List<VisInMemoryEntry>>(StringComparer.Ordinal);
        foreach (var entry in Entries)
        {
            var parts = entry.Name.Split(':');
            for (var depth = 1; depth < parts.Length; depth++)
            {
                var owner = string.Join(':', parts[..depth]);
                if (!owners.TryGetValue(owner, out var members)) owners[owner] = members = [];
                members.Add(entry);
            }
        }
        return [.. owners.Select(o => new OwnerInMemory(
            o.Key,
            VisInMemory.OwnerKind(o.Key),
            o.Value.Count,
            // the origin of the members, the user's code winning: one member of somebody's
            // code under an installation library is still somebody's code
            o.Value.Any(m => m.Origin == VisInMemory.OriginOther) ? VisInMemory.OriginOther
                : o.Value.GroupBy(m => m.Origin).OrderByDescending(g => g.Count()).First().Key,
            [.. o.Value.Where(m => m.Path is { Length: > 0 })
                .Select(m => System.IO.Path.GetDirectoryName(m.Path!)!)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)]))
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public JsonObject ToJson() => new()
    {
        ["count"] = Count,
        // Owners of the user's code in full; the rest (NI's service, vi.lib, helpers) only counted.
        ["owners"] = new JsonArray([.. Owners.Where(o => o.Origin == VisInMemory.OriginOther).Take(50)
            .Select(o => (JsonNode)o.ToJson())]),
        ["ownersTruncated"] = Owners.Count(o => o.Origin == VisInMemory.OriginOther) > 50 ? true : null,
        ["ownersElsewhere"] = Owners.Count(o => o.Origin != VisInMemory.OriginOther),
        ["byOrigin"] = new JsonObject
        {
            [VisInMemory.OriginService] = Entries.Count(e => e.Origin == VisInMemory.OriginService),
            [VisInMemory.OriginLabView] = Entries.Count(e => e.Origin == VisInMemory.OriginLabView),
            [VisInMemory.OriginHelper] = Entries.Count(e => e.Origin == VisInMemory.OriginHelper),
            [VisInMemory.OriginInstallation] = Entries.Count(e => e.Origin == VisInMemory.OriginInstallation),
            [VisInMemory.OriginOther] = Other.Count,
        },
        // Only the lines that matter, never all 400: the measured instance holds 419 VIs and a
        // full listing spilled the answer to a file in the skill's runs.
        ["modified"] = Lines(Modified),
        ["unreadable"] = Lines(Unreadable.Take(20)),
        ["unreadableCount"] = Unreadable.Count,
        ["unreadableOutsidePackedLibraries"] = UnreadableThatMatters.Count,
        // `other` is the user's code: what is NOT NI's service, a helper or under the LabVIEW
        // installation. With a project open that is a handful among hundreds (876 measured on
        // 2026-10-17, nearly all Caraya, OpenG and vi.lib), which is why the path is read.
        // READABLE FIRST: an unreadable line (1035 - a .ctl or a polymorphic VI opened by name)
        // carries no path and is listed under `unreadable` anyway; 122 of them hid the user's
        // seven VIs past the cut in the first project measurement.
        ["other"] = new JsonArray([.. Other.OrderBy(e => e.ErrorCode != 0).Take(50).Select(e => (JsonNode)(e.Path is { Length: > 0 }
            ? new JsonObject { ["name"] = e.Name, ["path"] = e.Path }
            : JsonValue.Create(e.Name)!))]),
        ["otherTruncated"] = Other.Count > 50 ? true : null,
        ["unparsedLines"] = Unparsed.Count > 0 ? Unparsed.Count : null,
        ["onlyServiceAndHelpers"] = OnlyServiceAndHelpers,
    };

    private static JsonArray Lines(IEnumerable<VisInMemoryEntry> entries) =>
        new([.. entries.Select(e => (JsonNode)new JsonObject
        {
            ["name"] = e.Name,
            ["origin"] = e.Origin,
            ["path"] = e.Path,
            ["frontPanelMods"] = e.FrontPanelMods,
            ["diagramMods"] = e.DiagramMods,
            ["errorCode"] = e.ErrorCode,
        })]);
}

/// <summary>
/// Parsing and sorting the answer of `scripts/lvai_vis_modified.xml` (the skill's measured
/// `pa_vis_modified` helper) and `lvai_vis_modified_project.xml`. Each line is
/// `fp TAB bd TAB error code TAB VI path TAB qualified name`; the fork's four-field line, with no
/// path, is still read.
///
/// WHAT THE HELPER SEES. `Application:All VIs In Memory` with no application reference reads the
/// instance the helper runs in. Measured three times on a production station (2026-10-07 twice,
/// 2026-10-08): 419 names, 341 from `LV AI gRPC Service.lvlibp`, 63 from `LV AI Core.lvlibp` and 15
/// from `GSW.lvlibp`, none modified - and docs/lvclass-creation.md measured the same instance at 404,
/// every one from the service library. An open project's VIs live in the project's own instance and
/// are NOT listed, and classes and libraries never are - they are known by their loaded members'
/// qualified names (<see cref="VisInMemoryReading.Owners"/>). So this is evidence about the default
/// instance only, and the answer says so.
/// </summary>
internal static class VisInMemory
{
    public const string OriginService = "service";
    public const string OriginLabView = "labview";
    public const string OriginHelper = "helper";
    public const string OriginInstallation = "installation";
    public const string OriginOther = "other";

    /// <summary>What Path To String makes of an empty path - an unsaved VI's (measured 2026-10-17).</summary>
    internal const string NotAPath = "<Not A Path>";

    public static VisInMemoryReading Parse(IEnumerable<string> lines, Func<string, bool> isHelper,
                                           string? installationRoot = null)
    {
        var entries = new List<VisInMemoryEntry>();
        var unparsed = new List<string>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('\t', 5);
            if (parts.Length >= 4 &&
                long.TryParse(parts[0].Trim(), out var fp) &&
                long.TryParse(parts[1].Trim(), out var bd) &&
                int.TryParse(parts[2].Trim(), out var code))
            {
                // A VI never saved has no path: LabVIEW's Path To String writes `<Not A Path>`.
                var path = parts.Length == 5 && parts[3].Trim() is { Length: > 0 } p && p != NotAPath ? p : null;
                var name = parts[^1].Trim();
                entries.Add(new VisInMemoryEntry(fp, bd, code, name,
                                                 OriginOf(name, isHelper, path, installationRoot), path));
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
    /// knows, one of the scratch names `LVMCP Validate…` / `LVMCP Convert…`, or a path in one of
    /// our helper caches or under `user.lib\LV_MCP` (the placeholder sockets). installation: a
    /// path under the running LabVIEW's own folder - vi.lib, user.lib, instr.lib, resource,
    /// LVAddons. other: everything else, which is somebody's code.
    /// </summary>
    internal static string OriginOf(string name, Func<string, bool> isHelper, string? path = null,
                                    string? installationRoot = null)
    {
        if (name.StartsWith("LV AI ", StringComparison.Ordinal) && name.Contains(".lvlibp:", StringComparison.Ordinal))
            return OriginService;
        if (name.StartsWith("GSW.lvlibp:", StringComparison.Ordinal)) return OriginLabView;
        if (LabViewState.IsScratchTitle(name) || isHelper(name) || IsHelperPath(path)) return OriginHelper;
        if (path is not null && installationRoot is { Length: > 0 } root && Under(path, root)) return OriginInstallation;
        return OriginOther;
    }

    /// <summary>What kind of owner a qualified-name prefix is, by its last part's extension.</summary>
    internal static string OwnerKind(string owner)
    {
        var last = owner[(owner.LastIndexOf(':') + 1)..];
        return System.IO.Path.GetExtension(last).ToLowerInvariant() switch
        {
            ".lvlib" => "library",
            ".lvclass" => "class",
            ".lvlibp" => "packedLibrary",
            ".xctl" => "xcontrol",
            ".lvsc" => "statechart",
            _ => "owner",
        };
    }

    private static bool IsHelperPath(string? path) =>
        path is not null &&
        (Under(path, System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LabVIEWMCP")) ||
         path.Contains(@"\user.lib\LV_MCP\", StringComparison.OrdinalIgnoreCase));

    private static bool Under(string path, string folder) =>
        path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

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
