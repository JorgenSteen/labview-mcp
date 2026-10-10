// =============================================================================
// LabViewState.cs - what a running LabVIEW looks like from outside, with no LabVIEW
// call: processes, classified windows (unsaved, possible dialogs), LVAutoSave, CPU.
// =============================================================================
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LabVIEWMcp.Infra;

/// <summary>A top-level window with the kind the guards gave it.</summary>
/// <param name="Reason">Why a window that its title alone does not flag is a possible dialog.</param>
internal sealed record ClassifiedWindow(MachineWindow Window, string Kind, string? Reason = null)
{
    /// <summary>An unsaved window of labview-mcp's own scratch VIs (`LVMCP Validate…`, `LVMCP Convert…`).</summary>
    public bool IsLabViewMcpScratch => Kind == WindowKinds.Unsaved && LabViewState.IsScratchTitle(Window.Title);
}

/// <summary>The kinds a LabVIEW window is sorted into. Strings, because they go into the answer as they are.</summary>
internal static class WindowKinds
{
    public const string Unsaved = "unsaved";
    public const string Frame = "labview-frame";
    public const string GettingStarted = "getting-started";
    public const string Project = "project";
    public const string Vi = "vi";
    public const string Service = "service";
    public const string HiddenSystem = "hidden-system";
    public const string Untitled = "untitled";
    public const string PossibleDialog = "possible-dialog";
    /// <summary>A HIDDEN titled window nothing measured explains: maybe a dialog, maybe a new system class.</summary>
    public const string HiddenUnrecognised = "hidden-unrecognised";
}

/// <summary>The LVAutoSave folder as found. <see cref="Count"/> is -1 when the folder could not be read.</summary>
internal sealed record AutoSaveState(string Directory, bool Exists, int Count, IReadOnlyList<string> Sample)
{
    public JsonObject ToJson() => new()
    {
        ["path"] = Directory,
        ["exists"] = Exists,
        ["count"] = Count,
        ["files"] = new JsonArray([.. Sample.Select(f => (JsonNode)JsonValue.Create(f)!)]),
    };
}

/// <summary>One CPU sample of one process over one window.</summary>
internal sealed record CpuSample(DateTime At, double WindowSeconds, double? CpuSeconds, bool? Busy,
                                 IReadOnlyList<string> VisibleTitles)
{
    public JsonObject ToJson() => new()
    {
        ["at"] = At.ToString("HH:mm:ss"),
        ["windowSeconds"] = WindowSeconds,
        ["cpuSeconds"] = CpuSeconds,
        ["verdict"] = Busy switch { true => "busy", false => "quiet", null => "unreadable" },
        ["visibleTitles"] = new JsonArray([.. VisibleTitles.Select(t => (JsonNode)JsonValue.Create(t)!)]),
    };
}

/// <summary>Everything read in one pass, at one time.</summary>
internal sealed record LabViewSnapshot(
    DateTime At,
    IReadOnlyList<MachineProcess> LabView,
    IReadOnlyList<MachineProcess> NigelService,
    IReadOnlyList<MachineProcess> NigelChat,
    AutoSaveState AutoSave,
    IReadOnlyList<ClassifiedWindow> Windows)
{
    public IEnumerable<ClassifiedWindow> Of(string kind) => Windows.Where(w => w.Kind == kind);
    /// <summary>VISIBLE possible dialogs - the hard stop.</summary>
    public IReadOnlyList<ClassifiedWindow> Dialogs => [.. Of(WindowKinds.PossibleDialog)];

    /// <summary>Hidden titled windows nothing measured explains - a go-ahead guard, named one by one.</summary>
    public IReadOnlyList<ClassifiedWindow> HiddenUnrecognised => [.. Of(WindowKinds.HiddenUnrecognised)];
    public IReadOnlyList<ClassifiedWindow> Unsaved => [.. Of(WindowKinds.Unsaved)];

    /// <summary>The one LabVIEW, or null when there is none or more than one.</summary>
    public MachineProcess? Single => LabView.Count == 1 ? LabView[0] : null;
}

