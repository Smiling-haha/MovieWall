using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MovieWall.Services;

public sealed record MediaInfo(
    TimeSpan? Duration,
    int? Width,
    int? Height,
    long? Bitrate,
    double? FrameRate,
    int? AudioChannels,
    string? VideoCodec,
    string? AudioCodec);

/// <summary>
/// 通过 Windows 属性系统（IPropertyStore）读取视频的时长/分辨率/码率。
///
/// 为什么不用 ffprobe：不想让用户额外装东西，也不想让 exe 带着 40MB 的 ffmpeg 二进制。
/// 为什么不用 WPF 的 MediaPlayer：它依赖 Media Foundation 解码器，MKV/HEVC 经常直接失败，
/// 而且必须在 UI 线程跑、容易阻塞界面。
/// 属性系统走的是资源管理器的同一套处理器，Windows 认得的格式它都认，而且线程安全。
/// </summary>
public static class MediaMetadataReader
{
    // ---------------- 属性键 ----------------

    private static readonly Guid MF_Media = new("64440490-4C8B-11D1-8B70-080036B11A03");
    private static readonly Guid MF_Video = new("64440491-4C8B-11D1-8B70-080036B11A03");

    private static PROPERTYKEY PK_Duration => new() { fmtid = MF_Media, pid = 3 };     // System.Media.Duration, VT_UI8 (100ns)
    private static PROPERTYKEY PK_VideoWidth => new() { fmtid = MF_Video, pid = 3 };   // System.Video.FrameWidth
    private static PROPERTYKEY PK_VideoHeight => new() { fmtid = MF_Video, pid = 4 };  // System.Video.FrameHeight
    private static PROPERTYKEY PK_Bitrate => new() { fmtid = MF_Video, pid = 8 };      // System.Video.TotalBitrate
    private static PROPERTYKEY PK_FrameRate => new() { fmtid = MF_Video, pid = 6 };    // System.Video.FrameRate
    private static PROPERTYKEY PK_VideoCodec => new() { fmtid = MF_Video, pid = 10 };  // System.Video.Compression
    private static PROPERTYKEY PK_AudioCodec => new() { fmtid = MF_Media, pid = 6 };   // System.Audio.Compression
    private static PROPERTYKEY PK_Channels => new() { fmtid = MF_Media, pid = 7 };     // System.Audio.ChannelCount

    // ---------------- 公共入口 ----------------

    public static MediaInfo Read(string path)
    {
        TimeSpan? duration = null;
        int? w = null, h = null, channels = null;
        long? bitrate = null;
        double? fps = null;
        string? vcodec = null, acodec = null;

        try
        {
            var store = OpenStore(path);
            if (store is not null)
            {
                try
                {
                    var dur100 = GetUInt64(store, PK_Duration);
                    if (dur100 is > 0) duration = TimeSpan.FromTicks((long)dur100.Value / 10); // 100ns → tick

                    w = (int?)GetUInt32(store, PK_VideoWidth);
                    h = (int?)GetUInt32(store, PK_VideoHeight);
                    if (w is <= 0) w = null;
                    if (h is <= 0) h = null;

                    var br = GetUInt32(store, PK_Bitrate);
                    if (br is > 0) bitrate = br;

                    fps = GetDouble(store, PK_FrameRate);
                    // 有的处理器把帧率存成 ×1000 的整型
                    if (fps is > 1000) fps /= 1000.0;
                    if (fps is <= 0 or > 240) fps = null;

                    channels = (int?)GetUInt32(store, PK_Channels);
                    if (channels is <= 0 or > 16) channels = null;

                    vcodec = GetString(store, PK_VideoCodec);
                    acodec = GetString(store, PK_AudioCodec);
                }
                finally
                {
                    Marshal.ReleaseComObject(store);
                }
            }
        }
        catch
        {
            // 属性系统对损坏文件 / 特殊容器会直接失败，静默降级
        }

        // 兜底：码率用「体积 ÷ 时长」估算，和播放器显示的码率误差通常在 2% 以内
        if (bitrate is null && duration is not null)
        {
            try
            {
                var size = new FileInfo(path).Length;
                if (size > 0 && duration.Value.TotalSeconds > 1)
                    bitrate = (long)(size * 8.0 / duration.Value.TotalSeconds);
            }
            catch { }
        }

        return new MediaInfo(duration, w, h, bitrate, fps, channels,
            Clean(vcodec), Clean(acodec));
    }

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.Length is 0 or > 40) return null;
        // 属性系统有时返回 GUID/ID 形式，不友好
        if (s.Length > 30 && s.Count(c => c == '-') >= 4) return null;
        return s;
    }

    // ---------------- COM 互操作 ----------------

    private static IPropertyStore? OpenStore(string path)
    {
        var iid = typeof(IPropertyStore).GUID;
        int hr = SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, GPS_BESTEFFORT, ref iid, out var store);
        return hr >= 0 ? store : null;
    }

    private const uint GPS_BESTEFFORT = 0x00000040;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetPropertyStoreFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        uint flags,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ppv);

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    /// <summary>x64 上 PROPVARIANT 是 24 字节：2+2+2+2 头 + 16 字节联合体。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort r1;
        public ushort r2;
        public ushort r3;
        public IntPtr p;
        public IntPtr p2;
    }

    [DllImport("propsys.dll", PreserveSig = true)]
    private static extern int PropVariantToUInt64(in PROPVARIANT pv, out ulong ret);

    [DllImport("propsys.dll", PreserveSig = true)]
    private static extern int PropVariantToUInt32(in PROPVARIANT pv, out uint ret);

    [DllImport("propsys.dll", PreserveSig = true)]
    private static extern int PropVariantToDouble(in PROPVARIANT pv, out double ret);

    [DllImport("propsys.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int PropVariantToStringAlloc(in PROPVARIANT pv, out IntPtr ppsz);

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int PropVariantClear(ref PROPVARIANT pv);

    private static ulong? GetUInt64(IPropertyStore store, PROPERTYKEY key)
    {
        PROPVARIANT pv = default;
        try
        {
            if (store.GetValue(ref key, out pv) < 0) return null;
            return PropVariantToUInt64(in pv, out var v) >= 0 ? v : null;
        }
        finally { PropVariantClear(ref pv); }
    }

    private static uint? GetUInt32(IPropertyStore store, PROPERTYKEY key)
    {
        PROPVARIANT pv = default;
        try
        {
            if (store.GetValue(ref key, out pv) < 0) return null;
            return PropVariantToUInt32(in pv, out var v) >= 0 ? v : null;
        }
        finally { PropVariantClear(ref pv); }
    }

    private static double? GetDouble(IPropertyStore store, PROPERTYKEY key)
    {
        PROPVARIANT pv = default;
        try
        {
            if (store.GetValue(ref key, out pv) < 0) return null;
            return PropVariantToDouble(in pv, out var v) >= 0 ? v : null;
        }
        finally { PropVariantClear(ref pv); }
    }

    private static string? GetString(IPropertyStore store, PROPERTYKEY key)
    {
        PROPVARIANT pv = default;
        try
        {
            if (store.GetValue(ref key, out pv) < 0) return null;
            if (PropVariantToStringAlloc(in pv, out var ptr) < 0 || ptr == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUni(ptr); }
            finally { Marshal.FreeCoTaskMem(ptr); }
        }
        finally { PropVariantClear(ref pv); }
    }
}
