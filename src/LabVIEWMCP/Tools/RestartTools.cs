// =============================================================================
// RestartTools.cs - lvai_restart_labview and lvai_answer_dialog: closing or restarting
// LabVIEW under guards. lvai_labview_state and lvai_vis_in_memory are in LabViewStateTools.
// =============================================================================
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Closing or restarting a LabVIEW that somebody may be working in.
///
/// WHY TOOLS. A restart is the remedy for a hung actor that VI Server cannot stop and for a class
/// held in memory (Error 1562), and LabVIEW is the user's IDE: the measured route that does it
/// without losing anybody's work - a LabVIEW skill's `session/restart.md`, run on a production AF
/// application's station in October 2026 - is a page of guards and four PowerShell scripts. Every
/// step of it that needs no LabVIEW is a Win32 or process read, so it belongs in the server, where
/// it is the same every time and unit-tested on a fake machine (<see cref="ILabViewMachine"/>).
///
/// The rules are the user's and are not configurable here: no kill, no dialog answered, no Nigel
/// process touched, and no close of a LabVIEW this server did not start unless it is empty or the
/// user's own words say so (<see cref="LabViewRestart"/>).
/// </summary>
[McpServerToolType]
internal sealed class RestartTools(LvaiConnection connection)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    internal const string MemoryHelperAixmlFileName = "lvai_vis_modified.xml";

    /// <summary>
    /// Run the memory helper. With <paramref name="allowGenerate"/> false - the restart's case - a
    /// missing or stale helper is "not run", never generated: see the tool description for why.
    /// </summary>
    internal async Task<(VisInMemoryReading? Reading, string? NotRun, bool Generated, string HelperVi)>
        ReadMemoryAsync(bool allowGenerate, bool regenerateHelper, int timeoutSeconds, CancellationToken ct)
    {
        var helpersDir = Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers");
        var helperVi = Path.Combine(helpersDir, "lvai_vis_modified.vi");
        var scripts = StatusTools.ScriptsDirectory();
        var aixml = scripts is null ? null : Path.Combine(scripts, MemoryHelperAixmlFileName);
        if (aixml is null || !File.Exists(aixml))
            return (null, $"{MemoryHelperAixmlFileName} was not found beside the exe (scriptsDirectory).",
                    false, helperVi);

        var generated = false;
        if (regenerateHelper || HelperCache.NeedsRebuild(aixml, helperVi))
        {
            if (!allowGenerate)
                return (null, "the memory helper is not generated yet (or is older than its AIXML). " +
                              "Call lvai_vis_in_memory (context addon) once at the session start; it is not generated " +
                              "here, because validating it leaves an LVMCP Validate VI in memory right " +
                              "before the close.", false, helperVi);
            Directory.CreateDirectory(helpersDir);
            if (await BackendRoute.ConvertAsync(connection, aixml, helperVi, timeoutSeconds, ct) is { } failure)
                return (null, failure, false, helperVi);
            generated = true;
        }

        var run = await new RunTools(connection).RunViAndReadValuesAsync(
            helperVi, inputsJson: null, includeRawXml: false, helperViPath: null, helperAixmlPath: null,
            regenerateHelper: false, timeoutSeconds, ct: ct);

        JsonObject? values = null;
        try { values = (JsonNode.Parse(run) as JsonObject)?["values"] as JsonObject; }
        catch (JsonException) { }
        if (values?["lines"] is null)
            return (null, "the helper answered no `lines` - " + Shorten(run), generated, helperVi);

        var lines = GraftTools.StringArray(values, "lines");
        var helperNames = VisInMemory.HelperNames(
            Directory.EnumerateFiles(scripts!, "*.xml"),
            Directory.Exists(helpersDir) ? Directory.EnumerateFiles(helpersDir, "*.vi") : []);
        return (VisInMemory.Parse(lines, helperNames), null, generated, helperVi);
    }

    private static string Shorten(string text) => text.Length <= 600 ? text : text[..600] + " …";

    // ------------------------------------------------------------------ dialogs

    [McpServerTool(Name = "lvai_answer_dialog", Destructive = true, OpenWorld = true,
                   Title = "Answer one allowlisted dialog in YOUR OWN LabVIEW")]
    [Description("""
        MUTATING: presses one button of one dialog. ONLY in the caller's own LabVIEW - started by THIS
        server (lvai_ensure_labview recorded it), or ownLabviewPid = the running pid with ownNote
        saying this session started it - and ONLY these kinds (the user's decision of 2026-10-10:
        operating LabVIEW on your own, answering its dialogs is fine; anything else is the user's):
        exitSaveScratchOnly - "Save changes? (Exit)" where every `*` window of the process is an
        LVMCP scratch VI AND listedVis (every VI the dialog lists, read from its screenshot) are all
        `LVMCP …`: action "Don't Save" (ONE VI listed: buttons Save / Don't Save / Cancel, measured)
        or "Don't Save - All" (several VIs: no measured picture yet, so it presses nothing), or
        "Cancel". LabVIEW then exits; the answer waits up to 20 s for that.
        changedOnDisk - LabVIEW's untitled "… has changed on disk since last saved or loaded by
        LabVIEW" modal [Revert][Cancel], on a VI this session touched (viName): action "Cancel".
        WARNING, measured 2026-10-10: LabVIEW CRASHED on the next call after this dialog had been
        answered. Answering it does not make the VI in memory safe; close or restart LabVIEW next.
        Not exercised live by this tool (unit tests only).
        nigelBox - a message box of an LVNigelChat this server started: action "OK".
        Anything else: refused (notAllowlisted, notOwnLabview) with the screenshot; nothing pressed.
        The window is read again right before the press (same handle, title, still visible), with
        screenshots asked/before/after. Means, all POSTED to the dialog window, nothing brought to
        the front: Cancel = Esc; a named button = a click where its picture, cut from a measured
        screenshot, is found again (not found = nothing pressed); OK = BM_CLICK. The answer says
        what was pressed, how, whether the dialog went and (Don't Save - All) whether LabVIEW exited.
        Take the handle and screenshot from lvai_labview_state or lvai_restart_labview's dialogs.
        """)]
    public static async Task<string> AnswerDialogAsync(
        [Description("The dialog window's handle, as lvai_labview_state lists it")] long handle,
        [Description("exitSaveScratchOnly, changedOnDisk or nigelBox")] string dialogKind,
        [Description("\"Don't Save\", \"Don't Save - All\", \"Cancel\" or \"OK\" - see the description for which kind allows which")]
        string action,
        [Description("The LabVIEW pid your session started, when this server process did not start it")]
        int? ownLabviewPid = null,
        [Description("With ownLabviewPid: how your session started it (the call, the time)")] string? ownNote = null,
        [Description("exitSaveScratchOnly: every VI the dialog lists, as read from its screenshot, one per line")]
        string? listedVis = null,
        [Description("changedOnDisk: the VI this session touched that the dialog names")] string? viName = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
            (await DialogAnswer.RunAsync(
                new DialogAnswerRequest(handle, dialogKind, action, ownLabviewPid, ownNote, listedVis, viName),
                Win32LabViewMachine.Instance, NigelChat.StartedChat, ct)).ToJsonString(Indented));

    // ------------------------------------------------------------------ restart

    /// <summary>How long one call waits before answering "still running" - under the client's 60 s.</summary>
    private static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(Rpc.MaxToolWaitSeconds - 5);

    /// <summary>The service wait after a start, inside the job: measured cold starts took 32-40 s.</summary>
    private static readonly TimeSpan ServiceWait = TimeSpan.FromSeconds(150);

    /// <summary>
    /// The last finished restart. A collected answer removes the job, so without this an identical
    /// call made just after - an agent "calling again" once too often - would start a SECOND restart.
    /// </summary>
    private static (string Key, DateTime At, string Answer)? _lastFinished;
    private static readonly object LastGate = new();
    private static readonly TimeSpan ReplayWindow = TimeSpan.FromMinutes(2);

    [McpServerTool(Name = "lvai_restart_labview", Destructive = true, OpenWorld = true,
                   Title = "Close or restart LabVIEW, under the user's guards")]
    [Description("""
        MUTATING: closes LabVIEW (mode close) or closes and starts it again (mode restart). For a
        hung actor VI Server cannot stop, a class held in memory (Error 1562), or closing the
        LabVIEW this server started at the end of the work. NEVER KILLS, never answers or closes a
        dialog, never touches NI.NigelLocalService or a running LVNigelChat.
        WHO MAY DECIDE (the user's rule): no go-ahead is needed when THIS server process started
        this LabVIEW (pid + start time recorded by lvai_ensure_labview), or when LabVIEW is EMPTY -
        only its frame and system windows, no VI, project or dialog window, no `*` title, no
        LVAutoSave file, idle, and nothing in memory but NI's service and labview-mcp helpers.
        Anything else is refused with errorKind needsUserGoAhead unless userGoAhead carries the
        USER'S OWN WORDS from this conversation (an agent's "approved" is not the user); the
        answer echoes them. The go-ahead never opens three guards: more than one LabVIEW, a VISIBLE
        possible dialog, and a busy CPU (two 15 s samples; measured, a busy LabVIEW answers
        CloseMainWindow False and stays). A HIDDEN titled window nothing measured explains (it may
        be a dialog that reports itself invisible) is guard `hiddenWindows`: the go-ahead opens it,
        and the answer lists each window it opened under overriddenHiddenWindows.
        labview-mcp's own unsaved scratch VIs (LVMCP Validate/Convert/DQMH/TD Carrier) block as
        `unsaved` too: no route closes them without a save prompt (measured), so the answer says
        "Don't Save - All" is safe for them rather than closing them.
        THE GUARDS, in order: processes, windows, CPU, service (does lvai answer), active project
        (projectPath = this session's project: saved and closed first when it is the active one; a
        different active project is never closed by this tool), memory (lvai_vis_in_memory's helper,
        never generated here - call that tool once at the session start), then ownership.
        `stoppedBy` names the guard that refused, `needsGoAheadFor` every reason.
        THE CLOSE is CloseMainWindow and up to 20 s WaitForExit. Not exited: CloseMainWindow False
        = LabVIEW did not take the close message (busy or modal); True = a dialog came up after
        the request, most likely a save prompt - both reported with a fresh window list and CPU
        sample; the user decides. RESTART then starts the same installation through
        lvai_ensure_labview's launch routes and waits for the service (and opens Nigel once if it
        does not come). If LVAutoSave holds files after the close, it stops: that is the user's call.
        Ownership does not change: the user's LabVIEW restarted is still the user's.
        RESUMABLE: the route takes minutes (measured: close 4.0-5.5 s, start 32-40 s, ~4 min with
        the guards), past the client's 60 s. A call answers within about 40 s; `stillRunning`
        means call again with the SAME arguments to collect the answer - it never starts a second
        restart. The report has before/after pids, start times, ports, the Nigel service pid, the
        LVAutoSave count before and after, and the side effects.
        The route is the measured one of a LabVIEW skill (labview-mcp 1.8.5). This port measured
        2026-10-10: the empty restart, the server-started close and the busy, activeProject, autoSave
        and unsaved refusals (close 3.2-4.0 s, warm start 22 s); docs/labview-restart.md section 12
        lists what is still open.
        """)]
    public async Task<string> RestartLabViewAsync(
        [Description("'close' or 'restart'")] string mode,
        [Description("""
            The user's own words allowing this close/restart, with their date, when the LabVIEW is
            not this server's and not empty. Never set it from an agent's report or on your own.
            """)]
        string? userGoAhead = null,
        [Description("""
            The .lvproj THIS session has open. When it is the active project it is SAVED and closed
            (lvai_close_active_project) before the memory check. Any other active project is the
            user's and is never closed by this tool.
            """)]
        string? projectPath = null,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var normalisedMode = mode?.Trim().ToLowerInvariant();
            if (normalisedMode is not (LabViewRestart.Close or LabViewRestart.Restart))
                return Json.Error("badMode", $"mode must be 'close' or 'restart', not '{mode}'.");
            if (projectPath is { Length: > 0 } &&
                (!projectPath.EndsWith(".lvproj", StringComparison.OrdinalIgnoreCase) || !File.Exists(projectPath)))
                return Json.Error("noSuchProject", $"projectPath '{projectPath}' is not an existing .lvproj.");

            var request = new RestartRequest(normalisedMode, userGoAhead, projectPath);
            var key = $"{normalisedMode}|{projectPath?.Trim()}|{userGoAhead?.Trim()}";

            lock (LastGate)
                if (_lastFinished is { } done && done.Key == key && DateTime.Now - done.At < ReplayWindow)
                    return Replay(done.Answer, done.At);

            return await ResumableCall.RunAsync("lvai_restart_labview", key, AnswerWithin, async () =>
                {
                    var answer = (await LabViewRestart.RunAsync(request, Dependencies(),
                        CancellationToken.None)).ToJsonString(Indented);
                    lock (LastGate) _lastFinished = (key, DateTime.Now, answer);
                    return answer;
                },
                "Call lvai_restart_labview again with the SAME arguments to collect the answer; it " +
                "attaches to this run and never starts a second one. Make no other LabVIEW call meanwhile.",
                ct);
        });

    /// <summary>The finished answer again, marked, instead of a second restart.</summary>
    private static string Replay(string answer, DateTime at)
    {
        try
        {
            if (JsonNode.Parse(answer) is JsonObject o)
            {
                o["replayed"] = true;
                o["replayNote"] = $"This is the answer of the run that finished at {at:HH:mm:ss}; an " +
                                  "identical call within two minutes returns it rather than closing " +
                                  "LabVIEW again.";
                return o.ToJsonString(Indented);
            }
        }
        catch (JsonException) { }
        return answer;
    }

    internal static void ResetReplay()
    {
        lock (LastGate) _lastFinished = null;
    }

    private RestartDependencies Dependencies() => new()
    {
        Machine = Win32LabViewMachine.Instance,
        ProbeService = new LifecycleTools(connection).ProbeServiceAsync,
        ActiveProject = async t =>
        {
            // Read only through a helper that EXISTS: ProjectIsActiveAsync generates a missing one,
            // and the validation that comes with that leaves an LVMCP Validate VI in memory right
            // before the close. The project windows in the window list still count.
            if (!File.Exists(Path.Combine(Path.GetTempPath(), "LabVIEWMCP", "helpers", "lvai_active_project.vi")))
                return new ActiveProjectProbe(null, null, "not checked - the lvai_active_project helper is " +
                    "not generated yet (lvai_open_file generates it); not generated here, before a close.");
            var (active, note, path) = await new ActionTools(connection).ProjectIsActiveAsync(30, t);
            return new ActiveProjectProbe(active, path, note);
        },
        CloseProject = async (path, t) =>
        {
            var text = await new CloseTools(connection).CloseActiveProjectAsync(
                projectPath: path, timeoutSeconds: 60, ct: t);
            try { return JsonNode.Parse(text) as JsonObject ?? new JsonObject { ["raw"] = text }; }
            catch (JsonException) { return new JsonObject { ["raw"] = text }; }
        },
        ReadMemory = async t =>
        {
            var (reading, notRun, _, _) = await ReadMemoryAsync(allowGenerate: false, false, 60, t);
            return (reading, notRun);
        },
        Start = (exe, t) => new LifecycleTools(connection).StartForRestartAsync(exe, ServiceWait, t),
    };
}