/// <summary>
/// Reading a running LabVIEW from the outside: the port of the skill's `lv_windows.ps1` and
/// `wait_lv_idle.ps1`, with one deliberate difference.
///
/// INVISIBLE IS NOT "NOT A DIALOG". The script flags only VISIBLE windows as possible dialogs. Measured
/// 2026-10-06 (docs/scratch-vis-in-memory.md §2a): an `Error list` and an `Externally Changed Files
/// List` dialog that the user saw on screen and could not click both reported `IsWindowVisible` FALSE.
/// So a hidden titled window is not ignored: unless its title is one of LabVIEW's measured hidden
/// system windows or its CLASS is one of the measured non-LabVIEW helper classes (IME, OLE, DDE,
/// GDI+, WPF's HwndWrapper), it is `hidden-unrecognised` and named with its class. Both lists come
/// from one `-All` window listing of a production LabVIEW 2026 Q3 on 2026-10-07. The user's decision
/// of 2026-10-10: a VISIBLE possible dialog is a hard stop, an unrecognised HIDDEN window is a guard
/// the user's go-ahead opens - and the answer names each one it opened, so its class can be added to
/// the list from a measurement rather than from a guess.
/// </summary>
internal static class LabViewState
{
    public const string LabViewProcess = "LabVIEW";
    public const string NigelServiceProcess = "NI.NigelLocalService";
    public const string NigelChatProcess = "LVNigelChat";

    /// <summary>Hidden LabVIEW windows that are never a dialog (the kit's ignore list, measured).</summary>
    internal static readonly HashSet<string> HiddenSystemTitles = new(StringComparer.Ordinal)
    {
        "Context Help", "Tools", "Set Title", "RunService.vi", "Closed", "Window monitor",
    };

    /// <summary>
    /// Window classes that live in LabVIEW.exe without being LabVIEW's own - input method editors,
    /// OLE and DDE plumbing, GDI+, NI's licence manager's WPF host. Measured 2026-10-07 in one `-All`
    /// listing; a prefix where the class carries an id.
    /// </summary>
    internal static readonly string[] SystemClassPrefixes =
    [
        "OleAutomationWnd", "HwndWrapper[", "GDI+ Hook Window Class", "LVWinSessionMonitor",
        "OleDdeWndClass", "IME", "MSCTFIME UI", "ATL:", "Internet Explorer_Hidden",
        // Measured 2026-10-10 in a LabVIEW 2026 Q3 listing: Windows' text services frame and NI-RPC's
        // message thread, both hidden and titled after their class.
        "CiceroUIWndFrame", "NI-RPC Message Thread Class",
    ];

    private static readonly Regex ViTitle = new(
        @"\.(vi|vim|vit|ctl|ctt)( (Front Panel|Block Diagram))?( on .+)?$", RegexOptions.Compiled);

    private static readonly Regex ProjectTitle = new(@"\.lvproj - Project Explorer$", RegexOptions.Compiled);

    /// <summary>The service's own VIs: NI's AI add-on ships as packed libraries named `LV AI …`.</summary>
    private static readonly Regex ServiceTitle = new(@"^LV AI [^:]*\.lvlibp:", RegexOptions.Compiled);

    /// <summary>
    /// The throwaway VI names this server hands LabVIEW and never saves under - and only those. Each
    /// is a `_name` substituted before a validate or convert so that a refusal cannot burn a real
    /// name; LabVIEW keeps the VI in memory, unsaved, and numbers a repeat (`LVMCP Validate2.vi`):
    /// `LVMCP Validate` (Infra/ValidationScratch.cs), `LVMCP Convert` (BulkTools,
    /// EventStructureTools), `LVMCP DQMH ` (DqmhHeadless), `LVMCP TD Carrier ` (TypedefCreateTools).
    /// The other `LVMCP …` names in the code - `Stub`, `ClsW`/`ClsR`/`Mth…` sockets, `Flat` - are
    /// FILES on disk, saved, and deliberately not here.
    /// </summary>
    internal static readonly string[] ScratchPrefixes =
        ["LVMCP Validate", "LVMCP Convert", "LVMCP DQMH ", "LVMCP TD Carrier "];

