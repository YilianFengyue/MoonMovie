using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Windows.AppLifecycle;
using MoonMovie.Core.Playback;
using Windows.ApplicationModel.Activation;

namespace MoonMovie.Services;

/// <summary>
/// What MoonMovie was started (or re-activated) to do: play files (command line, "Open with", file association) or
/// resume a title from the jump list (<c>--resume &lt;media key&gt;</c>).
/// </summary>
public static class ActivationRouter
{
    public const string ResumeSwitch = "--resume";

    /// <summary>The first launch, once the window exists.</summary>
    public static void OnLaunched() => Handle(AppInstance.GetCurrent().GetActivatedEventArgs(), Environment.GetCommandLineArgs().Skip(1).ToArray());

    /// <summary>A second launch handed over to this instance (raised on a background thread).</summary>
    public static void OnRedirected(AppActivationArguments args) =>
        App.MainWindow?.DispatcherQueue.Enqueue(() =>
        {
            App.MainWindow.BringToFront();
            Handle(args, null);
        });

    private static void Handle(AppActivationArguments args, string[]? commandLine)
    {
        var words = commandLine ?? [];
        // A click on a 下载完成 notification that launched (packaged) MoonMovie.
        if (args.Kind == ExtendedActivationKind.ToastNotification && args.Data is IToastNotificationActivatedEventArgs toast)
        {
            Notifications.Handle(toast.Argument);
            return;
        }

        if (args.Kind == ExtendedActivationKind.File && args.Data is IFileActivatedEventArgs file)
        {
            var paths = file.Files.Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToArray();
            if (paths.Length > 0) Open(paths);
            return;
        }

        if (args.Kind == ExtendedActivationKind.Launch && args.Data is ILaunchActivatedEventArgs launch && commandLine is null)
        {
            words = Split(launch.Arguments);
        }

        var resume = Array.IndexOf(words, ResumeSwitch);
        if (resume >= 0 && resume + 1 < words.Length)
        {
            Resume(words[resume + 1]);
            return;
        }

        // The command line of a redirected launch starts with our own exe: only media paths count.
        var existing = words.Where(w => (File.Exists(w) && !w.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) || Directory.Exists(w)).ToArray();
        if (existing.Length > 0) Open(existing);
    }

    private static async void Open(string[] paths)
    {
        try
        {
            await LocalPlayback.OpenPathsAsync(paths);
        }
        catch (Exception ex)
        {
            Log("open failed: " + ex);
        }
    }

    /// <summary>"tmdb:tv:1396|2": the title and season to continue.</summary>
    private static void Resume(string value)
    {
        var parts = value.Split('|');
        var progress = App.Services.GetRequiredService<WatchProgressStore>().Latest(parts[0]);
        if (progress is null) return;
        int? season = parts.Length > 1 && int.TryParse(parts[1], out var s) && s > 0 ? s : progress.Season;
        Navigator.Resume(progress.ToMediaItem(), season);
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void Log(string line)
    {
        try
        {
            File.AppendAllText(Path.Combine(Core.Configuration.AppPaths.Root, "nav.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    private static string[] Split(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return [];
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == 0) return [];
        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * nint.Size)) ?? "";
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
