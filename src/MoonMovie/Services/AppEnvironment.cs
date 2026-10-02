using System.Reflection;
using System.Runtime.InteropServices;

namespace MoonMovie.Services;

/// <summary>How this copy of MoonMovie runs: installed from the MSIX (package identity) or portable, and its version.</summary>
public static partial class AppEnvironment
{
    public static bool IsPackaged { get; } = DetectPackaged();

    /// <summary>The package version when installed, else the build's (stamped by build/package.ps1).</summary>
    public static Version Version { get; } = DetectVersion();

    public static string VersionText => Version.ToString(3);

    private static bool DetectPackaged()
    {
        var length = 0;
        return GetCurrentPackageFullName(ref length, 0) != 15700; // APPMODEL_ERROR_NO_PACKAGE
    }

    private static Version DetectVersion()
    {
        if (IsPackaged)
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            return new Version(v.Major, v.Minor, v.Build);
        }

        var informational = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informational is not null && Version.TryParse(informational.Split('+', '-')[0], out var parsed)) return parsed;
        return typeof(App).Assembly.GetName().Version ?? new Version(1, 0, 0);
    }

    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFullName(ref int length, nint name);
}
