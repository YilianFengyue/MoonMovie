using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MoonMovie.Core.Bilibili;
using MoonMovie.Services;
using QRCoder;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;

namespace MoonMovie.Controls;

/// <summary>
/// 「关联 B站账号」: a QR code for the B站 phone app, polled until confirmed. The code renews itself when it
/// expires; the dialog closes on success.
/// </summary>
public static class BiliLoginDialog
{
    public static async Task<bool> ShowAsync(XamlRoot root)
    {
        var client = App.Services.GetRequiredService<BiliClient>();
        var accounts = App.Services.GetRequiredService<BiliAccountService>();

        var qr = new Image { Width = 216, Height = 216, Stretch = Stretch.Uniform };
        var ring = new ProgressRing { Width = 32, Height = 32, IsActive = true };
        var status = new TextBlock
        {
            Text = "打开哔哩哔哩 App，扫一扫",
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 15,
            Foreground = (Brush)Application.Current.Resources["MoonTextPrimaryBrush"],
        };
        var hint = new TextBlock
        {
            Text = "关联后可以看 1080P 和大会员画质、把观看进度同步到 B站、查看全部评论。登录信息保存在 Windows 凭据管理器里，只在这台电脑上使用。",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            FontSize = 12,
            MaxWidth = 340,
            Foreground = (Brush)Application.Current.Resources["MoonTextTertiaryBrush"],
        };

        var card = new Grid
        {
            Width = 240,
            Height = 240,
            Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            CornerRadius = new CornerRadius(14),
        };
        card.Children.Add(ring);
        card.Children.Add(qr);

        var content = new StackPanel { Spacing = 16, Width = 360 };
        content.Children.Add(card);
        content.Children.Add(status);
        content.Children.Add(hint);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "关联 B站账号",
            Content = content,
            CloseButtonText = "取消",
            RequestedTheme = ElementTheme.Dark,
        };

        using var cts = new CancellationTokenSource();
        var signedIn = false;
        dialog.Closed += (_, _) => cts.Cancel();
        dialog.Opened += async (_, _) =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var (url, key) = await client.CreateLoginQrAsync(cts.Token);
                    qr.Source = await RenderAsync(url);
                    ring.IsActive = false;
                    status.Text = "打开哔哩哔哩 App，扫一扫";

                    var expired = false;
                    while (!expired && !cts.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
                        var poll = await client.PollLoginAsync(key, cts.Token);
                        switch (poll.State)
                        {
                            case BiliLoginState.Scanned:
                                status.Text = "已扫码，请在手机上点「确认登录」";
                                qr.Opacity = 0.25;
                                break;
                            case BiliLoginState.Expired:
                                expired = true;
                                qr.Opacity = 1;
                                ring.IsActive = true;
                                status.Text = "二维码已过期，正在刷新…";
                                break;
                            case BiliLoginState.Done when poll.Credentials is { } credentials:
                                status.Text = "登录成功";
                                await accounts.CompleteLoginAsync(credentials);
                                signedIn = true;
                                dialog.Hide();
                                return;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is HttpRequestException or BiliException or System.Text.Json.JsonException)
            {
                ring.IsActive = false;
                status.Text = "连接 B站失败，请稍后再试";
            }
        };

        await dialog.ShowAsync();
        return signedIn;
    }

    private static async Task<BitmapImage> RenderAsync(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8, [0x18, 0x18, 0x1B, 0xFF], [0xFF, 0xFF, 0xFF, 0xFF], drawQuietZones: false);

        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }
}