    internal static bool IsScratchTitle(string title) =>
        ScratchPrefixes.Any(p => title.StartsWith(p, StringComparison.Ordinal));

    /// <summary>The kind of one window. Order matters: `*` first, as in the kit.</summary>
    internal static string Classify(MachineWindow window)
    {
        var title = window.Title;
        if (title.Contains('*')) return WindowKinds.Unsaved;
        if (ServiceTitle.IsMatch(title)) return WindowKinds.Service;

        if (!window.Visible)
        {
            if (title.Length == 0 || HiddenSystemTitles.Contains(title)) return WindowKinds.HiddenSystem;
            if (IsSystemClass(window.ClassName)) return WindowKinds.HiddenSystem;
        }

        if (title.Length == 0) return WindowKinds.Untitled;
        if (title == "LabVIEW") return WindowKinds.Frame;
        if (title.StartsWith("Getting Started", StringComparison.Ordinal)) return WindowKinds.GettingStarted;
        if (ProjectTitle.IsMatch(title)) return WindowKinds.Project;
        if (ViTitle.IsMatch(title)) return WindowKinds.Vi;

        // Hidden and unexplained: see the class comment. A VI panel with a custom window title lands
        // in one of these two too, which the kit documents - better a panel looked at by eye than a
        // dialog closed over.
        return window.Visible ? WindowKinds.PossibleDialog : WindowKinds.HiddenUnrecognised;
    }

    /// <summary>
    /// The kinds with the context one window cannot give: who owns it, and whether LabVIEW's frame or
    /// a Project Explorer is disabled.
    ///
    /// THE UNTITLED MODAL DIALOG. Measured 2026-10-10: LabVIEW's "… has changed on disk" prompt
    /// ([Revert] [Cancel]) was a VISIBLE, UNTITLED `LVDChild` window of about 281x189, owned by the
    /// LabVIEW frame, with the frame and the Project Explorer disabled. By its title alone it is
    /// `untitled`, which the restart counts as empty - a close would have run into a modal dialog.
    /// So a visible window that its title leaves as `untitled` is a possible dialog when a LabVIEW
    /// frame owns it, or when a visible frame or Project Explorer is disabled (a modal dialog
    /// disables the windows it blocks). In that disabled state an ENABLED visible VI or start window
    /// is one too: a dialog VI titled after its file looks like any panel. Known helper classes
    /// (`Internet Explorer_Hidden`, visible and untitled on the measured station) stay out.
    /// </summary>
    internal static IReadOnlyList<ClassifiedWindow> ClassifyAll(IEnumerable<MachineWindow> windows)
    {
        var first = windows.Select(w => new ClassifiedWindow(w, Classify(w))).ToList();
        var frames = first.Where(c => c.Kind == WindowKinds.Frame).Select(c => c.Window.Handle).ToHashSet();
        var disabled = first.Where(c => c.Window.Visible && !c.Window.Enabled &&
                                        c.Kind is WindowKinds.Frame or WindowKinds.Project)
                            .Select(c => $"'{c.Window.Title}'").ToList();
        var blocked = disabled.Count > 0
            ? $"{string.Join(" and ", disabled)} disabled, as a modal dialog leaves them"
            : null;

        return [.. first.Select(c =>
        {
            var w = c.Window;
            if (!w.Visible || IsSystemClass(w.ClassName)) return c;
            if (c.Kind == WindowKinds.Untitled)
            {
                if (w.Owner != 0 && frames.Contains(w.Owner))
                    return c with { Kind = WindowKinds.PossibleDialog, Reason = "untitled, owned by the LabVIEW frame" +
                                                                         (blocked is null ? "" : "; " + blocked) };
                if (blocked is not null)
                    return c with { Kind = WindowKinds.PossibleDialog, Reason = "untitled; " + blocked };
            }
            if (blocked is not null && w.Enabled && c.Kind is WindowKinds.Vi or WindowKinds.GettingStarted)
                return c with { Kind = WindowKinds.PossibleDialog, Reason = $"enabled {c.Kind} window; " + blocked };
            return c;
        })];
    }

