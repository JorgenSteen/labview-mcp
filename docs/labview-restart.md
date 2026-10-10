# Closing and restarting LabVIEW under guards

`lvai_restart_labview`, `lvai_labview_state`, `lvai_vis_in_memory`, and the Nigel-chat start in
`lvai_ensure_labview`. The route itself was measured by a LabVIEW skill on a production Actor
Framework station in October 2026 (labview-mcp 1.8.5, LabVIEW 2026 Q3 32-bit); **the C# port in
this repository has not yet run against LabVIEW.** Section 11 separates the two, and section 12 is
the checklist that has to pass before the port's claims count as measured.

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
| 2 | `dialog`: a possible dialog (section 4) | window list, before and after the CPU samples, and again right before the close | **no**: the user answers it |
| 3 | `busy`: either of two 15 s CPU samples is busy, or the clock cannot be read | `TotalProcessorTime` | **no**: a busy LabVIEW does not take the close message |
| 4 | `unsaved`: a `*` title | window list | yes |
| 5 | `autoSave`: files in `LVAutoSave` | the Documents known folder, which may be redirected | yes |
| 6 | `service`: lvai does not answer (20 s probe) | `GetApplicationConfiguration` | yes. Project and memory are then not read |
| 7 | `activeProject`: a project is active or open that is not `projectPath` | `lvai_active_project` helper, project windows | yes. The IDE close closes it; this tool never closes a user's project itself |
| 8 | `memory`: a VI in memory has nonzero modification bits | `lvai_vis_modified` helper | yes |
| 9 | `ownership`: not this server's and not empty | sections 2 and 6 | yes |

The tool reports every reason that needs a go-ahead under `needsGoAheadFor`, and `stoppedBy` names
the first one. The order follows the skill: nothing changes before guards 1–7 have passed. When
`projectPath` is this session's project and it is the ACTIVE one, the tool saves and closes it
(`lvai_close_active_project`) before the memory check, because the memory probe would otherwise be
adopted into it. That close is reported as a side effect. The skill notes that it can rewrite
`.lvproj`, `.lvlps` and `.aliases`, so check the project's tree afterwards.

Two helpers are never generated inside the restart: `lvai_vis_modified` and `lvai_active_project`.
Generating a helper validates it, and validation leaves an `LVMCP Validate` VI in memory
(`docs/scratch-vis-in-memory.md`), which can raise the save prompt at the very close it precedes. If
a helper is missing, its check is reported as not run, and LabVIEW then does not count as empty. Call
`lvai_vis_in_memory` once at the start of a session.

## 4. The window classifier, and the dialog that says it is invisible

A port of the skill's `lv_windows.ps1`, with one deliberate change. The script flags only VISIBLE
windows as possible dialogs. Measured 2026-10-06 (`docs/scratch-vis-in-memory.md` §2a), LabVIEW's
`Error list` and `Externally Changed Files List` dialogs were on screen, could not be clicked, and
reported `IsWindowVisible` FALSE. So here a titled window is a possible dialog whatever its
visibility, unless:

- it is hidden and its title is one of LabVIEW's measured hidden windows: `Context Help`, `Tools`,
  `Set Title`, `RunService.vi`, `Closed`, `Window monitor`;
- it is hidden and its CLASS is one of the measured non-LabVIEW classes found in `LabVIEW.exe`:
  `OleAutomationWnd`, `HwndWrapper[…`, `GDI+ Hook Window Class`, `LVWinSessionMonitor`,
  `OleDdeWndClass`, `IME`, `MSCTFIME UI`, `ATL:…`, `Internet Explorer_Hidden`. These come from one
  `-All` listing of a production LabVIEW, 2026-10-07;
- its title is `LabVIEW`, `Getting Started…`, `….lvproj - Project Explorer`, a VI or control title
  (`.vi`, `.vim`, `.vit`, `.ctl`, `.ctt`, with or without `Front Panel` / `Block Diagram` /
  `on <project>/<target>`), or one of the service's own (`LV AI ….lvlibp:`).

