# What the non-gRPC setup still lacks

A tracking list of what the MCP cannot do on the `pylabview` backend (our composer and reader, with
ActiveX into a running LabVIEW), and whether a tool could close each gap. It matters most where
NI's gRPC service does not exist at all - LabVIEW 2020 to 2024.

Started 2026-10-29 from `docs/aixml-backends.md` and the `Unsupported(...)` refusals in
`scripts/aixml-pylabview/aixml2vi.py`. **The composer and the reader change weekly**: re-check a row
against the code, or a probe, before working on it, and tick it here when it closes.

Feasibility: **yes** = the route is known and the pattern exists in the repo; **likely** = a route
is plausible but unmeasured; **no** = the capability is NI's own.

## 1. Tools with no non-gRPC route

| | tool | feasible? | idea |
|---|---|---|---|
| [ ] | `pylv_route` | yes | Ask "does the READER + COMPOSER round-trip this VI" instead of "does NI's converter": read, recompose to scratch, compare facts (`compare_full.py`), report unread constructs |
| [ ] | `lvai_find_palette_item` | yes | `lvai_palette_index` already answers offline; route the tool onto it |
| [ ] | `lvai_drop_palette_item` | likely | Compose a one-node `Call` and paste it, or VI Server scripting (`New VI Object`) over ActiveX |
| [ ] | `lvai_status` service check | yes | Report the ActiveX/composer readiness instead (`pylv_status` already has most of it) |
| — | `lvai_discuss_file`, `_build_from_build_specification`, `_get_application_configuration`, `_log_usage_data`, info-cache, `_filter_*`, `_monitor_*` | no | NI's assistant features. Build specifications could get their own route (`{LV.Project}` build methods over ActiveX) - likely |

## 2. Composer gaps (AIXML -> VI), refused by name

| | gap | feasible? |
|---|---|---|
| [ ] | INDEXING tunnels on For/While loops | yes - the biggest everyday gap; needs `TunnelType` + `innerLpTunDCO` from a donor |
| [ ] | other tunnel modes / `cond=` on some tunnels | yes |
| [ ] | structures beyond While, For, Case, Event, Flat Sequence, Disable, Conditional Disable, In Place pairs, Formula Node | yes per structure (donor + measurement); Timed Loop and Stacked Sequence are the candidates |
| [ ] | types without a harvested part (further tag refnums, some refnum classes, FixedConst, rings of some types, some front-panel objects) | yes, but see 4a |
| [ ] | some `In Place ...` variants | yes |
| [ ] | `Array To Cluster` in some modes | yes |
| [ ] | some Compound Arithmetic operations | yes |
| [ ] | Invoke Node methods with several outputs | yes |
| [ ] | XNode calls (Express VIs) | likely - hard; XNodes carry their own state |
| [ ] | Index Array on some input types | yes |
| [ ] | string ranges in a Case Structure | yes |
| [ ] | enum/ring items containing a quote | yes - an escaping question |
| [ ] | some events, class-dependent event data, some Event Data Nodes | yes per event (scan LabVIEW's own VIs, as the key events were) |
| [ ] | Conditional Disable operators beyond the supported ones | yes |

## 3. Reader gaps (VI -> AIXML, used by every edit)

Measured 2026-10-15 over 865 cached VIs; each unread construct is named in a comment.

| | gap | feasible? |
|---|---|---|
| [ ] | In Place Element structure (largest by count) | yes |
| [ ] | Call By Reference | yes - the composer writes it now, the reader should match |
| [ ] | Stacked Sequence, Timed Loop | yes - Timed Loop configuration is not in NI's export either |
| [ ] | Call Library Function, .NET constructor, Python Node | likely - configuration blobs |
| [ ] | Express VIs / XNodes | likely - hard |
| [ ] | terminal descriptions | yes |
| [ ] | units, sets/maps, strictly typed VI refnums | yes |
| — | LabVIEW 9.0 constants, variants holding data | no / low value - values live in compiled code |

## 4. Infrastructure

| | gap | feasible? | idea |
|---|---|---|---|
| [ ] | **4a. New parts need NI's converter** to build the donor - impossible on a 2020-only station | likely | Build the donor with VI Server scripting over ActiveX (`New VI`, `New VI Object`, `Save`) in the LabVIEW at hand, then `harvest_parts.py --clean` on it. Also: harvest from LabVIEW's own saved VIs (`resource\`, `vi.lib`), already done for some parts |
| [ ] | **4b. No wiring validation** - a fault only shows as `execState 0` | yes | After composing, read the Error List (`lvai_error_list`, already over ActiveX) and map each object back to its AIXML `uid`, which the composer knows |
| [ ] | **4c. Connector pane orientation not measured** (rotated panes, pattern 4815) | yes | Compose the probe that reads `Terminal Bounds[]` and run it over ActiveX, as other helpers already are |
| — | ActiveX needs a running LabVIEW, Windows only | no | By design: COM-started LabVIEW quits and leaves an invisible dialog. `lvai_ensure_labview` starts the process |

## 5. Not measured

| | what |
|---|---|
| [ ] | A COMPOSED VI calling DAQmx actually run against a device |
| [ ] | Clean Up Diagram answering error 2 on LabVIEW 2020 f1 (2026-10-13; predates our own layout engine) |
| [ ] | Broad coverage on LabVIEW 2020 - most measurements are on 2026 |