    internal static bool IsSystemClass(string className) =>
        SystemClassPrefixes.Any(p => className.StartsWith(p, StringComparison.Ordinal));

    /// <summary>Where LabVIEW keeps its recovery files; Documents may be redirected (OneDrive).</summary>
    internal static string DefaultAutoSaveDirectory() => AutoSaveRecovery.DefaultDirectory();

    public static LabViewSnapshot Take(ILabViewMachine machine, string? autoSaveDirectory = null)
    {
        var labview = machine.Processes(LabViewProcess);
        var windows = new List<ClassifiedWindow>();
        foreach (var process in labview)
            windows.AddRange(ClassifyAll(machine.TopWindows(process.Id)));

        return new LabViewSnapshot(
            machine.Now, labview,
            machine.Processes(NigelServiceProcess),
            machine.Processes(NigelChatProcess),
            AutoSave(machine, autoSaveDirectory ?? DefaultAutoSaveDirectory()),
            windows);
    }

    internal static AutoSaveState AutoSave(ILabViewMachine machine, string directory)
    {
        var files = machine.FilesUnder(directory);
        return files is null
            ? new AutoSaveState(directory, false, 0, [])
            : new AutoSaveState(directory, true, files.Count,
                                [.. files.Take(20).Select(f => Path.GetRelativePath(directory, f))]);
    }

    /// <summary>
    /// The kit's threshold, kept: a window is quiet when LabVIEW used less than one CPU second per
    /// thirty seconds of wall clock. Scaled, so a shorter window keeps the same rate.
    /// </summary>
    internal const double IdleCpuSecondsPer30 = 1.0;

    internal static bool IsBusy(double cpuSeconds, double windowSeconds) =>
        cpuSeconds >= IdleCpuSecondsPer30 * windowSeconds / 30.0;

    /// <summary>
    /// One CPU sample of the LabVIEW process. Idle is not "safe" - a modal dialog leaves LabVIEW idle
    /// and the service blocked - which is why the visible titles at the end of the window come along.
    /// </summary>
    public static async Task<CpuSample> SampleCpuAsync(ILabViewMachine machine, int processId,
                                                       TimeSpan window, CancellationToken ct)
    {
        var at = machine.Now;
        var before = machine.CpuTime(processId);
        await machine.Delay(window, ct);
        var after = machine.CpuTime(processId);
        var titles = machine.TopWindows(processId)
            .Where(w => w.Visible && w.Title.Length > 0 && w.Title != "LabVIEW")
            .Select(w => w.Title).ToList();

        if (before is null || after is null)
            return new CpuSample(at, window.TotalSeconds, null, null, titles);

        var used = Math.Round((after.Value - before.Value).TotalSeconds, 2);
        return new CpuSample(at, window.TotalSeconds, used, IsBusy(used, window.TotalSeconds), titles);
    }

    /// <summary>
    /// A possible dialog named for a report: title, class, visibility and whatever child windows it
    /// has. LabVIEW draws its own dialogs (class `LVDChild`) without child controls, so for those the
    /// buttons cannot be read; a Win32 message box (`#32770`) lists its buttons and text. Read only:
    /// nothing is pressed, fronted or closed.
    /// </summary>
    public static JsonObject DescribeDialog(ILabViewMachine machine, ClassifiedWindow dialog)
    {
        var children = machine.Children(dialog.Window.Handle);
        var buttons = children.Where(c => c.ClassName == "Button" && c.Text.Length > 0)
                              .Select(c => (JsonNode)JsonValue.Create(c.Text)!).ToArray();
        var texts = children.Where(c => c.ClassName != "Button" && c.Text.Length > 0)
                            .Select(c => (JsonNode)JsonValue.Create(c.Text)!).ToArray();
        return new JsonObject
        {
            ["title"] = dialog.Window.Title,
            ["class"] = dialog.Window.ClassName,
            ["visible"] = dialog.Window.Visible,
            ["processId"] = dialog.Window.ProcessId,
            ["reason"] = dialog.Reason,
            ["buttons"] = new JsonArray(buttons),
            ["texts"] = new JsonArray(texts),
            ["note"] = children.Count == 0
                ? "No child windows: LabVIEW draws its own dialogs, so the buttons cannot be read " +
                  "from outside. A person answers it."
                : null,
        };
    }

