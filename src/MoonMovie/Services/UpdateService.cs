using MoonMovie.Core.Settings;
using MoonMovie.Core.Updates;

namespace MoonMovie.Services;

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Failed,
}

/// <summary>
/// New releases: a quiet check a little after start (every few hours at most, skipped versions left alone) or on
/// demand from Settings. Installed builds download the signed package and hand it to App Installer, which updates
/// in place; the portable build opens the release page.
/// </summary>
public sealed class UpdateService(UpdateChecker checker, SettingsStore settings)
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

    public UpdateInfo? Available { get; private set; }

    public UpdateState State { get; private set; }

    public double Progress { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Raised on the UI thread whenever the state above changes.</summary>
    public event Action? Changed;

    /// <summary>At start: one check after things settle, if it is due.</summary>
    public void Start()
    {
        var updates = settings.Current.Updates;
        if (!updates.AutoCheck) return;
        if (updates.LastCheck is { } last && DateTimeOffset.Now - last < CheckEvery) return;
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ => queue.Enqueue(() => _ = CheckAsync(manual: false)));
    }

    /// <summary>Asks GitHub; a manual check also offers a version the user skipped before.</summary>
    public async Task CheckAsync(bool manual)
    {
        if (State is UpdateState.Checking or UpdateState.Downloading) return;
        Set(UpdateState.Checking);
        try
        {
            var found = await checker.CheckAsync(AppEnvironment.Version);
#if DEBUG
            // QA hook: MOONMOVIE_DEBUG_UPDATE=1.9.0 pretends that release is out.
            if (Environment.GetEnvironmentVariable("MOONMOVIE_DEBUG_UPDATE") is { Length: > 0 } fake && UpdateChecker.TryParse(fake, out var v))
            {
                found = new UpdateInfo(v, "v" + fake, "测试", DateTimeOffset.Now, null, 0, UpdateChecker.ReleasesPage);
            }
#endif
            settings.Current.Updates.LastCheck = DateTimeOffset.Now;
            settings.Save();
            if (found is not null && !manual && settings.Current.Updates.SkippedVersion == found.Version.ToString(3)) found = null;
            Available = found;
            Set(found is null ? UpdateState.UpToDate : UpdateState.Available);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Error = manual ? "无法连接 GitHub，请稍后再试" : null;
            Set(manual ? UpdateState.Failed : UpdateState.Idle);
        }
    }

    public void Skip()
    {
        if (Available is not { } update) return;
        settings.Current.Updates.SkippedVersion = update.Version.ToString(3);
        settings.Save();
        Available = null;
        Set(UpdateState.Idle);
    }

    /// <summary>Installed: download and open the package (App Installer takes it from there). Portable: the release page.</summary>
    public async Task InstallAsync()
    {
        if (Available is not { } update || State == UpdateState.Downloading) return;
        if (!AppEnvironment.IsPackaged || update.MsixUrl is null)
        {
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri(update.PageUrl));
            return;
        }

        Progress = 0;
        Set(UpdateState.Downloading);
        try
        {
            var path = await checker.DownloadAsync(update, new Progress<double>(p =>
            {
                Progress = p;
                Changed?.Invoke();
            }));
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            await Windows.System.Launcher.LaunchFileAsync(file);
            Set(UpdateState.Available);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            Error = "下载更新失败，请稍后再试";
            Set(UpdateState.Failed);
        }
    }

    private void Set(UpdateState state)
    {
        State = state;
        if (state != UpdateState.Failed) Error = null;
        Changed?.Invoke();
    }
}
