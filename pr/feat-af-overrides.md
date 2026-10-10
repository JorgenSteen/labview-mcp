# Actor Framework overrides and hooks as tools; a safe re-run of lvai_add_class_method

## Why
An AF lifecycle override (Pre Launch Init, Actor Core, Stop Core, Handle Error, Handle Last Ack Core) needs Call
Parent, which AIXML cannot author, so an override can never be generated or regenerated. The working route is to let
NI's provider create the override and to place a separate body VI into its diagram by VI Server scripting.
`scripts/lvai_create_override.xml` exists but no tool wraps it, and the hooking is done by hand with traps:
- the panel terminal vs. a series body's output as the source;
- unfed required inputs that break the override silently;
- Handle Error's 43 and 1608;
- a deleted hook breaking a sibling override (1000088).

Meanwhile CLAUDE.md's index sends agents to `lvai_add_class_method` for an actor, which builds an override without
Call Parent. And re-running `lvai_add_class_method` over a member that a loaded VI calls fails with Error 56003
*after* the member entry was dropped, leaving the class worse than before.

## What
- **`lvai_create_override(classPath, parentMethod, projectPath?, newViPath?)`**: wraps
  `scripts/lvai_create_override.xml` (NI's `CLSUIP_CreateOverride`).
  - Before LabVIEW: checks the ancestry, resolves the lifecycle method under the actor's own LabVIEW install (or
    takes a plain parent `.vi`), and checks that no override exists yet. It derives the library-qualified in-memory
    name.
  - In LabVIEW: opens the project and reports the failing stage by name.
  - Afterwards: verifies member, scope and dynamic dispatch from the `.lvclass`, and returns the AF pane.
- **`lvai_af_add_hook(overridePath, classPath, hookViPath, shape, …)`**:
  - Shapes `branch`, `branchFromNode`, `series` and `seriesAfter`, with the panes of NI's overrides built in.
  - The plan is computed by terminal name. `dryRun` returns the planned wires.
  - It refuses before anything changes: an unfed required input, a Handle Error hook before Call Parent, a series cut
    on a branched wire, a name that does not fit, an override with unsaved edits it did not make.
  - The wiring uses VI Server scripting (`Control.Terminal`, `Wire.Delete`, `Connect Wire`). `branchFromNode` uses
    upstream's `lvbd_connect_by_names`.
  - It saves in a separate step and reads the exec state before and after.
- **`lvai_af_remove_hook`** deletes one hook node and runs Remove Bad Wires. It saves only the sibling overrides the
  remove made modified; a sibling holding someone's unsaved edits is refused by name.
- **`lvai_add_class_method`**:
  - a re-run over a member with loaded callers is refused up front (`memberHasLoadedCallers`, listing the callers);
  - scope and virtual folder are restored after a re-add (`keepPlacement`, on by default);
  - a `vi`-only re-run over a listed member is refused (`rerunNeedsAixml`).
- **Small fixes:**
  - `methodNotPublic` refusal in `lvai_create_message_class`;
  - the library-before-messages rule in two tool descriptions;
  - corrected descriptions (`lvai_create_message_class`'s default folder, `lvai_build_message_do.xml`,
    `lvai_create_override.xml`);
  - CLAUDE.md and the class-generator agent now route AF overrides to the new tools.
- **Docs:** `docs/af-override-tools.md` and catalogue rows.

## Measured (2026-10-10, LabVIEW 2026 Q3 32-bit, throwaway actors and project)
| Check | Result |
|---|---|
| `lvai_create_override`, all five lifecycle methods, an actor in no library and one in a `.lvlib` | 10 of 10 ok, library-qualified names, all state 1 cold; 1.1–1.8 s each |
| A plain-class override | ok, the parent's protected scope kept |
| A second override of the same method | `overrideAlreadyExists` in 0.1 s, no LabVIEW call |
| Handle Last Ack Core pane | read: pattern 4812, the five terminals as built in |
| `branch` on Actor Core | wired, state 1; a run logged the hook beside Call Parent |
| `series` on Actor Core with error | 2 series wires, state 1; export shows panel → hook → Call Parent |
| `seriesAfter` on Pre Launch Init, hook returning 5000 | Launch returned 5000; Actor Core never ran |
| `seriesAfter` on Stop Core and on Handle Error | state 1; at a Normal Stop the Handle Error hook logged 0, the Stop Core hook ran 7 ms later |
| `branchFromNode` after a series hook | qualified source matched; branch logged 1 ms after the series body |
| Refusals (`hookInputUnfed`, `wireIsBranched`, `handleErrorHookBeforeCallParent`) | refused, file times identical |
| `lvai_af_remove_hook` on a branched hook | 1 match, override and all 4 siblings state 1 cold |
| A sibling with an unsaved **hand** edit in the IDE | `siblingHasUnsavedEdits` naming it, nothing changed |
| A sibling with an unsaved **scripted** edit | not caught: VI Server edits leave the FP/BD Mods Bitsets at 0 (documented) |
| Re-run over a member a loaded override calls | `memberHasLoadedCallers`, class files byte-identical |
| Re-run over a protected member | scope restored |

Bugs found by the measurement and fixed on the branch:
- a hook name such as "Actor Core" matched the Call Parent node, so a remove would have deleted Call Parent; it is
  now never matched;
- a `vi`-only re-run dropped the member from the class (AddItemFromMemory 1004); it is now refused.

Not measured: restoring a member's virtual folder after a re-add.

## Tests
All checks, the pane table, the plans per shape, the refusals and the placement restore are unit-tested without
LabVIEW (about 130 new tests). 2711 of 2712 pass; the one failure is the existing example-index test for CompactRIO
examples that are not installed on the measuring PC.

## Notes for review
- Call Parent is listed in `SubVIs[]` (under the parent's VI name) on LabVIEW 2026 Q3, and Nodes[] reports
  `CallParentNode`.
- `docs/labview-actor-framework.md` §19 (a separate docs PR) describes the route these tools automate.
- `docs/guide/tools.md`, README and CLAUDE.md carry rows and counts that other open branches also change.