    /// <summary>
    /// The dialog finding for an error answer: the possible dialogs of every running LabVIEW, or null
    /// when there are none or nothing could be read. Never throws - this runs on a failure path.
    /// </summary>
    public static JsonArray? ProbeDialogs(ILabViewMachine machine)
    {
        try
        {
            var snapshot = Take(machine);
            // Both kinds here: this is for a TIMED-OUT call, where a hidden dialog is exactly the
            // finding worth having (scratch-vis-in-memory.md §2a); each entry carries `visible`.
            var found = snapshot.Dialogs.Concat(snapshot.HiddenUnrecognised).ToList();
            if (found.Count == 0) return null;
            return new JsonArray([.. found.Take(10).Select(d => (JsonNode)DescribeDialog(machine, d))]);
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            return null;
        }
    }

    public static JsonObject ProcessJson(MachineProcess process, DateTime now) => new()
    {
        ["pid"] = process.Id,
        ["startTime"] = process.StartTime?.ToString("yyyy-MM-dd HH:mm:ss"),
        ["upSeconds"] = process.StartTime is { } start ? (int)(now - start).TotalSeconds : null,
        ["path"] = process.Path,
    };

    public static JsonArray ProcessesJson(IEnumerable<MachineProcess> processes, DateTime now) =>
        new([.. processes.Select(p => (JsonNode)ProcessJson(p, now))]);

    public static JsonObject WindowJson(ClassifiedWindow w) => new()
    {
        ["kind"] = w.Kind,
        ["visible"] = w.Window.Visible,
        ["class"] = w.Window.ClassName,
        ["title"] = w.Window.Title,
        ["enabled"] = w.Window.Enabled ? null : false,
        ["ownerHandle"] = w.Window.Owner == 0 ? null : w.Window.Owner,
        ["handle"] = w.Window.Handle,
        ["labviewMcpScratch"] = w.IsLabViewMcpScratch ? true : null,
        ["reason"] = w.Reason,
    };
}

/// <summary>
/// Which LabVIEW THIS server process started - the one fact the ownership rule needs and nothing on
/// the machine records. The user's rule: a LabVIEW the server started may be closed by it with no
/// go-ahead; any other only with the user's words. Held in memory on purpose: a server that restarts
/// forgets, and forgetting errs towards asking. The start time is kept with the pid, because a pid is
/// reused and a reused pid is somebody else's LabVIEW.
/// </summary>
internal static class LabViewOwnership
{
    private static readonly object Gate = new();
    private static (int Pid, DateTime? Start, DateTime RecordedAt)? _owned;

    public static void Record(int pid, DateTime? startTime)
    {
        lock (Gate) _owned = (pid, startTime, DateTime.Now);
    }

    public static void Forget()
    {
        lock (Gate) _owned = null;
    }

    public static (int Pid, DateTime? Start, DateTime RecordedAt)? Current
    {
        get { lock (Gate) return _owned; }
    }

    /// <summary>True only when pid AND start time match what was recorded (a second's slack).</summary>
    public static bool IsServerStarted(MachineProcess process)
    {
        var owned = Current;
        if (owned is null || owned.Value.Pid != process.Id) return false;
        if (owned.Value.Start is null || process.StartTime is null) return owned.Value.Start is null;
        return Math.Abs((owned.Value.Start.Value - process.StartTime.Value).TotalSeconds) < 1.0;
    }

    public static JsonObject? ToJson() => Current is { } owned
        ? new JsonObject
        {
            ["pid"] = owned.Pid,
            ["startTime"] = owned.Start?.ToString("yyyy-MM-dd HH:mm:ss"),
            ["recordedAt"] = owned.RecordedAt.ToString("yyyy-MM-dd HH:mm:ss"),
        }
        : null;
}
