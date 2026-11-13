using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;
using LabVIEWMcp.Infra;
using LabVIEWMcp.Lvai;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// Making sure LabVIEW is up, so the other tools have something to talk to.
///
/// Every lvai_* tool needs a running LabVIEW: the gRPC server lives inside LabVIEW.exe. This
/// starts one when none is running, choosing the installation without knowing any version
/// number in advance (see <see cref="LabViewLocator"/>).
///
/// Deliberately a separate, explicit tool rather than something the connection does on its
/// own. Launching an IDE is a heavyweight, visible side effect, and LabVIEW needs far longer
/// to become answerable than the MCP client is willing to wait for a tool call - so an
/// implicit auto-start would turn every read into a minute-long gamble.
/// </summary>
[McpServerToolType]
internal sealed class LifecycleTools(LvaiConnection connection)
{
    private static readonly JsonSerializerOptions Indented = Json.Answer;

    [McpServerTool(Name = "lvai_ensure_labview", Destructive = true, OpenWorld = true,
                   Title = "Make sure a LabVIEW instance is running")]
    [Description("""
        Status: makes sure a LabVIEW instance is running. A running one is used as-is; otherwise the
        newest installed LabVIEW is launched, preferring 32-bit (the build hosting the AI gRPC
        service). MUTATING: may start a visible IDE, and then first empties LVAutoSave (see
        keepAutoSave). The launch is VERIFIED: only a process still alive two seconds later counts
        (`launchMethod`); if none survives the answer is `launch-did-not-survive` with every attempt.
        LabVIEW is started with explorer as its parent: `outsideServerTree: false` means it ends
        with this server process.
        The gRPC service comes up with LabVIEW, about 45 s after a cold start; while it is coming up,
        call again - the second call finds the instance and waits. A 'starting' that persists past
        about two minutes with LabVIEW visibly up: ask whoever is at the machine to open Nigel, then
        call lvai_status once. For a long unattended wait use `LabVIEWMCP --ensure-labview`.
        """)]
    public async Task<string> EnsureLabViewAsync(
        [Description("How long to wait for the gRPC service to answer, in seconds (capped at 45)")]
        int waitSeconds = 40,
        [Description("""
            Do NOT clear Documents\LabVIEW Data\LVAutoSave before starting LabVIEW (it is cleared
            only when this tool starts LabVIEW, because leftover data raises a modal recovery dialog
            that stops the service).
            """)]
        bool keepAutoSave = false,
        CancellationToken ct = default,
        [Description(AixmlBackend.ParameterDescription + """
             On pylabview READY means LabVIEW answers over ActiveX; nothing waits for NI's service.
            """)]
        string? backend = null,
        [Description("""
            The LabVIEW to start when none runs, for THIS call: a LabVIEW.exe path or a release year
            ('2020'). It is not remembered as the next default. When a different LabVIEW is already
            running the call is refused (otherLabviewRunning) - it never starts a second one.
            Omitted: the settings file's LabVIEW, else the one last seen running, else the newest.
            """)]
        string? labview = null)
    {
        if (!AixmlBackend.TryResolve(backend, "lvai_ensure_labview", out var choice, out var bad))
            return AixmlBackend.Refusal(bad);
        LabViewInstall? requested = null;
        if (labview is { Length: > 0 })
        {
            var (install, problem) = RequestedInstall(labview, LabViewLocator.Discover());
            if (install is null) return Json.Error("badArguments", problem!, new { labview });
            requested = install;
            if (OtherRunning(install) is { } running)
                return AixmlTools.WithBackend(Json.Error("otherLabviewRunning",
                    $"{running} is running, not {install.Describe()}. Two LabVIEWs at once make every later call " +
                    "ambiguous, so none is started beside it: close the running one first (lvai_restart_labview " +
                    "mode close), then call again.", new { labview, requested = install.ExePath }), choice);
        }
        return AixmlTools.WithBackend(await EnsureCoreAsync(waitSeconds, keepAutoSave,
            choice.Name == AixmlBackend.PyLabview, ct, requested), choice);
    }

