using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace MoonMovie.Playback;

/// <summary>A row in the player's episode panel.</summary>
public sealed partial class PlayerEpisodeItem(int index, string label, string? name, double watched, string? stillUrl = null)
    : ObservableObject
{
    public int Index { get; } = index;

    public string? StillUrl { get; } = stillUrl;

    public Visibility StillVisibility => StillUrl is null ? Visibility.Collapsed : Visibility.Visible;

    public string Label { get; } = label;

    public string Name { get; } = name ?? string.Empty;

    /// <summary>0..1 of the episode already watched.</summary>
    public double Watched { get; } = watched;

    public Visibility WatchedVisibility => Watched > 0.02 ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsCurrentChanged(bool value) => OnPropertyChanged(nameof(CurrentVisibility));
}

public static class TimeText
{
    public static string Format(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
}

/// <summary>Seek bar thumb tooltip: seconds → "1:23:45".</summary>
public sealed partial class SecondsToTimeConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        TimeText.Format(TimeSpan.FromSeconds(value is double d ? d : 0));

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
