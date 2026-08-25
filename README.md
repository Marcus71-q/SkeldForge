# SkeldForge

SkeldForge is a Windows mod manager for Among Us. It keeps each modpack in its own game folder, so you can switch setups without repeatedly copying files into the Steam installation.

Created and maintained by **NyxShade**.

> SkeldForge is an independent community project. It is not affiliated with or endorsed by Innersloth.

## Download

Download `SkeldForge-v1.0.10-Setup.exe` from the latest GitHub release and run the installer. It adds shortcuts to the desktop and Start menu and includes the .NET runtime.

Windows may show a SmartScreen warning because community builds are not code signed. Release hashes are published alongside each download.

## Features

- Finds the Steam installation of Among Us.
- Creates independent modpack copies.
- Imports mod ZIP files into a reusable library.
- Checks archives for unsafe paths before extraction.
- Tracks installed files and reports mod conflicts.
- Detects common BepInEx, Reactor and MiraAPI dependencies.
- Enables, disables and removes tracked mods.
- Launches vanilla and modded instances separately.
- Backs up recognized mod files before cleaning the Steam game folder.
- Imports and exports `.skeldpack` configurations.

SkeldForge does not include Among Us or third-party mods. You need a legitimate Steam copy of the game and must obtain mods from their respective authors.

## Languages

English is used on a fresh installation. The language can be changed without restarting.

English, Türkçe, Русский, Español, 日本語, ไทย, Bahasa Indonesia, 한국어, 简体中文, Deutsch, Français, Português (Brasil), Italiano, العربية and हिन्दी are available.

All 15 language catalogs cover the complete interface, including setup, modpack management, backups, cleanup, settings, status messages and error dialogs.

## Building

Requirements: Windows 10/11 x64, the .NET 10 SDK and the .NET desktop development workload when using Visual Studio.

```powershell
dotnet restore .\SkeldForge.sln
dotnet build .\SkeldForge.sln -c Release --no-restore
dotnet test .\SkeldForge.Tests\SkeldForge.Tests.csproj -c Release --no-build
```

Create a self-contained build:

```powershell
dotnet publish .\SkeldForge\SkeldForge.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false `
  -o .\artifacts\publish\win-x64
```

The installer definition is in `packaging/SkeldForge.nsi` and builds with NSIS 3.12 or newer.

## Data and uninstalling

Application data is stored in `%LOCALAPPDATA%\SkeldForge`. The official installer warns before uninstalling and removes this data after confirmation.

## Troubleshooting

- If Among Us is not detected, select the folder containing `Among Us.exe` in Settings.
- If a mod conflicts, check the reported path; two different files cannot own the same destination.
- If a mod does not load, confirm that it supports the installed Among Us build and that its dependencies are present.
- Logs are available from Settings or `%LOCALAPPDATA%\SkeldForge\Logs`.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and [docs/TESTING.md](docs/TESTING.md) for implementation details.

## Contributing

Bug reports and focused pull requests are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) before submitting a change. Do not attach copyrighted game files or redistributed mod packages to issues.

## License

SkeldForge is released under the [MIT License](LICENSE). Among Us, Innersloth, and third-party mod names and assets belong to their respective owners.
