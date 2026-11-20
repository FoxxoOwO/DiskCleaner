# Changelog

All notable changes to this project are documented here. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versioning: [SemVer](https://semver.org/).

## [0.0.1] - 2026-11-20

### Fixed

- Category headers honour `defaultChecked` from `rules.json`: items now keep their own default
  (`bin`/`obj`, `target`, `__pycache__` checked; `node_modules`, `.venv` unchecked) until the user
  explicitly toggles the whole category.
- Empty categories are hidden again, so the expander list no longer overlaps the empty-state text.
- The 24-hour Temp cutoff is honoured in recycle-bin mode: only files older than the cutoff are
  moved to the bin instead of the whole folder.
- Docker pruning reports the size Docker actually reclaimed instead of the scan-time estimate.
- `DockerCliService` drains stdout and stderr concurrently and kills the process on timeout, which
  removed a deadlock that could stall the scan for a minute.
- Cleaning and dry runs stream the file tree instead of materialising `FileInfo[]` /
  `DirectoryInfo[]` for entire trees (large `node_modules` folders no longer allocate hundreds of MB).
- Cancelling a scan clears the partial result instead of leaving it selectable for cleaning.
- A directory walk that throws (IO, access or path errors) now always releases its worker slot, so a
  scan can no longer wait forever on an unreachable folder.
- In recycle-bin mode a refused path is counted as skipped instead of being deleted permanently
  behind the user's back; the summary reports it.
- Cleaned folders disappear from the list with their byte counts, instead of staying selectable at
  their pre-clean sizes.
- A dry run counts the Recycle Bin and the Docker prunes in the projected total; previously they
  were only logged, so the number was too low.
- The drive list is rebuilt after a clean, so free space in the picker is current.
- A file disappearing mid-scan no longer aborts the dry-run size computation.
- Dependency caches for Cargo, pip, Gradle, Maven and NuGet now show the "application is probably
  running" badge.

### Changed

- The Cancel button also stops a running clean, not only a scan.

- Browser caches are scanned for every Chromium profile (`User Data\*\Cache`), not just `Default`.
- The running-process badge is computed from one process snapshot per scan instead of one
  enumeration per item, which removes hundreds of process queries from the UI thread.
- Package versions are pinned (`Microsoft.WindowsAppSDK` 1.6.250602001, `CommunityToolkit.Mvvm` 8.3.2).
- Category item counts use correct Czech plurals; dry runs cap their log at 5000 entries.

### Added

- `tests/DiskCleaner.Checks`: dependency-free check runner for byte formatting, the Docker output
  parsers, the localization switch and the shipped `rules.json` contract.
- Application icon (`Assets/app.ico`, `ApplicationIcon` in the project file).
- `README.md` with build, scan-scope and safety documentation.
- Crash logging to `%LOCALAPPDATA%\DiskCleaner\crash.log` for this unpackaged app.
- `AutomationProperties.Name` on the drive picker, checkboxes and icon-only buttons.
- Clicking a category checkbox no longer collapses or expands the category.
