# Summary of the update (for the maintainers)

## Short version

I have built four additions to labview-mcp on top of v1.9.5: tools that create Actor Framework overrides and hook
code into them, a guarded close/restart of LabVIEW that never kills and protects unsaved work, class and library icons
with read-back, and a corrected Actor Framework doc. All of them were measured against LabVIEW 2026 Q3 on throwaway
projects, fixing nine bugs on the way, and I also found a dozen issues in v1.9.5 itself — I'd like to know how you
want them as pull requests.

## Long version

**Actor Framework overrides and hooks.** An AF lifecycle override needs Call Parent, which AIXML cannot author, so
overrides were built by hand with VI Server scripting and its traps.
- `lvai_create_override` wraps the existing `lvai_create_override.xml`: it checks the class, derives the
  library-qualified name, names the failing stage and verifies the result from the `.lvclass`.
- `lvai_af_add_hook` places a class method into an override (branch, in series before or after Call Parent, or off
  a series body). It refuses unsafe hooks before touching anything, and has a dry run.
- `lvai_af_remove_hook` removes one hook and saves only the sibling overrides it changed.
- `lvai_add_class_method` no longer breaks a class when re-run over a method a loaded VI calls (Error 56003). It now
  refuses up front, and it keeps the member's scope and folder.
- Measured: every shape, the refusals, and the removal. Two bugs were found and fixed on the way: a hook name could
  match Call Parent itself, and a `vi`-only re-run dropped the member.

**Guarded LabVIEW close and restart.** Agents could only kill LabVIEW, and the LUnit agent prescribed
`Stop-Process -Force`.
- `lvai_labview_state` shows processes, windows, unsaved work, dialogs (including untitled and invisible ones, with a
  screenshot), autosave files and CPU, without calling LabVIEW.
- `lvai_restart_labview` closes or restarts only when that is safe. It needs the user's go-ahead unless LabVIEW was
  started by the server or is empty, never kills, and reports a close that ends in a save prompt.
- `lvai_answer_dialog` answers a short allowlist of harmless dialogs, only in the caller's own LabVIEW.
- `lvai_ensure_labview` opens Nigel when the AI service does not come up by itself.
- Measured with real closes and restarts, partly with a person at the machine: a hung actor freed in 24 s. Four bugs
  were fixed on the way: missed untitled dialogs, a new VI mistaken for a dialog, NI packed-library entries that made
  "empty" impossible, and the server's own project blocking its close.

**Class and library icons.** Only a VI's icon could be set, and the manual route fails silently in four ways.
- `lvai_set_owner_icon` sets a class or library icon, applies it to the members, saves each, and verifies by
  pixel-comparing read-backs.
- `lvai_read_icons` reads icons in batches, with a pixel compare against expected PNGs.
- `lvai_set_vi_icon keepOwnLayer` re-applies the template and keeps a VI's own layer instead of flattening it.
- Measured on a 9-member class and a plain library. One bug was fixed: non-Latin-1 paths gave Error 7.

**Actor Framework documentation.**
- A new §19 covers the lifecycle overrides: the hook shapes, the panes, the run order, error paths, and how a helper
  loop must be stopped.
- A new §0 gives the current recipe, and the outdated passages in §§1–18 are marked, not deleted.
- Ten claims were measured, including two surprises: a stop sent right after Launch can be lost and hang the actor,
  and LabVIEW does not add a library's project entry by itself.

**Issues found in v1.9.5 outside these changes.**
- The unit tests write a stub VI into the real helper cache, which breaks an installed plugin on the same PC.
- `lvai_graft_diagram` in place on a loaded VI leads to a modal dialog and then a LabVIEW crash.
- Dialog searches miss invisible LabVIEW dialogs.
- Smaller ones: a hardcoded server version, a stale essentials rule, a lint false positive.
- The full list is in `UPSTREAM-FINDINGS.md`.
