using System.Text.Json.Nodes;
using Google.Protobuf;
using Grpc.Core;

namespace LabVIEWMcp.Infra;

/// <summary>Shared plumbing so each tool stays a thin, readable wrapper over one RPC.</summary>
internal static class Rpc
{
    /// <summary>
    /// Turn any failure into a JSON result. A tool that throws gives the model an opaque
    /// transport error; a tool that returns {"ok":false,...} gives it something to act on.
    /// </summary>
    public static async Task<string> GuardAsync(Func<Task<string>> body)
    {
        try
        {
            return await body();
        }
        catch (RpcException e)
        {
            var error = Json.Error("rpc", $"{e.StatusCode}: {e.Status.Detail}", new
            {
                statusCode = e.StatusCode.ToString(),
                detail = e.Status.Detail,
                hint = e.StatusCode switch
                {
                    StatusCode.Unavailable =>
                        "LabVIEW's gRPC server is not reachable. Is LabVIEW running?",
                    StatusCode.DeadlineExceeded =>
                        "The RPC exceeded its deadline. Raise timeoutSeconds; note that a cold " +
                        "VI/module load inside LabVIEW can be slow on first touch.",
                    StatusCode.Unimplemented =>
                        "This LabVIEW version does not implement that RPC. Run lvai_dump_schema " +
                        "to see what the running server actually exposes.",
                    _ => null,
                },
            });
            return e.StatusCode == StatusCode.DeadlineExceeded ? WithDialogs(error) : error;
        }
        catch (OperationCanceledException)
        {
            return WithDialogs(Json.Error("cancelled", "The call was cancelled or timed out locally."));
        }
        catch (Exception e)
        {
            return Json.Error(e.GetType().Name, e.Message);
        }
    }

    /// <summary>
    /// LabVIEW's possible dialogs right now, or null - read from the window list, no LabVIEW call.
    /// Replaceable so tests do not read this machine's windows.
    /// </summary>
    internal static Func<JsonArray?> DialogProbe { get; set; } =
        () => LabViewState.ProbeDialogs(Win32LabViewMachine.Instance);

    /// <summary>
    /// A timed-out call names the dialog that may be blocking it. A modal LabVIEW dialog stops the
    /// whole gRPC service until a person answers it, and the timeout alone says nothing about why -
    /// measured more than once in this repository (a "Find the VI" dialog held a bind call for
    /// 720 s). Added only when a dialog is there, so every other answer stays as it was. Read only:
    /// nothing is pressed.
    /// </summary>
    internal static string WithDialogs(string error)
    {
        JsonArray? dialogs;
        try { dialogs = DialogProbe(); }
        catch (Exception failure) when (failure is not OutOfMemoryException) { return error; }
        if (dialogs is null || dialogs.Count == 0) return error;

        try
        {
            if (JsonNode.Parse(error) is not JsonObject answer) return error;
            answer["possibleDialogs"] = dialogs;
            answer["dialogNote"] = "LabVIEW shows a possible dialog. A modal dialog blocks the service until a " +
                                   "person answers it; nothing was pressed. lvai_labview_state names it again.";
            return answer.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch (System.Text.Json.JsonException)
        {
            return error;
        }
    }

    public static DateTime Deadline(int timeoutSeconds) =>
        DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 1, 3600));

    /// <summary>
    /// Longest a tool may block before the MCP CLIENT gives up on it.
    /// Measured, not guessed: a 120 s monitor wait died with "MCP error -32001: Request
    /// timed out" while a 45 s wait returned its own result cleanly. A tool that outlives
    /// the client's patience reports nothing at all, which is strictly worse than reporting
    /// a timeout - so monitor tools clamp to this and the CLI --watch mode exists for
    /// genuinely long waits.
    ///
    /// The ceiling those two measurements bracketed is now known exactly: 60 s. The client's own
    /// MCP log records 28 aborts in this project and every one reads "failed after 60s", across
    /// nine tools - so 45 here has a 15 s margin rather than an unknown one. It is NOT raisable
    /// from .claude/settings.json: MCP_TOOL_TIMEOUT reaches the process environment and the aborts
    /// still land at 60 s. docs/lvclass-creation.md has the evidence.
    /// </summary>
    public const int MaxToolWaitSeconds = 45;

    /// <summary>Clamp a caller-supplied wait to what the MCP client will actually await.</summary>
    public static int ClampToolWait(int timeoutSeconds) =>
        Math.Clamp(timeoutSeconds, 1, MaxToolWaitSeconds);

    /// <summary>
    /// Drain a server stream up to <paramref name="limit"/> messages or until the local
    /// budget expires, whichever comes first. Partial results are a legitimate outcome —
    /// several of these streams are open-ended by design.
    /// </summary>
    public static async Task<(List<T> Items, string StopReason)> CollectAsync<T>(
        IAsyncStreamReader<T> reader, int limit, int timeoutSeconds, CancellationToken ct)
        where T : IMessage
    {
        var items = new List<T>();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 3600)));

        try
        {
            while (items.Count < limit && await reader.MoveNext(budget.Token))
                items.Add(reader.Current);

            return (items, items.Count >= limit ? "limit reached" : "stream completed");
        }
        catch (OperationCanceledException)
        {
            return (items, "timeout");
        }
        catch (RpcException e) when (e.StatusCode is StatusCode.Cancelled or StatusCode.DeadlineExceeded)
        {
            return (items, "timeout");
        }
    }

    /// <summary>Parse an optional protobuf-JSON blob into a request message.</summary>
    public static T ParseJson<T>(string? json) where T : IMessage<T>, new()
    {
        if (string.IsNullOrWhiteSpace(json)) return new T();
        try
        {
            return JsonParser.Default.Parse<T>(json);
        }
        // Two distinct families: InvalidJsonException for a tokenizer failure (malformed JSON),
        // InvalidProtocolBufferException for a schema failure (unknown or mistyped field).
        // Catching only the latter let malformed input escape as an opaque protobuf exception.
        catch (Exception e) when (e is InvalidProtocolBufferException or InvalidJsonException)
        {
            throw new ArgumentException(
                $"Not valid protobuf-JSON for {typeof(T).Name}: {e.Message}", nameof(json));
        }
    }

    /// <summary>Split a comma/newline separated list, trimming blanks.</summary>
    public static string[] SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries |
                                                  StringSplitOptions.TrimEntries);

    /// <summary>
    /// Parse a JSON object into a flat string map, the shape RunVIAsTopLevel wants for
    /// control values. Numbers and booleans are accepted and stringified, so {"X":3} and
    /// {"X":"3"} behave the same — they are the same request on the wire. Note that this is
    /// where the equivalence ends: LabVIEW does NOT coerce the string onto a numeric control,
    /// it fails at Control Value:Set. Only the caller can fix that, by taking the value in as
    /// a string control and converting on the diagram.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>> ParseStringMap(
        string? json, string parameterName = "json")
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json);
        }
        catch (Exception e)
        {
            throw new ArgumentException($"{parameterName} is not valid JSON: {e.Message}", parameterName);
        }

        if (parsed is not JsonObject obj)
            throw new ArgumentException(
                $"{parameterName} must be a JSON object of name -> value.", parameterName);

        return obj.Select(pair =>
            new KeyValuePair<string, string>(pair.Key, pair.Value?.ToString() ?? ""));
    }
}
