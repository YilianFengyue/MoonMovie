using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MoonMovie.Core.Home;

namespace MoonMovie.ViewModels;

public sealed partial class HomeRowViewModel(HomeRowDefinition definition) : ObservableObject
{
    public HomeRowDefinition Definition { get; } = definition;

    public string Title => Definition.Title;

    public bool IsLandscape => Definition.Style == HomeRowStyle.Landscape;

    public ObservableCollection<MediaCardViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsLoaded { get; set; }
}
