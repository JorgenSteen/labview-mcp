// =============================================================================
// RunningViTools.cs - lvai_running_vi: start a VI and return, then read, set, press
// or signal its controls and stop it, over several calls - in LabVIEW's main instance.
// =============================================================================
using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LabVIEWMcp.Infra;
using ModelContextProtocol.Server;

namespace LabVIEWMcp.Tools;

/// <summary>
/// A VI that keeps running between calls. Both field projects built a helper family for exactly
/// this (one: launch, signal and snapshot helpers driven over ActiveX; the other: a helper VI
/// per run to press, set and read a DQMH tester), because lvai_run_vi_and_read_values runs a VI and
/// ends it within ONE call: an application, a tester or a Run VI that is started once and then
/// operated could not be driven. And a helper run through NI's service runs in the add-on's
/// application instance, where a VI running in LabVIEW's main instance is not found (error 1004,
/// measured in one of them). So everything here goes over ActiveX, into the main instance.
/// </summary>
[McpServerToolType]
internal sealed class RunningViTools
{
    internal const string SignalHelperFileName = "lvai_signal_running.xml";

    internal static readonly string[] Actions = ["start", "read", "set", "press", "signal", "stop"];

    [McpServerTool(Name = "lvai_running_vi", Destructive = true, OpenWorld = true,
                   Title = "Start a VI, drive it while it runs, stop it")]
    [Description("""
        Run: a VI that KEEPS RUNNING between calls, in LabVIEW's main instance over ActiveX (no NI
        service). MUTATING: the VI's side effects happen. One action per call:
        start - set valuesJson's controls, open the front panel (it keeps the VI in memory), run
        WITHOUT waiting, return. A broken VI is not run; one already running is left as it is.
        read - the execution state and values; `controls` names them, one per line, else every
        control and indicator of the panel (read from the file).
        set - valuesJson's controls with SetControlValue: NO Value Change event fires.
        press - each boolean in `controls` written TRUE with Value (Signaling): its Value Change
        event fires. A LATCHED boolean cannot be written by VI Server (error 1193, named).
        signal - signalsJson, a JSON array of {"control":..,"value":..} in order, each written with
        Value (Signaling) (a boolean TRUE/1, a numeric its number, a string its text, else LabVIEW
        XML), signalGapMs between.
        stop - press `stopControl` if given, wait up to waitMs for the VI to end, then ABORT it if
        it still runs (abort: false to leave it) - Abort runs no cleanup - and close its panel.
        For a VI that runs to its end, use lvai_run_vi_and_read_values.
        """)]
    public static async Task<string> RunningViAsync(
        [Description("Absolute path of the VI")] string viPath,
        [Description("start, read, set, press, signal or stop")] string action,
        [Description("start and set: {\"control\":\"value\",...}, values as text, converted to each control's type")]
        string? valuesJson = null,
        [Description("signal: a JSON array of {\"control\":..,\"value\":..}, fired in order")] string? signalsJson = null,
        [Description("press: the booleans to press; read: the controls/indicators to read - one per line")]
        string? controls = null,
        [Description("stop: a boolean to press first, so the VI ends through its own code")] string? stopControl = null,
        [Description("stop: how long to wait for the VI to end before aborting, in ms (default 5000)")]
        int waitMs = 5000,
        [Description("stop: abort the VI if it has not ended after waitMs (default true)")] bool abort = true,
        [Description("start: open the front panel (default true); stop: close it once the VI ended (default true)")]
        bool panel = true,
        [Description("press and signal: wait after each write, in ms (default 200)")] int signalGapMs = 200,
        [Description("Local budget in seconds")] int timeoutSeconds = 60,
        CancellationToken ct = default) =>
        await Rpc.GuardAsync(async () =>
        {
            var act = action?.Trim().ToLowerInvariant() ?? "";
            if (!Actions.Contains(act))
                return Json.Error("badArguments", $"action '{action}' is not one of {string.Join(", ", Actions)}.");
            var full = Path.GetFullPath(viPath);
            if (!LlbPath.Exists(full))
                return Json.Error("viNotFound", $"No file at '{full}'.", new { viPath = full });
            if (LabViewCom.NotReady() is { } notReady)
                return Json.Error("labviewNotRunning", notReady, new { viPath = full });

            try
            {
                return act switch
                {
                    "start" => await StartAsync(full, valuesJson, panel, timeoutSeconds, ct),
                    "read" => await ReadAsync(full, controls, timeoutSeconds, ct),
                    "set" => await SetAsync(full, valuesJson, timeoutSeconds, ct),
                    "press" => await SignalAsync(full, act, [.. Lines(controls)
                        .Select(c => KeyValuePair.Create(c, "TRUE"))], signalGapMs, timeoutSeconds, ct),
                    "signal" => await SignalAsync(full, act, RunTools.ParseSignals(signalsJson),
                                                  signalGapMs, timeoutSeconds, ct),
                    _ => await StopAsync(full, stopControl, waitMs, abort, panel, signalGapMs, timeoutSeconds, ct),
                };
            }
            catch (ArgumentException bad)
            {
                return Json.Error("badArguments", bad.Message, new { viPath = full, action = act });
            }
            catch (LabViewCom.ComException e)
            {
                return Json.Error(e.Kind, e.Message, new { viPath = full, action = act });
            }
        });

