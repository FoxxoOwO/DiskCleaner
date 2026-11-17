# Role & Objective
You are a Senior Windows Desktop Engineer specializing in **C# 12, .NET 8, and WinUI 3 (Windows App SDK)**.
Build a complete, production-ready, unpackaged Windows disk cleaning utility named **DiskCleaner** using the **MVVM architecture** (`CommunityToolkit.Mvvm`).

All user-facing UI labels, category names, descriptions, and impact notes MUST be in **Czech and English** (can be switched in settings).

---

## 1. Project Configuration & Architecture
1. **Target Framework:** `net8.0-windows10.0.19041.0`
2. **Deployment Mode:** Unpackaged (`<WindowsPackageType>None</WindowsPackageType>`, `<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>`) so the app has direct, unvirtualized access to `%LOCALAPPDATA%`, `%APPDATA%`, `%USERPROFILE%`, and system drives.
3. **Dependencies:**
   - `Microsoft.WindowsAppSDK` (1.6.*)
   - `CommunityToolkit.Mvvm` (8.3.*)
4. **Solution Structure:**
   - `Models/` (`ScanRule.cs`, `CategoryDefinition.cs`, `CleanResult.cs`)
   - `Services/` (`IDiskScannerService.cs`, `DiskScannerService.cs`, `ICleanerExecutionService.cs`, `CleanerExecutionService.cs`, `DockerCliService.cs`)
   - `ViewModels/` (`MainViewModel.cs`, `CleanCategoryViewModel.cs`, `CleanItemViewModel.cs`)
   - `Views/` (`MainWindow.xaml`, `MainWindow.xaml.cs`)
   - `Assets/rules.json` (Externalized static scan rules copied to output directory)

---

## 2. Core Functional Requirements

### A. Drive Selection & Scanning Engine
- Populate a `ComboBox` with all ready fixed/removable drives via `DriveInfo.GetDrives()` showing drive letter, label, and free/total space (e.g., `C:\ (Systém) - 42.5 GB volných z 512 GB`).
- **Drive-Aware Static Rules:** Expand environment variables in `rules.json` (`%LOCALAPPDATA%`, `%APPDATA%`, `%USERPROFILE%`, `%TEMP%`, `%WINDIR%`). Only include a static rule in the scan if its resolved root path matches the currently selected drive (or if it is a drive-relative rule like `$Recycle.Bin`).
- **Fast & Safe File Enumeration:** Use `DirectoryInfo.EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })` to avoid `UnauthorizedAccessException` and symlink loops.
- **Deep Artifact Scanner (Recursive on Selected Drive):**
  Scan the selected drive asynchronously (`Task.Run` with `CancellationToken` and `IProgress<string>` for real-time status updates) to detect project build artifacts. Skip system roots (`Windows`, `Program Files`, `Program Files (x86)`, `ProgramData`, `$Recycle.Bin`, `System Volume Information`) and hidden `.git` folders.
  Detect the following patterns and **DO NOT recurse into matched artifact directories** once found:
  1. `node_modules` (when sibling `package.json` exists) -> Default Checked: `false`
  2. `target` (when sibling `Cargo.toml` exists - Rust build artifacts) -> Default Checked: `true`
  3. `bin` and `obj` (when a sibling `*.csproj` or `*.fsproj` exists) -> Default Checked: `true`
  4. `.venv` or `venv` (when `pyvenv.cfg` exists inside) -> Default Checked: `false`
  5. `__pycache__` -> Default Checked: `true`

### B. Categories, Items & Czech Explanations (`rules.json` + Scanners)
Organize findings into these 6 expandable categories (`CleanCategoryViewModel`), each with a Czech description, Segoe Fluent Icon glyph, and child items (`CleanItemViewModel`) containing exact path, calculated byte size, and a specific Czech `ImpactNote` explaining what happens when deleted:

1. **Vývojářské a balíčkové cache** (`\uE943`)
   - Description: *"Globální mezipaměti správců balíčků. Smazání nepoškodí projekty, chybějící balíčky se při příštím buildu znovu stáhnou."*
   - Rules: NuGet (`%USERPROFILE%\.nuget\packages`, `%LOCALAPPDATA%\NuGet\v3-cache`), npm (`%LOCALAPPDATA%\npm-cache`), pnpm (`%LOCALAPPDATA%\pnpm-store`), Yarn (`%LOCALAPPDATA%\Yarn\Cache`), Cargo (`%USERPROFILE%\.cargo\registry\cache`, `%USERPROFILE%\.cargo\registry\src`), pip (`%LOCALAPPDATA%\pip\Cache`), Gradle (`%USERPROFILE%\.gradle\caches`), Maven (`%USERPROFILE%\.m2\repository`).

2. **Buildovací artefakty** (`\uE8F1`)
   - Description: *"Zkompilované binárky, mezivýsledky a lokální závislosti projektů nalezené na vybraném disku."*
   - Populated dynamically by the Deep Artifact Scanner (`node_modules`, Rust `target`, .NET `bin`/`obj`, Python `.venv` and `__pycache__`).

