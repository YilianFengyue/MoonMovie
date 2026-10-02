using System.Runtime.InteropServices;

namespace MoonMovie.Playback;

/// <summary>
/// The taskbar button while playing: previous / play-pause / next buttons in the thumbnail preview, and the
/// playback (or download) progress on the icon. Win32 ITaskbarList3 on the main window, whose window procedure is
/// subclassed to receive the button clicks.
/// </summary>
public sealed unsafe class TaskbarControls : IDisposable
{
    private const int IdPrevious = 1;
    private const int IdPlayPause = 2;
    private const int IdNext = 3;
    private const int WmCommand = 0x0111;
    private const int ThbnClicked = 0x1800;
    private const int GwlpWndProc = -4;

    private static readonly Guid ClsidTaskbarList = new("56FDF344-FD6D-11d0-958A-006097C9A090");
    private static readonly Guid IidTaskbarList3 = new("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF");

    private readonly nint _hwnd;
    private readonly uint _taskbarCreatedMessage;
    private readonly WndProc _proc; // kept alive: native code holds a pointer to it
    private readonly nint _oldProc;
    private readonly nint[] _icons;
    private nint _taskbar;
    private bool _buttonsAdded;
    private bool _visible;
    private bool _playing;
    private bool _hasPrevious;
    private bool _hasNext;

