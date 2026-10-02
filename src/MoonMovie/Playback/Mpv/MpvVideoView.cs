using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace MoonMovie.Playback.Mpv;

/// <summary>
/// Shows an <see cref="MpvPlayer"/>'s swap chain. Keeps mpv's output size equal to the panel in physical
/// pixels, and while a resize is in flight stretches the old buffer over the panel so nothing jumps.
/// XAML content (danmaku, controls) placed above this panel composes over the video as usual.
/// </summary>
public sealed partial class MpvVideoView : SwapChainPanel
{
    private readonly DispatcherQueueTimer _resizeWatch;
    private MpvPlayer? _player;
    private nint _swapChain;
    private (int Width, int Height) _wanted;
    private DateTime _watchUntil;

    public MpvVideoView()
    {
        _resizeWatch = DispatcherQueue.CreateTimer();
        _resizeWatch.Interval = TimeSpan.FromMilliseconds(16);
        _resizeWatch.Tick += (_, _) => UpdateMatrix();

        SizeChanged += (_, _) => PushSize();
        Loaded += (_, _) =>
        {
            if (XamlRoot is not null) XamlRoot.Changed += (_, _) => PushSize();
            PushSize();
        };
        Unloaded += (_, _) => Detach();
    }

    private double Scale => XamlRoot?.RasterizationScale ?? 1.0;

    /// <summary>Physical pixel size the player should render at.</summary>
    public (int Width, int Height) PixelSize =>
        (Math.Max(1, (int)Math.Round(ActualWidth * Scale)), Math.Max(1, (int)Math.Round(ActualHeight * Scale)));

    public void Attach(MpvPlayer player)
    {
        _player = player;
        player.SwapChainChanged += OnSwapChainChanged;
        if (player.GetInt64("display-swapchain") is > 0 and var existing) OnSwapChainChanged((nint)existing);
        PushSize();
    }

    public void Detach()
    {
        _resizeWatch.Stop();
        if (_player is not null) _player.SwapChainChanged -= OnSwapChainChanged;
        _player = null;
        if (_swapChain != 0)
        {
            // Never leave XAML presenting a swap chain that is about to be destroyed.
            SwapChainInterop.SetSwapChain(this, 0);
            _swapChain = 0;
        }
    }

    private void OnSwapChainChanged(nint swapChain)
    {
        if (swapChain == _swapChain) return;
        _swapChain = swapChain;
        SwapChainInterop.SetSwapChain(this, swapChain);
        Diag($"swapchain=0x{swapChain:X} loaded={IsLoaded} size={ActualWidth:0}x{ActualHeight:0}");
        UpdateMatrix();
    }

    private void PushSize()
    {
        if (_player is null || ActualWidth < 1 || ActualHeight < 1) return;
        var wanted = PixelSize;
        if (wanted != _wanted)
        {
            _wanted = wanted;
            _player.SetCompositionSize(wanted.Width, wanted.Height);
        }

        _watchUntil = DateTime.UtcNow.AddSeconds(1);
        UpdateMatrix();
        if (!_resizeWatch.IsRunning) _resizeWatch.Start();
    }

    /// <summary>Maps the current buffer onto the panel; stops watching once mpv has caught up with the size.</summary>
    private void UpdateMatrix()
    {
        var (bw, bh) = SwapChainInterop.BufferSize(_swapChain);
        if (bw <= 0 || bh <= 0)
        {
            if (DateTime.UtcNow > _watchUntil) _resizeWatch.Stop();
            return;
        }

        SwapChainInterop.SetMatrix(_swapChain, (float)(ActualWidth / bw), (float)(ActualHeight / bh));
        Diag($"matrix buffer={bw}x{bh} panel={ActualWidth:0}x{ActualHeight:0} wanted={_wanted.Width}x{_wanted.Height}");
        if ((bw, bh) == _wanted || DateTime.UtcNow > _watchUntil) _resizeWatch.Stop();
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void Diag(string line)
    {
        try
        {
            File.AppendAllText(Path.Combine(Core.Configuration.AppPaths.Root, "player.log"), $"[{DateTime.Now:HH:mm:ss.fff}] view {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
