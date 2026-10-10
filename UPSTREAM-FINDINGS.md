# Findings in upstream v1.9.5 outside the feature branches (2026-10-10)

Candidate issues or small pull requests. Found while building and measuring the feature branches.

| # | Finding | Where | Kind |
|---|---|---|---|
| U1 | **Unit tests write into the real helper cache.** A `dotnet test` run leaves a 14-byte stub `lvai_run_and_read_typed.vi` (text "new-and-longer") in `%TEMP%\LabVIEWMCP\helpers`: the fake service writes `ViFileContent` for every convert, including the runner helper that the convert's executable check generates. The helper cache compares only file times, so every runner call on that PC then answers Error 1059, an installed plugin's included. Measured 2026-10-10. Fix: per-test helper directory; the cache should also reject a file that is not a VI. | `tests/LabVIEWMCP.Tests/Tools/AixmlToolsTests.cs` (`Flags_an_overwrite_of_an_existing_vi`), `HelperCache` | bug |
| U2 | The LUnit agent prescribes `Stop-Process -Name LabVIEW -Force` for Error 1562, which loses unsaved work. (Changed on `feat/restart-guards`.) | `.claude/agents/labview-lunit-unit-test.md` | risky |
| U3 | The DQMH window search and the save-dialog watch filter on `IsWindowVisible`, but LabVIEW dialogs can report themselves invisible while on screen (upstream's own `docs/scratch-vis-in-memory.md` §2a). | `DqmhTools.cs`, `DqmhDialogWatch.cs` | bug |
| U4 | `LabViewLauncher`'s Explorer route cannot pass arguments. | `Grpc/LabViewLauncher.cs` | limitation |
| U5 | `LabViewUpSeconds` takes the youngest of several LabVIEW processes without saying so. | `StatusTools.cs` | minor |
| U6 | `ProjectIsActiveAsync` and the close helpers generate a missing helper on demand; the validation leaves an `LVMCP Validate` VI in memory, which can raise a save prompt at the next close. | `ActionTools.cs` | risky |
| U7 | `aixml_lint.py` reports a net consumed only by a tunnel's `cond=` as `net-unconsumed` (also on upstream's own `lvbd_connect_by_names.xml`). | `scripts/aixml_lint.py` | false positive |
| U8 | `{LV.Library}` is missing from `lvai_vi_server_reference`. | VI Server catalogue | gap |
| U9 | The MCP server info hardcodes `Version = "0.1.0"`, while `--version` and `lvai_status` report the stamped version. | `Program.cs:155` | bug |
| U10 | `KnowledgeTools.Essentials` rule 5 says a `Call` to project-local code is rejected; it resolves once the code is loaded, and loaded library members resolve too. The essentials are the first thing `lvai_aixml_reference` returns. | `KnowledgeTools.cs:60-62` | outdated |
| U11 | Paths outside Latin-1 in helper list files fail with Error 7 and no clear code (fixed for the icon tools on `feat/class-icons`); other list-file helpers likely share it. | list-file helpers | bug |
| U12 | AF review items not covered by the branches: see `docs/af-lifecycle-overrides` §0 and the remaining CLAUDE.md / guide points (message tools for interface methods are still refused; launch hazards of `runForMs`; `lvai_graft_diagram` says nothing about class members or overrides). | various | docs |
| U13 | **`lvai_graft_diagram` in place on a loaded VI crashes LabVIEW.** The in-place route copies `panelViPath` over `outputViPath` before its first LabVIEW step; with the output loaded in an open project, LabVIEW raised "…has changed on disk since last saved or loaded by LabVIEW… resulting in a corrupt VI" [Revert] [Cancel], the call gave no answer within 300 s, and after Cancel the next project call was followed by a fatal LabVIEW crash (its own crash report and dump). Measured 2026-10-10. Fix: refuse an output VI that is loaded, or close it first; never wait on a dialog. | `GraftTools.cs` | bug |
