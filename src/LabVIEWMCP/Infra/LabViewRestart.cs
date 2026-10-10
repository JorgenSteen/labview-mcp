// =============================================================================
// LabViewRestart.cs - the guarded close/restart of a running LabVIEW: the user's
// ownership rule, the guards in order, CloseMainWindow + wait (never a kill), the start.
// =============================================================================
using System.Text.Json.Nodes;

namespace LabVIEWMcp.Infra;

/// <summary>Does the lvai service answer, and on which port.</summary>
internal sealed record ServiceProbe(bool Answers, int? Port, string? Error);

/// <summary>Which project is active in the IDE. <see cref="Active"/> null = not checked.</summary>
internal sealed record ActiveProjectProbe(bool? Active, string? Path, string Note);

/// <summary>What the start step did.</summary>
internal sealed record StartResult(bool ServiceAnswers, int? Pid, DateTime? StartTime, int? Port, JsonObject Detail);

/// <summary>A close or restart request as the caller made it.</summary>
internal sealed record RestartRequest(string Mode, string? UserGoAhead, string? ProjectPath);

/// <summary>
/// Everything the route reaches outside itself. Delegates and an interface, so every guard is
/// unit-tested on a fake machine and fake LabVIEW answers; the tool wires the real ones.
/// </summary>
internal sealed class RestartDependencies
{
    public required ILabViewMachine Machine { get; init; }
    public required Func<CancellationToken, Task<ServiceProbe>> ProbeService { get; init; }
    public required Func<CancellationToken, Task<ActiveProjectProbe>> ActiveProject { get; init; }
    /// <summary>Saves and closes the active project (lvai_close_active_project); answers its JSON.</summary>
    public required Func<string, CancellationToken, Task<JsonObject>> CloseProject { get; init; }
    /// <summary>The memory reading, or why it was not taken (helper not generated yet, ...).</summary>
    public required Func<CancellationToken, Task<(VisInMemoryReading? Reading, string? NotRun)>> ReadMemory { get; init; }
    /// <summary>Starts the LabVIEW at the given exe path (or the default pick) and waits for the service.</summary>
    public required Func<string?, CancellationToken, Task<StartResult>> Start { get; init; }

    public string? AutoSaveDirectory { get; init; }
    public TimeSpan CpuWindow { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan CloseWait { get; init; } = TimeSpan.FromSeconds(20);
    public Func<MachineProcess, bool> IsServerStarted { get; init; } = LabViewOwnership.IsServerStarted;
    public Action<int, DateTime?> RecordOwnership { get; init; } = LabViewOwnership.Record;
}

/// <summary>
/// The skill's measured restart route (`session/restart.md`) as code. Guards in order - processes,
/// windows (dialogs), CPU, service, active project, memory - then ownership, then the close, then the
/// start.
///
/// THE OWNERSHIP RULE IS THE USER'S, verbatim in spirit: "I want to avoid closing when I open it".
/// No go-ahead is needed only for a LabVIEW this server process started (pid and start time recorded
/// by lvai_ensure_labview) or one that is EMPTY - only LabVIEW's frame windows, no VI, project or
/// dialog window, no `*` title, no LVAutoSave file, idle, and nothing in memory but NI's service and
/// labview-mcp's helpers. Anything else needs `userGoAhead` with the user's own words, which the
/// answer echoes. A check that cannot run makes LabVIEW not empty.
///
/// THREE GUARDS NO GO-AHEAD OPENS. More than one LabVIEW (which one is meant is the user's call); a
/// possible dialog (the user answers it - this never presses, fronts or closes one); a busy CPU
/// (measured: a LabVIEW running a VI Server helper answers CloseMainWindow False and does not exit).
///
/// NEVER KILLS. The close is CloseMainWindow and a bounded wait. A LabVIEW that does not exit is
/// reported with what the two outcomes mean, a fresh window list and a CPU sample.
/// </summary>
internal static class LabViewRestart
{
    public const string Close = "close";
    public const string Restart = "restart";

    // Guard names, as the answer reports them in `stoppedBy`.
    public const string GuardProcesses = "processes";
    public const string GuardDialog = "dialog";
    public const string GuardBusy = "busy";
    public const string GuardService = "service";
    public const string GuardActiveProject = "activeProject";
    public const string GuardMemory = "memory";
    public const string GuardUnsaved = "unsaved";
    public const string GuardAutoSave = "autoSave";
    public const string GuardOwnership = "ownership";
    public const string GuardHiddenWindows = "hiddenWindows";

