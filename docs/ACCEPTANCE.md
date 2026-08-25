# SkeldForge v1.0.0 acceptance matrix

This matrix is reviewed after the release build and tests. “Automated” means a
repeatable test uses temporary directories and synthetic data; no real Steam
installation is modified.

| Area | Verification |
| --- | --- |
| Identity and metadata | Build output metadata, About page, README |
| Steam/game detection | Unit/integration tests plus first-run UI inspection |
| Strong path validation | Synthetic valid and invalid game directories |
| Isolated instance lifecycle | Temporary source copy; source hash unchanged |
| ZIP traversal rejection | Mandatory `../../outside.txt` archive test |
| ZIP analysis/library hashing | Safe synthetic package and duplicate import tests |
| Ownership/conflicts/toggling | Two synthetic packages and tracked-file tests |
| Dependency evidence | Synthetic BepInEx/Reactor/MiraAPI filenames/references |
| Vanilla cleanup | Synthetic game tree; backup-first and unknown-preservation tests |
| Backup restore conflicts | Changed destination file test |
| `.skeldpack` exchange | Round-trip and invalid format/schema tests |
| Localization fallback | Missing key/culture tests and live language switch |
| UI workflows | Build, startup smoke test, and rendered window inspection |
| Publish output | `dotnet publish` win-x64 self-contained output and executable |

## Final v1.0.0 checklist review

Reviewed on 2026-08-23 against the Release build and the synthetic test suite:

- [x] Product identity is SkeldForge v1.0.0 and metadata credits NyxShade.
- [x] Steam registry/known-location and multi-library detection are implemented.
- [x] Automatic and manual Among Us selection use four-part validation.
- [x] Modpack create, open, rename, duplicate, delete, search, sort, edit-version,
  metadata export/import, and launch actions are wired to real services.
- [x] Every modpack owns an independent staged/copy-verified game instance.
- [x] Normal instance and mod paths are rejected if they target the Steam source.
- [x] ZIP browse/drop, pre-install analysis, wrapper-layout recognition, editable
  detected name/version, and unusual-safe-package review are implemented.
- [x] ZIP traversal/rooted/ADS/symlink/reparse/duplicate-target protections are
  enforced; `../../outside.txt` writes nothing outside staging in the test.
- [x] Mod Library retention and SHA-256 duplicate detection are implemented.
- [x] Tracked ownership drives conflict blocking, enable/disable, and uninstall.
- [x] Evidence-based BepInEx/Reactor/MiraAPI states are implemented without
  silent downloads or unsupported universal dependency assumptions.
- [x] Modded launch validates and launches the managed instance executable only;
  vanilla launch validates the source and prefers Steam protocol dispatch.
- [x] SkeldForge, game-source, mod, and modpack versions are separate fields.
- [x] Compatibility states do not infer success merely from installation.
- [x] Game-source fingerprint comparison is visible; instance update is explicit,
  warns about compatibility, creates a full verified backup, stages the new base,
  and reapplies tracked enabled mods.
- [x] Vanilla Cleanup explains scanning, known/unknown/protected behavior,
  confirmation, backup, and reporting before offering a scan.
- [x] Cleanup preview is conservative; unknown files remain unselected/untouched.
- [x] Every planned cleanup file is hashed and backed up before deletion; injected
  backup failure proves the cleanup stops before its first removal.
- [x] Cleanup produces removed/backed-up/skipped/unknown/error reporting and a
  final rescan without claiming unverifiable “100% vanilla” status.
- [x] Cleanup and full-instance backups appear on Backups; view, restore,
  conflict report, explicit overwrite approval, and confirmed delete are wired.
- [x] English/Turkish catalogs have identical key sets and switch live.
- [x] Settings covers language, game/data/backup paths, log/instance/library
  folders, window state, and a preference-only reset.
- [x] Startup, detection, copy/import/conflict/launch/cleanup/backup/restore and
  exception logging are implemented without unrelated sensitive collection.
- [x] Common invalid path/JSON/ZIP/disk/permission/lock/conflict/backup/launch
  failures return structured results and localized user summaries.
- [x] `.skeldpack` export contains metadata only; import validates version/schema,
  matches local hashes, lists missing mods, and never invents downloads.
- [x] Release build completes with 0 warnings and 0 errors; 25/25 tests pass.
- [x] README, architecture, testing, release notes, license, build/run/publish
  instructions, and known limitations are present.
- [x] Repository/package audit found no Among Us binaries/assets or bundled mod
  archives.
- [x] The self-contained win-x64 publish contains `SkeldForge.exe` and both
  language catalogs; the published executable passed a live startup/render test.
