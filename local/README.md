# local/ - the user's own build of labview-mcp

This folder exists only on the fork's `main`, which is the user's own version: upstream
(Zuehlke/labview-mcp) plus every feature branch merged in. It is **never part of a pull request**.
The feature and PR branches (`docs/af-lifecycle-overrides`, `feat/class-icons`, `feat/af-overrides`,
`feat/restart-guards`, `feat/class-housekeeping-tools`, `fix/pre-push-gate`) stay as they are and do
not contain it.

`Update-LocalPlugin.ps1` builds an installable plugin folder from `main` the same way
`.github/workflows/release.yml` assembles its staging tree (Release build + embedded-docs check,
self-contained single-file publish with a version stamp, pylabview bundle, plugin/, scripts/,
docs/, .claude/agents, VERSION.txt, plugin.json version, the release's asserts), and writes a local
marketplace next to it:

```
C:\project\claude\labview-mcp-local-plugin\
  .claude-plugin\marketplace.json   marketplace "labview-local", plugin "labview-mcp", source ./<last build's folder>
  labview-mcp\                      a plugin folder (bin\LabVIEWMCP.exe, agents\, hooks\, .mcp.json, ...)
  labview-mcp-b\                    its sibling: updates alternate between the two, see "Every later update"
```

It never installs, enables or disables anything. The version is
`<upstream version>-local.<yyyyMMdd-HHmm>+<short sha>`, e.g. `1.9.5-local.20261010-1532+9a20a76`
(date and time joined by a hyphen because NuGet refuses `.0930`, a numeric identifier with a leading
zero; see the script's help). `claude plugin list` and `LabVIEWMCP.exe --version` show it.

## Build

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File local\Update-LocalPlugin.ps1
```

- `-Check` prints what it would do (version, inputs, paths) and writes nothing.
- It refuses when tracked files have uncommitted changes; `-AllowDirty` builds anyway and marks the
  version `.dirty`.
- It reuses `tools\pylabview\runtime` when that is complete and patched; `-Reprovision`
  (optionally `-PythonSource <CPython folder with Pillow>`) rebuilds it with `provision.ps1`.
- It refuses to replace the folder while a `LabVIEWMCP.exe` from it is running; build into the
  sibling folder then (`-Destination`, see "Every later update").

## One-time swap (store plugin -> local build)

After the first build (optionally check the manifests first with
`claude plugin validate C:\project\claude\labview-mcp-local-plugin`):

```
claude plugin marketplace add C:\project\claude\labview-mcp-local-plugin
claude plugin disable labview-mcp@zuehlke-labview
claude plugin install labview-mcp@labview-local
```

Then restart Claude Code. Keep only one of the two enabled: both are named `labview-mcp` and serve
the same tool names.

## Every later update

**Build into the folder NO session runs from, and no session has to close.** Claude Code runs a
local-directory plugin straight from the build folder (measured 2026-10-10; `claude plugin list` shows
"Read from: …\labview-mcp-local-plugin\<folder>"), so while a session is open its `bin\LabVIEWMCP.exe`
is running and locked, and the script refuses to replace that folder. It does not have to: build
into the sibling instead. `-Destination` sets the folder, and the script points the marketplace's
`source` at it.

```
git checkout main            # with the change merged in, see below
claude plugin list           # "Read from:" names the folder in use - build into the OTHER one
powershell -NoProfile -ExecutionPolicy Bypass -File local\Update-LocalPlugin.ps1 -Destination C:\project\claude\labview-mcp-local-plugin\labview-mcp-b
claude plugin marketplace update labview-local
claude plugin update labview-mcp@labview-local
```

(When "Read from" names `labview-mcp-b`, leave `-Destination` out: the default is `labview-mcp`.)

Measured 2026-11-06 with two sessions running their servers from `labview-mcp`: the build, both
`claude plugin` commands and the pre-push gate left both servers running, and `claude plugin list`
then read from `labview-mcp-b`. Each session keeps the build it started with; a session restarted
afterwards gets the new one (`claude plugin update` says "Restart to apply changes"). `update`
compares versions, and every build has a new one. It copies each version into its own folder under
`%USERPROFILE%\.claude\plugins\cache\labview-local\labview-mcp\` and removes none of them.

The folder left behind is the next update's destination: once no `LabVIEWMCP.exe` runs from it, the
script replaces it. To clean up earlier, delete it only when no `LabVIEWMCP.exe` runs from it
(`Get-Process LabVIEWMCP | Select-Object Id, Path`).

## Switching back to the store plugin

```
claude plugin disable labview-mcp@labview-local
claude plugin enable labview-mcp@zuehlke-labview
```

Then restart Claude Code. To remove the local one entirely afterwards:
`claude plugin uninstall labview-mcp@labview-local` and
`claude plugin marketplace remove labview-local`.

## Keeping main up to date

Upstream changes:

```
git checkout main
git fetch upstream
git merge upstream/main
```

Feature-branch fixes: commit them on their branch (in its worktree) as usual, then merge the branch
into `main` again (`git merge --no-ff <branch>`). The tool counts in `README.md` and
`docs/guide/tools.md` are asserted by `ToolCatalogueTests`, so a merge that adds tools on both sides
needs the counts recomputed. Rebuild with the script afterwards.
