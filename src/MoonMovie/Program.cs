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

        var key = InstanceKey;
#if DEBUG
        // Test runs next to a copy that is in use: MOONMOVIE_INSTANCE=<name> keeps them apart.
        key += Environment.GetEnvironmentVariable("MOONMOVIE_INSTANCE");
#endif
        var main = AppInstance.FindOrRegisterForKey(key);
        if (!main.IsCurrent)
        {
            if (Redirect(main, AppInstance.GetCurrent().GetActivatedEventArgs())) return 0;

            // The running copy did not answer (it may be closing, or hung): open a window of our own rather than
            // leaving the user with nothing.
            Services.Lifecycle.Log($"redirect to {main.ProcessId} timed out; starting a window of our own");
        }
        else
        {
            main.Activated += (_, e) => Services.ActivationRouter.OnRedirected(e);
        }

        Services.Lifecycle.Log("start");
        Application.Start(_1 =>
        {
            SynchronizationContext.SetSynchronizationContext(new SafeSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        Services.Lifecycle.Log("exit");
        return 0;
    }

    /// <summary>
    /// Waits for the hand-off without blocking COM (the documented pattern), then yields focus. False when the
    /// running copy did not take it within a few seconds.
    /// </summary>
    private static bool Redirect(AppInstance target, AppActivationArguments args)
    {
        var done = CreateEventW(0, true, false, null);
        Task.Run(() =>
        {
            try
            {
                target.RedirectActivationToAsync(args).AsTask().Wait();
                SetEvent(done);
            }
            catch (AggregateException)
            {
                // The target went away mid-hand-off: the wait below times out and we start normally.
            }
        });
        var result = CoWaitForMultipleObjects(0, 5000, 1, [done], out _);
        if (result != 0) return false; // RPC_S_CALLPENDING: no answer in time

        try
        {
            AllowSetForegroundWindow((int)target.ProcessId);
        }
        catch (ArgumentException)
        {
        }

        return true;
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
