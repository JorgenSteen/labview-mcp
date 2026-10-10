# Class and library icons, icon read-back, and keeping a VI's own icon layer

## Why
`lvai_set_vi_icon` sets one VI's icon, but nothing sets a class or library icon, applies it to the members, or reads
icons back to check them. Doing it by hand runs into four silent failures, each measured:
- `ApplyIconToVIs` needs the project closed and saves nothing itself;
- on a plain `.lvlib` it changes nothing unless the member VIs were opened first;
- `Save VI Icon to File` writes nothing into a missing folder or past MAX_PATH, and reports no error;
- `lvai_set_vi_icon` with an image flattens the icon, so a member loses its class template layer.

## What
- **`lvai_set_owner_icon(ownerPath, pngPath, applyToMembers, memberPaths?, readBackDir?)`**: sets a `.lvclass` or
  plain `.lvlib` icon from a 32x32 PNG. With `applyToMembers` it opens every member, runs ApplyIconToVIs, saves each
  member and the owner, and returns a code per member. File checks before LabVIEW (owner kind, writable, PNG 32x32,
  members from the owner file, path rules); refuses with a project open (`projectOpen`); `failedAtStep` per stage.
  Verified by read-back: owner icon pixel-compared with the PNG; every member's `templateMissing` must be 0; layer
  data read from the file.
- **`lvai_read_icons(paths, outDir, expectedDir?, …)`**: VIs, classes, libraries or folders to PNG, 100 VIs per
  helper call; output folders created first; paths over 259 characters or outside Latin-1 refused before LabVIEW;
  with `expectedDir`, a per-icon verdict (OK / DIFF / SIZE / NO REFERENCE / AMBIGUOUS / ERROR) with the first
  differing pixels.
- **`lvai_set_vi_icon keepOwnLayer` (+ `ownerPath`)**: re-applies the owner's template to one member and keeps its
  own layer; takes no image. Reports layer data and pixels changed outside the template.
- New `Infra/PngPixels.cs` (PNG decoder and compare; `IconImage.cs` only encodes), `Infra/IconOwners.cs`,
  `Infra/ViIconLayers.cs`; eight helpers in `scripts/` (`lvai_icon_*`); `docs/class-icons.md`; catalogue and
  knowledge-table rows; the class generator agent may use the two owner tools.

## Measured (2026-10-10, LabVIEW 2026 Q3 32-bit, a throwaway actor project)
| Check | Result |
|---|---|
| Helpers generate and run through the 1.9.5 runner | all codes 0, no `helperAixmlInvalid` |
| Class icon, 9 members, `applyToMembers` | 6.9 s; all codes 0; owner 0 px different; `templateMissing` 0 of 378 on every member; template layer present; all members state 1 |
| Same with a project open | `projectOpen`, nothing changed |
| `memberPaths` = one member | only the class and that member changed; no sibling left modified in memory |
| Plain `.lvlib`, 2 member VIs | 4.7 s; only `NI.Lib.Icon` changed in the library; members saved, state 1 |
| Read-back, 12 items, compare with themselves | 12 of 12 OK; a PNG altered in 3 px → DIFF naming those 3 pixels |
| Read-back, 114 VIs | two helper calls (100 + 14), 114 PNGs, 20.9 s |
| Missing nested output folder | created and written |
| A 292-character PNG path | `pngPathTooLong` in 0.0 s, no LabVIEW call |
| `keepOwnLayer` after a class icon change | 2 layers before and after; 0 px changed outside the template; the template layer is **replaced**, not stacked |
| Control: plain `lvai_set_vi_icon` with an image | flattened (VITS 146 bytes) |
| A path outside Latin-1 | Error 7 and no PNG → **fixed**: refused up front with `pathNotLatin1` |

Not measured: the list-file twin of the apply helper (more than 100 members); an editable text layer made in the
icon editor.

## Tests
All new checks, plans and the PNG decoder are unit-tested without LabVIEW. 2632 of 2633 pass; the one failure is the
existing example-index test for CompactRIO examples that are not installed on the measuring PC.

## Notes for review
- `docs/guide/tools.md` and README carry a tool count that other open branches also change.
- `IconTools.PngSize` now duplicates `PngPixels.Size`; left alone to keep this change focused.
