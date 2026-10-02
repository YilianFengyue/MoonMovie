using Microsoft.UI.Dispatching;

namespace MoonMovie;

/// <summary>
/// Work handed to the UI thread outside XAML events — <see cref="DispatcherQueue"/> callbacks, timer ticks and
/// async continuations — fails the whole process in WinUI 3 when it throws (CoreMessaging stows the error and
/// terminates; <c>Application.UnhandledException</c> never sees it). Everything that enters the UI thread that way
/// goes through here instead: errors are written to crash.log and the app carries on, and once the window is
/// closing nothing more runs against the torn-down UI.
/// </summary>
public static class SafeDispatch
{
    /// <summary>Set when the main window closes: queued work is dropped from then on.</summary>
    public static volatile bool ShuttingDown;

    public static void Run(Action action)
    {
        if (ShuttingDown) return;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            App.WriteCrashLog(ex, "ui callback");
        }
    }

    /// <summary><see cref="DispatcherQueue.TryEnqueue(DispatcherQueueHandler)"/>, guarded.</summary>
    public static bool Enqueue(this DispatcherQueue queue, Action action) => queue.TryEnqueue(() => Run(action));

    public static bool Enqueue(this DispatcherQueue queue, DispatcherQueuePriority priority, Action action) =>
        queue.TryEnqueue(priority, () => Run(action));
}

/// <summary>The UI thread's context for <c>await</c>: continuations are guarded like any other UI callback.</summary>
public sealed class SafeSynchronizationContext(DispatcherQueue queue) : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) => queue.TryEnqueue(() => SafeDispatch.Run(() => d(state)));

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (queue.HasThreadAccess)
        {
            d(state);
            return;
        }

        using var done = new ManualResetEventSlim();
        Exception? error = null;
        queue.TryEnqueue(() =>
        {
            try
            {
                d(state);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait();
        if (error is not null) throw new InvalidOperationException("UI callback failed", error);
    }

    public override SynchronizationContext CreateCopy() => this;
}
