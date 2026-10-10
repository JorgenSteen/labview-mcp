# LabVIEW measurements of the feature branches

Each branch was built, then driven through its own build of the server (one server process per run, calls strictly
in sequence) on throwaway copies of an actor project. LabVIEW 2026 Q3 (26.3.1f1) 32-bit, Windows 10. Results land in
each branch's docs with their date; this file is the overview.

| Branch | Status | Result |
|---|---|---|
| `feat/class-icons` | measured 2026-10-10 | 6 of 8 checks pass, 1 skipped (needs more than 100 members), 1 failed and fixed (non-Latin-1 paths). Details in the branch's `docs/class-icons.md` §6 and `pr/feat-class-icons.md`. |
| `feat/af-overrides` | running | |
| `docs/af-lifecycle-overrides` | to do | nine claims marked unmeasured or reasoned |
| `feat/restart-guards` | to do (needs real LabVIEW restarts) | |

## Still open per branch
- **class-icons:** the apply helper's list-file twin (more than 100 members); an editable text layer made in the icon
  editor.
