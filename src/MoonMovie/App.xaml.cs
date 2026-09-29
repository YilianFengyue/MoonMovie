using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using MoonMovie.Core.Caching;
using MoonMovie.Core.Configuration;
using MoonMovie.Core.Home;
using MoonMovie.Core.Tmdb;
using MoonMovie.Imaging;
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

        var tmdbOptions = TmdbOptions.FromEnv(env);

        return new ServiceCollection()
            .AddSingleton(env)
            .AddSingleton(http)
            .AddSingleton(tmdbOptions)
            .AddSingleton(_ => new TmdbClient(http, tmdbOptions, new JsonDiskCache(AppPaths.ApiCache)))
            .AddSingleton<HomeFeedService>()
            .AddSingleton(sp => new ImageLoader(http, sp.GetRequiredService<TmdbOptions>().ImageRoots))
            .AddTransient<HomeViewModel>()
            .BuildServiceProvider();
    }
}
