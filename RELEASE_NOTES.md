# Release notes

## 1.0.12

- Added direct `.dll` mod selection and drag-and-drop support.
- Loose DLL plugins are analyzed, tracked and installed under `BepInEx/plugins` using the same safety and conflict checks as ZIP packages.
- Updated all 15 interface languages for ZIP and DLL package selection.
- The uninstaller now also removes the fallback `%LOCALAPPDATA%\SkeldForgeData` directory for a clean default reinstall.
- Setup and recursive uninstall cleanup are now locked to SkeldForge's dedicated per-user folder, preventing an unsafe custom install path from being removed recursively.
- GitHub Actions and test-only NuGet packages were updated to their current releases.
- Changed inferred BepInEx, Reactor and MiraAPI results from a hard launch blocker to an advisory warning.
- Fixed loose DLL and ZIP mods such as HydraMenu being prevented from launching by best-effort dependency detection.
- Dependency warnings remain available in logs and mod details without blocking the isolated game instance.

## 1.0.11

- Allowed multiple isolated modpacks to use the same display name.
- Kept each same-name modpack in a unique internal instance directory.
- Allowed the same library mod to be installed independently in different modpacks.
- Preserved instance-level BepInEx loader files when a full mod bundle contains another loader copy.
- Improved detection of mod content nested under multiple ZIP wrapper directories.
- Mapped loose single-DLL packages and their configuration files into the correct BepInEx folders.

## 1.0.10

- Added a Windows installer with desktop and Start menu shortcuts.
- Added English-first setup and support for 15 interface languages.
- Completed all interface strings in every supported language.
- Fixed first-run settings failing to save when the window position was unset.
- Fixed new and refreshed modpacks inheriting unmanaged plug-ins from a modded Steam folder.
- Fixed copied plug-ins being reported as conflicts without ownership metadata.
- Improved x86 BepInEx launch diagnostics and dependency refresh.
- The uninstaller now closes SkeldForge before removing application data.

## 1.0.5

- Added the current application icon and title-bar branding.
- Added translation button assets used by SkeldTranslate.

## 1.0.4

- Refreshed dependency state from the real instance before launch.
- Fixed stale BepInEx dependency warnings after installing a provider.

## 1.0.3

- Allowed compatible `doorstop_config.ini` variants from full BepInEx bundles.
- Stopped package README, license and changelog files from replacing game files.

## 1.0.2

- Improved Among Us version detection and compatibility messages.
- Added a guard for known-incompatible TOHE/game combinations.

## 1.0.1

- Fixed isolated instances opening a second game process.
- Added Steam-aware launch handling and regression tests.

## 1.0.0

- Initial release.
