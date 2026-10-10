# Notes for the fork's feature branches

This branch holds notes only: no code, and it is never part of a pull request. It records what the feature branches
change, why, how each change was measured, and what upstream may want to know beyond them.

| File | What it holds |
|---|---|
| `SUMMARY.md` | A short (2–3 sentences) and a long (a paragraph per topic) summary to send |
| `CHANGES.md` | The overall change log, per branch |
| `pr/<branch>.md` | A ready-to-paste pull request description per branch |
| `MEASUREMENTS.md` | What was run in LabVIEW, with dates and results, and what is still open |
| `UPSTREAM-FINDINGS.md` | Things found in upstream v1.9.5 that the branches do not change: candidate issues |

All branches start from upstream `7702e6d` (release v1.9.5 plus a CI commit). Measured on LabVIEW 2026 Q3
(26.3.1f1) 32-bit, Windows 10. The evidence came from a production Actor Framework application and a pilot project;
neither is named here.
