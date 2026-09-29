using CommunityToolkit.Mvvm.Input;
using MoonMovie.Core.Models;
using MoonMovie.Core.Tmdb;

namespace MoonMovie.ViewModels;

public sealed class PersonViewModel(Person person, TmdbClient tmdb)
{
    public string Name => person.Name;

    public string Role => string.IsNullOrWhiteSpace(person.Role) ? " " : person.Role!;

    public string? ProfileUrl => tmdb.ImageUrl(person.ProfilePath, "w185");

    public string Initial => person.Name.Length > 0 ? person.Name[..1] : "?";
}

public sealed partial class EpisodeViewModel(EpisodeInfo episode, TmdbClient tmdb, Action<EpisodeViewModel> play)
{
    public EpisodeInfo Episode { get; } = episode;

    public int Number => Episode.Number;

    public string Heading => $"第 {Episode.Number} 集";

    public string Name => Episode.Name;

    /// <summary>TMDB often fills untranslated names with "第 N 集"; avoid saying it twice.</summary>
    public string Title => Episode.Name.Contains(Episode.Number.ToString()) && Episode.Name.Contains('集')
        ? Heading
        : $"{Heading}  {Episode.Name}";

    public string? Overview => Episode.Overview;

    public string Meta
    {
        get
        {
            var parts = new List<string>(2);
            if (Episode.RuntimeMinutes is > 0 and var m) parts.Add($"{m} 分钟");
            if (Episode.AirDate is { Length: >= 10 } d) parts.Add(d);
            return string.Join(" · ", parts);
        }
    }

    public string? StillUrl => tmdb.ImageUrl(Episode.StillPath, "w300");

    public string NumberText => Episode.Number.ToString("00");

    [RelayCommand]
    private void Play() => play(this);
}
