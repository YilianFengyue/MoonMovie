namespace MoonMovie.Services;

/// <summary>
/// A few lines about starting and closing (lifecycle.log next to the data): when the window would not close or a
/// second launch went nowhere, this says which step it stopped at. Kept small; no personal data.
/// </summary>
public static class Lifecycle
{
    private static readonly string Path = System.IO.Path.Combine(Core.Configuration.AppPaths.Root, "lifecycle.log");

    public static void Log(string line)
    {
        try
        {
            var info = new FileInfo(Path);
            if (info.Exists && info.Length > 256 * 1024) File.Delete(Path);
            File.AppendAllText(Path, $"[{DateTime.Now:MM-dd HH:mm:ss.fff}] pid {Environment.ProcessId} {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
