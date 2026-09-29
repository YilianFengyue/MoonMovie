using System.Text.Json;
using System.Text.Json.Serialization;

namespace MoonMovie.Core.Sources;

/// <summary>Apple CMS v10 (<c>/api.php/provide/vod</c>) wire format and play-string parsing.</summary>
public static class MacCms
{
    public static IReadOnlyList<PlayLine> ParseLines(string? playFrom, string? playUrl)
    {
        if (string.IsNullOrWhiteSpace(playUrl))
        {
            return [];
        }

        var names = (playFrom ?? string.Empty).Split("$$$");
        var groups = playUrl.Split("$$$");
        var lines = new List<PlayLine>(groups.Length);

        for (var g = 0; g < groups.Length; g++)
        {
            var episodes = new List<PlayEpisode>();
            foreach (var part in groups[g].Split('#', StringSplitOptions.RemoveEmptyEntries))
            {
                var dollar = part.IndexOf('$');
                var name = dollar > 0 ? part[..dollar].Trim() : $"第{episodes.Count + 1}集";
                var url = (dollar >= 0 ? part[(dollar + 1)..] : part).Trim();
                if (IsPlayableUrl(url))
                {
                    episodes.Add(new PlayEpisode(episodes.Count, name, url));
                }
            }

            if (episodes.Count > 0)
            {
                var lineName = g < names.Length && names[g].Length > 0 ? names[g].Trim() : $"线路{g + 1}";
                lines.Add(new PlayLine(lineName, episodes));
            }
        }

        return lines;
    }

    /// <summary>Direct media only: share pages and player iframes are useless to a native player.</summary>
    public static bool IsPlayableUrl(string url) =>
        url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
        && (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
            || url.Contains(".mp4", StringComparison.OrdinalIgnoreCase));
}

public sealed class MacCmsResponse
{
    public int Code { get; set; }

    public List<MacCmsVod>? List { get; set; }
}

public sealed class MacCmsVod
{
    [JsonConverter(typeof(LooseStringConverter))]
    public string? VodId { get; set; }

    public string? VodName { get; set; }

    public string? VodSub { get; set; }

    public string? VodEn { get; set; }

    public string? TypeName { get; set; }

    public string? VodPic { get; set; }

    public string? VodRemarks { get; set; }

    [JsonConverter(typeof(LooseStringConverter))]
    public string? VodYear { get; set; }

    public string? VodArea { get; set; }

    public string? VodActor { get; set; }

    public string? VodDirector { get; set; }

    [JsonConverter(typeof(LooseStringConverter))]
    public string? VodDoubanId { get; set; }

    public string? VodPlayFrom { get; set; }

    public string? VodPlayUrl { get; set; }
}

/// <summary>Resource sites disagree on whether ids and years are numbers or strings.</summary>
public sealed class LooseStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l) ? l.ToString() : reader.GetDouble().ToString(),
            JsonTokenType.Null => null,
            _ => SkipAndNull(ref reader),
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);

    private static string? SkipAndNull(ref Utf8JsonReader reader)
    {
        reader.Skip();
        return null;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(MacCmsResponse))]
internal sealed partial class MacCmsJsonContext : JsonSerializerContext;
