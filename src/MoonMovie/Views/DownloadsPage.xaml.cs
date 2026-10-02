using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MoonMovie.Controls;
using MoonMovie.Core.Settings;
using MoonMovie.Services;
using MoonMovie.ViewModels;

namespace MoonMovie.Views;

/// <summary>下载: offline copies — progress, pause / resume, speed limit, where they are saved.</summary>
public sealed partial class DownloadsPage : Page
{
    private static readonly (string Label, int Mb)[] SpeedLimits = [("不限", 0), ("2 MB/s", 2), ("5 MB/s", 5), ("10 MB/s", 10)];

    private readonly SettingsStore _settings = App.Services.GetRequiredService<SettingsStore>();
    private readonly List<(Button Chip, int Mb)> _speedChips = [];

    public DownloadsPage()
    {
        ViewModel = App.Services.GetRequiredService<DownloadsViewModel>();
        InitializeComponent();
        ViewModel.ListChanged += () => EmptyState.Visibility = ViewModel.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var (label, mb) in SpeedLimits)
        {
            var chip = Chips.Create(label, () =>
            {
                _settings.Current.Downloads.SpeedLimitMb = mb;
                _settings.Save();
                SyncSpeed();
            });
            _speedChips.Add((chip, mb));
            SpeedChips.Children.Add(chip);
        }

        SyncSpeed();
    }

    public DownloadsViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Attach();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Detach();
    }

    private void SyncSpeed()
    {
        foreach (var (chip, mb) in _speedChips) Chips.Set(chip, _settings.Current.Downloads.SpeedLimitMb == mb);
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ViewModel.Manager.Folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{ViewModel.Manager.Folder}\"") { UseShellExecute = true });
    }

    /// <summary>New downloads go to the new folder; finished ones stay where they are.</summary>
    private async void OnChangeFolder(object sender, RoutedEventArgs e)
    {
        if (await LocalPlayback.PickFolderAsync() is not { } folder) return;
        _settings.Current.Downloads.Folder = folder;
        _settings.Save();
        ViewModel.Detach();
        ViewModel.Attach();
    }
}
