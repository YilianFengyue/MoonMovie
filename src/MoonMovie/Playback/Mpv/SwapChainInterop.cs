using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;

namespace MoonMovie.Playback.Mpv;

/// <summary>Binds mpv's DXGI composition swap chain to a WinUI SwapChainPanel and maps it onto the panel.</summary>
internal static unsafe class SwapChainInterop
{
    // microsoft.ui.xaml.media.dxinterop.h — the WinUI 3 interface (not the UWP one).
    private static readonly Guid IidSwapChainPanelNative = new("63aad0b8-7c24-40ff-85a8-640d944cc325");
    private static readonly Guid IidSwapChain2 = new("a8be2ac4-199f-4946-b331-79599fb98de7");

    // IDXGISwapChain2 vtable: IUnknown 0–2, IDXGIObject 3–6, IDXGIDeviceSubObject 7, IDXGISwapChain 8–17,
    // IDXGISwapChain1 18–28, IDXGISwapChain2 29–35.
    private const int SlotGetDesc1 = 18;
    private const int SlotSetMatrixTransform = 34;

    /// <summary>UI thread only. Pass 0 to detach.</summary>
    public static void SetSwapChain(SwapChainPanel panel, nint swapChain)
    {
        var unknown = ((WinRT.IWinRTObject)panel).NativeObject.ThisPtr;
        var iid = IidSwapChainPanelNative;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var native));
        try
        {
            var vtable = *(void***)native;
            var setSwapChain = (delegate* unmanaged[MemberFunction]<nint, nint, int>)vtable[3];
            Marshal.ThrowExceptionForHR(setSwapChain(native, swapChain));
        }
        finally
        {
            Marshal.Release(native);
        }
    }

    /// <summary>
    /// The panel shows the buffer at one DIP per pixel unless told otherwise; this scales it onto the panel
    /// (the inverse DPI once sizes match, a temporary stretch while a resize is in flight).
    /// </summary>
    public static void SetMatrix(nint swapChain, float scaleX, float scaleY)
    {
        if (swapChain == 0 || !(scaleX > 0) || !(scaleY > 0)) return;
        var iid = IidSwapChain2;
        if (Marshal.QueryInterface(swapChain, in iid, out var sc2) < 0) return;
        try
        {
            var matrix = stackalloc float[6] { scaleX, 0, 0, scaleY, 0, 0 };
            var vtable = *(void***)sc2;
            var set = (delegate* unmanaged[MemberFunction]<nint, float*, int>)vtable[SlotSetMatrixTransform];
            set(sc2, matrix);
        }
        finally
        {
            Marshal.Release(sc2);
        }
    }

    /// <summary>Back-buffer size in pixels, or (0, 0).</summary>
    public static (int Width, int Height) BufferSize(nint swapChain)
    {
        if (swapChain == 0) return (0, 0);
        var iid = IidSwapChain2;
        if (Marshal.QueryInterface(swapChain, in iid, out var sc2) < 0) return (0, 0);
        try
        {
            var desc = stackalloc uint[12]; // DXGI_SWAP_CHAIN_DESC1: Width, Height, …
            var vtable = *(void***)sc2;
            var get = (delegate* unmanaged[MemberFunction]<nint, uint*, int>)vtable[SlotGetDesc1];
            return get(sc2, desc) < 0 ? (0, 0) : ((int)desc[0], (int)desc[1]);
        }
        finally
        {
            Marshal.Release(sc2);
        }
    }
}
