# Changes in the fork (started 2026-10-10)

Four branches, each meant as its own pull request to Zuehlke/labview-mcp, all from upstream `7702e6d` (v1.9.5).
They come from workarounds that a LabVIEW skill used beside labview-mcp for months: helper VIs and scripts run by
hand. Each branch turns a group of them into tools, in the repository's own style: file checks before LabVIEW with
named errors, helpers generated once from AIXML in `scripts/`, one error indicator per stage (`failedAtStep`),
verification read back from disk, unit tests that need no LabVIEW, and a `docs/*.md` page with measured facts.

Status per branch: **measured** = run in LabVIEW and the results recorded in the branch's docs; **pending** = built
and unit-tested, LabVIEW measurement not finished yet.

## 1. `docs/af-lifecycle-overrides`: the Actor Framework doc (5 commits, docs only)
- **New §19 "Lifecycle overrides, their bodies, and stopping a helper loop"**: how to create the four AF overrides
  with Call Parent (AIXML cannot author it), hook a separate body VI into them by VI Server scripting (branch / in
  series), the panes of NI's overrides, the measured run order, and the stopping rule: a helper loop never sees its
  actor stop by itself and needs its own stop signal (user event, notifier or released reference), fired from Stop
  Core or after Actor Core's Call Parent.
- **Facts measured 2026-10-09** folded in: Stop Core does not run after a failed Pre Launch Init; an error into
  Actor Core's Call Parent skips Handle Error, runs Stop Core, and the next Send fails with 1556; a Handle Error
  handler belongs after Call Parent; create resources in Pre Launch Init after Call Parent with the error wired.
- **§§1–18 kept, outdated passages marked** inline ("Superseded by §N"), and a new **§0 "Current recipe"** at the top
  with a table of every superseded passage.
- Status: **measured 2026-10-10**: ten claims run (1608 at an Emergency Stop, overruling `stop actor?`, Pre Launch Init
  self-cleanup, deferred start, Generate/Destroy in Stop Core, a lost early stop that hangs the actor, the library entry
  LabVIEW does not write, the graft crash); one stays unmeasured (Register For Events with an incoming error).

## 2. `feat/af-overrides`: AF override and hook tools (12 commits)
- **`lvai_create_override`** wraps the existing but unwrapped `scripts/lvai_create_override.xml` (NI's
  `CLSUIP_CreateOverride`): checks ancestry, the lifecycle method and that no override exists yet, derives the
  library-qualified in-memory name, opens the project, names the failing stage, and verifies member, scope and
  dynamic dispatch from the `.lvclass`.
- **`lvai_af_add_hook`** places an existing class method into an override (shapes `branch`, `branchFromNode`,
  `series`, `seriesAfter`) with built-in pane knowledge per override. It refuses, before anything changes, a hook
  whose required input no wire feeds, a Handle Error hook before Call Parent, a series cut on a branched wire, and an
  override with unsaved edits it did not make. `dryRun` returns the planned wires. `branchFromNode` uses upstream's
  `lvbd_connect_by_names`.
- **`lvai_af_remove_hook`** deletes one hook node, runs Remove Bad Wires, and saves only the sibling overrides the
  remove made modified (a sibling with someone's unsaved edits is refused by name).
- **`lvai_add_class_method`**: a re-run over a member that a loaded VI calls now refuses up front
  (`memberHasLoadedCallers`) instead of failing with Error 56003 after the member entry was dropped; and the
  member's scope and virtual folder are restored after a re-add (`keepPlacement`, on by default).
- Small fixes: `methodNotPublic` refusal in `lvai_create_message_class`; the library-before-messages rule in two tool
  descriptions; CLAUDE.md routes AF overrides to the new tools (its index sent agents to `lvai_add_class_method`,
  which builds an override without Call Parent); stale descriptions corrected.
- Status: **measured 2026-10-10**; two bugs found and fixed on the way (a hook named like the override matched Call
  Parent; a `vi`-only re-run dropped the member). The unsaved-edit check sees hand edits, not scripted ones.

## 3. `feat/restart-guards`: guarded LabVIEW restart (6 commits)
- **`lvai_labview_state`** (no LabVIEW call): LabVIEW and Nigel processes, every LabVIEW window classified, unsaved
  `*` titles, possible dialogs including ones that report themselves invisible, LVAutoSave, a CPU sample, and whether
  this server started LabVIEW.
- **`lvai_vis_in_memory`**: VIs in memory with their modification bits, reduced to the ones that matter.
- **`lvai_restart_labview`** (`close` | `restart`): guards for one LabVIEW only, no visible dialog, not busy, no
  unsaved work, no LVAutoSave files, the service answering, the right project active, nothing modified in memory. An
  ownership rule: no go-ahead needed only for a LabVIEW this server started or an empty one; otherwise the caller
  passes the user's words in `userGoAhead`. It closes with `CloseMainWindow` and waits; it never kills and never
  answers a dialog. Resumable ("call again") because a restart takes minutes.
- **`lvai_ensure_labview`** opens the Nigel chat when LabVIEW is up but the AI service is not (it starts with Nigel,
  not with the IDE), once per LabVIEW process.
- A timed-out call's error names a dialog if one is up. The LUnit agent restarts through the tool instead of
  `Stop-Process -Force`.
- **`lvai_answer_dialog`** (added after the user's decision): answers a dialog only in the caller's own LabVIEW and only
  from an allowlist (the exit prompt for labview-mcp's own scratch VIs, the "changed on disk" quirk, a Nigel box);
  everything else is refused with a screenshot for the person.
- Status: **measured 2026-10-10** with real closes and restarts, partly with a person at the machine; four bugs found
  and fixed on the way (untitled dialogs missed, a new VI taken for a dialog, NI packed-library lines blocking
  "empty", the session's own project refused).

## 4. `feat/class-icons`: class and library icons (8 commits)
- **`lvai_set_owner_icon`**: a `.lvclass` or plain `.lvlib` icon from a 32x32 PNG; with `applyToMembers`, applies it
  to every member and saves each (ApplyIconToVIs saves nothing itself, and on a plain library does nothing unless the
  members were opened first). Refuses with a project open.
- **`lvai_read_icons`**: VI, class and library icons to PNG in batches of 100, creating folders first and refusing
  paths LabVIEW would silently fail on (missing folder, over MAX_PATH, outside Latin-1); with `expectedDir`, a pixel
  compare in C# (new PNG decoder).
- **`lvai_set_vi_icon keepOwnLayer`**: re-applies the owner's template and keeps the VI's own layer, instead of
  flattening the icon.
- Status: **measured 2026-10-10**; one bug found and fixed on the way (non-Latin-1 paths gave Error 7).

## Tests
Baseline on the measuring PC: 2574 of 2575 upstream tests pass (the one failure is an example-index test for
CompactRIO examples not installed there). Each branch adds its own tests; the same single failure remains.
