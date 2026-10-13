# Closing and restarting LabVIEW under guards

`lvai_restart_labview`, `lvai_labview_state`, `lvai_vis_in_memory`, `lvai_answer_dialog`, and the
Nigel-chat start in `lvai_ensure_labview`. The route itself was measured by a LabVIEW skill on a production Actor
Framework station in October 2026 (labview-mcp 1.8.5, LabVIEW 2026 Q3 32-bit). The C# port in this
repository first ran against LabVIEW on 2026-10-10, with real closes and restarts. Section 11
separates what is measured from what is not, and section 12 is the checklist with its results.

## 1. Why tools

A restart is the remedy for two things nothing else cures: an actor or helper loop that VI Server
cannot stop (`Abort VI` answers 1000), and a class held in memory (`Error 1562` on one class with
nothing visibly locked). LabVIEW is also the user's IDE. A close that discards somebody's unsaved
edits, or answers their dialog, is worse than the hang it was meant to cure. The skill's route
protects against that with a page of guards and four PowerShell scripts. Every step that needs no
LabVIEW is a Win32 or process read, so it now lives in the server. There it runs the same way each
time, and it is unit-tested on a fake machine (`ILabViewMachine`, `tests/.../FakeLabViewMachine.cs`).

## 2. Who may decide: the ownership rule

The user's rule, not configurable:

| LabVIEW | close / restart |
|---|---|
| started by THIS server process (`lvai_ensure_labview` recorded its pid and start time) | no go-ahead needed |
| any other, but **empty** | no go-ahead needed |
| any other | only with `userGoAhead`: the user's own words, from this conversation, echoed in the answer |
| a kill | never. The tool has no kill. |

**Empty** means all of these hold: the window list shows only LabVIEW's frame and system windows
(no VI, project or dialog window, no `*` title), `LVAutoSave` holds no file, two CPU samples are
quiet, and `lvai_vis_in_memory`'s helper finds nothing in memory but NI's service, LabVIEW's start
window and labview-mcp's helpers. If a check cannot run, LabVIEW counts as not empty. The answer's
`emptyChecks` says which check decided.

The record is in memory on purpose. A server that restarts forgets, and forgetting errs towards
asking. A restart does not change ownership: the user's LabVIEW, restarted, is still the user's.

## 3. The guards, in order

