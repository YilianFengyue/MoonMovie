using System.ComponentModel;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MoonMovie.Controls;
using MoonMovie.Imaging;
using MoonMovie.Services;
using MoonMovie.ViewModels;

namespace MoonMovie.Views;

/// <summary>个人主页: the local profile, linked accounts, recent plays and favourites.</summary>
public sealed partial class ProfilePage : Page
{
    public ProfilePage()
    {
        ViewModel = App.Services.GetRequiredService<ProfileViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelChanged;
    }

    public ProfileViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Attach();
        Apply();
#if DEBUG
        // QA hook: MOONMOVIE_DEBUG_BILI_LOGIN=1 opens the QR dialog.
        if (Environment.GetEnvironmentVariable("MOONMOVIE_DEBUG_BILI_LOGIN") == "1")
        {
            Loaded += async (_, _) => await BiliLoginDialog.ShowAsync(XamlRoot);
        }
#endif
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Detach();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) => Apply();

    /// <summary>Images and rows that follow the view model.</summary>
    private void Apply()
    {
        ImageEx.SetUrl(AvatarImage, ViewModel.AvatarUrl);
        ImageEx.SetUrl(BiliFaceImage, ViewModel.BiliFace);
        UseBiliAvatarItem.Visibility = ViewModel.Bili is not null ? Visibility.Visible : Visibility.Collapsed;
        RecentRow.Visibility = ViewModel.Recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FavoritesRow.Visibility = ViewModel.Favorites.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NothingYet.Visibility = ViewModel.Recent.Count + ViewModel.Favorites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Ambient.Show(ViewModel.Recent.FirstOrDefault(r => r.AmbientUrl is not null)?.AmbientUrl);
    }

    private async void OnEditName(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { Text = ViewModel.Name, MaxLength = 24, PlaceholderText = Environment.UserName };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "修改昵称",
            Content = box,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };
        box.Loaded += (_, _) => box.SelectAll();
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) ViewModel.SetNickname(box.Text);
    }

    private async void OnPickAvatar(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif" }) picker.FileTypeFilter.Add(ext);
        if (await picker.PickSingleFileAsync() is not { } file) return;

        // Frame the face: a round crop like the avatar itself; images the cropper cannot read are used whole.
        var cropper = new CommunityToolkit.WinUI.Controls.ImageCropper
        {
            CropShape = CommunityToolkit.WinUI.Controls.CropShape.Circular,
            AspectRatio = 1,
            Width = 440,
            Height = 440,
        };
        try
        {
            await cropper.LoadImageFromFile(file);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException or IOException)
        {
            ViewModel.SetAvatar(file.Path);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "裁剪头像",
            Content = cropper,
            PrimaryButtonText = "使用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var cropped = Path.Combine(Path.GetTempPath(), $"moonmovie-avatar-{Guid.NewGuid():N}.png");
        try
        {
            await using (var stream = File.Create(cropped))
            {
                await cropper.SaveAsync(stream.AsRandomAccessStream(), CommunityToolkit.WinUI.Controls.BitmapFileFormat.Png);
            }

            ViewModel.SetAvatar(cropped); // copied into the data folder
        }
        finally
        {
            File.Delete(cropped);
        }
    }

    private void OnUseBiliAvatar(object sender, RoutedEventArgs e) => ViewModel.UseBiliAvatar();

    private void OnClearAvatar(object sender, RoutedEventArgs e) => ViewModel.ClearAvatar();

    private async void OnLinkBili(object sender, RoutedEventArgs e) => await BiliLoginDialog.ShowAsync(XamlRoot);

    private async void OnUnlinkBili(object sender, RoutedEventArgs e) => await ViewModel.UnlinkBiliAsync();

    private void OnAllHistory(object sender, RoutedEventArgs e) => Navigator.OpenLibrary(LibraryTab.History);

    private void OnAllFavorites(object sender, RoutedEventArgs e) => Navigator.OpenLibrary(LibraryTab.Favorites);
}
