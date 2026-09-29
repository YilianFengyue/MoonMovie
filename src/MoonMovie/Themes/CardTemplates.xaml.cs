using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MoonMovie.ViewModels;

namespace MoonMovie.Themes;

public sealed partial class CardTemplates : ResourceDictionary
{
    public CardTemplates() => InitializeComponent();
}

/// <summary>Picks poster or wide tile for the discover wall.</summary>
public sealed partial class WallTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Poster { get; set; }

    public DataTemplate? Landscape { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is MediaCardViewModel { Variant: CardVariant.Landscape } ? Landscape : Poster;
}
