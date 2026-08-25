# SkeldForge architecture

SkeldForge v1.0.0 uses a WPF/MVVM-style presentation layer over explicit core
services. UI code requests operations and renders their results; it does not
perform archive extraction, recursive deletion, or game-copy logic itself.

## Layers

- `Models` contains JSON-serializable settings, modpack, package, dependency,
  conflict, cleanup, backup, and exchange contracts. Each persisted root has a
  schema or format version so a future release can migrate it.
- `Services` contains infrastructure: per-user paths, safe JSON defaults,
  settings, localization, logging, SHA-256 hashing, dialogs, ZIP helpers, and
  common filesystem operations.
- `Core` contains Steam/game detection, version/fingerprint comparison,
  isolated instance lifecycle, package analysis, library import, mod ownership,
  dependency/conflict detection, cleanup/backup/restore, `.skeldpack`
  exchange, and distinct launch paths.
- `ViewModels` exposes observable state and commands. Long-running operations
  accept cancellation/progress and never rely on timer-driven fake progress.
- `Views` contains XAML-only layout and narrowly scoped event plumbing for
  platform interactions such as drag/drop and window chrome.

## Data roots

Bootstrap settings live under `%LOCALAPPDATA%\SkeldForge\Config`. Unless the
user selects another data root, the same per-user area contains:

```text
SkeldForge/
├── Backups/
├── Config/
├── Instances/<modpack-id>/
│   ├── Game/
│   └── modpack.json
├── Logs/
└── ModLibrary/<mod-id>/
    ├── package.zip
    └── mod.json
```

The original Steam directory is read as a source during instance operations.
Only the explicit Vanilla Cleanup workflow is permitted to remove recognized
items from it.

## Transaction boundaries

Instance creation and duplication copy into a sibling staging directory and
commit by a final directory move. Mod installation analyzes the archive, stages
and hashes files, checks ownership conflicts, applies files, and only then saves
metadata; failed applies attempt to roll back their own writes. Cleanup first
copies and hashes the entire removal set into a completed manifest and stops on
any backup failure before deletion begins.

## Security boundaries

Archive entry names are normalized and resolved with `Path.GetFullPath`. A file
is eligible for extraction only when its canonical destination is a descendant
of the canonical staging root. Rooted, traversal, alternate-data-stream, and
ambiguous paths are rejected. Cleanup uses positive loader/plugin signatures;
unknown files are reported but never selected automatically.