    // ------------------------------------------------------------------ the actions

    private static async Task<string> StartAsync(string vi, string? valuesJson, bool openPanel,
                                                 int timeoutSeconds, CancellationToken ct)
    {
        var inputs = await TypedInputsAsync(vi, valuesJson, timeoutSeconds, ct);
        var start = await LabViewCom.StartAsync(vi, inputs, openPanel, timeoutSeconds, ct);
        var running = start.StateAfter is 2 or 3;
        return Answer(vi, "start", start.StateAfter, running && start.SetProblems.Count == 0, new JsonObject
        {
            ["alreadyRunning"] = start.AlreadyRunning,
            ["panelOpen"] = start.PanelOpen,
            ["setProblems"] = Strings(start.SetProblems),
            ["note"] = start.StateBefore == 0 ? "The VI is broken (execState 0), so it was not run."
                : start.SetProblems.Count > 0 ? "An input did not take, so the VI was not started."
                : start.AlreadyRunning ? "The VI was already running; it was left as it is and its inputs were not set."
                : running ? "Running. Drive it with read, set, press or signal; end it with stop."
                : "The VI was run and is no longer running - it ended within 300 ms (a VI that runs to its end " +
                  "is lvai_run_vi_and_read_values' job).",
        });
    }

    private static async Task<string> ReadAsync(string vi, string? controls, int timeoutSeconds, CancellationToken ct)
    {
        var names = Lines(controls).ToList();
        if (names.Count == 0)
        {
            if (AixmlComposer.Locate(out var missing) is not { } setup)
                return Json.Error("backendNotAvailable", missing + " Name the controls to read in `controls`.");
            var (panel, problem) = await AixmlComposer.ReadPanelAsync(setup, vi, timeoutSeconds, ct);
            if (panel is null)
                return Json.Error("panelNotRead", "The panel could not be listed from the file (" + problem +
                                                  "); name the controls to read in `controls`.", new { viPath = vi });
            names = [.. panel.Select(p => p.Name).Distinct()];
        }
        var (state, values) = await LabViewCom.ReadRunningAsync(vi, names, timeoutSeconds, ct);
        var read = new JsonObject();
        var problems = new JsonObject();
        foreach (var value in values)
        {
            if (value.Problem is { } p) problems[value.Name] = p;
            else read[value.Name] = value.Value;
        }
        return Answer(vi, "read", state, problems.Count == 0, new JsonObject
        {
            ["values"] = read,
            ["problems"] = problems.Count == 0 ? null : problems,
            ["note"] = state is 2 or 3 ? "Read while the VI runs: a snapshot."
                : "The VI is not running; these are its panel's values - or, when it had left memory (a stop that " +
                  "closed its panel), the values saved in the file.",
        });
    }

