# Guarded LabVIEW close and restart, LabVIEW state, and answering known dialogs

## Why
An agent working with labview-mcp sometimes has to restart LabVIEW: a hung actor that Abort VI cannot stop
(error 1000), a class held in memory (Error 1562), recovery after a failed step. Today it can only kill the process.
The LUnit agent even prescribes `Stop-Process -Name LabVIEW -Force`, which loses every unsaved edit of the person
working in the same LabVIEW. There is also no way to see from the server why LabVIEW does not answer:
- a modal dialog blocks the service, and LabVIEW dialogs can report themselves invisible or carry no title;
- the AI service starts with Nigel, not with the IDE.

## What
- **`lvai_labview_state`** (read-only, no LabVIEW call): the LabVIEW and Nigel processes, every LabVIEW window
  classified, unsaved `*` titles, possible dialogs (including invisible and untitled ones) with a PNG screenshot each
  (PrintWindow, nothing brought to the front), LVAutoSave, a CPU sample, and whether this server started LabVIEW.
- **`lvai_vis_in_memory`**: the VIs in memory with their modification bits, reduced to the ones that matter.
- **`lvai_restart_labview(mode: close | restart, userGoAhead?, projectPath?)`**:
  - **Hard guards**, which no go-ahead opens: exactly one LabVIEW; no visible dialog; not busy (two CPU samples).
  - **Go-ahead guards:** unsaved windows, LVAutoSave files, a service that does not answer, another project active,
    modified VIs in memory, unrecognised hidden windows, and ownership.
  - **The ownership rule:** no go-ahead is needed only for a LabVIEW this server started or an *empty* one (only
    LabVIEW's own windows and Nigel, idle, nothing in memory but labview-mcp's helpers). Otherwise the caller passes
    the user's words in `userGoAhead`, and they are echoed back.
  - **The close:** `CloseMainWindow`, then a wait. It never kills, and a close that ends in a save prompt is reported
    with the prompt named.
  - **Resumable:** a restart takes minutes, so the tool answers `stillRunning` and the caller calls again.
- **`lvai_answer_dialog`**: answers a dialog only in the caller's own LabVIEW, and only one on an allowlist.
  - **The allowlist:**
    - the exit save prompt when only labview-mcp's own `LVMCP …` scratch VIs are unsaved → Don't Save / Cancel;
    - the "changed on disk" prompt on a VI the caller touched → Cancel;
    - a message box of a Nigel chat this server started → OK.
  - **How it presses:** Esc for Cancel; a click only where the button's exact picture is found.
  - **Anything else** is refused, with its screenshot returned for the person to decide.
- **`lvai_ensure_labview`** opens the Nigel chat once when LabVIEW is up but the service is not.
- **A timed-out call's error** names a dialog if one is up.
- **The LUnit agent** restarts through the tool instead of a kill.
- **Docs:** `docs/labview-restart.md`.

## Measured (2026-10-10, LabVIEW 2026 Q3 32-bit; real closes and restarts; part with a person at the machine)
| Check | Result |
|---|---|
| State of a fresh LabVIEW | 20 hidden system windows, no possible dialog, nothing unrecognised |
| Empty LabVIEW restart, no go-ahead | `whose: empty`; close 3.2 s, start 22.1 s; new PID and port; Nigel's service untouched |
| Close of a LabVIEW this server started, with a project open | project saved and closed first; close 3.98 s; no go-ahead |
| Refusals: `busy`, `activeProject`, `autoSave`, `unsaved` | refused; same PID afterwards |
| The user's LabVIEW with a VI open, no go-ahead | refused; `whose: "the user's"` |
| A visible save prompt, with a go-ahead | refused `stoppedBy: dialog` in 0.0 s; prompt named |
| Close with an unsaved VI, with a go-ahead | `closeDidNotFinish`, `closeMainWindow: true`, "Save changes? (Exit)" named; nothing touched |
| An untitled modal dialog ("…changed on disk…") | found live; **was missed**, now caught (owner and enabled state) |
| A new VI ("Untitled 7") | **was a false dialog**, fixed: a VI window |
| `lvai_vis_in_memory` on a fresh LabVIEW | 419 lines, none modified; NI packed-library lines that answer 1035 **blocked "empty" on every LabVIEW**, fixed |
| labview-mcp's scratch VIs (`LVMCP Validate`) | not reachable by VI Server (Error 1004 in the helpers' and the project's instance); named, never closed silently |
| `lvai_answer_dialog` on a real exit prompt | Cancel (Esc): gone in 0.6 s; Don't Save (picture click): LabVIEW exited, 4.0 s |
| Nigel | the chat is needed when `NI.NigelLocalService` is down (this morning); with the local service up, LabVIEW's service came up without it (22–46 s) |

Not measured: two LabVIEW versions running; a blocked service; the "changed on disk" and Nigel answers (unit-tested;
the "changed on disk" case was followed by a LabVIEW crash once, so the description says to close or restart next);
"Don't Save - All" (the several-VI prompt).

## Tests
Every guard, the window classifier (with the measured window lists), ownership, the memory parsing, the dialog
allowlist and the press logic run against a fake machine without LabVIEW. 2708 of 2709 pass; the one failure is the
existing example-index test for CompactRIO examples that are not installed on the measuring PC.

## Notes for review
- The ownership record is kept in memory only: a restarted server forgets which LabVIEW it started.
- `docs/guide/tools.md`, README and CLAUDE.md carry rows and counts that other open branches also change.