3. **Cache aplikací, prohlížečů a shaderů** (`\uE774`)
   - Description: *"Dočasná data webových prohlížečů, Electron aplikací a zkompilované grafické shadery."*
   - Rules:
     - GPU Shaders: NVIDIA (`%LOCALAPPDATA%\NVIDIA\DXCache`, `%LOCALAPPDATA%\NVIDIA\GLCache`), AMD (`%LOCALAPPDATA%\AMD\DxCache`), DirectX (`%LOCALAPPDATA%\D3DSCache`). *ImpactNote:* "Vyčistí staré shadery. U hraných her může při prvním spuštění způsobit krátké kompilování."
     - Browsers (Cache, Code Cache, GPUCache only — NEVER cookies/history/passwords): Chrome, Edge, Brave, Vivaldi, Firefox (`%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\cache2`). *ImpactNote:* "Smaže pouze dočasné soubory stránek. Neodhlásí vás z účtů ani nesmaže hesla."
     - Apps: Discord (`%APPDATA%\discord\Cache`, `Code Cache`, `GPUCache`), Spotify (`%LOCALAPPDATA%\Spotify\Storage`), VS Code (`%APPDATA%\Code\Cache`, `CachedData`, `CachedExtensionVSIXs`), Bambu Studio (`%LOCALAPPDATA%\BambuStudio\cache`).

4. **Kontejnery a virtualizace (Docker)** (`\uE7B8`)
   - Description: *"Nepoužívané Docker image, zastavené kontejnery a build cache."*
   - Implement `DockerCliService`: Query `docker system df --format "{{json .}}"` asynchronously. If Docker CLI is available and the daemon responds, add items for Reclaimable Images, Build Cache, and Stopped Containers.
   - Cleaning executes `docker image prune -a -f`, `docker builder prune -f`, and `docker container prune -f`.

5. **Rozbalené instalátory, logy a koš** (`\uE74D`)
   - Description: *"Pozůstatky po instalacích ovladačů, výpisy pádů aplikací a koš na vybraném disku."*
   - Rules: `C:\NVIDIA`, `C:\AMD`, `C:\Intel`, `%LOCALAPPDATA%\CrashDumps`, `C:\Windows\Minidump`, `C:\Windows\Logs`, and the selected drive's `$Recycle.Bin`.

6. **Dočasné složky (Temp)** (`\uE8B7`)
   - Description: *"Dočasné pracovní soubory systému a aplikací."*
   - Rules: `%TEMP%` and `C:\Windows\Temp`.
   - *Safety rule:* Skip files modified within the last 24 hours (`LastWriteTimeUtc > DateTime.UtcNow.AddHours(-24)`) by default, with a UI toggle to override.

---

## 3. Three-State Tree Selection & UI/UX (`MainWindow.xaml`)
- Use `MicaBackdrop` and native WinUI 3 styling.
- **Top Bar:** Drive `ComboBox`, "Skenovat disk" (`AccentButtonStyle`), "Zrušit" button (visible during scan), and a `ProgressRing` + current scanned path status text.
- **Main Content:** A `ScrollViewer` containing an `ItemsRepeater` of `Expander` controls (one per category, only showing categories with > 0 B found):
  - **Expander.Header:**
    - Three-state `CheckBox` (`IsThreeState="True"`, bound TwoWay to `Category.IsChecked`).
    - Category `FontIcon`, bold Category `Name`, secondary wrapped `Description`, item count badge, and formatted selected size (`SelectedSizeFormatted`).
  - **Expander.Content:**
    - `ItemsRepeater` of child `CleanItemViewModel` cards.
    - Each row has: `CheckBox` (`IsChecked`), bold `Title`, selectable monospace `Path`, yellow/caution `ImpactNote` explaining what deletion affects, a button with folder icon (`\uE8B7`) that opens the folder in `explorer.exe`, a warning badge if the owning process is currently running (e.g., Chrome/Discord detected via `Process.GetProcessesByName`), and right-aligned `FormattedSize`.
- **Two-Way Three-State Logic:**
  - Checking/unchecking a category updates all its child items and recalculates total selected bytes across the app.
  - Checking/unchecking an individual child folder updates the parent category's `IsChecked` to `true` (all checked), `false` (none checked), or `null` (indeterminate/partial) without triggering infinite recursion, and updates both the category sum and global sum immediately.
- **Bottom Action Bar:**
  - Total selected size summary (`Celkem vybráno k uvolnění: X.XX GB`).
  - Toggle switches / checkboxes:
    1. `Testovací režim (Dry Run)` (simulates deletion and logs what would be removed without deleting).
    2. `Přesunout do koše místo trvalého smazání` (uses Win32 `SHFileOperation` / `IFileOperation` with `FOF_ALLOWUNDO` when checked).
  - "Vyčistit vybrané" button (`AccentButtonStyle`) with a confirmation `ContentDialog` summarizing the categories and total size before execution.

---

## 4. Safe Deletion & Win32 Interop (`CleanerExecutionService`)
- Gracefully handle locked/in-use files (`IOException`, `UnauthorizedAccessException`): skip locked files, increment a `SkippedFilesCount`, and continue deleting remaining files in the directory.
- Delete empty subdirectories bottom-up after file removal.
- For Recycle Bin emptying on the selected drive, use P/Invoke `SHEmptyRecycleBin(IntPtr.Zero, driveRoot, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND)`.
- After cleaning completes, display a summary `ContentDialog` showing actual freed space, number of deleted items, and number of skipped (locked) files, then refresh the scan state.

---

## Deliverables
Provide the complete, compilable source code for all files (`.csproj`, `app.manifest`, `Assets/rules.json`, Models, Services, ViewModels, and `MainWindow.xaml` / `.xaml.cs`). Do not use placeholder comments like `// TODO: implement rest of rules` — write out the complete implementation.