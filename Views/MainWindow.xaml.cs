using DiskCleaner.Services;
using DiskCleaner.ViewModels;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DiskCleaner.Views;

/// <summary>Shell window: builds the services, hosts the view model and runs the confirmation dialogs.</summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var docker = new DockerCliService();
        ViewModel = new MainViewModel(
            new DiskScannerService(docker),
            new CleanerExecutionService(docker),
            DispatcherQueue);

        RootGrid.DataContext = ViewModel;

        Title = "DiskCleaner";

        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
    }

    public MainViewModel ViewModel { get; }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.ScanAsync();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelScan();
    }

    /// <summary>The expander toggles on any tap in its header, which would fight the category checkbox.</summary>
    private void HeaderCheckBox_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        e.Handled = true;
    }

    private async void CleanButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.BuildTargets().Count == 0)
        {
            await ShowMessageAsync(
                ViewModel.NothingSelectedTitle,
                ViewModel.NothingSelectedText);
            return;
        }

        var content = new ScrollViewer
        {
            MaxHeight = 360,
            Content = new TextBlock
            {
                Text = ViewModel.BuildConfirmationText(),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            }
        };

        var confirm = new ContentDialog
        {
            Title = ViewModel.ConfirmTitle,
            Content = content,
            PrimaryButtonText = ViewModel.ConfirmButtonText,
            CloseButtonText = ViewModel.CancelDialogText,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var result = await ViewModel.CleanAsync();
        if (result is null)
        {
            return;
        }

        await ShowMessageAsync(ViewModel.CleanDoneTitle, ViewModel.BuildResultText(result));

        // The drive content changed, so refresh the scan state right away.
        await ViewModel.ScanAsync();
    }

    private async Task ShowMessageAsync(string title, string text)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 360,
                Content = new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                }
            },
            CloseButtonText = ViewModel.CloseButtonText,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };

        await dialog.ShowAsync();
    }
}
