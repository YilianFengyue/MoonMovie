namespace MoonMovie.Core.Configuration;

/// <summary>
/// Minimal dotenv reader. Later duplicate keys override earlier ones.
/// </summary>
public sealed class EnvFile
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static EnvFile Empty { get; } = new();

    public static EnvFile Load(params string[] candidatePaths)
    {
        var env = new EnvFile();
        var path = candidatePaths.FirstOrDefault(File.Exists);
        if (path is null)
        {
            return env;
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line[7..];
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            {
                value = value[1..^1];
            }
            else
            {
                var hash = value.IndexOf(" #", StringComparison.Ordinal);
                if (hash >= 0)
                {
                    value = value[..hash].TrimEnd();
                }
            }

            env._values[key] = value;
        }

        return env;
    }

    public string? Get(string key)
    {
        var fromProcess = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(fromProcess))
        {
            return fromProcess;
        }

        return _values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
    }
}
