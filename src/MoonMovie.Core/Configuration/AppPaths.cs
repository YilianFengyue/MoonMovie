namespace MoonMovie.Core.Configuration;

public static class AppPaths
{
    public static string Root { get; } = Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MoonMovie"));

    public static string ApiCache { get; } = Ensure(Path.Combine(Root, "cache", "api"));

    public static string ImageCache { get; } = Ensure(Path.Combine(Root, "cache", "img"));

    public static string Data { get; } = Ensure(Path.Combine(Root, "data"));

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
