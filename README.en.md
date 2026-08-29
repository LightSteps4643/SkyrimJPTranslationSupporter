# Skyrim JP Translation Supporter

*[日本語](./README.md) | English*

A tool that helps you batch-translate English Skyrim SE mods into Japanese across
an entire Mod Organizer 2 (MO2) load order. It generates translation files for
[DSD (Dynamic String Distributor)](https://www.nexusmods.com/skyrimspecialedition/mods/107676)
without modifying any ESP/ESM files at all.

The distributed release (precompiled, no source code needed, ready to use) is
published on Nexus Mods:
https://www.nexusmods.com/skyrimspecialedition/mods/189369

As an alternative distribution point in case the Nexus file is temporarily
unavailable (e.g. due to an automated quarantine check), the same package is
also published on
[GitHub Releases](https://github.com/LightSteps4643/SkyrimJPTranslationSupporter/releases).
Build steps for that package are below, under "Building".

This repository depends on [Mutagen](https://github.com/Mutagen-Modding/Mutagen),
which is licensed under [GPL-3.0](./LICENSE), so this tool's own source code is
also published under GPL-3.0.

This tool is developed using "vibe coding" with
[Claude Code](https://www.anthropic.com/claude-code) (Anthropic's AI coding
agent) and [houseCARL](https://www.nexusmods.com/skyrimspecialedition/mods/181738)
(an MCP tool for working with the Skyrim load order's data layer). While AI
assistance is used throughout, development proceeds with repeated verification
against real game data.

## Structure

- `Core/` `PickUpTarget/` `Translation/` `GenerateDsdFile/` `Program.cs` — the
  CLI itself (`SkyrimJPStringPatcher.csproj`). A 3-stage pipeline: ① extract
  translation candidates from the MO2 load order (PickUpTarget) → ② auto-
  translate via several methods (Translation) → ③ generate DSD-format JSON
  (GenerateDsdFile)
- `SkyrimJPStringPatcherGui/` — the GUI (`SkyrimJPStringPatcherGui.csproj`). A
  thin layer that just launches the CLI as a subprocess
- `Data/` — bundled corpus/glossary data
- `CREDITS.md` — credits for the technologies and data this tool relies on
- `publish-release.ps1` — script that builds the distributable package
  (self-contained, source not included)

## Building

Requires the .NET 9 SDK.

```powershell
dotnet build SkyrimJPStringPatcher.csproj
dotnet build SkyrimJPStringPatcherGui\SkyrimJPStringPatcherGui.csproj
```

To build the distributable package (self-contained, win-x64):

```powershell
.\publish-release.ps1
```

## License

[GPL-3.0](./LICENSE)

## Acknowledgements

See [CREDITS.md](./CREDITS.md).
