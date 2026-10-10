// =============================================================================
// NigelChat.cs - starts the Nigel chat (LVNigelChat.exe --lv-version --lv-bitness)
// when LabVIEW runs and its AI service does not answer; once, and never by any other route.
// =============================================================================
using System.Text.Json.Nodes;
using LabVIEWMcp.Grpc;

namespace LabVIEWMcp.Infra;

/// <summary>The chat executable and the two arguments LabVIEW's own menu plug-in passes it.</summary>
internal sealed record NigelChatTarget(string Exe, string Version, string Bitness)
{
    public string Arguments => $"--lv-version {Version} --lv-bitness {Bitness}";
}

/// <summary>
/// Opening the Nigel chat without a person - the port of the skill's `lv_start_nigel_chat.ps1`.
///
/// WHY. The `LV AI gRPC Service` starts with Nigel, not with the IDE (measured; `lvai_ensure_labview`'s
/// description has the twenty-minute LabVIEW that served lvai.LVAI on none of its 30 ports). The skill
/// measured the remedy: start `LVNigelChat.exe` the way LabVIEW's menu does, with BOTH
/// `--lv-version &lt;add-on folder&gt;` and `--lv-bitness x86|x64`. Without both it never exits and
/// leaves a modal "Object reference not set to an instance of an object." box - so there is no
/// fallback that starts it some other way, and a message box after the start is REPORTED, never
/// pressed.
///
/// WHEN. Only for a LabVIEW that has been up longer than a cold start takes (measured 32-40 s, so
/// <see cref="MinimumLabViewUpSeconds"/>), whose service does not answer, with no chat running and no
/// possible dialog (a dialog blocks the service and a chat does not unblock it), and only once per
/// LabVIEW process. NI.NigelLocalService and a running chat are never touched.
/// </summary>
internal static class NigelChat
{
    /// <summary>Past the measured 32-40 s cold start, with margin; a reading aid, not a measured boundary.</summary>
    public const int MinimumLabViewUpSeconds = 60;

    /// <summary>The kit waits 10 s before it reads the chat's windows; two of them go to the survive check.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(8);

    private static readonly object Gate = new();
    private static readonly Dictionary<int, JsonObject> Attempts = [];

    /// <summary>The add-on root, <c>%ProgramFiles%\NI\LVAddons\lvai</c> (the 64-bit one even for 32-bit LabVIEW).</summary>
    public static string? DefaultAddonRoot() => LvaiVersion.DefaultRoot();

    /// <summary>
    /// Which chat to start for this LabVIEW: the add-on folder named after LabVIEW.exe's file version
    /// (`26.3.1f1` -> `26.3`, the folder the skill measured for LabVIEW 2026 Q3), and the bitness from
    /// the executable's PE header. The version rule is DERIVED from that one station, where folders
    /// `1`, `26.1` and `26.3` coexist; a folder that does not exist is refused rather than guessed.
    /// </summary>
    public static (NigelChatTarget? Target, string? Problem) Resolve(
        ILabViewMachine machine, MachineProcess labview, string? addonRoot)
    {
        if (labview.Path is not { Length: > 0 } exePath)
            return (null, "LabVIEW.exe's path could not be read, so its version and bitness are unknown.");
        if (addonRoot is null)
            return (null, "No NI\\LVAddons\\lvai folder under Program Files: the AI add-on is not installed.");
        if (machine.FileVersion(exePath) is not { } version)
            return (null, $"The file version of '{exePath}' could not be read.");

        var is32 = LabViewLocator.ReadIs32Bit(exePath)
                   ?? exePath.Contains("(x86)", StringComparison.OrdinalIgnoreCase);
        var folder = $"{version.Major}.{version.Minor}";
        var exe = Path.Combine(addonRoot, folder, "resource", "AI", "LVNigelChat", "LVNigelChat.exe");
        if (!machine.FileExists(exe))
            return (null, $"No chat at '{exe}'. Add-on versions present: " +
                          string.Join(", ", machine.SubdirectoryNames(addonRoot)) + ".");
        return (new NigelChatTarget(exe, folder, is32 ? "x86" : "x64"), null);
    }

