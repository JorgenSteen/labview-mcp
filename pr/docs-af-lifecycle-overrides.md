# Actor Framework doc: lifecycle overrides, stopping a helper loop, and a current recipe

## Why
`docs/labview-actor-framework.md` covers messages and libraries (§§1–18) but nothing about the four lifecycle
overrides (Pre Launch Init, Actor Core, Stop Core, Handle Error), where most real actor code lives. And as an
append-only log of 1 300+ lines, it holds passages that later sections retract: an agent reading from the top meets
the outdated recipe first.

## What
- **New §19 "Lifecycle overrides, their bodies, and stopping a helper loop":**
  - **The override and its body:** create the override with NI's provider (Call Parent cannot be authored in AIXML),
    write the body as its own class member, and hook it in by VI Server scripting. The shapes are branch, in series
    before Call Parent, and in series after Call Parent.
  - **The panes** of NI's overrides, and the full measured run order.
  - **The stopping rule:** a helper loop never sees its actor stop by itself, so it needs its own stop signal (a user
    event, a notifier or a released reference). There are two measured places to fire it: Stop Core (Generate before
    Destroy), or after Actor Core's Call Parent, feeding a `close` VI.
  - **Error paths:**
    - create resources in Pre Launch Init *after* Call Parent with the error wired;
    - Stop Core does not run after a failed Pre Launch Init, so Pre Launch Init cleans up after itself;
    - an error into Actor Core's Call Parent skips Handle Error;
    - a Handle Error handler belongs after Call Parent.
  - **Scope traps:** a private dynamic-dispatch VI is broken, and `Read Self Enqueuer` is protected.
  - **Scripting quirks:** Call Parent is in `SubVIs[]` under the parent's name; Merge Errors is GrowableFunction 2147.
- **New §0 "Current recipe"** at the top: the sequence that works today, linking §14 as amended by §18a, then §19,
  plus a table of every superseded passage.
- **§§1–18 kept, never deleted:** each outdated passage is marked inline ("Superseded by §N, <date>").

## Measured (2026-10-09 and 2026-10-10, LabVIEW 2026 Q3 32-bit, throwaway actors)
| Claim | Result |
|---|---|
| Handle Error after Call Parent: what reaches it | 0 at a Normal Stop (43 cleared by the parent), 1608 at an Emergency Stop; 2 of 2 each |
| Overruling `stop actor?` to FALSE after Call Parent | the actor kept running after a reported error; a later Normal Stop answered 0; control without the hook: Stop Core at once, next Send 1556 |
| Pre Launch Init self-cleanup (create → error → release in a case structure, after Call Parent) | Launch returned the error, the reference was released, no Stop Core line; 2 of 2 |
| Deferred start after Pre Launch Init's Call Parent, error wired | a failed Send failed the Launch (1556); 2 of 2 |
| Generate then Destroy in Stop Core | the loop stopped in the same ms; no hang; 3 of 3 |
| A stop sent right after Launch | **lost**: the helper loop started 38 ms after the stop was sent and never exited; the actor hung (n = 1) |
| Stop Core after a failed Pre Launch Init | does not run (2026-10-09) |
| An error into Actor Core's Call Parent | Handle Error skipped, Stop Core runs, Launch saw 0, the next Send 1556 (2026-10-09) |
| A library made for a class that the project never listed | LabVIEW does **not** add the `.lvlib` entry to the `.lvproj` itself; §8's hand edit stays |
| `lvai_graft_diagram` in place on a loaded override | "changed on disk" modal, then a LabVIEW crash on the next call; not a route into an override |
| The Handle Last Ack Core pane | read: pattern 4812 |

Still unmeasured (§19e): Register For Events with an incoming error (reasoned to hang the loop).

## Notes for review
- §19 names two tools proposed in a separate PR (`lvai_create_override`, `lvai_af_add_hook`); the manual route works
  without them.
- The section numbers 1–19 are unchanged, because CLAUDE.md cites §8, §16 and §17a.
