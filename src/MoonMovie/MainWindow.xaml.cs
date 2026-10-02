using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using MoonMovie.Core.Browse;
using MoonMovie.Core.Search;
using MoonMovie.Core.Tmdb;
using MoonMovie.Services;
using MoonMovie.ViewModels;
using MoonMovie.Views;
using Windows.Graphics;
using Windows.UI;

namespace MoonMovie;

public sealed partial class MainWindow : Window
{
    private bool _syncingNav;
    private DispatcherQueueTimer? _suggestTimer;
    private CancellationTokenSource? _suggestCts;

    public MainWindow()
    {
        InitializeComponent();
        ConfigureWindow();
        StartSplash();
        var updates = App.Services.GetRequiredService<UpdateService>();
        updates.Changed += SyncUpdateBar;
        UpdateBar.CloseButtonClick += (_, _) => _updateDismissed = true;

        AppTitleBar.Loaded += (_, _) => UpdateTitleBarRegions();
        foreach (var item in Nav.Items) item.Tapped += OnNavItemTapped;
        AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarRegions();
        // Interactive parts move when their neighbours change size (search box, badges, nav): keep the
        // click-through rectangles in step or the buttons stop responding to clicks.
        foreach (var element in new FrameworkElement[] { BackButton, Nav, SearchBox, Actions })
        {
            element.SizeChanged += (_, _) => UpdateTitleBarRegions();
        }

        ContentFrame.Navigate(typeof(HomePage), null, new SuppressNavigationTransitionInfo());

        Taskbar = new Playback.TaskbarControls(WinRT.Interop.WindowNative.GetWindowHandle(this));
        AppWindow.Closing += (_, _) => Lifecycle.Log("close requested");
        Closed += OnWindowClosed;

        var downloads = App.Services.GetRequiredService<Core.Downloads.DownloadManager>();
        downloads.Changed += _ => DispatcherQueue.Enqueue(UpdateDownloadBadge);
        UpdateDownloadBadge();

        App.Services.GetRequiredService<Core.Library.ProfileStore>().Changed += (_, _) => UpdateProfilePicture();
        App.Services.GetRequiredService<BiliAccountService>().Changed += () => DispatcherQueue.Enqueue(UpdateProfilePicture);
        UpdateProfilePicture();
    }

    /// <summary>Thumbnail buttons and icon progress; the player owns them while it is open.</summary>
    public Playback.TaskbarControls Taskbar { get; }

    public bool PlayerOwnsTaskbar { get; set; }

    /// <summary>The number of downloads still running or waiting, on the title-bar button.</summary>
    private void UpdateDownloadBadge()
    {
        var active = App.Services.GetRequiredService<Core.Downloads.DownloadManager>().ActiveCount;
        DownloadBadge.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;
        DownloadBadge.Value = Math.Min(active, 99);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DownloadsButton, active > 0 ? $"下载，{active} 个进行中" : "下载");

