# Release notes

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
