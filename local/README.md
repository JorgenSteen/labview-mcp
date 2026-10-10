# local/ - the user's own build of labview-mcp

This folder exists only on the fork's `main`, which is the user's own version: upstream
(Zuehlke/labview-mcp) plus every feature branch merged in. It is **never part of a pull request**.
The PR branches (`docs/af-lifecycle-overrides`, `feat/class-icons`, `feat/af-overrides`,
`feat/restart-guards`) stay as they are and do not contain it.

`Update-LocalPlugin.ps1` builds an installable plugin folder from `main` the same way
`.github/workflows/release.yml` assembles its staging tree (Release build + embedded-docs check,
self-contained single-file publish with a version stamp, pylabview bundle, plugin/, scripts/,
docs/, .claude/agents, VERSION.txt, plugin.json version, the release's asserts), and writes a local
marketplace next to it:

```
C:\project\claude\labview-mcp-local-plugin\
  .claude-plugin\marketplace.json   marketplace "labview-local", plugin "labview-mcp", source ./labview-mcp
  labview-mcp\                      the plugin folder (bin\LabVIEWMCP.exe, agents\, hooks\, .mcp.json, ...)
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
- It refuses to replace the folder while a `LabVIEWMCP.exe` from it is running.

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

```
git checkout main            # with the change merged in, see below
powershell -NoProfile -ExecutionPolicy Bypass -File local\Update-LocalPlugin.ps1
claude plugin marketplace update labview-local
claude plugin update labview-mcp@labview-local
```

Then restart Claude Code (`claude plugin update` says "restart required to apply"). Every build has a
new version, which is what `update` compares.

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
