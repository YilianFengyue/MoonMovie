using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using MoonMovie.Core.Caching;
using MoonMovie.Core.Configuration;
using MoonMovie.Core.Danmaku;
using MoonMovie.Core.Downloads;
using MoonMovie.Core.Settings;
using MoonMovie.Core.Home;
using MoonMovie.Core.Library;
using MoonMovie.Core.Local;
using MoonMovie.Core.Playback;
using MoonMovie.Core.Search;
using MoonMovie.Core.Sources;
using MoonMovie.Core.Tmdb;
using MoonMovie.Imaging;
using MoonMovie.Playback;
using MoonMovie.Services;
using MoonMovie.ViewModels;

namespace MoonMovie;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
        UnhandledException += (_, e) => WriteCrashLog(e.Exception);
    }

    private static void WriteCrashLog(Exception ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppPaths.Root, "crash.log"),
                $"[{DateTimeOffset.Now:O}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    public static IServiceProvider Services { get; private set; } = null!;

    public static MainWindow MainWindow { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        _window = MainWindow;
        _window.Activate();
        Services.GetRequiredService<LocalLibrary>().Initialize();
        Services.GetRequiredService<BiliAccountService>().Restore();
        Services.GetRequiredService<DownloadManager>().Start();

        // Files from the command line or a file association, a jump-list "继续观看" entry.
        MainWindow.DispatcherQueue.TryEnqueue(ActivationRouter.OnLaunched);

        // A build without keys (public release): ask for the TMDB key first, nothing works without it.
        if (!Services.GetRequiredService<TmdbOptions>().IsConfigured)
        {
            MainWindow.DispatcherQueue.TryEnqueue(() => MainWindow.Navigate(typeof(Views.SettingsPage), "tmdb"));
        }
        JumpListUpdater.Start();
    }

    private static ServiceProvider ConfigureServices()
    {
        var env = EnvFile.Load(
            Path.Combine(AppContext.BaseDirectory, ".env"),
            Path.Combine(AppPaths.Root, ".env"));

        var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 12,
            ConnectTimeout = TimeSpan.FromSeconds(6),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MoonMovie/0.1 (Windows)");

        // Mainland resource sites and their CDNs reject overseas proxy exits, so they are always reached directly,
        // while TMDB keeps using the system proxy.
        var direct = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        // B站 gets its own client without a cookie jar: the account's cookies are sent explicitly, so a login's
        // Set-Cookie must not linger (and leak to other sites' requests) after unlinking.
        var bili = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        var settings = new SettingsStore();
        var tmdbOptions = TmdbOptions.FromEnv(env, settings.Current.Services.TmdbApiKey);

        // The danmu server fans out to several platforms per request and can take well over the default timeout.
        var danmakuHttp = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(8),
        })
        {
            Timeout = TimeSpan.FromSeconds(75),
        };
        danmakuHttp.DefaultRequestHeaders.UserAgent.ParseAdd("MoonMovie/0.1 (Windows)");

        return new ServiceCollection()
            .AddSingleton(env)
            .AddSingleton(http)
            .AddSingleton(tmdbOptions)
            .AddSingleton(_ => new TmdbClient(http, tmdbOptions, new JsonDiskCache(AppPaths.ApiCache)))
            .AddSingleton(_ => new SourceSearchService(direct, SourceSearchService.LoadBundledSites()))
            .AddSingleton(settings)
            .AddSingleton<SourceMatchCache>()
            .AddSingleton(_ => new Core.Bilibili.BiliClient(bili))
            .AddSingleton(_ => new Core.Douban.DoubanClient(direct))
            .AddSingleton<Core.Bilibili.BiliPgcSource>()
            .AddSingleton<BiliAccountService>()
            .AddSingleton<ProfileStore>()
            .AddTransient<ProfileViewModel>()
            .AddSingleton(sp => new DanmakuClient(danmakuHttp, env, sp.GetRequiredService<SettingsStore>()))
            .AddSingleton<DanmakuLibrary>()
            .AddSingleton<FavoritesStore>()
            .AddSingleton<WatchProgressStore>()
            .AddSingleton<TitlePrefsStore>()
            .AddSingleton<LocalLibrary>()
            .AddSingleton(sp => new DownloadManager(direct, sp.GetRequiredService<SegmentCache>(),
                sp.GetRequiredService<SettingsStore>(), sp.GetRequiredService<LocalLibrary>()))
            .AddSingleton(sp => new SegmentCache(() => (long)sp.GetRequiredService<SettingsStore>().Current.Cache.SegmentCacheGb << 30))
            .AddSingleton(sp => new MediaProxy(direct, sp.GetRequiredService<SegmentCache>()))
            .AddSingleton<HomeFeedService>()
            .AddSingleton<SearchService>()
            .AddSingleton(sp => new ImageLoader(http, sp.GetRequiredService<TmdbOptions>().ImageRoots))
            .AddTransient<HomeViewModel>()
            .AddTransient<DetailViewModel>()
            .AddTransient<SearchViewModel>()
            .AddTransient<LibraryViewModel>()
            .AddTransient<LocalLibraryViewModel>()
            .AddTransient<DownloadsViewModel>()
            .AddSingleton<BrowseSections>()
            .BuildServiceProvider();
    }
}
