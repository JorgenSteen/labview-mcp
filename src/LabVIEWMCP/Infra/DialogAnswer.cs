// =============================================================================
// DialogAnswer.cs - answers ONE LabVIEW or Nigel dialog from a short allowlist, only in
// the caller's own LabVIEW: checks, a picture before and after, and the press itself.
// =============================================================================
using System.Text.Json.Nodes;

namespace LabVIEWMcp.Infra;

/// <summary>What lvai_answer_dialog was asked to do.</summary>
internal sealed record DialogAnswerRequest(
    long Handle, string DialogKind, string Action, int? OwnLabviewPid, string? OwnNote,
    string? ListedVis, string? ViName);

/// <summary>
/// The allowlist, and the only code in this server that presses anything in LabVIEW.
///
/// THE RULE IT IMPLEMENTS. Until 2026-10-10 a dialog was always the user's to answer. The user's
/// decision of that day (Q60): "if you are operating labview on your own like starting and
/// stopping then answering these by yourself is fine." So two conditions, both checked here:
/// the LabVIEW is the caller's own (started by THIS server, or the caller names its pid and says
/// its session started it), and the dialog is one of three measured kinds with one safe answer
/// each. Everything else is refused with the screenshot, for the user.
///
/// HOW IT PRESSES. LabVIEW draws its own dialogs: no child buttons, no text to read. Input is
/// POSTED to the dialog window, never through the foreground or the real mouse: Esc for Cancel,
/// and for a named button a left click where that button's picture, cut from a measured
/// screenshot (<see cref="DialogButtons"/>), is found in a fresh screenshot - not found, nothing
/// is pressed. A Win32 message box (Nigel's) gets BM_CLICK on its OK button.
/// </summary>
internal static class DialogAnswer
{
    public const string ExitSaveScratchOnly = "exitSaveScratchOnly";
    public const string ChangedOnDisk = "changedOnDisk";
    public const string NigelBox = "nigelBox";

    public const string DontSaveAll = "Don't Save - All";
    public const string DontSave = "Don't Save";
    public const string Cancel = "Cancel";
    public const string Ok = "OK";

    public const string ExitSaveTitle = "Save changes? (Exit)";

