# A VI that keeps running: `lvai_running_vi`

`lvai_run_vi_and_read_values` runs a VI and ends it within one call (with `runForMs` it takes a
snapshot and aborts). An application, a DQMH tester or a Run VI is different: it is started once and
then operated - a button pressed, a value changed, an indicator read - and stopped at the end. Both
field projects built their own helper family for that (one: launch, signal and snapshot
helpers driven over ActiveX; the other: a helper VI per run to press, set and read a tester), and
both met the same wall: a helper run through NI's service runs in the add-on's application instance,
where a VI running in LabVIEW's main instance is not found (error 1004, measured in one of them).

`lvai_running_vi` does it over ActiveX, in LabVIEW's main instance, with no NI service. One action
per call:

| action | what it does |
|---|---|
| `start` | sets `valuesJson`'s controls, opens the front panel, runs WITHOUT waiting, returns. The open panel keeps the VI in memory after the call lets go of it. A broken VI is not run; one already running is left as it is. |
| `read` | the execution state and the values; `controls` names them one per line, else every control and indicator of the panel (read from the file by pylabview's reader - ActiveX cannot list them) |
| `set` | `valuesJson` with `SetControlValue`: NO Value Change event fires |
| `press` | each boolean in `controls` written TRUE with Value (Signaling): its Value Change event fires |
| `signal` | `signalsJson`, `[{"control":..,"value":..}]`, each written with Value (Signaling) in order, `signalGapMs` between |
| `stop` | presses `stopControl` if given, waits up to `waitMs` for the VI to end, then ABORTS it if it still runs (`abort: false` leaves it), and closes its panel |

Value (Signaling) is not in ActiveX, so `press` and `signal` run a composed helper,
`scripts/lvai_signal_running.xml`. It is the signal section of `lvai_run_for_ms.xml` - the part
`lvai_run_vi_and_read_values signalsJson` uses - copied node for node, with Open VI Reference by path
in place of the run: over ActiveX the helper runs in the main instance, so the path reaches the VI
already running there.

## Measured 2026-11-13

LabVIEW 2026 Q3, pylabview backend, a branch build over raw stdio, a composed `Pump.vi` (a While
Loop every 50 ms: `out` = `gain` x 2, `loops` = the iteration, a switch `stop` ending the loop, a
latched `Go`):

| call | answer |
|---|---|
| `start`, `gain` 1.5 | running, panel open |
| `read` (no names) | all six: `out` 3, `loops` 19 |
| `set` `gain` 5, then `read` | `out` 10 |
| `press` `Go` (latched) | error 1193, named as a latch |
| `signal` `gain` 7, then `read` | `out` 14 |
| `stop` with `stopControl` `stop` | the VI ended through its own loop, no abort |
| `start` again, `stop` with `waitMs` 500 | aborted after 844 ms |

**A latched boolean cannot be pressed.** VI Server writes neither Value nor Value (Signaling) of a
boolean whose mechanical action is a latch (error 1193); the answer names it. One field harness pressed
switch-action booleans only, for the same reason.

**`signalled` lists only the writes that took.** `press` and `signal` run the helper once per
write and stop at the first refusal: `failed` names that write and `notSent` the ones after it.
Until 2026-11-13 one helper run took them all and `signalled` echoed the request - an e2e press on a
field project's latched `OK` answered 1193 with `signalled: ["OK = TRUE"]`.

**A stop that closes the panel lets the VI leave memory**, so a later `read` opens it from disk and
shows the values saved in the file, not the ones it ended with (measured: `out` 0 after the run above).
