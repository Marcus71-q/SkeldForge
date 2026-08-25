# Mod manager research

This document records implementation lessons reviewed while comparing SkeldForge with current Among Us mod managers.

## Package deployment

- A full BepInEx bundle is rooted at the game directory.
- Plug-ins belong under `BepInEx/plugins`.
- A ZIP may contain one or more harmless release-wrapper directories before its real content.
- A loose single-DLL release should be mapped into `BepInEx/plugins` instead of the game root.
- Loader files are instance infrastructure. A mod package that bundles another loader copy must not take ownership of an already installed runtime.

## Profiles

- Each profile or modpack needs an internal identity independent of its display name.
- Display names do not need to be unique.
- Installed-mod state and file ownership must be scoped to one profile.
- The same library package can be installed independently in multiple profiles.

## Store and updates

A future store should use a reviewed registry rather than guessing arbitrary downloads. Each entry should include its source project, release provider, supported game versions, required architecture, dependencies, incompatibilities and asset-selection rules. Downloaded assets must still pass the normal archive safety and manifest pipeline.

## Safety boundary

SkeldForge keeps isolated full game instances rather than switching plug-ins inside the user's Steam folder. This costs more disk space but gives each profile an independent game build, loader, configuration and mod set. Archive traversal checks, staged installation, ownership manifests and rollback remain mandatory even when a package is listed in a store.
