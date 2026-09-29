using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MoonMovie.Services;

namespace MoonMovie.Views;

public sealed partial class PlaceholderPage : Page
{
    public PlaceholderPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is PlaceholderArgs args)
        {
            TitleText.Text = args.Title;
            CaptionText.Text = args.Caption;
        }
    }
}