    /// <summary>
    /// The installation a `labview` argument names - an exe path or a release year (the 32-bit build
    /// first, as the selection rule prefers) - or why none matches.
    /// </summary>
    internal static (LabViewInstall? Install, string? Problem) RequestedInstall(
        string text, IReadOnlyList<LabViewInstall> installs)
    {
        var t = text.Trim().Trim('"');
        if (int.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
                         out var year))
        {
            var byYear = installs.Where(i => i.Release == year).OrderByDescending(i => i.Is32Bit).FirstOrDefault();
            return byYear is not null ? (byYear, null)
                : (null, $"No LabVIEW {year} is installed. Installed: {string.Join(", ", installs.Select(i => i.Describe()))}.");
        }
        var match = installs.FirstOrDefault(i => string.Equals(Full(i.ExePath), Full(t), StringComparison.OrdinalIgnoreCase));
        return match is not null ? (match, null)
            : (null, $"'{t}' is not an installed LabVIEW.exe. Installed: {string.Join(", ", installs.Select(i => i.ExePath))}.");
    }

    private static string Full(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    /// <summary>A running LabVIEW that is not <paramref name="wanted"/>, described; null when none.</summary>
    private static string? OtherRunning(LabViewInstall wanted)
    {
        var running = LabViewContext.RunningInstances();
        if (running.Count == 0 ||
            running.Any(r => string.Equals(Full(r.Exe), Full(wanted.ExePath), StringComparison.OrdinalIgnoreCase)))
            return null;
        return string.Join(", ", running.Select(r => $"pid {r.Pid} ({r.Exe})"));
    }

    private async Task<string> EnsureCoreAsync(int waitSeconds, bool keepAutoSave, bool overCom,
                                               CancellationToken ct, LabViewInstall? requested = null) =>
        await Rpc.GuardAsync(async () =>
        {
            var budget = TimeSpan.FromSeconds(Rpc.ClampToolWait(waitSeconds));
            var stopwatch = Stopwatch.StartNew();

            // The budget covers the WHOLE call - launch and poll alike - and it is enforced by a
            // token, not by a loop condition. It used to be a loop condition only, and that is not
            // the same thing: one port-discovery pass can take tens of seconds, so the deadline was
            // checked, a fresh pass started, and the call ran long past its promise. Measured
            // waitedMs of 53 812 and 47 242 against a nominal 45 000 - and past roughly 60 s the MCP
            // client stops waiting, answers "Request timed out", and the caller learns nothing at all.
            using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budgetCts.CancelAfter(budget);
            var token = budgetCts.Token;

            var startedNow = false;
            string? launched = null;
            LaunchOutcome? launch = null;
            AutoSaveRecovery.Result? autoSave = null;
            var lateLaunch = false;

            // Launches LabVIEW and answers null, or the refusal to return. A local function because
            // it runs either at once or, once, from the poll below (see there).
            async Task<string?> LaunchAsync()
            {
                // BEFORE the launch. Leftover auto-save data makes LabVIEW raise a modal recovery
                // dialog on start, and a modal dialog stops this very gRPC service until somebody
                // dismisses it - so an unattended start clears the store first. Only when WE start
                // LabVIEW: a process already running owns its own recovery data.
                if (!keepAutoSave) autoSave = AutoSaveRecovery.Clear();

                // the LabVIEW to start: the settings file's, else the one last seen running WITH
                // its -pref, else the newest installed (LabViewContext.ToStart, issue #99)
                var (pick, pref, startContext) = LabViewContext.ToStart();
                var chosenBy = startContext.Source;
                if (requested is not null)
                {
                    (pick, pref, chosenBy) = (requested, null, "this call's `labview`");
                    LabViewContext.MarkStartedForOneCall(requested.ExePath);
                }
                if (pick is null)
                    return Json.Error("no-labview",
                        "No LabVIEW installation found under the program-files roots.", new
                        {
                            searched = "…\\National Instruments\\LabVIEW*\\LabVIEW.exe",
                            // Naming the roots turns "found nothing" into something diagnosable:
                            // an empty or unexpected list points at the environment, not at LabVIEW.
                            rootsProbed = LabViewLocator.ProgramFilesRoots(),
                            note = "LabVIEW NXG is excluded - it does not host the lvai service.",
                        });

                // Not "did the call return without error" but "is a LabVIEW still alive" - see
                // LabViewLauncher for why those differ by about 500 ms.
                launch = await LabViewLauncher.StartAndConfirmAsync(pick, token, pref);
                if (!launch.Ok)
                    return Json.Error("launch-did-not-survive",
                        $"Every launch strategy was tried for {pick.Describe()} and no LabVIEW " +
                        "process stayed alive.", new
                        {
                            exe = pick.ExePath,
                            attempts = launch.Attempts,
                            note = "A process that appears and vanishes within a second is being " +
                                   "terminated from outside - normally a job object this server " +
                                   "belongs to. Starting LabVIEW by hand is the workaround; the " +
                                   "attempts above say which routes were refused.",
                        });

                startedNow = true;
                launched = pick.Describe() + (pref is null ? "" : $" -pref {pref}") +
                           $" (chosen: {chosenBy})";
                RecordOwnership(launch.Pid);
                return null;
            }

            if (LabViewLocator.RunningInstances().Count == 0 && await LaunchAsync() is { } refused)
                return refused;

            // Poll until the service answers. A cold LabVIEW start is far slower than this
            // budget, hence the explicit "call again" outcome rather than a failure.
            Exception? last = null;

            while (!token.IsCancellationRequested)
            {
                // A LabVIEW that is being ended still lists for a moment. Measured 2026-11-05: a
                // call right after the orchestrator killed LabVIEW found it, launched nothing,
                // polled out its budget and answered `starting` with runningProcesses 0. If the
                // process this call relied on is gone, start one - once.
                if (!startedNow && !lateLaunch && LabViewLocator.RunningInstances().Count == 0)
                {
                    lateLaunch = true;
                    if (await LaunchAsync() is { } refusedLate) return refusedLate;
                }

                if (overCom)
                {
                    // ready = a running LabVIEW with a window answers over ActiveX; NotReady keeps
                    // COM away from a LabVIEW still starting, which would launch a second one
                    if (LabViewCom.NotReady() is { } notYet)
                        last = new InvalidOperationException(notYet);
                    else
                    {
                        try
                        {
                            var version = await LabViewCom.WithLabViewAsync(app =>
                                app.GetType().InvokeMember("Version", System.Reflection.BindingFlags.GetProperty,
                                    null, app, null, System.Globalization.CultureInfo.InvariantCulture)?.ToString(),
                                10, token);
                            return new JsonObject
                            {
                                ["ok"] = true,
                                ["state"] = "ready",
                                ["readyMeans"] = "LabVIEW answers over ActiveX (the pylabview backend's route); NI's gRPC service was not asked",
                                ["startedByThisCall"] = startedNow,
                                ["launched"] = launched,
                                ["launchMethod"] = launch?.Method,
                ["launchParent"] = launch?.Parent,
                ["outsideServerTree"] = launch?.OutsideServerTree,
                                ["autoSaveCleared"] = autoSave?.Describe(),
                                ["labviewVersion"] = version,
                                ["waitedMs"] = stopwatch.ElapsedMilliseconds,
                            }.ToJsonString(Indented);
                        }
                        catch (Exception e) when (!ct.IsCancellationRequested)
                        {
                            last = e;
                        }
                    }
                    try { await Task.Delay(2000, token); }
                    catch (OperationCanceledException) { /* budget spent */ }
                    continue;
                }
                try
                {
                    connection.Invalidate();   // the port changes with every LabVIEW start
                    var client = await connection.GetClientAsync(token);
                    var config = await client.GetApplicationConfigurationAsync(
                        new GetApplicationConfigurationRequest(),
                        deadline: Rpc.Deadline(10), cancellationToken: token);

                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["state"] = "ready",
                        ["startedByThisCall"] = startedNow,
                        ["launched"] = launched,
                        ["launchMethod"] = launch?.Method,
                ["launchParent"] = launch?.Parent,
                ["outsideServerTree"] = launch?.OutsideServerTree,
                    ["autoSaveCleared"] = autoSave?.Describe(),
                        ["port"] = connection.Port,
                        ["discoveredVia"] = connection.DiscoveredVia,
                        ["applicationLanguage"] = config.Language,
                        ["waitedMs"] = stopwatch.ElapsedMilliseconds,
                    }.ToJsonString(Indented);
                }
                // The caller's own cancellation must still escape; only the budget is absorbed.
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    last = e;
                    try { await Task.Delay(2000, token); }
                    catch (OperationCanceledException) { /* budget spent - the loop ends below */ }
                }
            }

            ct.ThrowIfCancellationRequested();

            var stillRunning = LabViewLocator.RunningInstances().Count;
            return new JsonObject
            {
                ["ok"] = false,
                ["state"] = "starting",
                ["startedByThisCall"] = startedNow,
                ["launched"] = launched,
                ["launchMethod"] = launch?.Method,
                ["launchParent"] = launch?.Parent,
                ["outsideServerTree"] = launch?.OutsideServerTree,
                    ["autoSaveCleared"] = autoSave?.Describe(),
                ["runningProcesses"] = stillRunning,
                ["waitedMs"] = stopwatch.ElapsedMilliseconds,
                // THE BUDGET IS CLAMPED AND SAYING SO IS THE POINT. waitSeconds is capped at
                // Rpc.MaxToolWaitSeconds, so asking for 90 waits 45 - and four runs on 2026-09-01
                // read `waitedMs: 45009` against a nominal 90 and took it for a fault. The cap is
                // deliberate: past roughly 60 s the MCP client stops waiting and the caller learns
                // nothing at all, so waiting longer inside ONE call is not available. That is also
                // why this tool cannot retry internally, which was proposed and does not work.
                ["waitSecondsRequested"] = waitSeconds,
                ["waitSecondsUsed"] = (int)budget.TotalSeconds,
                ["lastError"] = last?.Message,
                // "LabVIEW is up" was asserted unconditionally here, and it was wrong exactly when
                // it mattered: with runningProcesses 0 the advice sent the reader after the AI
                // service while the IDE itself was gone.
                ["next"] = stillRunning == 0
                    ? "No LabVIEW process is running any more - it was started and did not stay up. " +
                      "Start LabVIEW by hand and call this tool again; the README's Troubleshooting " +
                      "table has the measurement behind this."
                    : "LabVIEW is up but its AI gRPC service is not answering yet. THIS IS THE " +
                      "NORMAL COLD-START ANSWER, not a fault - CALL THIS TOOL AGAIN and it will " +
                      "almost certainly answer `ready` at once. Measured four times on 2026-09-01: " +
                      "the first call spent its whole 45 s budget and the second answered ready in " +
                      "1.2 s, 12.1 s, 27.7 s and 1.2 s. Do NOT read `lastError: \"The operation " +
                      "was canceled.\"` as a failure; it is this call's own budget expiring. Only " +
                      "if a SECOND call also comes back `starting` is something wrong - the service " +
                      "comes up with LabVIEW itself, about 45 s after a cold start (measured " +
                      "2026-10-17); if it is still missing after about two minutes, ask the user to " +
                      "open Nigel, or use the CLI --ensure-labview for an unattended long wait.",
            }.ToJsonString(Indented);
        });

    // ---- for lvai_restart_labview (fork-only: upstream did not port the restart) ----

    /// <summary>
    /// Poll the service until it answers or <paramref name="token"/> (the budget) runs out. On the
    /// way, start the Nigel chat once if <see cref="NigelChat.ReasonNotToStart"/> has no objection
    /// and at least 15 s of budget are left - the start and its settle time take about 12 s, and a
    /// call that overruns the client's 60 s answers nothing at all.
    /// </summary>
    private async Task<(GetApplicationConfigurationResponse? Config, Exception? Last, JsonObject? Nigel)>
        PollServiceAsync(CancellationToken token, CancellationToken ct, bool startNigelChat,
                         Func<TimeSpan> remaining)
    {
        Exception? last = null;
        JsonObject? nigel = null;

        while (!token.IsCancellationRequested)
        {
            try
            {
                connection.Invalidate();   // the port changes with every LabVIEW start
                var client = await connection.GetClientAsync(token);
                var config = await client.GetApplicationConfigurationAsync(
                    new GetApplicationConfigurationRequest(),
                    deadline: Rpc.Deadline(10), cancellationToken: token);
                return (config, null, nigel);
            }
            // The caller's own cancellation must still escape; only the budget is absorbed.
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                last = e;
            }

            // A report with a state is a start that was MADE; a bare reason may have gone by the
            // next poll (LabVIEW passing 60 s, say), so that one is asked again.
            if (startNigelChat && nigel?["state"] is null && remaining() >= TimeSpan.FromSeconds(15))
                nigel = await MaybeStartNigelChatAsync(ct);

            try { await Task.Delay(2000, token); }
            catch (OperationCanceledException) { /* budget spent - the loop ends below */ }
        }

        return (null, last, nigel);
    }

    /// <summary>The Nigel step: a report of the start made, or of why none was.</summary>
    private static async Task<JsonObject> MaybeStartNigelChatAsync(CancellationToken ct)
    {
        var machine = Win32LabViewMachine.Instance;
        var snapshot = LabViewState.Take(machine);
        if (snapshot.Single is { } one && NigelChat.AttemptFor(one.Id) is { } earlier)
        {
            earlier["note"] = "Started once already for this LabVIEW process; not started again.";
            return earlier;
        }

        if (NigelChat.ReasonNotToStart(snapshot) is { } reason)
            return new JsonObject { ["started"] = false, ["reason"] = reason };

        var labview = snapshot.LabView[0];
        var (target, problem) = NigelChat.Resolve(machine, labview, NigelChat.DefaultAddonRoot());
        if (target is null)
            return new JsonObject { ["started"] = false, ["state"] = "notResolved", ["reason"] = problem };

        return await NigelChat.StartAsync(machine, labview, target,
            (exe, arguments, token) => LabViewLauncher.StartProgramAndConfirmAsync(exe, arguments,
                () => machine.Processes(LabViewState.NigelChatProcess).Select(p => p.Id).ToHashSet(), token),
            ct);
    }


    /// <summary>
    /// Can the service be reached now, with a short deadline - the restart's service guard. Through a
    /// fresh discovery, because a stale channel would answer for the LabVIEW of before.
    /// </summary>
    internal async Task<ServiceProbe> ProbeServiceAsync(CancellationToken ct)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(20));
            connection.Invalidate();
            var client = await connection.GetClientAsync(budget.Token);
            await client.GetApplicationConfigurationAsync(new GetApplicationConfigurationRequest(),
                deadline: Rpc.Deadline(10), cancellationToken: budget.Token);
            return new ServiceProbe(true, connection.Port, null);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return new ServiceProbe(false, null, $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// The restart's start step: the SAME installation that was running when its exe is known (a
    /// LabVIEW 2025 comes back as 2025), else the default pick; the same launch routes as
    /// lvai_ensure_labview; then the service, for longer than one tool call may wait, because the
    /// restart runs as a resumable job. Ownership is NOT recorded here - the restart decides that.
    /// </summary>
    /// <summary>Poll until LabVIEW answers ActiveX's Version, or the token ends; the last problem otherwise.</summary>
    private static async Task<(string? Version, string? LastProblem)> PollActiveXAsync(CancellationToken token)
    {
        string? last = null;
        while (!token.IsCancellationRequested)
        {
            if (LabViewCom.NotReady() is { } notYet) last = notYet;
            else
            {
                try
                {
                    var version = await LabViewCom.WithLabViewAsync(app =>
                        app.GetType().InvokeMember("Version", System.Reflection.BindingFlags.GetProperty,
                            null, app, null, System.Globalization.CultureInfo.InvariantCulture)?.ToString(),
                        10, token);
                    if (version is not null) return (version, null);
                }
                catch (Exception e) when (!token.IsCancellationRequested) { last = e.Message; }
            }
            try { await Task.Delay(2000, token); }
            catch (OperationCanceledException) { break; }
        }
        return (null, last);
    }

    internal async Task<StartResult> StartForRestartAsync(string? exePath, TimeSpan serviceWait,
                                                          CancellationToken ct, bool overCom = false)
    {
        var machine = Win32LabViewMachine.Instance;
        var detail = new JsonObject();
        var clock = Stopwatch.StartNew();
        int? pid = null;

        if (LabViewLocator.RunningInstances().Count == 0)
        {
            var installs = LabViewLocator.Discover();
            var pick = installs.FirstOrDefault(i => string.Equals(i.ExePath, exePath, StringComparison.OrdinalIgnoreCase))
                       ?? LabViewLocator.Select(installs);
            if (pick is null)
            {
                detail["error"] = "No LabVIEW installation found to start.";
                return new StartResult(false, null, null, null, detail);
            }

            detail["exe"] = pick.ExePath;
            detail["sameAsBefore"] = string.Equals(pick.ExePath, exePath, StringComparison.OrdinalIgnoreCase);
            var launch = await LabViewLauncher.StartAndConfirmAsync(pick, ct);
            detail["launchMethod"] = launch.Method;
            if (!launch.Ok)
            {
                detail["error"] = "launch-did-not-survive";
                detail["attempts"] = new JsonArray([.. launch.Attempts.Select(a => (JsonNode)JsonValue.Create(
                    $"{a.Method}: started {a.Started}, appeared {a.Appeared}, survived {a.Survived} ({a.Detail})")!)]);
                return new StartResult(false, null, null, null, detail);
            }
            pid = launch.Pid;
        }
        else
        {
            detail["note"] = "A LabVIEW process was already running after the close; it was used, not a second one started.";
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(serviceWait);
        if (overCom)
        {
            // THE PYLABVIEW ROUTE: ready means LabVIEW answers over ActiveX, past its start-up splash
            // (LabViewCom.NotReady) - not NI's service, which a LabVIEW older than 2025 never has, and
            // no Nigel chat is opened for it.
            var (version, lastProblem) = await PollActiveXAsync(budget.Token);
            ct.ThrowIfCancellationRequested();
            var running = machine.Processes(LabViewState.LabViewProcess);
            var up = pid is { } p ? running.FirstOrDefault(r => r.Id == p) : running.Count == 1 ? running[0] : null;
            detail["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
            detail["readyMeans"] = "LabVIEW answers over ActiveX (pylabview route); NI's service was not waited for";
            detail["labviewVersion"] = version;
            detail["lastError"] = version is null ? lastProblem : null;
            return new StartResult(version is not null, up?.Id ?? pid, up?.StartTime, null, detail);
        }
        var (config, last, nigel) = await PollServiceAsync(budget.Token, ct, startNigelChat: true,
            () => serviceWait - clock.Elapsed);
        ct.ThrowIfCancellationRequested();

        var now = machine.Processes(LabViewState.LabViewProcess);
        var started = pid is { } id ? now.FirstOrDefault(p => p.Id == id) : now.Count == 1 ? now[0] : null;
        detail["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1);
        detail["serviceAnswers"] = config is not null;
        detail["lastError"] = config is null ? last?.Message : null;
        detail["nigelChat"] = nigel;
        return new StartResult(config is not null, started?.Id ?? pid, started?.StartTime,
                               config is not null ? connection.Port : null, detail);
    }

    [McpServerTool(Name = "lvai_list_labview_installations", ReadOnly = true,
                   Title = "List installed LabVIEW versions")]
    [Description("""
        Status: lists the LabVIEW installations on this machine (bitness from the PE header) and
        which one would be started, without starting anything - check it before lvai_ensure_labview.
        """)]
    public static string ListInstallations()
    {
        var installs = LabViewLocator.Discover();
        var (pick, pickPref, pickContext) = LabViewContext.ToStart();
        var running = LabViewLocator.RunningInstances();

        var list = new JsonArray();
        foreach (var i in installs.OrderByDescending(i => i.Release).ThenByDescending(i => i.Is32Bit))
            list.Add(new JsonObject
            {
                ["folder"] = i.FolderName,
                ["release"] = i.Release,
                ["bitness"] = i.Is32Bit ? "32-bit" : "64-bit",
                ["exe"] = i.ExePath,
                ["wouldBeStarted"] = pick is not null && LabViewContext.SamePath(pick.ExePath, i.ExePath),
            });

        // The roots are reported alongside the result: an empty or unexpected list is the
        // signature of a scrubbed environment rather than of a missing LabVIEW.
        var rootsProbed = new JsonArray();
        foreach (var root in LabViewLocator.ProgramFilesRoots())
            rootsProbed.Add(root);

        var runningPaths = new JsonArray();
        foreach (var p in running)
        {
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { /* access denied is fine */ }
            runningPaths.Add(new JsonObject { ["pid"] = p.Id, ["path"] = path });
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["installationCount"] = installs.Count,
            ["rootsProbed"] = rootsProbed,
            ["alreadyRunning"] = runningPaths,
            ["selection"] = pick?.Describe(),
            ["selectionPref"] = pickPref,
            ["selectionSource"] = pickContext.Source,
            ["rule"] = "the running LabVIEW, else \"labviewExe\"/\"labviewPref\" in the settings file, " +
                       "else the LabVIEW last seen running with the -pref it ran with, else the newest " +
                       "installed release (32-bit preferred, NXG excluded)",
            ["labviewContext"] = System.Text.Json.Nodes.JsonNode.Parse(
                System.Text.Json.JsonSerializer.Serialize(pickContext.Describe())),
            ["installations"] = list,
        }.ToJsonString(Indented);
    }

    /// <summary>
    /// Record the LabVIEW this server just started, with its start time (pids are reused), so
    /// lvai_labview_state can say `startedByThisServer`. From the colleague's fork, where the
    /// restart's ownership rule reads it; here only the state tool does.
    /// </summary>
    private static void RecordOwnership(int? pid)
    {
        if (pid is not { } id) return;
        var process = Win32LabViewMachine.Instance.Processes(LabViewState.LabViewProcess)
            .FirstOrDefault(p => p.Id == id);
        if (process is not null) LabViewOwnership.Record(id, process.StartTime);
    }
}
