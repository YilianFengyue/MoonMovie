using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace MoonMovie;

/// <summary>
/// Single instance: opening a video from Explorer (or a jump-list entry) while MoonMovie runs hands the request to
/// the running window instead of starting a second player.
/// </summary>
public static class Program
{
    private const string InstanceKey = "MoonMovie.Main";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!main.IsCurrent)
        {
            Redirect(main, AppInstance.GetCurrent().GetActivatedEventArgs());
            return 0;
        }

        main.Activated += (_, e) => Services.ActivationRouter.OnRedirected(e);
        Application.Start(_1 =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    /// <summary>Waits for the hand-off without blocking COM (the documented pattern), then yields focus.</summary>
    private static void Redirect(AppInstance target, AppActivationArguments args)
    {
        var done = CreateEventW(0, true, false, null);
        Task.Run(() =>
        {
            target.RedirectActivationToAsync(args).AsTask().Wait();
            SetEvent(done);
        });
        _ = CoWaitForMultipleObjects(0, 0xFFFFFFFF, 1, [done], out _);
        try
        {
            AllowSetForegroundWindow((int)target.ProcessId);
        }
        catch (ArgumentException)
        {
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateEventW(nint attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(nint handle);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint flags, uint timeout, uint count, nint[] handles, out uint index);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
