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

        AppTitleBar.Loaded += (_, _) => UpdateTitleBarRegions();
        AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarRegions();

        ContentFrame.Navigate(typeof(HomePage), null, new SuppressNavigationTransitionInfo());
    }

    public void Navigate(Type page, object? parameter, NavigationTransitionInfo? transition = null) =>
        ContentFrame.Navigate(page, parameter, transition ?? new EntranceNavigationTransitionInfo());

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
        DispatcherQueue.TryEnqueue(UpdateTitleBarRegions);
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
            rects.Add(new RectInt32(
                (int)Math.Round(bounds.X * scale),
                (int)Math.Round(bounds.Y * scale),
                (int)Math.Round(bounds.Width * scale),
                (int)Math.Round(bounds.Height * scale)));
        }

        InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
            .SetRegionRects(NonClientRegionKind.Passthrough, rects.ToArray());
    }

    private void OnNavSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncingNav || sender.SelectedItem is not { } item)
        {
            return;
        }

        if ((string)item.Tag == "home")
        {
            if (ContentFrame.CurrentSourcePageType != typeof(HomePage))
            {
                Navigate(typeof(HomePage), null);
            }

            return;
        }

        Navigate(typeof(PlaceholderPage), new PlaceholderArgs(item.Text, "这一页正在路上"));
    }

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        BackButton.Visibility = ContentFrame.CanGoBack ? Visibility.Visible : Visibility.Collapsed;
        if (e.SourcePageType == typeof(HomePage))
        {
            _syncingNav = true;
            Nav.SelectedItem = Nav.Items[0];
            _syncingNav = false;
        }

        DispatcherQueue.TryEnqueue(UpdateTitleBarRegions);
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => GoBack();

    private void OnBackAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
        args.Handled = GoBack();

    public bool GoBack()
    {
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
        timer.Tick += async (_, _) => await UpdateSuggestionsAsync();
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
        SearchBox.ItemsSource = history.Take(8).Select(SuggestionItem.ForHistory).ToArray();
        SearchBox.IsSuggestionListOpen = history.Count > 0;
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
