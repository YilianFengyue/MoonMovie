using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MoonMovie.Controls;

/// <summary>
/// Pill toggles built in code: quiet text that becomes a solid light pill when on. (The default ToggleButton and
/// ToggleSwitch templates carry implicit transitions — see Animations/Motion.cs — so the state is drawn here.)
/// </summary>
public static class Chips
{
    private static readonly SolidColorBrush OnText = new(Windows.UI.Color.FromArgb(0xFF, 0x0C, 0x0D, 0x10));
    private static readonly SolidColorBrush OffText = new(Windows.UI.Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF));

    public static Button Create(string label, Action onClick)
    {
        var chip = new Button { Content = Content(label), Style = (Style)Application.Current.Resources["MoonChipButtonStyle"] };
        AutomationProperties.SetName(chip, label);
        chip.Click += (_, _) => onClick();
        return chip;
    }

    public static Grid Content(string label)
    {
        var grid = new Grid();
        grid.Children.Add(new Border
        {
            Background = (Brush)Application.Current.Resources["MoonPrimaryBrush"],
            CornerRadius = new CornerRadius(16),
            Visibility = Visibility.Collapsed,
        });
        grid.Children.Add(new TextBlock { Text = label, FontSize = 13, Margin = new Thickness(14, 6, 14, 7), Foreground = OffText });
        return grid;
    }

    public static void Set(Button chip, bool on)
    {
        if (chip.Content is not Grid { Children: [Border fill, TextBlock text] }) return;
        fill.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        text.Foreground = on ? OnText : OffText;
    }

    public static void SetLabel(Button chip, string label)
    {
        if (chip.Content is Grid { Children: [_, TextBlock text] }) text.Text = label;
        AutomationProperties.SetName(chip, label);
    }
}
