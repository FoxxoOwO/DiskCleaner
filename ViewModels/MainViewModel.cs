using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskCleaner.Models;
using DiskCleaner.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DiskCleaner.ViewModels;

/// <summary>
/// Drives the whole window: drive picker, scan/clean lifecycle, three-state selection total,
/// bilingual labels and the language switch.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly IDiskScannerService _scanner;
    private readonly ICleanerExecutionService _cleaner;
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _cancellation;
    private long _totalSelectedBytes;

    public MainViewModel(IDiskScannerService scanner, ICleanerExecutionService cleaner, DispatcherQueue dispatcher)
    {
        _scanner = scanner;
        _cleaner = cleaner;
        _dispatcher = dispatcher;

        Categories.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasFindings));
            OnPropertyChanged(nameof(EmptyStateVisibility));
        };

        LoadDrives();
        _statusText = Localization.Pick(
            "Vyberte disk v seznamu a spusťte skenování.",
            "Select a drive from the list and start the scan.");
    }

    public ObservableCollection<DriveItem> Drives { get; } = new();

    public ObservableCollection<CleanCategoryViewModel> Categories { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartScan), nameof(CanClean))]
    private DriveItem? _selectedDrive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartScan), nameof(CanInteract), nameof(CanClean),
        nameof(ScanProgressVisibility), nameof(CancelVisibility), nameof(EmptyStateVisibility),
        nameof(ScanningStateVisibility))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartScan), nameof(CanInteract), nameof(CanClean))]
    private bool _isCleaning;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>Simulates the deletion and only logs what would be removed.</summary>
    [ObservableProperty]
    private bool _dryRun;

    /// <summary>Uses SHFileOperation with FOF_ALLOWUNDO instead of a permanent delete.</summary>
    [ObservableProperty]
    private bool _useRecycleBin;

    /// <summary>Lets the Temp category include files younger than 24 hours.</summary>
    [ObservableProperty]
    private bool _includeRecentFiles;

    /// <summary>0 = Czech, 1 = English; bound by the settings flyout.</summary>
    [ObservableProperty]
    private int _selectedLanguageIndex = (int)Localization.Language;

    public bool HasFindings => Categories.Any(category => category.Items.Count > 0);

    public bool CanStartScan => !IsScanning && !IsCleaning && SelectedDrive is not null;

    public bool CanInteract => !IsScanning && !IsCleaning;

    public bool CanClean => CanInteract && _totalSelectedBytes > 0;

    public Visibility ScanProgressVisibility => IsScanning ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CancelVisibility => IsScanning ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EmptyStateVisibility =>
        !IsScanning && !HasFindings ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ScanningStateVisibility => IsScanning ? Visibility.Visible : Visibility.Collapsed;

    public long TotalSelectedBytes => _totalSelectedBytes;

    public string TotalSelectedText => Localization.IsCzech
        ? $"Celkem vybráno k uvolnění: {Format.Bytes(_totalSelectedBytes)}"
        : $"Total selected for release: {Format.Bytes(_totalSelectedBytes)}";

    // ----- static labels (refreshed when the language changes) -----------------

    public string DriveLabelText => Localization.Pick("Disk", "Drive");

    public string ScanButtonText => Localization.Pick("Skenovat disk", "Scan disk");

    public string CancelButtonText => Localization.Pick("Zrušit", "Cancel");

    public string CleanButtonText => Localization.Pick("Vyčistit vybrané", "Clean selected");

    public string DryRunText => Localization.Pick("Testovací režim (Dry Run)", "Dry run (test mode)");

    public string RecycleBinText => Localization.Pick(
        "Přesunout do koše místo trvalého smazání",
        "Move to Recycle Bin instead of deleting permanently");

    public string IncludeRecentText => Localization.Pick(
        "Zahrnout i soubory mladší 24 hodin (Temp)",
        "Include files younger than 24 hours (Temp)");

    public string SettingsText => Localization.Pick("Nastavení", "Settings");

    public string LanguageLabelText => Localization.Pick("Jazyk", "Language");

    public string EmptyStateText => Localization.Pick(
        "Zatím nejsou k dispozici žádná data. Vyberte disk a spusťte skenování.",
        "No data yet. Select a drive and start the scan.");

    public string ScanningStateText => Localization.Pick(
        "Procházím vybraný disk. Velké disky a plné balíčkové cache mohou trvat i několik minut – průběh vidíte v horní liště.",
        "Scanning the selected drive. Large drives and full package caches can take several minutes – progress is shown in the top bar.");

    public string ConfirmTitle => Localization.Pick("Potvrdit vyčištění", "Confirm cleaning");

    public string ConfirmButtonText => Localization.Pick("Vyčistit", "Clean");

    public string CloseButtonText => Localization.Pick("Zavřít", "Close");

    public string CancelDialogText => Localization.Pick("Zpět", "Back");

    public string CleanDoneTitle => Localization.Pick("Vyčištění dokončeno", "Cleaning finished");

    public string NothingSelectedTitle => Localization.Pick("Nic není vybráno", "Nothing is selected");

    public string NothingSelectedText => Localization.Pick(
        "Zaškrtněte alespoň jednu položku, kterou chcete vyčistit.",
        "Check at least one item you want to clean.");

    // ----- drive list ---------------------------------------------------------

    private void LoadDrives()
    {
        var previous = SelectedDrive?.RootPath;
        Drives.Clear();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            {
                continue;
            }

            bool ready;
            try
            {
                ready = drive.IsReady;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ready = false;
            }

            if (!ready)
            {
                continue;
            }

            long free = 0;
            long total = 0;
            string label;
            try
            {
                free = drive.AvailableFreeSpace;
                total = drive.TotalSize;
                label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? Localization.Pick("Místní disk", "Local disk")
                    : drive.VolumeLabel;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                label = Localization.Pick("Místní disk", "Local disk");
            }

            Drives.Add(new DriveItem(drive.RootDirectory.FullName, label, free, total));
        }

        SelectedDrive = Drives.FirstOrDefault(d =>
                             previous is not null &&
                             string.Equals(d.RootPath, previous, StringComparison.OrdinalIgnoreCase))
                         ?? Drives.FirstOrDefault();
    }

    // ----- scan ---------------------------------------------------------------

    public async Task ScanAsync()
    {
        if (!CanStartScan || SelectedDrive is null)
        {
            return;
        }

        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        var driveRoot = SelectedDrive.RootPath;

        Categories.Clear();
        foreach (var definition in _scanner.Definitions)
        {
            Categories.Add(new CleanCategoryViewModel(definition, OnSelectionChanged));
        }

        RecalculateTotal();
        IsScanning = true;
        StatusText = Localization.Pick("Připravuji skenování…", "Preparing the scan…");

        var progress = new Progress<string>(Report);
        var itemProgress = new Progress<ScanItemFound>(PublishItem);

        try
        {
            var categories = await Task.Run(
                () => _scanner.ScanAsync(driveRoot, IncludeRecentFiles, progress, itemProgress, token),
                token).ConfigureAwait(true);

            RecalculateTotal();
            var found = categories.Count(category => category.Items.Count > 0);
            StatusText = found == 0
                ? Localization.Pick("Na vybraném disku nebyly nalezeny žádné soubory k vyčištění.",
                    "No cleanable files were found on the selected drive.")
                : Localization.Pick(
                    $"Skenování dokončeno – {found} kategorií, celkem {Format.Bytes(categories.Sum(c => c.TotalBytes))}.",
                    $"Scan finished – {found} categories, {Format.Bytes(categories.Sum(c => c.TotalBytes))} in total.");
        }
        catch (OperationCanceledException)
        {
            StatusText = Localization.Pick("Skenování bylo zrušeno.", "The scan was cancelled.");
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsScanning = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    public void CancelScan()
    {
        if (IsScanning)
        {
            _cancellation?.Cancel();
            StatusText = Localization.Pick("Ruším skenování…", "Cancelling the scan…");
        }
    }

    private void Report(string text)
    {
        if (_dispatcher.HasThreadAccess)
        {
            StatusText = text;
        }
        else
        {
            _dispatcher.TryEnqueue(() => StatusText = text);
        }
    }

    // ----- clean --------------------------------------------------------------

    public IReadOnlyList<CleanTarget> BuildTargets() =>
        Categories.SelectMany(category => category.BuildTargets()).ToList();

    public string BuildConfirmationText()
    {
        var builder = new StringBuilder();
        builder.AppendLine(Localization.Pick(
            "Následující kategorie budou vyčištěny:",
            "The following categories will be cleaned:"));
        builder.AppendLine();

        foreach (var category in Categories)
        {
            var count = category.Items.Count(item => item.IsChecked);
            if (count == 0)
            {
                continue;
            }

            builder.AppendLine($"• {category.Name} — {count} × {category.SelectedSizeFormatted}");
        }

        builder.AppendLine();
        builder.AppendLine(TotalSelectedText);

        if (DryRun)
        {
            builder.AppendLine();
            builder.AppendLine(Localization.Pick(
                "Testovací režim je zapnutý – soubory se nesmažou, pouze se zapíše, co by bylo odstraněno.",
                "Dry run is enabled – nothing is deleted, only what would be removed is logged."));
        }

        if (UseRecycleBin)
        {
            builder.AppendLine();
            builder.AppendLine(Localization.Pick(
                "Vybrané složky se přesunou do koše.",
                "The selected folders will be moved to the Recycle Bin."));
        }

        return builder.ToString();
    }

    public async Task<CleanResult?> CleanAsync()
    {
        if (!CanClean || SelectedDrive is null)
        {
            return null;
        }

        var targets = BuildTargets();
        if (targets.Count == 0)
        {
            return null;
        }

        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;

        IsCleaning = true;
        StatusText = Localization.Pick("Čistím vybrané položky…", "Cleaning the selected items…");

        var progress = new Progress<string>(Report);

        try
        {
            var result = await Task.Run(
                () => _cleaner.CleanAsync(SelectedDrive.RootPath, targets, DryRun, UseRecycleBin, progress, token),
                token).ConfigureAwait(true);

            StatusText = DryRun
                ? Localization.Pick(
                    $"Testovací režim: smazalo by se {Format.Bytes(result.FreedBytes)}.",
                    $"Dry run: {Format.Bytes(result.FreedBytes)} would have been removed.")
                : Localization.Pick(
                    $"Vyčištěno {Format.Bytes(result.FreedBytes)}, přeskočeno {result.SkippedFiles} souborů.",
                    $"Freed {Format.Bytes(result.FreedBytes)}, skipped {result.SkippedFiles} files.");

            return result;
        }
        catch (OperationCanceledException)
        {
            StatusText = Localization.Pick("Čištění bylo zrušeno.", "The cleaning was cancelled.");
            return null;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return null;
        }
        finally
        {
            IsCleaning = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    public string BuildResultText(CleanResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Localization.IsCzech
            ? $"Uvolněné místo: {Format.Bytes(result.FreedBytes)}"
            : $"Freed space: {Format.Bytes(result.FreedBytes)}");
        builder.AppendLine(Localization.IsCzech
            ? $"Odstraněné soubory: {result.DeletedItems}"
            : $"Deleted files: {result.DeletedItems}");
        builder.AppendLine(Localization.IsCzech
            ? $"Přeskočené (uzamčené) soubory: {result.SkippedFiles}"
            : $"Skipped (locked) files: {result.SkippedFiles}");

        if (result.DryRun)
        {
            builder.AppendLine();
            builder.AppendLine(Localization.Pick(
                "Během testovacího režimu se nic nesmazalo.",
                "Nothing was deleted during the dry run."));
        }

        if (!string.IsNullOrEmpty(result.LogPath))
        {
            builder.AppendLine();
            builder.AppendLine(Localization.Pick($"Protokol: {result.LogPath}", $"Log: {result.LogPath}"));
        }

        return builder.ToString();
    }

    // ----- selection & language ----------------------------------------------

    /// <summary>
    /// Adds a freshly measured path the moment the scanner reports it, so results appear while the
    /// disk walk is still running instead of after the slowest cache has been counted.
    /// </summary>
    private void PublishItem(ScanItemFound found)
    {
        var category = Categories.FirstOrDefault(existing =>
            string.Equals(existing.Id, found.CategoryId, StringComparison.Ordinal));

        if (category is null)
        {
            return;
        }

        category.AddItem(found.Item);
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(EmptyStateVisibility));
    }

    private void OnSelectionChanged() => RecalculateTotal();

    private void RecalculateTotal()
    {
        _totalSelectedBytes = Categories.Sum(category => category.SelectedBytes);
        OnPropertyChanged(nameof(TotalSelectedBytes));
        OnPropertyChanged(nameof(TotalSelectedText));
        OnPropertyChanged(nameof(CanClean));
    }

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        Localization.Language = value == 1 ? AppLanguage.English : AppLanguage.Czech;
        RefreshLanguage();
    }

    private void RefreshLanguage()
    {
        LoadDrives();

        foreach (var category in Categories)
        {
            category.RefreshLanguage();
        }

        OnPropertyChanged(nameof(DriveLabelText));
        OnPropertyChanged(nameof(ScanButtonText));
        OnPropertyChanged(nameof(CancelButtonText));
        OnPropertyChanged(nameof(CleanButtonText));
        OnPropertyChanged(nameof(DryRunText));
        OnPropertyChanged(nameof(RecycleBinText));
        OnPropertyChanged(nameof(IncludeRecentText));
        OnPropertyChanged(nameof(SettingsText));
        OnPropertyChanged(nameof(LanguageLabelText));
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(ScanningStateText));
        OnPropertyChanged(nameof(ConfirmTitle));
        OnPropertyChanged(nameof(ConfirmButtonText));
        OnPropertyChanged(nameof(CloseButtonText));
        OnPropertyChanged(nameof(CancelDialogText));
        OnPropertyChanged(nameof(CleanDoneTitle));
        OnPropertyChanged(nameof(NothingSelectedTitle));
        OnPropertyChanged(nameof(NothingSelectedText));

        RecalculateTotal();
    }
}