    /// <summary>Why the chat is NOT to be started now, or null when it is.</summary>
    public static string? ReasonNotToStart(LabViewSnapshot snapshot)
    {
        if (snapshot.LabView.Count == 0) return "no LabVIEW process";
        if (snapshot.LabView.Count > 1) return $"{snapshot.LabView.Count} LabVIEW processes - which one is meant is the user's call";
        if (snapshot.NigelChat.Count > 0) return $"LVNigelChat already runs (pid {snapshot.NigelChat[0].Id})";
        if (snapshot.Dialogs.Count > 0)
            return "a possible dialog is up, and a modal dialog blocks the service whatever Nigel does - a person answers it";
        var labview = snapshot.LabView[0];
        if (labview.StartTime is not { } start) return "LabVIEW's start time could not be read";
        var up = (snapshot.At - start).TotalSeconds;
        if (up < MinimumLabViewUpSeconds)
            return $"LabVIEW is {up:F0} s old and a cold start takes 32-40 s - the service may still come up by itself";
        lock (Gate)
            if (Attempts.ContainsKey(labview.Id)) return "already started once for this LabVIEW process";
        return null;
    }

    /// <summary>The attempt made for this LabVIEW process, if any - so a later answer can report it.</summary>
    public static JsonObject? AttemptFor(int labviewPid)
    {
        lock (Gate) return Attempts.TryGetValue(labviewPid, out var o) ? (JsonObject)o.DeepClone() : null;
    }

    internal static void Reset()
    {
        lock (Gate) Attempts.Clear();
    }

    /// <summary>
    /// Start the chat once for <paramref name="labview"/> and report what came up. Never presses
    /// anything: a message box is named, with its text, for the user.
    /// </summary>
    public static async Task<JsonObject> StartAsync(
        ILabViewMachine machine, MachineProcess labview, NigelChatTarget target,
        Func<string, string, CancellationToken, Task<LaunchOutcome>> launch, CancellationToken ct)
    {
        var report = new JsonObject
        {
            ["exe"] = target.Exe,
            ["arguments"] = target.Arguments,
            ["forLabViewPid"] = labview.Id,
            ["startedAt"] = machine.Now.ToString("HH:mm:ss"),
        };
        // Recorded BEFORE the launch: "once" has to hold even when the launch throws.
        Store(labview.Id, report);

        var outcome = await launch(target.Exe, target.Arguments, ct);
        report["launchMethod"] = outcome.Method;
        report["attempts"] = new JsonArray([.. outcome.Attempts.Select(a => (JsonNode)new JsonObject
        {
            ["method"] = a.Method, ["started"] = a.Started, ["appeared"] = a.Appeared,
            ["survived"] = a.Survived, ["detail"] = a.Detail,
        })]);
        if (!outcome.Ok || outcome.Pid is not { } pid)
        {
            report["state"] = "didNotSurvive";
            Store(labview.Id, report);
            return report;
        }

        report["pid"] = pid;
        await machine.Delay(SettleTime, ct);

        if (machine.Processes(LabViewState.NigelChatProcess).All(p => p.Id != pid))
        {
            report["state"] = "exited";
            report["note"] = "The chat exited within seconds of the start. Report it; it is not started again.";
            Store(labview.Id, report);
            return report;
        }

        var visible = machine.TopWindows(pid).Where(w => w.Visible).ToList();
        var boxes = visible.Where(w => w.ClassName == "#32770").ToList();
        if (boxes.Count > 0)
        {
            report["state"] = "messageBox";
            report["messageBoxes"] = new JsonArray([.. boxes.Select(b => (JsonNode)LabViewState.DescribeDialog(
                machine, new ClassifiedWindow(b, WindowKinds.PossibleDialog)))]);
            report["note"] = "The chat shows a message box. It is the user's to read and answer; nothing was pressed.";
        }
        else
        {
            report["state"] = visible.Count > 0 ? "running" : "runningNoWindowYet";
            report["windows"] = new JsonArray([.. visible.Select(w => (JsonNode)JsonValue.Create(w.Title)!)]);
        }
        Store(labview.Id, report);
        return report;
    }

    private static void Store(int labviewPid, JsonObject report)
    {
        // A copy: the caller puts the returned object into its answer, and a JsonNode has one parent.
        lock (Gate) Attempts[labviewPid] = (JsonObject)report.DeepClone();
    }
}
