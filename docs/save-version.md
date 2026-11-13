# Save version: files for an older LabVIEW

A project kept for an older LabVIEW pins its save version in the `.lvproj`:
`<Property Name="NI.LV.All.SaveVersion" Type="Str">21.0</Property>` (or `Editor version`, which
means "the LabVIEW that saves it"). Two field projects worked that way - one at 21.0 for stations
running LabVIEW 2021, a driver project at 18.0 for LabVIEW 2018 - and both ranked the same gap
first: the tools saved files in the IDE's version, and the target LabVIEW found out only when it
loaded them, on the target PC (`VI version (26.0) is newer`, load error 9, the 21.0 project, 2026-10-30; nine
saving tools wrote 25.0 into 18.0 projects, the driver project).

## 1. What the server does (since 2026-11-13)

**Every tool answer is checked** (`Infra/SaveVersions.cs`, called from `DiagnosingTool`, which every
served tool passes). Before a call, the version of each LabVIEW file its ARGUMENTS name is read;
after it, every LabVIEW file named in the arguments or the ANSWER that was written during the call
is read again. A file now saved newer than it was before, or newer than the version its project
pins, is reported:

```json
"saveVersionCheck": {
  "files": [ { "path": "...\\AddTwo.vi", "savedAs": "26.0", "was": "20.0",
               "projectSaveVersion": "20.0", "project": "...\\P20.lvproj" } ],
  "warning": "1 file(s) were saved by this call in a NEWER LabVIEW version than they had, ..."
}
```

Nothing is refused or undone there - the file is already written; the answer says so while it can
still be restored. An answer with nothing to report is not touched. The versions come from the
FILES, no LabVIEW: a resource file's `LVSR` block starts with the version word (`26008000` = 26.0,
`20008000` = 20.0, measured on files LabVIEW 2026 and 2020 wrote); an XML file carries it as the root
element's `LVVersion`. The pin that applies is the one of a project that lists the file, or lists the
library or class that owns it. Files are recognised by extension (`.vi .ctl .vim .vit .ctt .lvclass
.lvlib .lvproj`); a call naming more than 300 is not checked.

**The composer writes the pinned version** (`AixmlComposer.PinnedTarget`), on the pylabview backend:
a VI a project lists takes that project's pin; a NEW VI takes the pin every project in its folder
and up to three folders above agrees on. The composer writes files for LabVIEW 2020 and later only,
so an older pin (18.0) is reported in `versionWarning` and the file is written in the newest version
it would otherwise use.

**`lvai_generate_vi` skips the steps that save through LabVIEW** for a VI older than the running
LabVIEW: the constant tidy, the panel and diagram clean-up and the icon each SAVE, and would
rewrite the file in the running LabVIEW's version. The answer has a `savingStepsSkipped` step and
says so in `note`; the diagram is then the composer's own layout. The size measurement still runs
(a render saves nothing).

**`lvai_describe_project`** answers `saveVersion` (the pin, or `Editor version`) and
`projectFileSavedBy` (the LabVIEW that last saved the `.lvproj`).

## 2. Measured 2026-11-13

A project pinned to `20.0`, LabVIEW 2026 Q3 running, pylabview backend, a branch build over raw
stdio (`mcp_temp`, not committed):

| step | result |
|---|---|
| `lvai_describe_project` | `saveVersion: "20.0"`, `projectFileSavedBy: "20.0"` |
| `lvai_generate_vi` of two VIs the project lists | both written as LabVIEW 20.0 files, `executable: true` in LabVIEW 2026, `savingStepsSkipped` |
| `lvai_run_vi_and_read_values` in LabVIEW 2026 | 2.5 + 40 = 42.5 |
| `lvai_set_vi_icon` on one of them | saved by LabVIEW 2026 as 26.0; `saveVersionCheck`: `was 20.0`, `savedAs 26.0`, `projectSaveVersion 20.0` |
| LabVIEW 2020: the other VI | `execState 1`, ran 42.5 |
| LabVIEW 2020: the one the icon tool re-saved | the reference never opened |

## 3. Not done, and why

- **Saving through LabVIEW in an older version.** The tools that save through LabVIEW (layout,
  icons, swaps, typedef binds, class tools) still write the running LabVIEW's version; the check
  reports it. A save through the project that pins the version is what LabVIEW itself honours (the
  driver project saw `lvai_bind_typedef_constants`, which saves with the project active, write 18.0),
  but opening the project for it clashes with the rule that no tool opens or closes a project while
  agents work in parallel, and a newer LabVIEW closing an older project rewrites the `.lvproj`
  (measured in the 21.0 project). Not attempted.
- **NI's converter** (`backend: grpc`) writes the running LabVIEW's version; only the composer takes
  a target version.
- **`.lvversion` files** and a library's or class's own save version are not read: not measured here.
