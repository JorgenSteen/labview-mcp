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
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [McpServerTool(Name = "lvai_ensure_labview", Destructive = true, OpenWorld = true,
                   Title = "Make sure a LabVIEW instance is running")]
    [Description("""
        Start a LabVIEW instance. If one is already running it is used as-is and nothing is
        started. Otherwise the newest installed LabVIEW is launched, preferring the 32-bit build
        - that is the one hosting the AI gRPC service. No version number is hardcoded; LabVIEW
        NXG is ignored.
        MUTATING: may start an IDE, which is visible to whoever is at the machine.
        The launch is VERIFIED, not assumed: a LabVIEW created as a child of this server is
        killed by the host's job object within about half a second, so the tool tries several
        launch routes and keeps only one whose process is still alive two seconds later. The
        winner is reported as launchMethod; if none survives you get errorKind
        'launch-did-not-survive' listing every attempt, never a hopeful 'starting'.
        THE SERVICE NEEDS NI.NigelLocalService RUNNING, NOT THE NIGEL CHAT. That process
        outlives LabVIEW and the chat (one had run for three days), and while it runs LabVIEW
        alone brings lvai.LVAI up: measured 2026-10-13, LabVIEW.exe started directly with no
        chat at any point, the service's features started 39-44 s later and lvai.LVAI
        answered at 79 s, a ValidateAIXML included; the same on five starts on 2026-10-10. So
        while LabVIEW is still coming up, calling again is right - the second call finds the
        instance and waits. For an unattended long wait use the CLI: LabVIEWMCP --ensure-labview.
        WITHOUT NI.NigelLocalService it does not come up: one sample on 2026-10-10, where the
        service answered only after the chat was opened, which brought the local service up.
        An older LabVIEW, up twenty minutes, served lvai.LVAI on none of 30 listener ports
        until Nigel was opened; whether the local service ran then was not recorded.
        lvai_labview_state shows whether it runs. That is the case for the Nigel chat below.
        NIGEL CHAT (startNigelChat, default on): when LabVIEW has run for over 90 s, its service
        does not answer, no LVNigelChat runs and no possible dialog is up, this starts the chat
        ONCE per LabVIEW process the way LabVIEW's menu does - LVNigelChat.exe --lv-version
        <add-on folder> --lv-bitness x86|x64 - through the same survive-the-job-object launch, and
        reports it under nigelChat (a message box is named, never pressed). The command line is
        the one a LabVIEW skill measured; this tool's port of it is UNMEASURED.
        OWNERSHIP: a LabVIEW this call starts is recorded (pid + start time) as this server's, and
        lvai_restart_labview may close it with no go-ahead. A LabVIEW found running is not.
        """)]
    public async Task<string> EnsureLabViewAsync(
        [Description("How long to wait for the gRPC service to answer, in seconds (capped at 45)")]
        int waitSeconds = 40,
        [Description("""
            Do NOT clear Documents\\LabVIEW Data\\LVAutoSave before starting LabVIEW. It is
            cleared by default because leftover recovery data makes LabVIEW raise a modal
            recovery dialog on start, and a modal dialog stops this gRPC service until a
            human dismisses it. Only ever cleared when this tool starts LabVIEW itself.
            """)]
        bool keepAutoSave = false,
        [Description("""
            Start the Nigel chat once when LabVIEW has run for over 90 s and its service still does
            not answer - the remedy for NI.NigelLocalService not running, which the chat brings
            up. While that runs, the service comes up without it. Never touches a running chat or the
            NI.NigelLocalService process.
            """)]
        bool startNigelChat = true,
        CancellationToken ct = default) =>
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

            var running = LabViewLocator.RunningInstances();
            var startedNow = false;
            string? launched = null;
            LaunchOutcome? launch = null;
            AutoSaveRecovery.Result? autoSave = null;

            if (running.Count == 0)
            {
                // BEFORE the launch. Leftover auto-save data makes LabVIEW raise a modal recovery
                // dialog on start, and a modal dialog stops this very gRPC service until somebody
                // dismisses it - so an unattended start clears the store first. Only when WE start
                // LabVIEW: a process already running owns its own recovery data.
                if (!keepAutoSave) autoSave = AutoSaveRecovery.Clear();

                var installs = LabViewLocator.Discover();
                var pick = LabViewLocator.Select(installs);
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
                launch = await LabViewLauncher.StartAndConfirmAsync(pick, token);
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
                launched = pick.Describe();
                RecordOwnership(launch.Pid);
            }

            // Poll until the service answers. A cold LabVIEW start is far slower than this
            // budget, hence the explicit "call again" outcome rather than a failure.
            var (config, last, nigel) = await PollServiceAsync(token, ct, startNigelChat,
                () => budget - stopwatch.Elapsed);

            if (config is not null)
                return new JsonObject
                {
                    ["ok"] = true,
                    ["state"] = "ready",
                    ["startedByThisCall"] = startedNow,
                    ["launched"] = launched,
                    ["launchMethod"] = launch?.Method,
                    ["autoSaveCleared"] = autoSave?.Describe(),
                    ["port"] = connection.Port,
                    ["discoveredVia"] = connection.DiscoveredVia,
                    ["applicationLanguage"] = config.Language,
                    ["waitedMs"] = stopwatch.ElapsedMilliseconds,
                    ["ownedByThisServer"] = OwnedJson(),
                    ["nigelChat"] = nigel,
                }.ToJsonString(Indented);

            ct.ThrowIfCancellationRequested();

            var stillRunning = LabViewLocator.RunningInstances().Count;
            return new JsonObject
            {
                ["ok"] = false,
                ["state"] = "starting",
                ["startedByThisCall"] = startedNow,
                ["launched"] = launched,
                ["launchMethod"] = launch?.Method,
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
                ["ownedByThisServer"] = OwnedJson(),
                ["nigelChat"] = nigel,
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
                      "if a SECOND call also comes back `starting` is something wrong - then the " +
                      "service has not come up with LabVIEW - check that NI.NigelLocalService " +
                      "runs (lvai_labview_state); opening the Nigel chat brings it up - or use the CLI " +
                      "--ensure-labview for an unattended long wait.",
            }.ToJsonString(Indented);
        });

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

    /// <summary>Record the LabVIEW this server just started, with its start time (pids are reused).</summary>
    private static void RecordOwnership(int? pid)
    {
        if (pid is not { } id) return;
        var process = Win32LabViewMachine.Instance.Processes(LabViewState.LabViewProcess)
            .FirstOrDefault(p => p.Id == id);
        if (process is not null) LabViewOwnership.Record(id, process.StartTime);
    }

    private static JsonObject? OwnedJson() => LabViewOwnership.ToJson();

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
    internal async Task<StartResult> StartForRestartAsync(string? exePath, TimeSpan serviceWait,
                                                          CancellationToken ct)
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
        List the LabVIEW installations found on this machine and which one would be started,
        without starting anything. Bitness is read from each executable's PE header rather than
        guessed from its folder. Useful to check the choice before calling lvai_ensure_labview.
        """)]
    public static string ListInstallations()
    {
        var installs = LabViewLocator.Discover();
        var pick = LabViewLocator.Select(installs);
        var running = LabViewLocator.RunningInstances();

        var list = new JsonArray();
        foreach (var i in installs.OrderByDescending(i => i.Release).ThenByDescending(i => i.Is32Bit))
            list.Add(new JsonObject
            {
                ["folder"] = i.FolderName,
                ["release"] = i.Release,
                ["bitness"] = i.Is32Bit ? "32-bit" : "64-bit",
                ["exe"] = i.ExePath,
                ["wouldBeStarted"] = pick is not null && pick.ExePath == i.ExePath,
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
            ["rule"] = "newest release first, 32-bit preferred within a release, NXG excluded",
            ["installations"] = list,
        }.ToJsonString(Indented);
    }
}
