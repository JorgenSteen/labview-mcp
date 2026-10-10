# LabVIEW measurements of the feature branches

Each branch was built, then driven through its own build of the server (one server process per run, calls strictly
in sequence) on throwaway copies of an actor project. LabVIEW 2026 Q3 (26.3.1f1) 32-bit, Windows 10. Results land in
each branch's docs with their date; this file is the overview.

| Branch | Status | Result |
|---|---|---|
| `feat/class-icons` | measured 2026-10-10 | 6 of 8 checks pass, 1 skipped (needs more than 100 members), 1 failed and fixed (non-Latin-1 paths). Details in the branch's `docs/class-icons.md` §6 and `pr/feat-class-icons.md`. |
| `feat/af-overrides` | measured 2026-10-10 | all tool routes pass; 2 bugs found and fixed; scripted edits are invisible to the unsaved-edit check (hand edits are caught). Details in `pr/feat-af-overrides.md`. |
| `docs/af-lifecycle-overrides` | running | nine claims marked unmeasured or reasoned |
| `feat/restart-guards` | measured 2026-10-10 | real closes and restarts; part with a person at the machine; 4 bugs found and fixed; dialog answering proven on a real prompt. Details in `pr/feat-restart-guards.md`. |

## Still open per branch
- **class-icons:** the apply helper's list-file twin (more than 100 members); an editable text layer made in the icon
  editor.
- **af-overrides:** restoring a member's virtual folder after a re-add.
- **restart-guards:** two LabVIEW versions running; a blocked service; the "changed on disk" and Nigel answers (unit-tested only); "Don't Save - All".