**What this costs.** That listing had 13 hidden titled windows outside the ignore list, and two
later runs had 16 and 19. Not every one of them is named above. A hidden window of an unknown
class therefore BLOCKS the close as a possible dialog, and the answer names its class. The list is
meant to grow from measurements, not from guesses. A VI panel with a custom window title is flagged
too, as the kit already documented. Dialogs are named with their title, class, visibility and child
windows. LabVIEW draws its own dialogs (`LVDChild`) without child controls, so their buttons cannot
be read. A Win32 message box (`#32770`) lists its buttons and text. Nothing is pressed, fronted or
closed.

`unsaved` windows whose title starts `LVMCP Validate` or `LVMCP Convert` are labview-mcp's own
scratch VIs. They still block the close, because the close would raise LabVIEW's save prompt, but
the answer says that "Don't Save - All" is safe for them.

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
scratch name) or `other`, and returns only the modified, unreadable and `other` lines. The full
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

**Nigel.** The AI service starts with Nigel, not with the IDE. When LabVIEW has been up for more than
60 s and its service does not answer, `lvai_ensure_labview` and the restart's start step start the
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
| hidden system classes and titles | one `-All` listing, 2026-10-07 |
| `pa_vis_modified` → `lvai_vis_modified.xml`, 419 lines, none modified | measured 2026-10-07/08 (the copy itself: unmeasured) |
| `LVNigelChat.exe --lv-version --lv-bitness` | measured by the skill |
| the add-on folder from the exe's file version | derived from one station: **unmeasured** |
| this repository's C# port of all of the above, the resumable job, the replay, the dialog fold in timeouts | **unmeasured** |
| `CloseMainWindow` with a modal file dialog up | **unmeasured** |

## 12. Measurement checklist

Every real close or restart in this list needs the user's go-ahead, or a LabVIEW that is empty or
that this build started. Use a throwaway project copy and the fork's own build, never the installed
plugin at the same time.

1. **State, nothing open.** `lvai_labview_state`: one LabVIEW, `possibleDialogs` empty, `windowCounts`
   only frame and system kinds. With `includeHiddenWindows`, check that every hidden window of the
   station is classified: a `possible-dialog` that is not a dialog means a class for the list in
   section 4.
2. **An invisible dialog.** Raise the Error list or a save prompt by hand. `lvai_labview_state` names
   it, and `lvai_restart_labview` answers `stoppedBy: dialog` without closing.
3. **Each refusal.** Two LabVIEW versions running → `processes`. A busy LabVIEW (a long helper run) →
   `busy` with two samples. A `*` panel → `unsaved`. An `LVAutoSave` file → `autoSave`. The service
   blocked → `service`. Another project active → `activeProject`. A modified VI with no window
   (`lvai_vis_in_memory` lists it) → `memory`. A user's LabVIEW with a VI open → `ownership`. In
   each case LabVIEW stays up with the same pid.
4. **`lvai_vis_in_memory`** on a fresh LabVIEW: about 400 names, all `service`/`labview`, none
   modified. Then a modified loose VI: one `modified` line.
5. **Empty LabVIEW restart** (no go-ahead): `ok`, `whose: empty`, a new pid, start time after the
   close, a new port, the same `NI.NigelLocalService` pid, `LVAutoSave` 0 → 0, about 4 s close and
   32–40 s start.
6. **Server-started LabVIEW close:** `lvai_ensure_labview` from no LabVIEW, open a project with
   `projectPath`, then `lvai_restart_labview mode close projectPath=…`: the project is saved and
   closed first, LabVIEW closes, and there is no go-ahead.
7. **A close that does not finish:** an unsaved VI the user agrees to leave, with a go-ahead:
   `closeDidNotFinish`, `closeMainWindow: true`, the save prompt named in `dialogsNow`. The user
   answers it.
8. **Nigel:** LabVIEW up for more than 60 s with no chat and the service down. `lvai_ensure_labview`
   reports `nigelChat.state: running`, the chat window appears, and the next `lvai_status` answers.
   Call it again: there is no second start.
9. **Timeout with a dialog:** a call that times out while a dialog is up carries `possibleDialogs`.
