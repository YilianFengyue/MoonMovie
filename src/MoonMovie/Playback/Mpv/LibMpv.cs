using System.Runtime.InteropServices;

namespace MoonMovie.Playback.Mpv;

/// <summary>The handful of libmpv client API (include/mpv/client.h, API 2.x) calls MoonMovie uses.</summary>
internal static unsafe partial class LibMpv
{
    private const string Dll = "libmpv-2.dll";

    [LibraryImport(Dll)]
    public static partial uint mpv_client_api_version();

    [LibraryImport(Dll)]
    public static partial nint mpv_create();

    [LibraryImport(Dll)]
    public static partial int mpv_initialize(nint ctx);

    [LibraryImport(Dll)]
    public static partial void mpv_terminate_destroy(nint ctx);

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_set_option_string(nint ctx, string name, string data);

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_set_property_string(nint ctx, string name, string data);

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_get_property(nint ctx, string name, MpvFormat format, void* data);

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint mpv_get_property_string(nint ctx, string name);

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_observe_property(nint ctx, ulong replyUserdata, string name, MpvFormat format);

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int mpv_request_log_messages(nint ctx, string minLevel);

    [LibraryImport(Dll)]
    public static partial int mpv_command(nint ctx, nint* args);

    [LibraryImport(Dll)]
    public static partial int mpv_command_async(nint ctx, ulong replyUserdata, nint* args);

    [LibraryImport(Dll)]
    public static partial MpvEvent* mpv_wait_event(nint ctx, double timeout);

    [LibraryImport(Dll)]
    public static partial void mpv_wakeup(nint ctx);

    [LibraryImport(Dll)]
    public static partial void mpv_free(nint data);

    [LibraryImport(Dll)]
    public static partial nint mpv_error_string(int error);

    public static string ErrorString(int error) => Marshal.PtrToStringUTF8(mpv_error_string(error)) ?? error.ToString();

    /// <summary>Runs a command given as separate arguments (no quoting/escaping issues with paths and URLs).</summary>
    public static int Command(nint ctx, params string[] args) => WithArgs(args, p => mpv_command(ctx, p));

    public static int CommandAsync(nint ctx, ulong userdata, params string[] args) =>
        WithArgs(args, p => mpv_command_async(ctx, userdata, p));

    private delegate int ArgsCall(nint* args);

    private static int WithArgs(string[] args, ArgsCall call)
    {
        var pointers = new nint[args.Length + 1];
        try
        {
            for (var i = 0; i < args.Length; i++) pointers[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            fixed (nint* p = pointers) return call(p);
        }
        finally
        {
            foreach (var ptr in pointers)
            {
                if (ptr != 0) Marshal.FreeCoTaskMem(ptr);
            }
        }
    }
}

public enum MpvFormat
{
    None = 0,
    String = 1,
    OsdString = 2,
    Flag = 3,
    Int64 = 4,
    Double = 5,
    Node = 6,
}

internal enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    GetPropertyReply = 3,
    SetPropertyReply = 4,
    CommandReply = 5,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    ClientMessage = 16,
    VideoReconfig = 17,
    AudioReconfig = 18,
    Seek = 20,
    PlaybackRestart = 21,
    PropertyChange = 22,
    QueueOverflow = 24,
    Hook = 25,
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserdata;
    public void* Data;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpvEventProperty
{
    public byte* Name;
    public MpvFormat Format;
    public void* Data;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpvEventLogMessage
{
    public byte* Prefix;
    public byte* Level;
    public byte* Text;
    public int LogLevel;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventEndFile
{
    /// <summary>0 eof, 2 stop, 3 quit, 4 error, 5 redirect.</summary>
    public int Reason;
    public int Error;
    public long PlaylistEntryId;
    public long PlaylistInsertId;
    public int PlaylistInsertNumEntries;
}
