using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskCleaner.Models;
using Microsoft.UI.Xaml;

namespace DiskCleaner.ViewModels;

/// <summary>
/// One expander: three-state checkbox, bilingual texts and the child folders. Items arrive one by one
/// while the scan runs, so the expander only becomes visible once the first one has been measured.
/// </summary>
public sealed partial class CleanCategoryViewModel : ObservableObject
{
    private readonly CategoryDefinition _definition;
    private readonly Action _selectionChanged;
    private bool? _isChecked;
    private bool _syncing;
    private bool _isExpanded;
    private long _selectedBytes;
    private long _totalBytes;

    public CleanCategoryViewModel(CategoryDefinition definition, Action selectionChanged)
    {
        _definition = definition;
        _selectionChanged = selectionChanged;
        Items = new ObservableCollection<CleanItemViewModel>();
        _isChecked = false;
        _isExpanded = true;
    }

    public ObservableCollection<CleanItemViewModel> Items { get; }

    public string Glyph => _definition.Glyph;
    public string Id => _definition.Id;
    public string Name => Localization.Pick(_definition.NameCs, _definition.NameEn);
    public string Description => Localization.Pick(_definition.DescriptionCs, _definition.DescriptionEn);
    public long TotalBytes => _totalBytes;
    public string TotalSizeFormatted => Format.Bytes(_totalBytes);
    public string ItemCountText => Localization.IsCzech
        ? $"{Items.Count} položek"
        : $"{Items.Count} items";

    public Visibility CategoryVisibility => Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public long SelectedBytes => _selectedBytes;
    public string SelectedSizeFormatted => Format.Bytes(_selectedBytes);

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool? IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            OnPropertyChanged();

            if (_syncing)
            {
                return;
            }

            // A three-state checkbox cycles false -> true -> null on click. "null" therefore means
            // "not everything is selected", which the user expresses by turning the whole group off.
            var target = value == true;
            _syncing = true;
            try
            {
                foreach (var item in Items)
                {
                    item.SetCheckedSilently(target);
                }
            }
            finally
            {
                _syncing = false;
            }

            Recalculate();
            _selectionChanged();
        }
    }

    /// <summary>Appends a freshly measured item without disturbing the selection the user already made.</summary>
    public void AddItem(ScanItem item)
    {
        var viewModel = new CleanItemViewModel(item, OnChildChanged);

        // A group the user has already switched off stays off, even for items discovered afterwards.
        if (_isChecked == false)
        {
            viewModel.SetCheckedSilently(false);
        }

        Items.Add(viewModel);
        _totalBytes += item.Bytes;
        Recalculate();

        OnPropertyChanged(nameof(CategoryVisibility));
        OnPropertyChanged(nameof(ItemCountText));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(TotalSizeFormatted));
        _selectionChanged();
    }

    public IEnumerable<CleanTarget> BuildTargets() =>
        Items.Where(item => item.IsChecked)
            .Select(item => new CleanTarget(item.Path, item.Kind, item.MinAgeHours, item.Bytes))
            .ToList();

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(ItemCountText));
        foreach (var item in Items)
        {
            item.RefreshLanguage();
        }
    }

    private void OnChildChanged(CleanItemViewModel item)
    {
        if (_syncing)
        {
            return;
        }

        Recalculate();
        _selectionChanged();
    }

    private void Recalculate()
    {
        _selectedBytes = Items.Where(i => i.IsChecked).Sum(i => i.Bytes);
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedSizeFormatted));

        var state = ComputeState();
        if (_isChecked == state)
        {
            return;
        }

        _syncing = true;
        try
        {
            IsChecked = state;
        }
        finally
        {
            _syncing = false;
        }
    }

    private bool? ComputeState()
    {
        var checkedCount = Items.Count(i => i.IsChecked);
        if (Items.Count == 0 || checkedCount == 0)
        {
            return false;
        }

        return checkedCount == Items.Count ? true : null;
    }
}
