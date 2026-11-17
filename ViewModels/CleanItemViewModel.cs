using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskCleaner.Models;
using Microsoft.UI.Xaml;

namespace DiskCleaner.ViewModels;

/// <summary>One found path with its checkbox, size, impact note and warning badge.</summary>
public sealed partial class CleanItemViewModel : ObservableObject
{
    private readonly ScanItem _item;
    private bool _isChecked;
    private bool _silent;

    public CleanItemViewModel(ScanItem item, Action<CleanItemViewModel>? selectionChanged = null)
    {
        _item = item;
        _isChecked = item.DefaultChecked;
        SelectionChanged = selectionChanged;
        IsProcessRunning = DetectProcess(item.ProcessNames);
        OpenFolderCommand = new RelayCommand(OpenFolder);
    }

    public Action<CleanItemViewModel>? SelectionChanged { get; }

    public string Path => _item.Path;
    public long Bytes => _item.Bytes;
    public string Kind => _item.Kind;
    public int MinAgeHours => _item.MinAgeHours;
    public string FormattedSize => Format.Bytes(Bytes);
    public string Title => Localization.Pick(_item.TitleCs, _item.TitleEn);
    public string ImpactNote => Localization.Pick(_item.ImpactCs, _item.ImpactEn);
    public bool IsProcessRunning { get; }
    public Visibility WarningVisibility => IsProcessRunning ? Visibility.Visible : Visibility.Collapsed;
    public string WarningText => Localization.Pick("Aplikace pravděpodobně běží", "Application is probably running");
    public string OpenFolderTooltip => Localization.Pick("Otevřít složku", "Open folder");

    public IRelayCommand OpenFolderCommand { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!SetProperty(ref _isChecked, value) || _silent)
            {
                return;
            }

            SelectionChanged?.Invoke(this);
        }
    }

    /// <summary>Used by the category when it drives its children, so no event bounces back.</summary>
    public void SetCheckedSilently(bool value)
    {
        if (_isChecked == value)
        {
            return;
        }

        _silent = true;
        try
        {
            IsChecked = value;
        }
        finally
        {
            _silent = false;
        }
    }

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ImpactNote));
        OnPropertyChanged(nameof(WarningText));
        OnPropertyChanged(nameof(OpenFolderTooltip));
    }

    private void OpenFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Explorer refusing to open a path is not worth an error dialog.
        }
    }

    private static bool DetectProcess(string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                var matches = Process.GetProcessesByName(name);
                try
                {
                    if (matches.Length > 0)
                    {
                        return true;
                    }
                }
                finally
                {
                    foreach (var match in matches)
                    {
                        match.Dispose();
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Treat an unqueryable process name as not running.
            }
        }

        return false;
    }
}
