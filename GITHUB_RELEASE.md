# SkeldForge 1.0.12

SkeldForge is a Windows mod manager for Among Us. It creates isolated game copies for each modpack, allowing you to switch between vanilla and modded setups without repeatedly changing the original Steam installation.

## What changed in 1.0.12

- Direct `.dll` mod selection and drag-and-drop support.
- Loose DLL plugins use the same analysis, conflict detection and ownership tracking as ZIP packages.
- DLL plugins are installed under `BepInEx/plugins` automatically.
- The uninstaller also clears the fallback local SkeldForge data directory.

## Previous 1.0.11 improvements

- Multiple modpacks can now use the same display name while remaining fully isolated.
- The same mod can be installed independently in any number of modpacks.
- Full BepInEx mod bundles no longer fail because an instance already contains loader files.
- Mod files nested under several ZIP wrapper folders are mapped to the correct game root.
- Loose DLL packages are automatically installed under `BepInEx/plugins`.

## Highlights

- Creates separate, isolated Among Us installations for modpacks
- Imports mod ZIP files safely and stores them in a reusable library
- Detects file conflicts before installation and does not overwrite conflicting files
- Recognizes common BepInEx, Reactor and MiraAPI dependencies
- Enables, disables and removes tracked mods
- Launches vanilla and modded installations separately
- Creates backups before cleaning recognized mod files from the Steam installation
- Imports and exports `.skeldpack` configurations
- Includes complete interface translations for 15 languages

## Supported languages

English, Türkçe, Русский, Español, 日本語, ไทย, Bahasa Indonesia, 한국어, 简体中文, Deutsch, Français, Português (Brasil), Italiano, العربية and हिन्दी.

English is selected on a fresh installation. You can change the interface language from Settings without restarting the application.

## Installation

1. Download `SkeldForge-v1.0.12-Setup.exe` below.
2. Run the installer.
3. Start SkeldForge using the desktop or Start menu shortcut.
4. Confirm the detected Steam Among Us folder, or select it manually.
5. Create a modpack and add a compatible mod ZIP or DLL file.

The installer is self-contained; a separate .NET installation is not required.

Windows SmartScreen may warn about an unknown publisher because this community build is not code signed. Verify the SHA-256 hash before running it:

`335A10EDB2FEB749BE16ED77C2E65263ABAEFF3311EDD1323BBAAB52336E67D4`

## How it works

SkeldForge copies your legitimate Steam Among Us installation into a separate instance folder. Mods are installed into that isolated copy and tracked using manifests. Your original game folder is not silently modified or replaced. Package paths are checked before extraction, dependencies are inspected, and conflicting files are reported instead of overwritten.

SkeldForge does not include Among Us or third-party mods. You need a legitimate Steam copy of the game and must download mods from their respective authors.

## Requirements

- Windows 10 or Windows 11, 64-bit
- A legitimate Steam installation of Among Us
- Mods compatible with your installed Among Us version

## Notes

- Mod compatibility is determined by each mod author.
- Back up important data before changing game files.
- Logs are available from Settings or `%LOCALAPPDATA%\SkeldForge\Logs`.

SkeldForge is an independent community project and is not affiliated with or endorsed by Innersloth.
