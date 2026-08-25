# Contributing

Thanks for taking the time to contribute.

## Issues

- Check that the problem still occurs on the latest release.
- Include the Among Us version, mod name/version and the relevant log excerpt.
- Remove account names, API keys and private information from logs.
- Do not upload Among Us files or third-party mod archives.

## Pull requests

Keep changes focused and explain the behavior being fixed. Add tests when changing archive handling, paths, ownership, cleanup, settings or launch behavior.

```powershell
dotnet build .\SkeldForge.sln -c Release
dotnet test .\SkeldForge.Tests\SkeldForge.Tests.csproj -c Release
```

Translations live under `SkeldForge/Languages`. Preserve placeholders such as `{0}` and keep files as valid UTF-8 JSON.
