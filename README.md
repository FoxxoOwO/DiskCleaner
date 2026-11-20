# DiskCleaner

Unpackaged WinUI 3 (Windows App SDK 1.6) disk cleanup utility for Windows 10/11.
Scans a selected drive for developer caches, build artifacts, browser/Electron caches,
Docker leftovers, installers, logs and temp files, then deletes only what you tick.

All user-facing text is bilingual (Czech / English) and can be switched in the settings flyout.

## Requirements

- Windows 10 1809 (build 17763) or newer
- .NET 8 SDK
- Optional: Docker CLI on `PATH` (the Docker category is skipped when the daemon does not answer)

## Build and run

```powershell
dotnet build                 # x64 by default; also builds for x86 / ARM64 via -p:Platform=
dotnet run                   # starts the unpackaged app
```

The build is self-contained (`WindowsAppSDKSelfContained=true`) and unpackaged
(`WindowsPackageType=None`), so the app reads the real `%LOCALAPPDATA%`, `%APPDATA%`,
`%USERPROFILE%` and drive roots without virtualization.

## Checks

```powershell
dotnet run --project tests/DiskCleaner.Checks
```

A dependency-free console runner that compiles the WinUI-free sources directly (no test
framework, no restore): byte formatting, Docker output parsers, the localization switch, the
shipped `Assets/rules.json` (category set, bilingual texts, duplicate paths, known environment
variables, the 24 h `%TEMP%` cutoff, the `$Recycle.Bin` rule) and the deletion service (dry run
counts only files past the cutoff and touches nothing, a real run removes a temp sandbox).
Exit code 1 on failure.

## What is scanned

| Category | Content |
| --- | --- |
| Developer and package caches | NuGet, npm, pnpm, Yarn, Cargo, pip, Gradle, Maven |
| Build artifacts | `node_modules`, Rust `target`, .NET `bin`/`obj`, Python `.venv`/`venv`, `__pycache__` |
| App, browser and shader caches | NVIDIA/AMD/DirectX shader caches, Chrome/Edge/Brave/Vivaldi caches (every profile), Firefox `cache2`, Discord, Spotify, VS Code, Bambu Studio |
| Containers and virtualization | Docker reclaimable images, build cache, stopped containers |
| Installers, logs and recycle bin | `C:\NVIDIA`, `C:\AMD`, `C:\Intel`, `CrashDumps`, `C:\Windows\Minidump`, `C:\Windows\Logs`, selected drive's `$Recycle.Bin` |
| Temp folders | `%TEMP%`, `C:\Windows\Temp` (files newer than 24 h are skipped unless overridden) |

Static rules live in `Assets/rules.json` and are drive-aware: a rule is only used when its
expanded path is on the selected drive. Paths may contain environment variables and `*`
segments (for example `%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\cache2`).

```json
{
  "path": "%USERPROFILE%\\.nuget\\packages",
  "titleCs": "NuGet packages",
  "titleEn": "NuGet packages",
  "impactCs": "…",
  "impactEn": "…",
  "defaultChecked": true,
  "processNames": ["dotnet", "MSBuild"]
}
```

`processNames` drives the "application is probably running" badge; it is one snapshot of the
running process list per scan, not one query per item.

## Safety

- **Dry run** only counts and logs what would be removed.
- **Recycle bin** mode routes deletions through `SHFileOperation` with `FOF_ALLOWUNDO`;
  the 24 h Temp cutoff is applied per file even in that mode. If the shell refuses a path
  (long path, protected folder) it is counted as skipped — the app never silently deletes
  permanently instead.
- The **Cancel** button stops a running clean as well as a scan; partial results stay listed
  until the next scan.
- Locked or in-use files are skipped and counted, never forced; empty folders are removed
  bottom-up afterwards.
- Deep artifact scanning skips `Windows`, `Program Files`, `Program Files (x86)`,
  `ProgramData`, `$Recycle.Bin`, `System Volume Information` and `.git`, and never recurses
  into a matched artifact folder.
- A crash is logged to `%LOCALAPPDATA%\DiskCleaner\crash.log`; every clean run writes its own log.

## Layout

```
Models/     ScanRule, CategoryDefinition, CleanResult, Format, Localization, RunningProcesses
Services/   DiskScannerService, CleanerExecutionService, DockerCliService (+ interfaces)
ViewModels/ MainViewModel, CleanCategoryViewModel, CleanItemViewModel
Views/      MainWindow.xaml(.cs)
Assets/     rules.json
```

## Known limits

- Docker pruning is global (`docker image/builder/container prune`), not scoped to the selected drive.
- Paths longer than `MAX_PATH` cannot go to the Recycle Bin; in recycle-bin mode they are
  skipped and reported in the summary instead of being deleted permanently.
- The checks runner covers the models, the static rules and the deletion service (dry run,
  24 h cutoff, real removal in a temp sandbox); the view models and WinUI views are not
  exercised automatically yet.