        // Outside the player the icon shows overall download progress.
        if (PlayerOwnsTaskbar) return;
        var running = App.Services.GetRequiredService<Core.Downloads.DownloadManager>().Items
            .Where(i => i.State is Core.Downloads.DownloadState.Queued or Core.Downloads.DownloadState.Running).ToArray();
        Taskbar.SetProgress(running.Length > 0 ? running.Average(i => i.Progress) : null, paused: false);
    }

    private void OnDownloadsClick(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.CurrentSourcePageType != typeof(DownloadsPage)) Navigate(typeof(DownloadsPage), null);
    }

    public void Navigate(Type page, object? parameter, NavigationTransitionInfo? transition = null) =>
        ContentFrame.Navigate(page, parameter, transition ?? new EntranceNavigationTransitionInfo());

    public object? CurrentPage => ContentFrame.Content;

    /// <summary>Another launch handed us work: come forward (restoring from the taskbar if minimized).</summary>
    // ----- 更新 -----------------------------------------------------------------------------------------------

    private bool _updateDismissed;

    private void SyncUpdateBar()
    {
        var updates = App.Services.GetRequiredService<UpdateService>();
        if (updates.Available is not { } update || _updateDismissed && updates.State != UpdateState.Downloading)
        {
            UpdateBar.IsOpen = false;
            return;
        }

        UpdateBar.Title = $"MoonMovie {update.Version.ToString(3)} 可以更新";
        UpdateBar.Message = updates.State switch
        {
            UpdateState.Downloading => $"正在下载… {updates.Progress:P0}",
            UpdateState.Failed => updates.Error ?? "更新失败",
            _ => AppEnvironment.IsPackaged ? $"当前版本 {AppEnvironment.VersionText}，下载后由系统完成安装" : $"当前版本 {AppEnvironment.VersionText}，便携版请到发布页下载",
        };
        UpdateBar.Severity = updates.State == UpdateState.Failed ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        UpdateProgress.Visibility = updates.State == UpdateState.Downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.Value = updates.Progress;
        UpdateNowButton.IsEnabled = updates.State != UpdateState.Downloading;
        UpdateNowButton.Content = AppEnvironment.IsPackaged ? "立即更新" : "前往下载";
        UpdateBar.IsOpen = true;
    }

    private async void OnUpdateNow(object sender, RoutedEventArgs e) => await App.Services.GetRequiredService<UpdateService>().InstallAsync();

    private void OnUpdateSkip(object sender, RoutedEventArgs e) => App.Services.GetRequiredService<UpdateService>().Skip();

    private void OnUpdateNotes(object sender, RoutedEventArgs e)
    {
        if (App.Services.GetRequiredService<UpdateService>().Available is { } update)
        {
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri(update.PageUrl));
        }
    }

    /// <summary>The settings page shows the bar again after it was closed here.</summary>
    public void ShowUpdateBar()
    {
        _updateDismissed = false;
        SyncUpdateBar();
    }

    /// <summary>With Mica available the canvas steps aside: pages without artwork sit on the window's Mica.</summary>
    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
        {
            // Dark Mica: a deep veil of the canvas colour keeps the cinema mood with a hint of the wallpaper.
            Root.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xB3, 0x0C, 0x0D, 0x10));
        }
    }

    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    /// <summary>Forgets the page before the current one (used when a page replaces itself).</summary>
    public void DropPreviousEntry()
    {
        if (ContentFrame.BackStack.Count > 0) ContentFrame.BackStack.RemoveAt(ContentFrame.BackStack.Count - 1);
    }

    public bool IsFullScreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    public void SetFullScreen(bool fullScreen)
    {
        if (fullScreen == IsFullScreen) return;
        AppWindow.SetPresenter(fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
    }

    /// <summary>Hides the app chrome (wordmark, navigation, search) so video owns the window; back stays.</summary>
    public void SetImmersive(bool immersive)
    {
        var chrome = immersive ? Visibility.Collapsed : Visibility.Visible;
        Wordmark.Visibility = chrome;
        Nav.Visibility = chrome;
        SearchBox.Visibility = chrome;
        Actions.Visibility = chrome;
        TitleScrim.Visibility = chrome;
        DispatcherQueue.Enqueue(UpdateTitleBarRegions);
    }

    /// <summary>Title bar overlay visibility for the player's auto-hiding controls (caption buttons included).</summary>
    public void SetTitleBarVisible(bool visible)
    {
        AppTitleBar.Opacity = visible ? 1 : 0;
        var bar = AppWindow.TitleBar;
        bar.ButtonForegroundColor = visible ? Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF) : Colors.Transparent;
        bar.ButtonInactiveForegroundColor = visible ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Colors.Transparent;
        bar.ButtonHoverBackgroundColor = visible ? Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF) : Colors.Transparent;
        bar.ButtonHoverForegroundColor = visible ? Colors.White : Colors.Transparent;
    }

    public bool IsCompactOverlay => AppWindow.Presenter.Kind == AppWindowPresenterKind.CompactOverlay;

    /// <summary>Picture-in-picture: a small always-on-top window.</summary>
    public void SetCompactOverlay(bool compact)
    {
        if (compact == IsCompactOverlay) return;
        AppWindow.SetPresenter(compact ? AppWindowPresenterKind.CompactOverlay : AppWindowPresenterKind.Overlapped);
        if (compact)
        {
            var scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
            AppWindow.Resize(new SizeInt32((int)(480 * scale), (int)(270 * scale)));
        }
    }

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Taskbar, Alt+Tab and thumbnails (the packaged app also has its tile assets; unpackaged needs this).
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        var titleBar = AppWindow.TitleBar;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonInactiveForegroundColor = Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonHoverForegroundColor = Colors.White;
        titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);

        var scale = Content?.XamlRoot?.RasterizationScale ?? GetDpiScale();
        var width = (int)(1480 * scale);
        var height = (int)(920 * scale);
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        width = Math.Min(width, (int)(area.Width * 0.92));
        height = Math.Min(height, (int)(area.Height * 0.92));
        AppWindow.MoveAndResize(new RectInt32(
            area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2,
            width,
            height));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(1080 * scale);
            presenter.PreferredMinimumHeight = (int)(680 * scale);
        }
    }

    private double GetDpiScale()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        return GetDpiForWindow(hwnd) / 96.0;
    }

    /// <summary>
    /// Closing: the player saves its place and lets go of mpv now (its page is not navigated away from), and a
    /// watchdog makes sure the process ends even if some native teardown hangs.
    /// </summary>
    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        Lifecycle.Log("window closed");
        SafeDispatch.ShuttingDown = true; // queued UI work must not run against the torn-down window
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            Lifecycle.Log("still running 5 s after closing: forcing exit");
            Environment.Exit(0);
        });

        try
        {
            if (ContentFrame.Content is PlayerPage player) player.Shutdown();
            Taskbar.Dispose();
        }
        catch (Exception ex)
        {
            Lifecycle.Log("teardown failed: " + ex.GetType().Name);
        }
    }

    /// <summary>Interactive title bar elements must be carved out of the drag region.</summary>
    private void UpdateTitleBarRegions()
    {
        if (AppTitleBar.XamlRoot is null)
        {
            return;
        }

        var scale = AppTitleBar.XamlRoot.RasterizationScale;
        LeftInsetColumn.Width = new GridLength(AppWindow.TitleBar.LeftInset / scale);
        RightInsetColumn.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);

        var rects = new List<RectInt32>();
        foreach (var element in new FrameworkElement[] { BackButton, Nav, SearchBox, Actions })
        {
            if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0)
            {
                continue;
            }

            var bounds = element.TransformToVisual(null).TransformBounds(
                new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            // Never over the caption buttons: a click-through rectangle there would swallow 关闭.
            var left = (int)Math.Round(bounds.X * scale);
            var right = Math.Min((int)Math.Round(bounds.Right * scale), AppWindow.ClientSize.Width - AppWindow.TitleBar.RightInset);
            if (right <= left) continue;
            rects.Add(new RectInt32(left, (int)Math.Round(bounds.Y * scale), right - left, (int)Math.Round(bounds.Height * scale)));
        }

        InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
            .SetRegionRects(NonClientRegionKind.Passthrough, rects.ToArray());
    }

    public void NavigateHome()
    {
        if (ContentFrame.CurrentSourcePageType != typeof(HomePage)) Navigate(typeof(HomePage), null);
    }

    private void OnNavSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        NavLog($"nav selection {(sender.SelectedItem as SelectorBarItem)?.Tag} syncing={_syncingNav}");
        if (_syncingNav || sender.SelectedItem is not { } item)
        {
            return;
        }

        NavigateToTab(item);
    }

    /// <summary>
    /// The highlighted tab clicked again from an inner page (detail, search, player) raises no SelectionChanged,
    /// so taps are handled too: they go back to that tab's own page.
    /// </summary>
    private void OnNavItemTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is SelectorBarItem item && item == Nav.SelectedItem) NavigateToTab(item);
    }

    private void NavigateToTab(SelectorBarItem item)
    {
        // The tab of the page already on screen does nothing.
        if (NavTag(ContentFrame.CurrentSourcePageType, _currentParameter) == (string)item.Tag)
        {
            return;
        }

        switch ((string)item.Tag)
        {
            case "home":
                Navigate(typeof(HomePage), null);
                break;
            case "movie":
                Navigator.OpenBrowse(BrowseSection.Movie);
                break;
            case "tv":
                Navigator.OpenBrowse(BrowseSection.Tv);
                break;
            case "anime":
                Navigator.OpenBrowse(BrowseSection.Anime);
                break;
            case "library":
                Navigator.OpenLibrary();
                break;
            case "bili":
                Navigate(typeof(BiliPage), null);
                break;
            default:
                Navigate(typeof(PlaceholderPage), new PlaceholderArgs(item.Text, "这一页正在路上"));
                break;
        }
    }

    private object? _currentParameter;

    /// <summary>The nav tab a page belongs to; null for pages that sit "inside" a tab (detail, search, player).</summary>
    private static string? NavTag(Type? page, object? parameter) => page switch
    {
        _ when page == typeof(HomePage) => "home",
        _ when page == typeof(LibraryPage) => "library",
        _ when page == typeof(BrowsePage) => parameter switch
        {
            BrowseSection.Tv => "tv",
            BrowseSection.Anime => "anime",
            _ => "movie",
        },
        _ when page == typeof(BiliPage) => "bili",
        _ => null,
    };

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        // The home page releases the splash once its spotlight is up; any other first page shows at once.
        if (e.SourcePageType != typeof(Views.HomePage)) ReleaseSplash();

        NavLog($"navigated {e.SourcePageType.Name} mode={e.NavigationMode}");
        _currentParameter = e.Parameter;
        BackButton.Visibility = ContentFrame.CanGoBack ? Visibility.Visible : Visibility.Collapsed;

        // Keep the highlighted tab in step with back/forward and in-page links. Inner pages keep the tab they
        // were opened from: clearing the selection makes SelectorBar re-select its first item a moment later,
        // which would navigate home.
        var tag = NavTag(e.SourcePageType, e.Parameter);
        var navItem = tag is null ? null : Nav.Items.FirstOrDefault(i => (string)i.Tag == tag);
        if (navItem is not null && Nav.SelectedItem != navItem)
        {
            _syncingNav = true;
            Nav.SelectedItem = navItem;
            _syncingNav = false;
        }

        DispatcherQueue.Enqueue(UpdateTitleBarRegions);
    }

    // ----- Local files: drag and drop, Ctrl+O --------------------------------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Link;
        e.DragUIOverride.IsCaptionVisible = false;
        e.DragUIOverride.IsGlyphVisible = false;

        var inPlayer = ContentFrame.Content is PlayerPage;
        DropTitle.Text = inPlayer ? "松开即可播放或加载字幕" : "松开即可播放";
        DropCaption.Text = inPlayer ? "字幕文件加到当前视频，视频文件换成新的播放" : "视频文件直接播放，文件夹会加入本地媒体库";
        DropOverlay.Visibility = Visibility.Visible;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;

        var deferral = e.GetDeferral();
        IReadOnlyList<string> paths;
        try
        {
            paths = (await e.DataView.GetStorageItemsAsync()).Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToArray();
        }
        finally
        {
            deferral.Complete();
        }

        var subtitles = paths.Where(Core.Local.LocalNameParser.IsSubtitle).ToArray();
        if (subtitles.Length > 0 && ContentFrame.Content is PlayerPage player)
        {
            player.LoadSubtitles(subtitles);
            paths = paths.Except(subtitles).ToArray();
        }

        if (paths.Count > 0) await LocalPlayback.OpenPathsAsync(paths);
    }

    private async void OnOpenFileAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await LocalPlayback.PickAndOpenAsync();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.CurrentSourcePageType != typeof(SettingsPage)) Navigate(typeof(SettingsPage), null);
    }

    private void OnProfileClick(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.CurrentSourcePageType != typeof(ProfilePage)) Navigate(typeof(ProfilePage), null);
    }

    /// <summary>The title-bar avatar mirrors the profile: chosen picture, B站 avatar, or the initial.</summary>
    private void UpdateProfilePicture()
    {
        var profile = App.Services.GetRequiredService<Core.Library.ProfileStore>();
        var account = App.Services.GetRequiredService<BiliAccountService>().Account;
        var name = profile.DisplayName;
        ProfileAvatar.Initials = name.Length > 0 ? name[..1].ToUpperInvariant() : "M";
        ProfileAvatar.DisplayName = name;
        var source = profile.Current.UseBiliAvatar && account?.Face is { } face
            ? Core.Bilibili.BiliClient.Thumb(face, 64, 64)
            : profile.Current.AvatarFile is { } file && File.Exists(file) ? file : null;
        ProfileAvatar.ProfilePicture = source is null ? null : new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(source));
    }

    private void OnHistoryClick(object sender, RoutedEventArgs e) => Navigator.OpenLibrary(LibraryTab.History);

    private void OnBackClick(object sender, RoutedEventArgs e) => GoBack();

    private void OnBackAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
        args.Handled = GoBack();

    public bool GoBack()
    {
        NavLog("GoBack " + Environment.StackTrace);
        if (!ContentFrame.CanGoBack)
        {
            return false;
        }

        ContentFrame.GoBack();
        return true;
    }

    // ----- Search ---------------------------------------------------------------------------------------

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

        // Debounce typing; only the latest request may populate the list.
        _suggestTimer ??= CreateSuggestTimer();
        _suggestTimer.Stop();
        _suggestTimer.Start();
    }

    private DispatcherQueueTimer CreateSuggestTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(220);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => SafeDispatch.Run(() => _ = UpdateSuggestionsAsync());
        return timer;
    }

    private async Task UpdateSuggestionsAsync()
    {
        _suggestCts?.Cancel();
        var text = SearchBox.Text.Trim();
        if (text.Length == 0)
        {
            ShowSearchHistory();
            return;
        }

        var cts = _suggestCts = new CancellationTokenSource();
        try
        {
            var search = App.Services.GetRequiredService<SearchService>();
            var tmdb = App.Services.GetRequiredService<TmdbClient>();
            var items = await search.SuggestAsync(text, cts.Token);
            if (cts.IsCancellationRequested) return;
            SearchBox.ItemsSource = items.Select(i => SuggestionItem.ForMedia(i, tmdb)).ToArray();
            SearchBox.IsSuggestionListOpen = items.Count > 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
        }
    }

    private void ShowSearchHistory()
    {
        var history = App.Services.GetRequiredService<SearchService>().History;
        SearchBox.ItemsSource = history.Count == 0
            ? null
            : history.Take(8).Select(SuggestionItem.ForHistory).Append(SuggestionItem.ClearAll()).ToArray();
        SearchBox.IsSuggestionListOpen = history.Count > 0;
    }

    private void OnForgetQuery(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string query })
        {
            App.Services.GetRequiredService<SearchService>().Forget(query);
            ShowSearchHistory();
        }
    }

    private void OnSearchGotFocus(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Text.Length == 0) ShowSearchHistory();
    }

    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _suggestTimer?.Stop();
        _suggestCts?.Cancel();
        var search = App.Services.GetRequiredService<SearchService>();

        if (args.ChosenSuggestion is SuggestionItem { IsClearAll: true })
        {
            search.ClearHistory();
            sender.Text = string.Empty;
            sender.ItemsSource = null;
            sender.IsSuggestionListOpen = false;
            return;
        }

        if (args.ChosenSuggestion is SuggestionItem { Item: { } item })
        {
            search.Remember(item.Title);
            sender.Text = string.Empty;
            Services.Navigator.OpenMedia(item, SearchService.Parse(args.QueryText).Season);
            return;
        }

        var query = (args.ChosenSuggestion as SuggestionItem)?.Text ?? args.QueryText;
        if (string.IsNullOrWhiteSpace(query)) return;

        search.Remember(query);
        sender.Text = query;
        sender.ItemsSource = null;
        Navigate(typeof(SearchPage), query.Trim());
    }

    private void OnSearchAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void NavLog(string line)
    {
        try
        {
            File.AppendAllText(Path.Combine(Core.Configuration.AppPaths.Root, "nav.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
