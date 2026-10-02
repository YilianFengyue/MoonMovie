using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using MoonMovie.Core.Downloads;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace MoonMovie.Services;

/// <summary>
/// Windows notifications: a download finished (播放 / 打开文件夹) or failed (查看下载). The classic toast API works
/// both packaged (the package identity) and unpackaged (an app id registered per user, as Windows asks of desktop
/// apps). Clicks are handled in-process while MoonMovie runs; the packaged app is also launched by them.
/// </summary>
public static partial class Notifications
{
    private const string UnpackagedAppId = "Ylfmoonn.MoonMovie.Desktop";

    private static ToastNotifier? _notifier;

    /// <summary>At startup: the notifier, and download events.</summary>
    public static void Init()
    {
        try
        {
            if (AppEnvironment.IsPackaged)
            {
                _notifier = ToastNotificationManager.CreateToastNotifier();
            }
            else
            {
                RegisterAppId();
                _notifier = ToastNotificationManager.CreateToastNotifier(UnpackagedAppId);
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException or SecurityException)
        {
            Log("unavailable: " + ex.Message);
            return; // downloads still show in the app
        }

        var downloads = App.Services.GetRequiredService<DownloadManager>();
        downloads.Finished += DownloadFinished;
#if DEBUG
        // QA hook: MOONMOVIE_DEBUG_NOTIFY=1 shows a 下载完成 notification for the newest finished download.
        if (Environment.GetEnvironmentVariable("MOONMOVIE_DEBUG_NOTIFY") == "1")
        {
            _ = Task.Delay(4000).ContinueWith(_ => DownloadFinished(downloads.Items.LastOrDefault(i => i.State == DownloadState.Completed)
                ?? new DownloadItem { Title = "测试通知", EpisodeLabel = "第 1 集", State = DownloadState.Completed }));
        }
#endif
    }

    private static void DownloadFinished(DownloadItem item)
    {
        if (_notifier is null) return;
        var name = string.IsNullOrEmpty(item.EpisodeLabel) ? item.Title : $"{item.Title} {item.EpisodeLabel}";
        var done = item.State == DownloadState.Completed;
        var heading = done ? "下载完成" : "下载失败";
        var body = done || string.IsNullOrEmpty(item.Error) ? name : $"{name}：{item.Error}";
        var actions = done
            ? $"""
               <action content="播放" arguments="{Args("play", item.Id)}" activationType="foreground" />
               <action content="打开文件夹" arguments="{Args("folder", item.Id)}" activationType="foreground" />
               """
            : $"""<action content="查看下载" arguments="{Args("downloads", item.Id)}" activationType="foreground" />""";

        try
        {
            var xml = new XmlDocument();
            xml.LoadXml($"""
                <toast launch="{Args("downloads", item.Id)}">
                  <visual>
                    <binding template="ToastGeneric">
                      <text>{SecurityElement.Escape(heading)}</text>
                      <text>{SecurityElement.Escape(body)}</text>
                    </binding>
                  </visual>
                  <actions>{actions}</actions>
                </toast>
                """);
            var toast = new ToastNotification(xml) { Tag = item.Id[..Math.Min(16, item.Id.Length)], Group = "downloads" };
            toast.Activated += (_, args) =>
            {
                if (args is ToastActivatedEventArgs activated)
                {
                    App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                    {
                        App.MainWindow.BringToFront();
                        Handle(activated.Arguments);
                    });
                }
            };
            _notifier.Show(toast);
            Log($"shown {item.State}");
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            Log("show failed: " + ex.Message);
        }
    }

    /// <summary>"action=play&amp;id=…" from a notification or one of its buttons.</summary>
    public static void Handle(string arguments)
    {
        var parts = arguments.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        var action = parts.GetValueOrDefault("action", "downloads");
        var item = parts.TryGetValue("id", out var id) ? App.Services.GetRequiredService<DownloadManager>().Find(id) : null;
        var path = item?.OutputPath is { } p && File.Exists(p) ? p : null;

        switch (action)
        {
            case "play" when path is not null:
                _ = LocalPlayback.OpenPathsAsync([path]);
                break;
            case "folder" when path is not null:
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                break;
            default:
                App.MainWindow?.Navigate(typeof(Views.DownloadsPage), null);
                break;
        }
    }

    private static string Args(string action, string id) => SecurityElement.Escape($"action={action}&id={Uri.EscapeDataString(id)}");

    /// <summary>Windows shows desktop apps' notifications under an app id registered for the user: name and icon.</summary>
    private static void RegisterAppId()
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{UnpackagedAppId}");
        key.SetValue("DisplayName", "MoonMovie");
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppLogoLarge.png");
        if (File.Exists(icon)) key.SetValue("IconUri", icon);
        key.SetValue("IconBackgroundColor", "FF0C0D10");
    }

    [Conditional("DEBUG")]
    private static void Log(string line)
    {
        try
        {
            File.AppendAllText(Path.Combine(Core.Configuration.AppPaths.Root, "nav.log"), $"[{DateTime.Now:HH:mm:ss.fff}] notify {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