    /// <summary>
    /// Why the server's own unsaved scratch VIs cannot be closed for the user, measured: VI Server
    /// finds them by name in NONE of the application instances it reaches, and closing their
    /// windows raises LabVIEW's save prompt, which this tool never answers.
    /// </summary>
    internal const string ScratchCannotBeClosed =
        "Not closed by this tool, because no route exists that does it without a prompt. Measured " +
        "2026-09-26 (docs/scratch-vis-in-memory.md §1): Open VI Reference by name answers Error 1004 " +
        "for them in the helpers' instance, the IDE's main instance and the active project's, and " +
        "the converter's own 1051 check does not see them either, so lvai_close_vi and " +
        "lvai_close_own_panel cannot reach them. Closing their windows instead raises LabVIEW's save " +
        "prompt (§2a), and a dialog is never answered here. So they keep blocking; with the user's " +
        "go-ahead the close raises the prompt, and \"Don't Save - All\" is safe for these.";

    /// <summary>Window kinds that do not stop LabVIEW being "empty".</summary>
    private static readonly HashSet<string> EmptyKinds =
    [
        WindowKinds.Frame, WindowKinds.GettingStarted, WindowKinds.HiddenSystem, WindowKinds.Service,
        WindowKinds.Untitled,
    ];

    /// <summary>
    /// A recovery copy LabVIEW auto-saved for the project file <paramref name="ownProjectFile"/>.
    /// Measured once (2026-10-10, LabVIEW 2026 Q3 32-bit), for an unsaved `Probe.lvproj`:
    /// `NI32LV2631f1ASD1_Probe.lvproj` and `NI32LV2631f1ASD1_Probe.txt`, gone after the project was
    /// saved and closed. The user's decision (2026-10-10): those do not block a close that saves
    /// the project; any other file does.
    /// </summary>
    internal static bool IsOwnProjectAutoSave(string relativeFile, string ownProjectFile)
    {
        var match = AutoSaveName.Match(Path.GetFileName(relativeFile));
        if (!match.Success) return false;
        var rest = match.Groups["rest"].Value;
        return string.Equals(rest, ownProjectFile, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rest, Path.GetFileNameWithoutExtension(ownProjectFile) + ".txt",
                             StringComparison.OrdinalIgnoreCase);
    }

    private static readonly System.Text.RegularExpressions.Regex AutoSaveName =
        new(@"^NI\w*?ASD\d+_(?<rest>.+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static Task<JsonObject> RunAsync(RestartRequest request, RestartDependencies deps,
                                            CancellationToken ct) =>
        new Run(request, deps).ExecuteAsync(ct);

    private sealed class Run(RestartRequest request, RestartDependencies deps)
    {
        private readonly ILabViewMachine _m = deps.Machine;
        private readonly JsonObject _guards = new();
        private readonly JsonArray _sideEffects = [];
        private readonly JsonArray _deviations = [];
        private readonly List<string> _needGoAhead = [];
        private readonly List<ClassifiedWindow> _hiddenSeen = [];
        private readonly Dictionary<string, string> _goAheadReasons = [];
        private readonly DateTime _started = deps.Machine.Now;
        private string GoAhead => request.UserGoAhead?.Trim() ?? "";
        private bool HasGoAhead => GoAhead.Length > 0;