    /// <summary>Each kind and the answers it allows. Cancel changes nothing, so it is always allowed for LabVIEW's.</summary>
    internal static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        // With ONE unsaved VI the prompt says "Don't Save" (measured 2026-10-10); "Don't Save - All"
        // is the several-VI form, allowed but with no measured picture yet, so it presses nothing.
        [ExitSaveScratchOnly] = [DontSave, DontSaveAll, Cancel],
        [ChangedOnDisk] = [Cancel],
        [NigelBox] = [Ok],
    };

    private const int VkEscape = 0x1B;

    public static async Task<JsonObject> RunAsync(DialogAnswerRequest r, ILabViewMachine m,
                                                  Func<int, bool> nigelChatStartedHere, CancellationToken ct)
    {
        var started = m.Now;

        // ---------- the window ----------
        var found = FindWindow(m, r.Handle);
        if (found is not { } hit || !hit.Window.Visible)
            return Error("noSuchDialog", $"No visible window with handle {r.Handle} in a LabVIEW or LVNigelChat " +
                                         "process. Read lvai_labview_state again for the current handle.");
        var (window, process) = hit;
        var before0 = WindowShot.Save(m, window, tag: "asked");

        // ---------- the allowlist ----------
        if (!Allowed.TryGetValue(r.DialogKind ?? "", out var actions))
            return Refuse("notAllowlisted", $"dialogKind '{r.DialogKind}' is not on the allowlist " +
                $"({string.Join(", ", Allowed.Keys)}). The user answers this one.", window, before0);
        if (!actions.Contains(r.Action, StringComparer.Ordinal))
            return Refuse("notAllowlisted", $"action '{r.Action}' is not allowed for {r.DialogKind} " +
                $"(allowed: {string.Join(", ", actions.Select(a => $"'{a}'"))}).", window, before0);

        // ---------- whose ----------
        if (r.DialogKind == NigelBox)
        {
            if (process.Name != LabViewState.NigelChatProcess || !nigelChatStartedHere(process.Id))
                return Refuse("notOwnLabview", $"The window belongs to {process.Name} pid {process.Id}, not to " +
                    "an LVNigelChat this server started. The user answers it.", window, before0);
        }
        else
        {
            if (process.Name != LabViewState.LabViewProcess)
                return Refuse("notAllowlisted", $"{r.DialogKind} is a LabVIEW dialog; this window belongs to " +
                    $"{process.Name}.", window, before0);
            var own = LabViewOwnership.IsServerStarted(process) ||
                      (r.OwnLabviewPid == process.Id && !string.IsNullOrWhiteSpace(r.OwnNote));
            if (!own)
                return Refuse("notOwnLabview", $"LabVIEW pid {process.Id} was not started by this server, and " +
                    "ownLabviewPid with a note saying this session started it was not given for it. A dialog " +
                    "in somebody else's LabVIEW is the user's to answer.", window, before0);
        }

        // ---------- the kind's own conditions ----------
        var snapshot = LabViewState.Take(m);
        if (Mismatch(r, window, process, snapshot, m) is { } why)
            return Refuse("notAllowlisted", why, window, before0);

        // ---------- re-read right before acting ----------
        var again = FindWindow(m, r.Handle);
        if (again is not { } now || !now.Window.Visible || now.Window.Title != window.Title)
            return Error("dialogChanged", "The window changed or went away between the checks and the press; " +
                                          "nothing was pressed.");
        var before = WindowShot.Save(m, now.Window, tag: "before");

        // ---------- the press ----------
        string means;
        bool posted;
        if (r.Action == Cancel)
        {
            posted = m.PostKey(r.Handle, VkEscape);
            means = "Esc posted to the dialog window (WM_KEYDOWN/WM_KEYUP); nothing brought to the front";
        }
        else if (r.Action == Ok)
        {
            var ok = m.Children(r.Handle).FirstOrDefault(c => c.ClassName == "Button" &&
                                                              c.Text.Replace("&", "") == "OK" && c.Handle != 0);
            if (ok is null)
                return Refuse("buttonNotFound", "The message box has no OK button; nothing was pressed.", now.Window, before);
            posted = m.ClickButton(ok.Handle);
            means = $"BM_CLICK posted to the OK button (handle {ok.Handle})";
        }
        else
        {
            var template = DialogButtons.For(r.DialogKind!, r.Action);
            var picture = m.Capture(r.Handle);
            if (template is null || picture is null)
                return Refuse("buttonNotFound", template is null
                    ? $"No measured picture of '{r.Action}' ships with this server; nothing was pressed."
                    : "The dialog could not be captured; nothing was pressed.", now.Window, before);
            var spot = DialogButtons.Find(picture, template);
            if (spot is not { } at)
                return Refuse("buttonNotFound", $"The measured picture of '{r.Action}' was not found in the dialog " +
                    "(another layout, scale or theme?); nothing was pressed.", now.Window, before);
            var (cx, cy) = (at.X + template.Width / 2, at.Y + template.Height / 2);
            posted = m.PostClick(r.Handle, cx, cy);
            means = $"left click posted to the dialog window at ({cx},{cy}) from its corner, the centre of the " +
                    $"'{r.Action}' picture found at ({at.X},{at.Y}); nothing brought to the front";
        }

        // ---------- did it go ----------
        var gone = false;
        for (var i = 0; i < 20 && !gone; i++)
        {
            await m.Delay(TimeSpan.FromMilliseconds(500), ct);
            gone = FindWindow(m, r.Handle) is not { Window.Visible: true };
        }
        bool? exited = null;
        if (gone && r.DialogKind == ExitSaveScratchOnly && r.Action is DontSave or DontSaveAll)
        {
            exited = false;
            for (var i = 0; i < 40 && exited == false; i++)
            {
                if (m.Processes(process.Name).All(p => p.Id != process.Id)) exited = true;
                else await m.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
        }
        var after = gone ? null : WindowShot.Save(m, window, tag: "after");

        return new JsonObject
        {
            ["ok"] = posted && gone,
            ["errorKind"] = !posted ? "postFailed" : gone ? null : "dialogStillThere",
            ["dialogKind"] = r.DialogKind,
            ["title"] = window.Title,
            ["handle"] = r.Handle,
            ["process"] = new JsonObject { ["name"] = process.Name, ["pid"] = process.Id },
            ["pressed"] = r.Action,
            ["means"] = means,
            ["dialogGone"] = gone,
            ["processExited"] = exited,
            ["screenshots"] = new JsonObject { ["asked"] = before0, ["before"] = before, ["after"] = after },
            ["seconds"] = Math.Round((m.Now - started).TotalSeconds, 1),
            ["note"] = gone
                ? null
                : "The dialog is still there after 10 s: the press did not take. Nothing else was tried; read the " +
                  "'after' screenshot - the user decides.",
        };
    }

    /// <summary>Why this window is not the kind the caller named, or null when it is.</summary>
    internal static string? Mismatch(DialogAnswerRequest r, MachineWindow window, MachineProcess process,
                                     LabViewSnapshot snapshot, ILabViewMachine m)
    {
        switch (r.DialogKind)
        {
            case ExitSaveScratchOnly:
            {
                if (window.Title != ExitSaveTitle)
                    return $"exitSaveScratchOnly is the '{ExitSaveTitle}' dialog; this window is titled '{window.Title}'.";
                // Cancel keeps everything as it was: the checks below guard the answer that discards.
                if (r.Action == Cancel) return null;
                var unsaved = snapshot.Unsaved.Where(w => w.Window.ProcessId == process.Id).ToList();
                var users = unsaved.Where(w => !w.IsLabViewMcpScratch).Select(w => w.Window.Title).ToList();
                if (users.Count > 0)
                    return "Unsaved windows that are not labview-mcp scratch VIs are open: " +
                           string.Join("; ", users.Take(5)) + ". Their changes are somebody's work.";
                var listed = SplitList(r.ListedVis);
                if (listed.Count == 0)
                    return "listedVis is empty: pass every VI the dialog lists, as read from its screenshot.";
                var foreign = listed.Where(v => !LabViewState.IsScratchTitle(v)).ToList();
                if (foreign.Count > 0)
                    return "The dialog lists VIs that are not labview-mcp scratch VIs: " + string.Join(", ", foreign) +
                           ". Their changes are somebody's work.";
                return null;
            }
            case ChangedOnDisk:
            {
                if (window.Title.Length != 0 || window.ClassName != "LVDChild")
                    return "changedOnDisk is LabVIEW's UNTITLED 'has changed on disk' modal (class LVDChild); " +
                           $"this window is '{window.Title}' ({window.ClassName}).";
                if (string.IsNullOrWhiteSpace(r.ViName))
                    return "viName is required: the VI this session touched that the dialog names.";
                var classified = snapshot.Windows.FirstOrDefault(w => w.Window.Handle == window.Handle);
                if (classified?.Kind != WindowKinds.PossibleDialog)
                    return "The untitled window is not classified as a possible dialog (not owned by the frame, no " +
                           "disabled frame or Project Explorer): it does not look like the measured modal.";
                return null;
            }
            case NigelBox:
                return window.ClassName == "#32770" ? null
                    : $"nigelBox is a Win32 message box (#32770); this window is {window.ClassName}.";
            default:
                return "unknown kind";
        }
    }

    internal static List<string> SplitList(string? text) =>
        [.. (text ?? "").Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static (MachineWindow Window, MachineProcess Process)? FindWindow(ILabViewMachine m, long handle)
    {
        foreach (var name in new[] { LabViewState.LabViewProcess, LabViewState.NigelChatProcess })
            foreach (var p in m.Processes(name))
                if (m.TopWindows(p.Id).FirstOrDefault(w => w.Handle == handle) is { } w)
                    return (w, p);
        return null;
    }

    private static JsonObject Error(string kind, string message) => new()
    {
        ["ok"] = false, ["errorKind"] = kind, ["error"] = message, ["pressed"] = null,
    };

    private static JsonObject Refuse(string kind, string message, MachineWindow window, string? screenshot) => new()
    {
        ["ok"] = false,
        ["errorKind"] = kind,
        ["error"] = message,
        ["pressed"] = null,
        ["title"] = window.Title,
        ["class"] = window.ClassName,
        ["handle"] = window.Handle,
        ["screenshot"] = screenshot,
        ["next"] = "Nothing was pressed. Show the user the screenshot; the user answers this dialog.",
    };
}