    private static async Task<string> SetAsync(string vi, string? valuesJson, int timeoutSeconds, CancellationToken ct)
    {
        var inputs = await TypedInputsAsync(vi, valuesJson, timeoutSeconds, ct);
        if (inputs.Count == 0) throw new ArgumentException("set needs valuesJson: {\"control\":\"value\",...}.");
        var (state, problems) = await LabViewCom.SetRunningAsync(vi, inputs, timeoutSeconds, ct);
        return Answer(vi, "set", state, problems.Count == 0, new JsonObject
        {
            ["set"] = Strings([.. inputs.Select(i => i.Name)]),
            ["setProblems"] = Strings(problems),
            ["note"] = "Set with SetControlValue, which fires no Value Change event: an event structure does not " +
                       "see it. Use press or signal to fire the event.",
        });
    }

    private static async Task<string> SignalAsync(string vi, string act, IReadOnlyList<KeyValuePair<string, string>> signals,
                                                  int gapMs, int timeoutSeconds, CancellationToken ct)
    {
        if (signals.Count == 0)
            throw new ArgumentException(act == "press"
                ? "press needs `controls`: the booleans to press, one per line."
                : "signal needs signalsJson: [{\"control\":..,\"value\":..}].");
        var (stateBefore, _) = await LabViewCom.ReadRunningAsync(vi, [], timeoutSeconds, ct);
        var (helper, problem) = await ComposedHelpers.EnsureAsync(SignalHelperFileName, timeoutSeconds, ct);
        if (helper is null)
            return Json.Error("helperNotComposed", $"The {SignalHelperFileName} helper could not be composed: {problem}",
                              new { viPath = vi });
        // ONE HELPER RUN PER SIGNAL, so the answer can say which write took. The helper chains its
        // error, so after a refused write nothing more is written - but its error names no control,
        // and `signalled` used to list every REQUESTED write: measured 2026-11-13, a press on a
        // latched OK answered 1193 with `signalled: ["OK = TRUE"]`. The gap is still waited after
        // each write, inside the helper, as before.
        var gap = Math.Clamp(gapMs, 0, 60_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var written = new List<string>();
        int? code = 0;
        string? failed = null, errorXml = null;
        foreach (var (name, value) in signals)
        {
            var outputs = await LabViewCom.RunHelperAsync(helper, new Dictionary<string, string>
            {
                ["VI Path"] = vi,
                ["Signal Names"] = name,
                ["Signal Values"] = value,
                ["signal gap ms"] = gap,
            }, ["error xml"], timeoutSeconds, ct);
            code = ErrorCode(outputs.GetValueOrDefault("error xml"));
            if (code != 0)
            {
                failed = $"{name} = {value}";
                errorXml = outputs.GetValueOrDefault("error xml");
                break;
            }
            written.Add($"{name} = {value}");
        }
        var notSent = signals.Skip(written.Count + 1).Select(s => $"{s.Key} = {s.Value}").ToList();
        var (state, _) = await LabViewCom.ReadRunningAsync(vi, [], timeoutSeconds, ct);
        var wasRunning = stateBefore is 2 or 3;
        return Answer(vi, act, state, code == 0, new JsonObject
        {
            ["signalled"] = Strings(written),
            ["failed"] = failed,
            ["notSent"] = failed is null ? null : Strings(notSent),
            ["errorCode"] = code,
            ["wasRunning"] = wasRunning,
            ["note"] = code switch
            {
                0 => "Written with Value (Signaling): each control's Value Change event fired." +
                     (!wasRunning ? " The VI was not running, so nothing handled them."
                      : state is 2 or 3 ? "" : " The VI ENDED after it - it handled the write."),
                1193 => "Error 1193: `failed` is a LATCHED boolean - its mechanical action is latch, and VI Server " +
                        "cannot write a latch (Value and Value (Signaling) both refuse). `signalled` took; `notSent` " +
                        "was not written. Make the control switch-action, or stop the VI with stop.",
                null => "The helper's error could not be read; see its answer. `signalled` took.",
                _ => $"The helper answered error {code} on `failed`: a name that is not a control on the panel, or " +
                     "a value that does not convert to its type. `signalled` took; `notSent` was not written.",
            },
            ["helperError"] = errorXml,
        });
    }

    private static async Task<string> StopAsync(string vi, string? stopControl, int waitMs, bool abort, bool closePanel,
                                                int gapMs, int timeoutSeconds, CancellationToken ct)
    {
        var (stateBefore, _) = await LabViewCom.ReadRunningAsync(vi, [], timeoutSeconds, ct);
        JsonNode? pressed = null;
        if (stopControl is { Length: > 0 } && stateBefore is 2 or 3)
            pressed = JsonNode.Parse(await SignalAsync(vi, "press", [KeyValuePair.Create(stopControl, "TRUE")],
                                                       gapMs, timeoutSeconds, ct));
        var stop = await LabViewCom.StopAsync(vi, Math.Clamp(waitMs, 0, Math.Max(0, timeoutSeconds * 1000 - 5000)),
                                              abort, closePanel, timeoutSeconds, ct);
        var ended = stop.StateAfter is not (2 or 3);
        var wasRunning = stateBefore is 2 or 3;
        return Answer(vi, "stop", stop.StateAfter, ended, new JsonObject
        {
            ["wasRunning"] = wasRunning,
            ["pressedStop"] = pressed,
            ["aborted"] = stop.Aborted,
            ["waitedMs"] = stop.WaitedMs,
            ["panelClosed"] = stop.PanelClosed,
            ["note"] = (!wasRunning ? "The VI was not running."
                : stop.Aborted ? "The VI did not end within waitMs and was ABORTED: no cleanup it may hold ran."
                : ended ? (pressed is null ? "The VI ended on its own." : $"The VI ended after '{stopControl}' was pressed.")
                : "The VI still runs (abort was false).") +
                (stop.PanelClosed ? " Its panel was closed, which lets an idle VI leave memory: a later read shows " +
                                    "the values saved in the file, not the ones it ended with." : ""),
        });
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>valuesJson converted to each control's type, read from the file's panel.</summary>
    private static async Task<List<LabViewCom.Input>> TypedInputsAsync(string vi, string? valuesJson,
                                                                       int timeoutSeconds, CancellationToken ct)
    {
        var pairs = Rpc.ParseStringMap(valuesJson, nameof(valuesJson)).ToList();
        if (pairs.Count == 0) return [];
        if (AixmlComposer.Locate(out var missing) is not { } setup)
            throw new ArgumentException(missing);
        var (panel, problem) = await AixmlComposer.ReadPanelAsync(setup, vi, timeoutSeconds, ct);
        if (panel is null) throw new ArgumentException("The panel could not be read from the file: " + problem);
        var inputs = new List<LabViewCom.Input>();
        foreach (var (name, text) in pairs)
        {
            var control = panel.FirstOrDefault(i => !i.Indicator && i.Name == name)
                ?? throw new ArgumentException($"'{name}' is not a control on this VI's panel. Controls: " +
                                               string.Join(", ", panel.Where(i => !i.Indicator).Select(i => i.Name)));
            var value = ComValues.FromText(text, control.Type, out var why)
                ?? throw new ArgumentException($"'{name}' ({control.Type}): {why}");
            inputs.Add(new LabViewCom.Input(name, value, text));
        }
        return inputs;
    }

    /// <summary>The code of a flattened error cluster: its `code` element's value; null when unreadable.</summary>
    internal static int? ErrorCode(string? errorXml)
    {
        if (string.IsNullOrEmpty(errorXml)) return null;
        var m = Regex.Match(errorXml, @"<Name>code</Name>\s*<Val>(-?\d+)</Val>");
        return m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    /// <summary>One name per line - a control label may hold a comma, so commas do not split.</summary>
    internal static IEnumerable<string> Lines(string? text) =>
        (text ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    private static JsonArray Strings(IEnumerable<string> items) =>
        new([.. items.Select(s => (JsonNode)JsonValue.Create(s)!)]);

    private static string Answer(string vi, string action, int state, bool ok, JsonObject extra)
    {
        var answer = new JsonObject
        {
            ["ok"] = ok,
            ["action"] = action,
            ["viPath"] = vi,
            ["execState"] = state,
            ["running"] = state is 2 or 3,
            ["meaning"] = ExecStateTools.Meaning(state),
            ["via"] = "ActiveX, LabVIEW's main instance",
        };
        foreach (var (key, value) in extra)
            if (value is not null) answer[key] = value.DeepClone();
        return Json.Document(answer);
    }
}