        public async Task<JsonObject> ExecuteAsync(CancellationToken ct)
        {
            // ---------- 1. processes and windows, no LabVIEW call ----------
            var first = LabViewState.Take(_m, deps.AutoSaveDirectory);
            _guards["windows"] = WindowsJson(first);
            _guards["autoSave"] = first.AutoSave.ToJson();

            if (first.LabView.Count != 1)
                return Refuse(GuardProcesses, first.LabView.Count == 0
                    ? request.Mode == Close
                        ? "No LabVIEW process runs, so there is nothing to close."
                        : "No LabVIEW process runs. Starting one is lvai_ensure_labview's job, not a restart's."
                    : $"{first.LabView.Count} LabVIEW processes run. Which one is meant is the user's " +
                      "call; nothing is closed while that is open.",
                    extra: new JsonObject { ["labview"] = LabViewState.ProcessesJson(first.LabView, _m.Now) });

            var labview = first.LabView[0];
            if (first.Dialogs.Count > 0) return RefuseDialogs(first);

            // ---------- 2. CPU, twice in a row ----------
            var samples = new JsonArray();
            _guards["cpu"] = samples;
            for (var i = 0; i < 2; i++)
            {
                var sample = await LabViewState.SampleCpuAsync(_m, labview.Id, deps.CpuWindow, ct);
                samples.Add(sample.ToJson());
                if (sample.Busy is not false)
                    return Refuse(GuardBusy, sample.Busy is null
                        ? "LabVIEW's CPU time could not be read (did it exit?). A LabVIEW whose state " +
                          "cannot be read is not closed."
                        : $"LabVIEW used {sample.CpuSeconds} CPU s in {sample.WindowSeconds} s at " +
                          $"{sample.At:HH:mm:ss}: busy. A busy LabVIEW does not take the close message " +
                          "(measured: CloseMainWindow False while a VI Server helper ran). Wait and call " +
                          "again; one idle sample can be stale within minutes.");
            }

            // Idle is not "safe": a modal dialog leaves LabVIEW idle. So the window list again.
            var snapshot = LabViewState.Take(_m, deps.AutoSaveDirectory);
            _guards["windows"] = WindowsJson(snapshot);
            _guards["autoSave"] = snapshot.AutoSave.ToJson();
            if (snapshot.Single?.Id != labview.Id)
                return Refuse(GuardProcesses, "The LabVIEW process changed while it was sampled.",
                    extra: new JsonObject { ["labview"] = LabViewState.ProcessesJson(snapshot.LabView, _m.Now) });
            if (snapshot.Dialogs.Count > 0) return RefuseDialogs(snapshot);

            var serverStarted = deps.IsServerStarted(labview);
            var ownProjectFile = string.IsNullOrWhiteSpace(request.ProjectPath)
                ? null : Path.GetFileName(request.ProjectPath.Trim());

            // ---------- 3. windows the user may be working in ----------
            // This session's own Project Explorer with a `*` is not the user's work: when it is the
            // active project this tool saves and closes it below. Measured 2026-10-10: a project opened
            // by lvai_open_file showed `Probe.lvproj * - Project Explorer` at once, before any edit.
            // It is set aside here and judged after the active-project read (step 5).
            var ownProjectUnsaved = snapshot.Unsaved.Where(w => IsOwnProjectExplorer(w, ownProjectFile)).ToList();
            var unsaved = snapshot.Unsaved.Except(ownProjectUnsaved).ToList();
            if (unsaved.Count > 0)
            {
                var scratchOnly = unsaved.All(w => w.IsLabViewMcpScratch);
                NeedGoAhead(GuardUnsaved, scratchOnly
                    ? "Unsaved labview-mcp scratch VIs are open (LVMCP Validate/Convert/DQMH/TD Carrier): " +
                      "the IDE close will raise LabVIEW's save prompt. " + ScratchCannotBeClosed
                    : "Windows with a `*` title - unsaved edits. Closing raises LabVIEW's save prompt or, " +
                      "for a project close, discards them. Save what this session made; the rest is the " +
                      "user's.");
            }
            var scratch = snapshot.Unsaved.Where(w => w.IsLabViewMcpScratch).ToList();
            if (scratch.Count > 0)
                _guards["scratchVis"] = new JsonObject
                {
                    ["windows"] = new JsonArray([.. scratch.Select(w => (JsonNode)LabViewState.WindowJson(w))]),
                    ["closedByThisTool"] = false,
                    ["why"] = ScratchCannotBeClosed,
                };
            NoteHidden(snapshot);
            // LabVIEW's own recovery copies of THIS session's project are set aside like its `*`
            // window and judged after the active-project read: the close saves that project.
            var ownAutoSave = ownProjectFile is null
                ? []
                : snapshot.AutoSave.Sample.Where(f => IsOwnProjectAutoSave(f, ownProjectFile)).ToList();
            var otherAutoSave = snapshot.AutoSave.Count - ownAutoSave.Count;
            if (otherAutoSave > 0)
                NeedGoAhead(GuardAutoSave, $"LVAutoSave holds {otherAutoSave} file(s) that are not this " +
                    "session's project: somebody has unsaved work, or a crash left recovery data. Either way " +
                    "it is the user's.");

            // ---------- 4. the service ----------
            var service = await deps.ProbeService(ct);
            _guards["service"] = new JsonObject
            {
                ["answers"] = service.Answers, ["port"] = service.Port, ["error"] = service.Error,
            };
            if (!service.Answers)
                NeedGoAhead(GuardService, "The lvai service does not answer, so what is in memory and which " +
                    "project is active cannot be read. A helper may still run inside LabVIEW. The measured " +
                    "route stops here and the user decides whether to close on the window list alone.");

            // ---------- 5. the active project ----------
            var closeOwnProject = false;
            var activeKnown = false;
            if (service.Answers)
            {
                var active = await deps.ActiveProject(ct);
                _guards["activeProject"] = new JsonObject
                {
                    ["active"] = active.Active, ["path"] = active.Path, ["note"] = active.Note,
                };
                activeKnown = active.Active is not null;
                if (active.Active is true)
                {
                    closeOwnProject = SameFile(active.Path, request.ProjectPath);
                    if (!closeOwnProject)
                        NeedGoAhead(GuardActiveProject, $"The project '{active.Path}' is active and it is " +
                            "not the projectPath passed. A user's project is never closed with " +
                            "lvai_close_active_project; the IDE close closes it, which only the user's " +
                            "go-ahead covers.");
                }
                else if (active.Active is null)
                {
                    _deviations.Add("the active project could not be read: " + active.Note);
                }
            }
            if (ownProjectUnsaved.Count > 0)
            {
                _guards["ownProjectUnsaved"] = new JsonObject
                {
                    ["windows"] = new JsonArray([.. ownProjectUnsaved.Select(w => (JsonNode)LabViewState.WindowJson(w))]),
                    ["savedByTheProjectClose"] = closeOwnProject,
                };
                if (!closeOwnProject)
                    NeedGoAhead(GuardUnsaved, "This session's project has unsaved changes, and it is not the " +
                        "active project (or the active project could not be read), so this tool cannot save " +
                        "and close it: the IDE close would raise LabVIEW's save prompt.");
            }
            if (ownAutoSave.Count > 0)
            {
                _guards["ownProjectAutoSave"] = new JsonObject
                {
                    ["files"] = new JsonArray([.. ownAutoSave.Select(f => (JsonNode)JsonValue.Create(f)!)]),
                    ["setAsideBecauseTheCloseSavesIt"] = closeOwnProject,
                    ["basis"] = "one sample, 2026-10-10: LabVIEW's recovery copies of an unsaved project " +
                                "are named after the project file and were gone after its clean save and close",
                };
                if (!closeOwnProject)
                    NeedGoAhead(GuardAutoSave, "LVAutoSave holds recovery copies of this session's project, " +
                        "and it is not the active project (or that could not be read), so this tool cannot " +
                        "save it before the close.");
            }
            var otherProjectWindows = snapshot.Of(WindowKinds.Project)
                .Where(w => ownProjectFile is null ||
                            !w.Window.Title.StartsWith(ownProjectFile + " - ", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (otherProjectWindows.Count > 0 && !_needGoAhead.Contains(GuardActiveProject))
                NeedGoAhead(GuardActiveProject, "Project windows are open that are not the projectPath " +
                    "passed: " + string.Join("; ", otherProjectWindows.Select(w => w.Window.Title)));

            // ---------- decision A, before anything changes ----------
            var windowsEmpty = WindowsEmpty(snapshot, ownProjectFile, out var windowsNote);
            var mayBeEmpty = windowsEmpty && otherAutoSave == 0 && (ownAutoSave.Count == 0 || closeOwnProject) &&
                             service.Answers &&
                             !_needGoAhead.Contains(GuardActiveProject);
            if (!HasGoAhead && _needGoAhead.Count > 0)
                return RefuseForGoAhead(labview, serverStarted, EmptyChecks(windowsEmpty, windowsNote, snapshot,
                    service, memoryNote: "not run: an earlier guard already needs the user's go-ahead"));
            if (!HasGoAhead && !serverStarted && !mayBeEmpty)
            {
                NeedGoAhead(GuardOwnership, OwnershipReason());
                return RefuseForGoAhead(labview, serverStarted, EmptyChecks(windowsEmpty, windowsNote, snapshot,
                    service, memoryNote: "not run: LabVIEW is already not empty"));
            }

            // ---------- 6. this session's project closed ----------
            // Unknown which project is active, and a project window open: treat it as still open.
            var projectStillActive = _needGoAhead.Contains(GuardActiveProject) ||
                                     (!activeKnown && snapshot.Of(WindowKinds.Project).Any());
            if (closeOwnProject)
            {
                var closed = await deps.CloseProject(request.ProjectPath!.Trim(), ct);
                _guards["projectClose"] = closed;
                var didClose = Flag(closed, "closed");
                var ok = didClose || Flag(closed, "nothingToClose");
                _sideEffects.Add(didClose
                    ? $"project '{request.ProjectPath}' SAVED and closed by lvai_close_active_project " +
                      "(the close may rewrite the .lvproj, .lvlps, .aliases: check the project's tree)"
                    : ok ? "project close: nothing was active any more (1055)"
                         : $"project '{request.ProjectPath}': the close did not report closed; see guards.projectClose");
                if (!ok)
                    return Refuse(GuardActiveProject, "The project close did not report ok, so the project " +
                        "may still be open. Nothing else was done; read guards.projectClose.");
            }

            // ---------- 7. memory ----------
            VisInMemoryReading? memory = null;
            string memoryNote;
            if (!service.Answers)
                memoryNote = "not run: the service does not answer";
            else if (projectStillActive)
                memoryNote = "not run: a project stays active, and a probe VI run now would be adopted into it";
            else
            {
                var (reading, notRun) = await deps.ReadMemory(ct);
                memory = reading;
                memoryNote = reading is null ? "not run: " + notRun : "read";
                if (reading is not null)
                {
                    var json = reading.ToJson();
                    json["instance"] = "the default (helper) application instance only: an open project's " +
                                       "VIs, classes and libraries are not listed";
                    _guards["memory"] = json;
                    if (reading.Modified.Count > 0)
                        NeedGoAhead(GuardMemory, $"{reading.Modified.Count} VI(s) in memory carry unsaved " +
                            "changes: " + string.Join(", ", reading.Modified.Take(10).Select(e => e.Name)));
                }
            }

            // ---------- decision B: ownership ----------
            var empty = mayBeEmpty && memory is { OnlyServiceAndHelpers: true };
            var emptyChecks = EmptyChecks(windowsEmpty, windowsNote, snapshot, service, memoryNote, memory);
            if (!serverStarted && !empty) NeedGoAhead(GuardOwnership, OwnershipReason());
            if (!HasGoAhead && _needGoAhead.Count > 0)
                return RefuseForGoAhead(labview, serverStarted, emptyChecks);

            // ---------- 8. record before ----------
            var before = Record(snapshot, labview, service.Port);

            // A dialog may have come up while the guards ran (a project close can raise one).
            var last = LabViewState.Take(_m, deps.AutoSaveDirectory);
            if (last.Dialogs.Count > 0) return RefuseDialogs(last);
            if (NoteHidden(last) && !HasGoAhead)
                return RefuseForGoAhead(labview, serverStarted, emptyChecks);

            // ---------- 9. the close: CloseMainWindow + wait, never a kill ----------
            var close = _m.CloseMainWindow(labview.Id, deps.CloseWait);
            var closeJson = new JsonObject
            {
                ["startedAt"] = close.StartedAt.ToString("HH:mm:ss.fff"),
                ["seconds"] = close.Seconds,
                ["closeMainWindow"] = close.CloseMessageTaken,
                ["waitForExit"] = close.Exited,
                ["exitCode"] = close.ExitCode,
            };
            var answer = Answer(labview, serverStarted, empty, emptyChecks);
            answer["before"] = before;
            answer["close"] = closeJson;

            if (!close.Found)
            {
                closeJson["meaning"] = "LabVIEW had already exited when the close was sent.";
                _deviations.Add("LabVIEW was gone before the close");
            }
            else if (!close.Exited)
            {
                closeJson["meaning"] = close.CloseMessageTaken
                    ? "CloseMainWindow returned True and LabVIEW did not exit: a dialog came up after the " +
                      "request, most likely a save prompt. The user answers it."
                    : "CloseMainWindow returned False: LabVIEW did not take the close message - busy with a " +
                      "VI Server helper, or modal. The user decides; LabVIEW is never killed here.";
                var now = LabViewState.Take(_m, deps.AutoSaveDirectory);
                closeJson["windowsNow"] = WindowsJson(now);
                closeJson["dialogsNow"] = new JsonArray([.. now.Dialogs.Concat(now.HiddenUnrecognised).Select(d => (JsonNode)LabViewState.DescribeDialog(_m, d))]);
                if (now.Single is { } still)
                    closeJson["cpuNow"] = (await LabViewState.SampleCpuAsync(_m, still.Id, TimeSpan.FromSeconds(5), ct)).ToJson();
                answer["ok"] = false;
                answer["state"] = "closeDidNotFinish";
                answer["errorKind"] = "closeDidNotFinish";
                answer["error"] = closeJson["meaning"]!.GetValue<string>();
                return Finish(answer, snapshot.AutoSave);
            }
            else
            {
                closeJson["meaning"] = "LabVIEW exited.";
                _sideEffects.Add($"LabVIEW pid {labview.Id} closed (CloseMainWindow, {close.Seconds} s)");
            }

            if (request.Mode == Close)
            {
                answer["ok"] = true;
                answer["state"] = "closed";
                answer["after"] = Record(LabViewState.Take(_m, deps.AutoSaveDirectory), null, null);
                return Finish(answer, snapshot.AutoSave);
            }

            // ---------- 10. the start ----------
            var afterClose = LabViewState.Take(_m, deps.AutoSaveDirectory);
            if (afterClose.AutoSave.Count > 0)
            {
                answer["ok"] = false;
                answer["state"] = "closedNotStarted";
                answer["errorKind"] = "autoSaveAfterClose";
                answer["error"] = $"LabVIEW closed, and LVAutoSave still holds {afterClose.AutoSave.Count} " +
                    "file(s). A start would either clear them (lvai_ensure_labview's default) or raise the " +
                    "recovery dialog that blocks the service. That is the user's call: ask, then call " +
                    "lvai_ensure_labview, or recover by hand first.";
                answer["after"] = Record(afterClose, null, null);
                return Finish(answer, snapshot.AutoSave);
            }
            if (afterClose.LabView.Count > 0)
                _deviations.Add($"{afterClose.LabView.Count} LabVIEW process(es) run after the close; the start " +
                                "step reuses a running one rather than starting a second");

            var start = await deps.Start(labview.Path, ct);
            answer["start"] = start.Detail;
            if (start.Pid is { } newPid)
            {
                // Ownership does not change with a restart: the user's LabVIEW stays the user's.
                if (serverStarted) deps.RecordOwnership(newPid, start.StartTime);
                _sideEffects.Add($"LabVIEW started, pid {newPid}" +
                                 (serverStarted ? " (recorded as this server's, as the one before was)"
                                                : " (the user's, as the one before was: closing it again needs a new go-ahead)"));
            }
            var after = LabViewState.Take(_m, deps.AutoSaveDirectory);
            answer["after"] = Record(after, after.Single, start.Port);
            answer["ok"] = start.ServiceAnswers;
            answer["state"] = start.ServiceAnswers ? "restarted" : "startNotConfirmed";
            if (!start.ServiceAnswers)
            {
                answer["errorKind"] = "startNotConfirmed";
                answer["error"] = "LabVIEW was closed and a start was made, but the service did not answer " +
                                  "within this call. Call lvai_status; do not restart again.";
            }
            return Finish(answer, snapshot.AutoSave);
        }

        // ---------- helpers ----------

        private void NeedGoAhead(string guard, string reason)
        {
            if (_needGoAhead.Contains(guard)) return;
            _needGoAhead.Add(guard);
            _goAheadReasons[guard] = reason;
        }

        /// <summary>
        /// Record the unrecognised hidden windows of a snapshot as a go-ahead guard. True when this
        /// snapshot shows one not seen before - a window that came up while the guards ran.
        /// </summary>
        private bool NoteHidden(LabViewSnapshot s)
        {
            var fresh = s.HiddenUnrecognised
                .Where(w => _hiddenSeen.All(seen => seen.Window.Handle != w.Window.Handle)).ToList();
            if (fresh.Count == 0) return false;
            _hiddenSeen.AddRange(fresh);
            _needGoAhead.Remove(GuardHiddenWindows);
            NeedGoAhead(GuardHiddenWindows, $"{_hiddenSeen.Count} hidden titled window(s) that no " +
                "measurement explains: " + string.Join("; ", _hiddenSeen.Select(w =>
                    $"'{w.Window.Title}' (class {w.Window.ClassName})")) + ". One may be a dialog that " +
                "reports itself invisible (measured 2026-10-06), or a system window whose class is not " +
                "in the measured list yet. The user's go-ahead opens this guard, and the answer then " +
                "names each window it opened, so its class can be added.");
            return true;
        }

        private JsonArray HiddenJson() =>
            new([.. _hiddenSeen.Select(w => (JsonNode)new JsonObject
            {
                ["title"] = w.Window.Title, ["class"] = w.Window.ClassName,
            })]);

        private string OwnershipReason() =>
            "This LabVIEW was not started by this server process (no matching pid and start time was " +
            "recorded by lvai_ensure_labview) and it is not empty - see emptyChecks. The user's rule: " +
            "only the user's own words open it.";

        private static bool Flag(JsonObject o, string name) =>
            o[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

        private static bool SameFile(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a.Trim()), Path.GetFullPath(b.Trim()),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        /// <summary>`Mine.lvproj - Project Explorer` or, unsaved, `Mine.lvproj * - Project Explorer`.</summary>
        internal static bool IsOwnProjectExplorer(ClassifiedWindow w, string? ownProjectFile) =>
            ownProjectFile is not null &&
            (string.Equals(w.Window.Title, ownProjectFile + " - Project Explorer", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(w.Window.Title, ownProjectFile + " * - Project Explorer", StringComparison.OrdinalIgnoreCase));

        private static bool WindowsEmpty(LabViewSnapshot s, string? ownProjectFile, out string note)
        {
            var blocking = s.Windows.Where(w =>
            {
                if (EmptyKinds.Contains(w.Kind)) return false;
                if (IsOwnProjectExplorer(w, ownProjectFile)) return false;
                if (ownProjectFile is not null)
                {
                    if (w.Kind == WindowKinds.Project &&
                        w.Window.Title.StartsWith(ownProjectFile + " - ", StringComparison.OrdinalIgnoreCase))
                        return false;
                    if (w.Kind == WindowKinds.Vi &&
                        w.Window.Title.Contains(" on " + ownProjectFile + "/", StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                // A hidden scratch VI of labview-mcp's own, saved: not the user's work.
                return !(w.Kind == WindowKinds.Vi && !w.Window.Visible && LabViewState.IsScratchTitle(w.Window.Title));
            }).ToList();
            note = blocking.Count == 0
                ? "only LabVIEW's own frame and system windows"
                : string.Join("; ", blocking.Take(10).Select(w => $"{w.Kind}: {w.Window.Title}"));
            return blocking.Count == 0;
        }

        private static JsonObject EmptyChecks(bool windowsEmpty, string windowsNote, LabViewSnapshot s,
                                              ServiceProbe service, string memoryNote,
                                              VisInMemoryReading? memory = null) => new()
        {
            ["windows"] = new JsonObject { ["empty"] = windowsEmpty, ["note"] = windowsNote },
            ["autoSave"] = new JsonObject { ["empty"] = s.AutoSave.Count == 0, ["count"] = s.AutoSave.Count },
            ["idle"] = new JsonObject { ["empty"] = true, ["note"] = "two quiet CPU samples" },
            ["service"] = new JsonObject { ["answers"] = service.Answers },
            ["memory"] = new JsonObject
            {
                ["empty"] = memory?.OnlyServiceAndHelpers,
                ["note"] = memoryNote,
            },
        };

        private JsonObject WindowsJson(LabViewSnapshot s) => new()
        {
            ["unsaved"] = new JsonArray([.. s.Unsaved.Select(w => (JsonNode)LabViewState.WindowJson(w))]),
            ["dialogs"] = new JsonArray([.. s.Dialogs.Select(d => (JsonNode)LabViewState.DescribeDialog(_m, d))]),
            ["hiddenUnrecognised"] = new JsonArray([.. s.HiddenUnrecognised.Select(d => (JsonNode)LabViewState.WindowJson(d))]),
            ["open"] = new JsonArray([.. s.Windows
                .Where(w => w.Kind is WindowKinds.Vi or WindowKinds.Project)
                .Select(w => (JsonNode)LabViewState.WindowJson(w))]),
            ["note"] = s.Windows.Any(w => w.Kind == WindowKinds.Vi && w.Window.Visible)
                ? "Open VI panels: their running state is unknown - nothing measured detects a running VI " +
                  "from outside, and a modified VI with no open window does not show here."
                : null,
        };

        private JsonObject Record(LabViewSnapshot s, MachineProcess? labview, int? port) => new()
        {
            ["labview"] = labview is null ? LabViewState.ProcessesJson(s.LabView, _m.Now) : LabViewState.ProcessJson(labview, _m.Now),
            ["port"] = port,
            ["nigelLocalService"] = LabViewState.ProcessesJson(s.NigelService, _m.Now),
            ["nigelChat"] = LabViewState.ProcessesJson(s.NigelChat, _m.Now),
            ["autoSaveCount"] = s.AutoSave.Count,
        };

        private JsonObject Answer(MachineProcess labview, bool serverStarted, bool empty, JsonObject emptyChecks) => new()
        {
            ["ok"] = false,
            ["mode"] = request.Mode,
            ["state"] = null,
            ["stoppedBy"] = null,
            ["ownership"] = new JsonObject
            {
                ["labviewPid"] = labview.Id,
                ["whose"] = serverStarted ? "started by this server" : empty ? "empty" : "the user's",
                ["goAhead"] = GoAheadJson(serverStarted || empty),
            },
            ["emptyChecks"] = emptyChecks,
            ["guards"] = _guards.DeepClone(),
            // Named so each class can be added to LabViewState.SystemClassPrefixes from a measurement.
            ["overriddenHiddenWindows"] = HasGoAhead && _hiddenSeen.Count > 0 ? HiddenJson() : null,
        };

        private JsonObject? GoAheadJson(bool ownershipFree) => HasGoAhead
            ? new JsonObject
            {
                // Echoed verbatim so the reader sees whose words opened which guard.
                ["words"] = GoAhead,
                ["covers"] = new JsonArray([.. _needGoAhead.Select(g => (JsonNode)JsonValue.Create(g)!)]),
                ["needed"] = _needGoAhead.Count > 0,
            }
            : ownershipFree && _needGoAhead.Count == 0
                ? new JsonObject { ["needed"] = false }
                : null;

        private JsonObject RefuseDialogs(LabViewSnapshot s) => Refuse(GuardDialog,
            $"{s.Dialogs.Count} visible possible dialog(s) in LabVIEW: " +
            string.Join("; ", s.Dialogs.Take(5).Select(d =>
                $"'{d.Window.Title}' ({d.Window.ClassName})")) +
            ". A modal dialog blocks the service and the close. The user answers it - nothing here presses, " +
            "fronts or closes a dialog. A VI panel with a custom window title is flagged too: look at it. " +
            "Call again once it is gone.");

        private JsonObject Refuse(string guard, string message, JsonObject? extra = null)
        {
            var answer = new JsonObject
            {
                ["ok"] = false,
                ["errorKind"] = "refusedByGuard",
                ["stoppedBy"] = guard,
                ["error"] = message,
                ["mode"] = request.Mode,
                ["state"] = "refused",
                ["guards"] = _guards.DeepClone(),
            };
            if (extra is not null)
                foreach (var (k, v) in extra) answer[k] = v?.DeepClone();
            return Finish(answer, null);
        }

        private JsonObject RefuseForGoAhead(MachineProcess labview, bool serverStarted, JsonObject emptyChecks)
        {
            var first = _needGoAhead[0];
            var answer = Answer(labview, serverStarted, empty: false, emptyChecks);
            answer["errorKind"] = "needsUserGoAhead";
            answer["stoppedBy"] = first;
            answer["state"] = "refused";
            answer["error"] = "Not closed: " + _goAheadReasons[first] + " Pass userGoAhead with the user's " +
                              "own words (and their date) only once the user has said so in this context; an " +
                              "agent's 'approved' is not the user.";
            answer["needsGoAheadFor"] = new JsonObject(_needGoAhead.Select(g =>
                new KeyValuePair<string, JsonNode?>(g, JsonValue.Create(_goAheadReasons[g]))));
            return Finish(answer, null);
        }

        private JsonObject Finish(JsonObject answer, AutoSaveState? before)
        {
            if (before is not null)
            {
                var now = LabViewState.AutoSave(_m, before.Directory);
                answer["autoSave"] = $"{before.Count} before -> {now.Count} after ({before.Directory})";
            }
            answer["sideEffects"] = _sideEffects.Count == 0
                ? new JsonArray(JsonValue.Create("none")!)
                : (JsonArray)_sideEffects.DeepClone();
            if (_deviations.Count > 0) answer["deviations"] = _deviations.DeepClone();
            answer["elapsedSeconds"] = Math.Round((_m.Now - _started).TotalSeconds, 1);
            answer["neverDone"] = "no kill, no dialog answered, no Nigel process touched";
            return answer;
        }
    }
}