| # | guard (`stoppedBy`) | read from | a go-ahead opens it? |
|---|---|---|---|
| 1 | `processes`: none, or more than one LabVIEW | process list | **no**: which one is meant is the user's call |
| 2 | `dialog`: a VISIBLE possible dialog (section 4) | window list, before and after the CPU samples, and again right before the close | **no**: the user answers it |
| 3 | `busy`: either of two 15 s CPU samples is busy, or the clock cannot be read | `TotalProcessorTime` | **no**: a busy LabVIEW does not take the close message |
| 4 | `unsaved`: a `*` title, including labview-mcp's own scratch VIs (section 4) | window list | yes |
| 4a | `hiddenWindows`: a HIDDEN titled window no measurement explains (section 4) | window list, also right before the close | yes. The answer names each window it opened (`overriddenHiddenWindows`) |
| 5 | `autoSave`: files in `LVAutoSave`, except LabVIEW's recovery copies of this session's ACTIVE project (`guards.ownProjectAutoSave`; the close saves it; the user's decision of 2026-10-10, naming from one sample) | the Documents known folder, which may be redirected | yes |
| 6 | `service`: lvai does not answer (20 s probe) | `GetApplicationConfiguration` | yes. Project and memory are then not read |
| 7 | `activeProject`: a project is active or open that is not `projectPath` | `lvai_active_project` helper, project windows | yes. The IDE close closes it; this tool never closes a user's project itself |
| 8 | `memory`: a VI in memory has nonzero modification bits | `lvai_vis_modified` helper | yes |
| 9 | `ownership`: not this server's and not empty | sections 2 and 6 | yes |

The tool reports every reason that needs a go-ahead under `needsGoAheadFor`, and `stoppedBy` names
the first one. The order follows the skill: nothing changes before guards 1–7 have passed. When
`projectPath` is this session's project and it is the ACTIVE one, the tool saves and closes it
(`lvai_close_active_project`) before the memory check, because the memory probe would otherwise be
adopted into it. That close is reported as a side effect. The skill notes that it can rewrite
`.lvproj`, `.lvlps` and `.aliases`, so check the project's tree afterwards. This session's own Project
Explorer with a `*` title is not counted by the `unsaved` guard when its project is the active one,
because that close saves it. Measured 2026-10-10: a project opened by `lvai_open_file` showed
`Probe.lvproj * - Project Explorer` before any edit. When it is not the active project, nothing
saves it, so it still needs a go-ahead (`guards.ownProjectUnsaved` says which case applied).

Two helpers are never generated inside the restart: `lvai_vis_modified` and `lvai_active_project`.
Generating a helper validates it, and validation leaves an `LVMCP Validate` VI in memory
(`docs/scratch-vis-in-memory.md`), which can raise the save prompt at the very close it precedes. If
a helper is missing, its check is reported as not run, and LabVIEW then does not count as empty. Call
`lvai_vis_in_memory` once at the start of a session.

## 4. The window classifier, and the dialog that says it is invisible

A port of the skill's `lv_windows.ps1`, with one deliberate change. The script flags only VISIBLE
windows as possible dialogs. Measured 2026-10-06 (`docs/scratch-vis-in-memory.md` §2a), LabVIEW's
`Error list` and `Externally Changed Files List` dialogs were on screen, could not be clicked, and
reported `IsWindowVisible` FALSE. So a hidden titled window is not ignored here. A VISIBLE titled
window is a possible dialog (`possible-dialog`); a HIDDEN one is `hidden-unrecognised`, unless:

- it is hidden and its title is one of LabVIEW's measured hidden windows: `Context Help`, `Tools`,
  `Set Title`, `RunService.vi`, `Closed`, `Window monitor`;
- it is hidden and its CLASS is one of the measured non-LabVIEW classes found in `LabVIEW.exe`:
  `OleAutomationWnd`, `HwndWrapper[…`, `GDI+ Hook Window Class`, `LVWinSessionMonitor`,
  `OleDdeWndClass`, `IME`, `MSCTFIME UI`, `ATL:…`, `Internet Explorer_Hidden`. These come from one
  `-All` listing of a production LabVIEW, 2026-10-07. `CiceroUIWndFrame` (Windows' text services)
  and `NI-RPC Message Thread Class` were added from a listing of 2026-10-10;
- its title is `LabVIEW`, `Getting Started…`, `….lvproj - Project Explorer`, a VI or control title
  (`.vi`, `.vim`, `.vit`, `.ctl`, `.ctt`, with or without `Front Panel` / `Block Diagram` /
  `on <project>/<target>`), a never-saved new VI (`Untitled 7 Front Panel`, `Untitled 7 Block
  Diagram`), or one of the service's own (`LV AI ….lvlibp:`). The `Untitled N` rule was added
  after a measurement on 2026-10-10: File > New VI gave those two visible titled windows with no
  `*`, and they were flagged as possible dialogs, a hard stop that no go-ahead opens.

**The untitled modal dialog, measured 2026-10-10.** LabVIEW's "… has changed on disk" prompt
([Revert] [Cancel]) was a VISIBLE but UNTITLED `LVDChild` window of about 281x189, owned by the
LabVIEW frame, and the frame and the Project Explorer were disabled. By its title alone it was
`untitled`, which counts as empty, so a close would have run into a modal dialog. Each window now
carries its owner (`GetWindow GW_OWNER`) and `IsWindowEnabled`, and a second pass over the list
(`LabViewState.ClassifyAll`) makes a visible window a `possible-dialog`, with a `reason`, when:

- it is untitled and a LabVIEW frame window (title `LabVIEW`) owns it; or
- it is untitled and a visible frame or Project Explorer is disabled, which is what a modal dialog
  does to the windows it blocks; or
- it is an ENABLED VI or start window while a visible frame or Project Explorer is disabled. A
  dialog VI titled after its file looks like any panel.

Visible windows of the known helper classes stay out (`Internet Explorer_Hidden` is visible and
untitled on the measured station). Owner alone is not used for titled windows. On a fresh LabVIEW
(2026-10-10) every `LVDChild` window, the Project Explorer and the hidden `Tools` and
`Context Help` included, is owned by the visible `LVFrame`. The state answer lists `enabled`
(when false), `ownerHandle` and `handle` for each window. `VI Hierarchy`, a hidden `LVDChild` seen
once on a LabVIEW that had been in use for two hours, is not on a fresh LabVIEW. One sighting does
not show that it is always harmless, so it stays `hidden-unrecognised`.

**The user's decision of 2026-10-10.** A VISIBLE possible dialog is a hard stop that no go-ahead
opens. A `hidden-unrecognised` window is a go-ahead guard (`hiddenWindows`). It may be a dialog that
reports itself invisible, or a system window whose class is not in the list yet: that listing had 13
hidden titled windows outside the ignore list, and two later runs had 16 and 19, and not every one of
them is named above. With `userGoAhead` the guard is opened, and the answer lists each window it
opened, with title and class, under `overriddenHiddenWindows`, so the class can be added to
`SystemClassPrefixes` from that measurement rather than from a guess. A hidden window that comes up
while the guards run (a project close can raise one) is caught by the last window list before the
close. A timed-out call's error lists both kinds. A VI panel with a custom window title is flagged
too, as the kit already documented. Dialogs are named with their title, class, visibility and child
windows. LabVIEW draws its own dialogs (`LVDChild`) without child controls, so their buttons cannot
be read, so each visible one comes with a screenshot (section 13). A Win32 message box (`#32770`)
lists its buttons and text. These tools press, front or close nothing; `lvai_answer_dialog` may,
in the caller's own LabVIEW only (section 13).

**labview-mcp's own scratch VIs.** The server hands LabVIEW throwaway `_name`s it never saves
under, so that a refused validate or convert cannot burn a real name: `LVMCP Validate`
(`Infra/ValidationScratch.cs`, numbered by LabVIEW on repeats: `LVMCP Validate2.vi` …),
`LVMCP Convert` (`BulkTools`, `EventStructureTools`), `LVMCP DQMH ` (`DqmhHeadless`) and
`LVMCP TD Carrier ` (`TypedefCreateTools`). The other `LVMCP …` names in the code (`Stub`, the
`ClsW`/`ClsR`/`Mth…` sockets, `Flat`) are saved files and are not scratch. The user asked for the
unsaved scratch VIs to be closed without saving before the IDE close. **There is no route that does
it, and the tool says so instead.** Measured 2026-09-26 (`docs/scratch-vis-in-memory.md` §1):
`Open VI Reference` by name answers `Error 1004` for them in the helpers' instance, the IDE's main
instance and the active project's, and the converter's `1051` check does not see them either. So
neither `lvai_close_vi` nor `scripts/lvai_close_own_panel.xml` can reach them, and generating any
new helper would itself leave another `LVMCP Validate` VI. Closing their WINDOWS raises LabVIEW's
save prompt (§2a), and a dialog is never answered here. So they keep blocking as `unsaved`. The
answer's `guards.scratchVis` lists them with `closedByThisTool: false` and the reason. With the
user's go-ahead the close raises the prompt, and "Don't Save - All" is safe for these.

## 5. CPU

The skill's threshold, kept: a sample is quiet below 1 CPU second per 30 s of wall clock, scaled to
the window. The restart takes two 15 s samples in a row. The skill's rule is two samples at least
15 s apart, because one idle sample can be stale within minutes (measured: 0 s over 20 s, then
11.3 s over 15 s a minute later). Idle is not safe: a modal dialog leaves LabVIEW idle. So the
window list is read again after the samples.

## 6. Memory: `lvai_vis_in_memory`

`scripts/lvai_vis_modified.xml` is the skill's `pa_vis_modified` helper with only its name and
description changed. It reads `Application:All VIs In Memory` with no application reference, opens
each VI by name, and reads `Modifications:Front Panel Mods Bitset` and `Block Diagram Mods Bitset`.
**Default instance only.** That is the instance the helper runs in. Measured three times on the
production station (2026-10-07 twice, 2026-10-08): 419 names, 341 `LV AI gRPC Service.lvlibp`, 63
`LV AI Core.lvlibp`, 15 `GSW.lvlibp`, none modified. `docs/lvclass-creation.md` counted the same
instance at 404, every one from the service. An open project's VIs live in the project's instance
and are not listed, and classes and libraries never are. The tool sorts each name as `service`,
`labview` (`GSW.lvlibp`), `helper` (a `scripts\*.xml` name, a cached helper `.vi`, or an `LVMCP`
scratch name) or `other`, and returns only the modified, unreadable and `other` lines.
**Unreadable lines of NI's packed libraries do not count against "empty".** Measured 2026-10-10 on
a fresh LabVIEW: the port's run gave 419 lines (404 service, 15 `GSW.lvlibp`, none modified), and
20 of them, every one a `.ctl` of `LV AI gRPC Service.lvlibp` or `LV AI Core.lvlibp`, answered
error 1035. After a project had been open and closed it was 96 lines, all in the same libraries.
The first port required every line to be readable, so LabVIEW could never count as empty. Built
packed-library code cannot carry unsaved edits. Any other unreadable line still blocks, and the
answer gives `unreadableCount` and `unreadableOutsidePackedLibraries`. The full
listing was 83 KB. Upstream's `scripts/lvai_vis_in_memory.xml`, which reads both instances, is
unchanged and is not what this tool runs.

## 7. The close

`Process.CloseMainWindow`, then up to 20 s `WaitForExit`. **Never a kill.** Measured close times
were 4.0–5.5 s. When LabVIEW does not exit:

| `closeMainWindow` | meaning (measured) |
|---|---|
| False | LabVIEW did not take the close message: busy with a VI Server helper, or modal (2026-10-07: False, 20.06 s, LabVIEW stayed) |
| True | a dialog came up after the request, most likely a save prompt |

In both cases the answer has a fresh window list with any dialogs named, and a 5 s CPU sample. The
user decides what happens next. How `CloseMainWindow` behaves with a modal FILE dialog up has not
been measured.

## 8. The start, Nigel, and LVAutoSave

`restart` starts the same `LabVIEW.exe` that was running (the default pick when that is unknown)
through `lvai_ensure_labview`'s launch routes, which survive the host's job object, and waits up to
150 s for the service. Measured cold starts took 32–40 s. If `LVAutoSave` holds files after a clean
close, the tool stops with `closedNotStarted`. A start would either clear those files or raise the
recovery dialog, and that choice is the user's.

**Nigel.** The AI service comes up with LabVIEW alone; the Nigel chat is not needed. It needs
`NI.NigelLocalService`, a separate process that outlives LabVIEW and the chat (on 2026-10-13 one had
run for three days) and runs as a child of NI's Discovery service (`NI.Discovery.V1.Service.exe`).
Measured 2026-10-13, both times with LabVIEW closed by this tool and `LabVIEW.exe` then started
directly, no `LVNigelChat` at any point:

| local service before the start | local service | features `Started monitoring` | `lvai.LVAI` answered |
|---|---|---|---|
| running (since 2026-10-10) | the same pid | 39-44 s after the launch | at 79 s, the first time asked |
| stopped by hand (`Stop-Process`), still gone 15 s later | started by Discovery 20 s after the launch | 57-62 s | at 136 s, the first time asked |

`GetApplicationConfiguration` and a `ValidateAIXML` answered in both. Five starts on 2026-10-10
came up the same way (section 12, item 10). Twice the service has NOT come up until Nigel was
opened: on the morning of 2026-10-10, with the local service not running, and on an older LabVIEW,
up twenty minutes, serving `lvai.LVAI` on none of 30 listener ports. The second run above shows a
missing local service is not enough to cause that, so the cause is not established. The chat
start below is the remedy for that case. When
LabVIEW has been up for more than 90 s (60 s until 2026-10-13, raised because the service's features
were measured starting 39-44 s after a launch, too close to 60) and its service does not answer, `lvai_ensure_labview` and the restart's start step start the
chat ONCE per LabVIEW process, under these conditions: no `LVNigelChat` runs, no possible dialog is
up, and at least 15 s of the call's budget remain. They start it the way LabVIEW's own menu does:
`LVNigelChat.exe --lv-version <add-on folder> --lv-bitness x86|x64`, from
`%ProgramFiles%\NI\LVAddons\lvai\<folder>\resource\AI\LVNigelChat\`. The folder is named after
`LabVIEW.exe`'s file version (`26.3.1f1` → `26.3`). That rule is derived from one station, where the
folders `1`, `26.1` and `26.3` sit side by side. A folder that does not exist is refused, never
guessed. Bitness comes from the PE header. The launch uses the job-object breakaway route, then the
shell. The Explorer hand-off is left out because it drops arguments, and the chat started without
both arguments leaves a modal "Object reference not set to an instance of an object." box (measured
by the skill). A message box after the start is reported with its text and never pressed.
`NI.NigelLocalService` and a running chat are never touched.

## 9. Resumable, and never twice

The route takes longer than the client's 60 s request ceiling: about 30 s of CPU samples, the
close, then a 32–40 s start. So it runs as a `ResumableCall` job. Each call answers within about
40 s, and `stillRunning` means: call again with the SAME arguments. The later call attaches to the
same job. The client should make no other LabVIEW call meanwhile. A collected answer is kept for
two minutes. An identical call in that window gets the same answer back (`replayed: true`) and does
not close LabVIEW a second time.

## 10. A timed-out call names the dialog

`Rpc.GuardAsync` adds `possibleDialogs` and `dialogNote` to the error of a call that ends in
`DeadlineExceeded` or a local timeout, when the window list shows a possible dialog. A modal LabVIEW
dialog stops the whole service, and a bare timeout says nothing about why. The skill measured a "Find
the VI" dialog holding a bind call for 720 s. Every other answer is unchanged.

## 11. Measured, and not measured

| claim | status |
|---|---|
| the guards, their order, the empty rule, the close sizing (4.0–5.5 s), the start sizing (32–40 s), `CloseMainWindow` False while a helper runs | measured by the skill's route, labview-mcp 1.8.5, 2026-10-07/08 |
| invisible on-screen dialogs | measured 2026-10-06 (`docs/scratch-vis-in-memory.md` §2a) |
| scratch VIs unreachable by name in every VI Server instance | measured 2026-09-26 (`docs/scratch-vis-in-memory.md` §1) |
| hidden system classes and titles | one `-All` listing, 2026-10-07 |
| `pa_vis_modified` → `lvai_vis_modified.xml`, 419 lines, none modified | measured 2026-10-07/08 (the copy itself: unmeasured) |
| `LVNigelChat.exe --lv-version --lv-bitness` | measured by the skill |
| the add-on folder from the exe's file version | derived from one station: **unmeasured** |
| the port: window classification on a fresh LabVIEW, the empty restart (resumable, collected on the second call), the server-started close, refusals `busy`, `activeProject`, `autoSave` and `unsaved` (scratch VI), `lvai_vis_in_memory` | measured 2026-10-10 (section 12) |
| the untitled modal dialog (owner and disabled frame) | the window list measured 2026-10-10; the classifier on it is unit-tested, not re-run against a live dialog |
| dialog screenshots (`PrintWindow`), `lvai_answer_dialog`: Esc = Cancel and a click on the found "Don't Save" picture on a real "Save changes? (Exit)" | measured 2026-10-10 in the caller's own LabVIEW (section 13) |
| `lvai_answer_dialog` for `Don't Save - All`, `changedOnDisk`, `nigelBox` | **unmeasured**: unit tests only (section 13) |
| the `dialog` stop with a go-ahead, the `ownership` refusal, a close that ends in the save prompt (`closeDidNotFinish`) | measured 2026-10-10 with a person at the machine (section 12) |
| the service up with no Nigel chat (79 s after a direct `LabVIEW.exe` start; 136 s with `NI.NigelLocalService` stopped first, which the start brought back); a close of the user's LabVIEW with a go-ahead overriding `hiddenWindows` (a hidden `VI Hierarchy`), 3.79 s, the chat exiting with LabVIEW | measured 2026-10-13 (section 8) |
| the replay, the Nigel chat start, the dialog fold in timeouts, the `processes`, `service` and `memory` refusals | **unmeasured** |
| `CloseMainWindow` on a LabVIEW with a hung actor (running VIs, no dialog) | measured 2026-10-10: exits in 1.72 s, no prompt (section 12, item C) |
| `CloseMainWindow` with a modal file dialog up | **unmeasured** |

## 12. Measurement checklist

Every real close or restart in this list needs the user's go-ahead, or a LabVIEW that is empty or
that this build started. Use a throwaway project copy and the fork's own build, never the installed
plugin at the same time.

1. **State, nothing open.** `lvai_labview_state`: one LabVIEW, `possibleDialogs` empty, `windowCounts`
   only frame and system kinds. With `includeHiddenWindows`, check that every hidden window of the
   station is classified: each `hidden-unrecognised` entry that is not a dialog is a class for the
   list in section 4. Until it is added, every restart there needs a go-ahead.
2. **A dialog.** Raise a save prompt by hand. If it is visible: `lvai_labview_state` lists it under
   `possibleDialogs`, and `lvai_restart_labview` answers `stoppedBy: dialog` even with a go-ahead.
   If it reports itself invisible (the Error list case): it is listed under `hiddenUnrecognised`.
3. **An unrecognised hidden window, overridden.** On a LabVIEW with a `hidden-unrecognised` window
   (from step 1, or the invisible Error list): without a go-ahead, `stoppedBy: hiddenWindows` naming
   title and class, and LabVIEW stays. With the user's go-ahead: the close goes ahead and
   `overriddenHiddenWindows` lists each window with its class. Add the classes that turn out to be
   system windows to `SystemClassPrefixes`.
4. **An unsaved `LVMCP Validate` VI in memory.** Make one by validating a document the generator
   refuses (`lvai_validate_aixml` with an unsupported subVI), then check that `lvai_labview_state`
   lists `LVMCP Validate….vi … *` under `unsaved`, and that a user VI is open and unsaved beside it.
   Expected without a go-ahead: `stoppedBy: unsaved`, `guards.scratchVis` listing only the LVMCP
   windows with `closedByThisTool: false`, and both VIs untouched. Re-confirm §1 on this build:
   `Open VI Reference` by that name in the helpers' and the active project's instance answers 1004.
   If it does NOT, a close-without-save route exists, so record it and wire it in before the PR. With
   the user's go-ahead, the close raises "Save changes? (Exit)": the user's VI must be listed beside
   the LVMCP ones, and the user answers it.
5. **Each refusal.** Two LabVIEW versions running → `processes`. A busy LabVIEW (a long helper run) →
   `busy` with two samples. A `*` panel → `unsaved`. An `LVAutoSave` file → `autoSave`. The service
   blocked → `service`. Another project active → `activeProject`. A modified VI with no window
   (`lvai_vis_in_memory` lists it) → `memory`. A user's LabVIEW with a VI open → `ownership`. In
   each case LabVIEW stays up with the same pid.
6. **`lvai_vis_in_memory`** on a fresh LabVIEW: about 400 names, all `service`/`labview`, none
   modified. Then a modified loose VI: one `modified` line.
7. **Empty LabVIEW restart** (no go-ahead): `ok`, `whose: empty`, a new pid, start time after the
   close, a new port, the same `NI.NigelLocalService` pid, `LVAutoSave` 0 → 0, about 4 s close and
   32–40 s start.
8. **Server-started LabVIEW close:** `lvai_ensure_labview` from no LabVIEW, open a project with
   `projectPath`, then `lvai_restart_labview mode close projectPath=…`: the project is saved and
   closed first, LabVIEW closes, and there is no go-ahead.
9. **A close that does not finish:** an unsaved VI the user agrees to leave, with a go-ahead:
   `closeDidNotFinish`, `closeMainWindow: true`, the save prompt named in `dialogsNow`. The user
   answers it.
10. **Nigel:** LabVIEW up for more than 90 s with no chat and the service down. `lvai_ensure_labview`
   reports `nigelChat.state: running`, the chat window appears, and the next `lvai_status` answers.
   Call it again: there is no second start.
11. **Timeout with a dialog:** a call that times out while a dialog is up carries `possibleDialogs`.

### Results, 2026-10-10 (LabVIEW 2026 Q3 32-bit, a throwaway project, the fork's own build)

| item | result |
|---|---|
| 1 | **pass.** Fresh LabVIEW, nothing open: 20 hidden system windows, 2 frame windows, the service's; no `possibleDialogs`, no `hidden-unrecognised`. Every `LVDChild` window is owned by the visible `LVFrame`. `CiceroUIWndFrame` and `NI-RPC Message Thread Class` (seen on a LabVIEW in use) were added to the known classes; `VI Hierarchy` is not on a fresh LabVIEW and stays unrecognised. |
| 2 | **pass**, with a person at the machine (13:3x): the user edited a class VI and closed its window. "Save changes before closing?" was listed under `possibleDialogs` (frame disabled, the VI's windows disabled, a hidden twin of the prompt as `hidden-unrecognised`). `lvai_restart_labview restart` WITH the go-ahead answered `stoppedBy: dialog` in 0.0 s. |
| 3 | not run: no unrecognised hidden window appeared to override |
| 4 | **pass.** A refused validate (`Error 53`) left `LVMCP Validate.vi Front Panel *` and `… Block Diagram *`, both hidden. `stoppedBy: unsaved`, `scratchVis` lists exactly those two, `closedByThisTool: false`, same pid. `Open VI Reference` by that name: 1004 in the helpers' instance and in the active project's; the probe's own name, 0 (positive control). The IDE's main instance was not probed. The go-ahead close that raises the save prompt needs the user. |
| 5 | **pass** for `ownership`, with a person at the machine: the user opened a class VI and did not edit it; a restart without a go-ahead was refused, `whose: the user's`, `stoppedBy: unsaved` (the item 4 scratch VI was still in memory), two quiet samples. **Pass** for `busy` (3.86 CPU s in the first 15 s sample right after a project open), `activeProject` (another `projectPath` while a project is active), `autoSave` (one dummy file, removed again) and `unsaved` (item 4). LabVIEW stayed up with the same pid each time. `processes`, `service` and `memory` were not run. |
| 6 | **pass** after a fix: 419 lines, none modified. The unreadable packed-library lines made `onlyServiceAndHelpers` false (section 6). Generating the helper left no scratch window. The modified-loose-VI half was not run. |
| 7 | **pass** after the section 6 fix: a LabVIEW this server did not start, empty, no go-ahead: `whose: empty`, two quiet samples, memory read (419 lines, 96 unreadable, all in packed libraries). The first call answered `stillRunning` at 40 s and the second collected the answer. Close 3.2 s, start 22.1 s (a warm start), 56.2 s in all, new pid and port, the same `NI.NigelLocalService` pid, `LVAutoSave` 0 → 0. |
| 8 | **pass** after a fix: `lvai_ensure_labview` from no LabVIEW (pid recorded), `lvai_open_file`, then `close` with `projectPath` and no go-ahead: `whose: started by this server`, the project saved and closed, close 3.98 s. Before the fix, the project opened as `Name.lvproj * - Project Explorer` before any edit, and the `unsaved` guard counted the tool's own project (section 3). After a project open, LabVIEW was busy for about 90 s (11–23 CPU s per 30 s). |
| 9 | **pass**, with a person at the machine, and the go-ahead half of item 4: the user pressed Cancel on the item 2 prompt; `close` WITH the go-ahead answered `stillRunning` at 40.0 s, then `closeDidNotFinish`: `closeMainWindow: true`, 20.01 s waited, `dialogsNow` naming "Save changes? (Exit)", `sideEffects` none. The user saw both the edited VI and `LVMCP Validate.vi` listed, pressed "Don't Save - All", and LabVIEW exited. |
| 10 | not run: the service answered without the chat 21–46 s after each of five starts, so the chat start never applied (it correctly waited for 60 s). One sample each way: in the morning `NI.NigelLocalService` was NOT running and the service came up only after the chat was opened; on all five later starts `NI.NigelLocalService` (started by that chat) was still running, and the service came up with the IDE alone. So the chat appears to be needed when the local service is down, not after every LabVIEW start. **Not so, measured 2026-10-13:** with the local service stopped by hand, a LabVIEW start brought it back and the service came up with no chat (section 8). |
| 11 | not run: no call timed out |
| C | **pass**, the real use case (14:27): an AF actor left hung by a lost early stop - its `Actor Core` clone and its helper loop at execState 3, the loop's front panel open, Abort not possible. `lvai_labview_state`: no dialog, CPU quiet; `lvai_vis_in_memory`: 499 lines, none modified, 95 `other` (the actor's and AF's VIs). `restart` WITH the go-ahead: `stillRunning` at 40 s, then `restarted`. `CloseMainWindow` closed the LabVIEW with the running actor in **1.72 s, with no prompt**; start 22.2 s, new pid and port, the same `NI.NigelLocalService` pid. |

Found and fixed on the way: a new VI (`Untitled N`, section 4) blocked as a possible dialog. Its
fix is unit-tested on the measured window list and has not been re-run live.

LabVIEW auto-saves an unsaved project into `LVAutoSave` within minutes: two files named after the
project file (`NI32LV2631f1ASD1_<name>.lvproj`, `…_<name>.txt`), removed at the clean close. By
the user's decision of 2026-10-10 those files do not block a close that saves that project (it is
`projectPath` and active); any other file still needs a go-ahead. The naming rests on one sample.

## 13. Dialogs: screenshots, and the allowlist the server may answer

**Screenshots.** LabVIEW draws its dialogs (`LVDChild`) without child controls, so Win32 cannot
read their text or buttons. Each VISIBLE possible dialog in `lvai_labview_state` (parameter
`screenshots`, on by default; a few ms, and only when a dialog is up) and in every dialog list of
`lvai_restart_labview` carries `screenshot`: a PNG under `%TEMP%\LabVIEWMCP\dialogs`, taken with
`PrintWindow` (`PW_RENDERFULLCONTENT`). The window renders itself into the picture whatever covers
it, and nothing is brought to the front. A screen copy of the dialog's rectangle, tried first on
2026-10-10, captured the terminal that lay on top of it. Each entry also has the dialog's `handle`.

**Who may answer.** Until 2026-10-10 every dialog was the user's to answer. The user's decision
of that day: "if you are operating labview on your own like starting and stopping then answering
these by yourself is fine." `lvai_answer_dialog` implements exactly that, and nothing wider:

1. The LabVIEW is the caller's own: started by THIS server process (the pid and start time that
   `lvai_ensure_labview` recorded), or the caller passes `ownLabviewPid` equal to the running pid
   with `ownNote` saying how its session started it. Otherwise: `notOwnLabview`.
2. The dialog is on the allowlist, with the one answer each kind allows. Otherwise:
   `notAllowlisted`.

| `dialogKind` | the dialog | allowed `action` | extra conditions |
|---|---|---|---|
| `exitSaveScratchOnly` | "Save changes? (Exit)" | `Don't Save` (one VI listed), `Don't Save - All` (several), `Cancel` | for a Don't Save: every `*` window of the process is an `LVMCP …` scratch VI, and `listedVis` (every VI the dialog lists, read from its screenshot) are all `LVMCP …` |
| `changedOnDisk` | the untitled "… has changed on disk since last saved or loaded by LabVIEW …" modal [Revert] [Cancel] | `Cancel` | untitled `LVDChild` classified as a possible dialog (section 4); `viName`, the VI this session touched |
| `nigelBox` | a Win32 message box (`#32770`) of an `LVNigelChat` this server started | `OK` | |

A refusal presses nothing and returns the screenshot, so the user can decide. Before the press,
the window is read again (same handle, same title, still visible) and a `before` picture is
taken. After it, the tool waits up to 10 s for the dialog to go and, for a Don't Save, up to
20 s for LabVIEW to exit. The answer reports what was pressed, by which means, `dialogGone`,
`processExited`, and the `asked`/`before`/`after` pictures. A press that does not take is reported
as `dialogStillThere`, and nothing else is tried.

**How it presses, measured 2026-10-10** (LabVIEW 2026 Q3 32-bit, Windows 10, 96 dpi, the
caller's own LabVIEW, one `LVMCP Validate.vi` scratch VI in memory, the IDE closed through
`lvai_restart_labview`):

- **Cancel = Esc posted to the dialog window** (`WM_KEYDOWN`/`WM_KEYUP`, `PostMessage`). Nothing
  was brought to the front. On the real "Save changes? (Exit)" prompt the dialog went in 0.6 s and
  LabVIEW stayed.
- **A named button = a left click posted where the button's PICTURE is found.** With one unsaved
  VI the prompt reads "LVMCP Validate.vi has unsaved changes" with buttons Save / Don't Save /
  Cancel. There is no "Don't Save - All" in that form. The "Don't Save" picture (58x15 px, the text
  on the button face) was cut from a screenshot of that prompt and ships in `DialogButtons.cs`. It
  was found again at 0.0 mean difference in three pictures of two prompts, and the next-best place
  scored 21.2 (threshold 6). Before a click the tool searches a fresh picture and clicks only at a
  single match. Another layout, DPI, theme or a hovered button finds nothing, and nothing is
  pressed (`buttonNotFound`). Measured: the click was posted at (63,181) of the dialog, the dialog
  went, and LabVIEW exited, 4.0 s in all. No real mouse moved.
- **OK of a message box = `BM_CLICK` on its OK button**, the skill kit's measured route
  (`lv_press_ok.ps1`) for a box raised by `LVNigelChat`.

Not measured live, and unit-tested only: `Don't Save - All` (no picture yet, so it answers
`buttonNotFound` until a multi-VI prompt has been measured), `changedOnDisk` and `nigelBox`.
**`changedOnDisk` carries a warning.** On 2026-10-10 LabVIEW crashed on the next call after that
dialog had been answered: a graft call had left a VI inconsistent in memory. Answering the dialog
does not make the VI in memory safe, so close or restart LabVIEW next. That dialog was not raised
on purpose to test this tool.