    public TaskbarControls(nint hwnd)
    {
        _hwnd = hwnd;
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarButtonCreated");
        _icons = [GlyphIcon.Previous(), GlyphIcon.Play(), GlyphIcon.Pause(), GlyphIcon.Next()];
        _proc = HookProc;
        _oldProc = SetWindowLongPtrW(hwnd, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_proc));
        Connect();
    }

    public event Action? PreviousPressed;

    public event Action? PlayPausePressed;

    public event Action? NextPressed;

    /// <summary>Shows the buttons (or updates them) for the episode on screen.</summary>
    public void ShowPlayer(bool playing, bool hasPrevious, bool hasNext)
    {
        _visible = true;
        _playing = playing;
        _hasPrevious = hasPrevious;
        _hasNext = hasNext;
        UpdateButtons();
    }

    public void HidePlayer()
    {
        _visible = false;
        UpdateButtons();
        SetProgress(null, paused: false);
    }

    /// <summary>0..1 on the taskbar icon (null clears it); paused shows the yellow state.</summary>
    public void SetProgress(double? fraction, bool paused)
    {
        if (_taskbar == 0) return;
        var vtable = *(nint**)_taskbar;
        var setState = (delegate* unmanaged[MemberFunction]<nint, nint, int, int>)vtable[10];
        if (fraction is not { } f)
        {
            setState(_taskbar, _hwnd, 0); // TBPF_NOPROGRESS
            return;
        }

        var setValue = (delegate* unmanaged[MemberFunction]<nint, nint, ulong, ulong, int>)vtable[9];
        setValue(_taskbar, _hwnd, (ulong)(Math.Clamp(f, 0, 1) * 10000), 10000);
        setState(_taskbar, _hwnd, paused ? 8 : 2); // TBPF_PAUSED : TBPF_NORMAL
    }

    private void Connect()
    {
        if (_taskbar != 0) Marshal.Release(_taskbar);
        _taskbar = 0;
        _buttonsAdded = false;
        var clsid = ClsidTaskbarList;
        var iid = IidTaskbarList3;
        if (CoCreateInstance(&clsid, 0, 1 /* CLSCTX_INPROC_SERVER */, &iid, out var taskbar) < 0) return;
        _taskbar = taskbar;
        var hrInit = (delegate* unmanaged[MemberFunction]<nint, int>)(*(nint**)_taskbar)[3];
        if (hrInit(_taskbar) < 0)
        {
            Marshal.Release(_taskbar);
            _taskbar = 0;
        }
    }

    /// <summary>Buttons can only be added once per taskbar button, so they are added hidden and toggled after.</summary>
    private void UpdateButtons()
    {
        if (_taskbar == 0) return;
        var buttons = stackalloc ThumbButton[3];
        Fill(ref buttons[0], IdPrevious, _icons[0], "上一集", _visible && _hasPrevious, _visible);
        Fill(ref buttons[1], IdPlayPause, _playing ? _icons[2] : _icons[1], _playing ? "暂停" : "播放", _visible, _visible);
        Fill(ref buttons[2], IdNext, _icons[3], "下一集", _visible && _hasNext, _visible);

        var vtable = *(nint**)_taskbar;
        var slot = _buttonsAdded ? 16 : 15; // ThumbBarUpdateButtons : ThumbBarAddButtons
        var call = (delegate* unmanaged[MemberFunction]<nint, nint, uint, ThumbButton*, int>)vtable[slot];
        if (call(_taskbar, _hwnd, 3, buttons) >= 0) _buttonsAdded = true;
    }

    private static void Fill(ref ThumbButton button, int id, nint icon, string tip, bool enabled, bool visible)
    {
        button.Mask = 0x2 | 0x4 | 0x8; // THB_ICON | THB_TOOLTIP | THB_FLAGS
        button.Id = (uint)id;
        button.Icon = icon;
        button.Flags = visible ? (enabled ? 0u : 1u) : 8u; // ENABLED / DISABLED / HIDDEN
        fixed (char* dest = button.Tip)
        {
            var span = new Span<char>(dest, 260);
            span.Clear();
            tip.AsSpan(0, Math.Min(tip.Length, 259)).CopyTo(span);
        }
    }

    private nint HookProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WmCommand && ((wParam >> 16) & 0xFFFF) == ThbnClicked)
        {
            switch ((int)(wParam & 0xFFFF))
            {
                case IdPrevious: PreviousPressed?.Invoke(); break;
                case IdPlayPause: PlayPausePressed?.Invoke(); break;
                case IdNext: NextPressed?.Invoke(); break;
            }

            return 0;
        }

        if (msg == _taskbarCreatedMessage)
        {
            // Explorer restarted: the buttons have to be added again.
            Connect();
            UpdateButtons();
        }

        return CallWindowProcW(_oldProc, hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        SetWindowLongPtrW(_hwnd, GwlpWndProc, _oldProc);
        if (_taskbar != 0) Marshal.Release(_taskbar);
        _taskbar = 0;
        foreach (var icon in _icons) DestroyIcon(icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ThumbButton
    {
        public uint Mask;
        public uint Id;
        public uint Bitmap;
        public nint Icon;
        public fixed char Tip[260];
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, out nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProcW(nint previous, nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);
}

/// <summary>Small white transport glyphs drawn into icons (no image assets, crisp at any DPI step we use).</summary>
internal static unsafe class GlyphIcon
{
    private const int Size = 32;

    public static nint Play() => Draw((x, y) => Triangle(x, y, 10, 7, 10, 25, 25, 16));

    public static nint Pause() => Draw((x, y) => Rect(x, y, 9, 7, 14, 25) || Rect(x, y, 18, 7, 23, 25));

    public static nint Previous() => Draw((x, y) => Rect(x, y, 7, 8, 10, 24) || Triangle(x, y, 25, 8, 25, 24, 11, 16));

    public static nint Next() => Draw((x, y) => Rect(x, y, 22, 8, 25, 24) || Triangle(x, y, 7, 8, 7, 24, 21, 16));

    private static bool Rect(double x, double y, double l, double t, double r, double b) => x >= l && x <= r && y >= t && y <= b;

    private static bool Triangle(double x, double y, double ax, double ay, double bx, double by, double cx, double cy)
    {
        double Side(double px, double py, double qx, double qy) => (x - qx) * (py - qy) - (px - qx) * (y - qy);
        var d1 = Side(ax, ay, bx, by);
        var d2 = Side(bx, by, cx, cy);
        var d3 = Side(cx, cy, ax, ay);
        var negative = d1 < 0 || d2 < 0 || d3 < 0;
        var positive = d1 > 0 || d2 > 0 || d3 > 0;
        return !(negative && positive);
    }

    /// <summary>4×4 supersampled coverage into a premultiplied 32-bit icon.</summary>
    private static nint Draw(Func<double, double, bool> inside)
    {
        var pixels = new uint[Size * Size];
        for (var py = 0; py < Size; py++)
        {
            for (var px = 0; px < Size; px++)
            {
                var hits = 0;
                for (var sy = 0; sy < 4; sy++)
                {
                    for (var sx = 0; sx < 4; sx++)
                    {
                        if (inside(px + (sx + 0.5) / 4, py + (sy + 0.5) / 4)) hits++;
                    }
                }

                var a = (uint)(hits * 255 / 16);
                pixels[py * Size + px] = (a << 24) | (a << 16) | (a << 8) | a;
            }
        }

        var header = new BitmapInfoHeader
        {
            Size = (uint)sizeof(BitmapInfoHeader),
            Width = Size,
            Height = -Size, // top-down
            Planes = 1,
            BitCount = 32,
        };
        void* bits;
        var color = CreateDIBSection(0, &header, 0, &bits, 0, 0);
        fixed (uint* source = pixels) Buffer.MemoryCopy(source, bits, pixels.Length * 4, pixels.Length * 4);
        var mask = CreateBitmap(Size, Size, 1, 1, null);
        var info = new IconInfo { IsIcon = 1, Color = color, Mask = mask };
        var icon = CreateIconIndirect(&info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public int HotspotX;
        public int HotspotY;
        public nint Mask;
        public nint Color;
    }

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint dc, BitmapInfoHeader* info, uint usage, void** bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint CreateBitmap(int width, int height, uint planes, uint bitCount, void* bits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);

    [DllImport("user32.dll")]
    private static extern nint CreateIconIndirect(IconInfo* info);
}
