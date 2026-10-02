using System.Runtime.InteropServices;
using MoonMovie.Core.Settings;

namespace MoonMovie.Playback.Engines;

/// <summary>The primary GPU (DXGI adapter 0), used to pick a sensible picture-quality preset.</summary>
public static unsafe class GpuInfo
{
    private static readonly Lazy<(string Name, long DedicatedBytes, uint Vendor)> Primary = new(Query);

    public static string Name => Primary.Value.Name;

    public static long DedicatedBytes => Primary.Value.DedicatedBytes;

    /// <summary>Discrete NVIDIA / AMD with ≥ 4 GB → 画质; ≥ 1.5 GB → 均衡; integrated → 性能.</summary>
    public static QualityPreset Recommended
    {
        get
        {
            var (_, dedicated, vendor) = Primary.Value;
            var discreteVendor = vendor is 0x10DE or 0x1002; // NVIDIA, AMD
            if (discreteVendor && dedicated >= 4L << 30) return QualityPreset.Quality;
            if (dedicated >= 1536L << 20) return QualityPreset.Balanced;
            return QualityPreset.Performance;
        }
    }

    private static (string, long, uint) Query()
    {
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
            if (CreateDXGIFactory1(&iid, out var factory) < 0) return ("", 0, 0);
            try
            {
                // IDXGIFactory1::EnumAdapters1 (vtable 12) → IDXGIAdapter1::GetDesc1 (vtable 10).
                var vtable = *(void***)factory;
                nint adapter;
                var enumAdapters = (delegate* unmanaged[MemberFunction]<nint, uint, nint*, int>)vtable[12];
                if (enumAdapters(factory, 0, &adapter) < 0) return ("", 0, 0);
                try
                {
                    var desc = stackalloc byte[512];
                    var getDesc = (delegate* unmanaged[MemberFunction]<nint, byte*, int>)(*(void***)adapter)[10];
                    if (getDesc(adapter, desc) < 0) return ("", 0, 0);

                    // DXGI_ADAPTER_DESC1: WCHAR Description[128]; UINT VendorId, DeviceId, SubSysId, Revision;
                    // SIZE_T DedicatedVideoMemory, …
                    var name = new string((char*)desc).TrimEnd('\0');
                    var vendor = *(uint*)(desc + 256);
                    var dedicated = *(long*)(desc + 256 + 16);
                    return (name, dedicated, vendor);
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return ("", 0, 0);
        }
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(Guid* riid, out nint factory);
}
