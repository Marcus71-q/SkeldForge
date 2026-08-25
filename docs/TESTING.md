# SkeldForge v1.0.2 verification

The automated suite uses only temporary synthetic directories and archives. It
does not discover, launch, copy, or clean a real Steam installation.

## Release commands

```powershell
dotnet restore .\SkeldForge.sln
dotnet build .\SkeldForge.sln -c Release --no-restore
dotnet test .\SkeldForge.Tests\SkeldForge.Tests.csproj -c Release --no-build
```

Verified on Windows x64 with the project targeting `net10.0-windows`:

```text
Build: 0 warnings, 0 errors
Tests: 29 passed, 0 failed, 0 skipped
```

## Covered release risks

- SHA-256 correctness.
- Strong Among Us directory-shape validation.
- Modpack and backup-manifest JSON round trips.
- Zip Slip using a real `../../outside.txt` entry, plus rooted paths, drive
  paths, alternate-data-stream syntax, and internal traversal.
- Safe BepInEx-shaped archive extraction inside the selected root.
- Package structure/name/version detection and duplicate library hashes.
- Blocking conflict detection for two packages that provide the same path with
  different content; identical shared content remains non-blocking.
- Tracked-file disable, re-enable, and ownership-aware uninstall behavior.
- Evidence-based BepInEx, Reactor, and MiraAPI dependency states without
  assuming requirements that are not represented in the package.
- Isolated instance creation with actual copy progress, safe mod installation,
  and a before/after SHA-256 snapshot proving the source tree was unchanged.
- User-controlled base-game refresh with a verified full instance backup, a
  changed source fingerprint, tracked-mod reapplication, and another unchanged
  source snapshot.
- Conservative cleanup classification, unknown-file preservation, complete
  backup creation, cleanup report, and verified restore.
- Injected backup failure proving cleanup stops before its first deletion.
- Restore conflict behavior proving a changed destination is not overwritten.
- English fallback, visible missing localization keys, and live Turkish switch.
- `.skeldpack` schema/format validation and proof that export contains only
  `manifest.json`.

The suite intentionally does not launch fixture executables and does not perform
automated destructive testing against Steam. Actual WPF startup and page layout
are checked separately with a local smoke run of the built application.
