using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MovieWall.Services;

/// <summary>
/// 监听 U 盘 / 移动硬盘插拔。
///
/// 用窗口消息 WM_DEVICECHANGE 而不是 WMI：WMI 要起一个 COM 查询服务，慢且吃内存；
/// 卷设备的 DBT_DEVICEARRIVAL 是系统广播给所有顶层窗口的，零成本就能收到，还能直接拿到盘符。
/// </summary>
public sealed class DriveWatcher : IDisposable
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_VOLUME = 0x0002;

    private readonly HwndSource? _source;
    private bool _disposed;

    /// <summary>新卷挂载完成（已确认可读写），参数为根路径如 "F:\"。</summary>
    public event Action<string>? DriveArrived;

    /// <summary>卷被拔出，参数为根路径。</summary>
    public event Action<string>? DriveRemoved;

    public DriveWatcher(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_DEVICECHANGE) return IntPtr.Zero;

        var evt = wParam.ToInt32();

        if (evt is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE && lParam != IntPtr.Zero)
        {
            var hdr = Marshal.PtrToStructure<DEV_BROADCAST_HDR>(lParam);
            if (hdr.dbch_devicetype != DBT_DEVTYP_VOLUME) return IntPtr.Zero;

            var vol = Marshal.PtrToStructure<DEV_BROADCAST_VOLUME>(lParam);
            var roots = DecodeVolumes(vol.dbcv_unitmask);

            foreach (var root in roots)
            {
                if (evt == DBT_DEVICEARRIVAL)
                {
                    // 系统广播到达时卷往往还没完全挂载好，等它一下再读
                    _ = ConfirmArrivalAsync(root);
                }
                else
                {
                    DriveRemoved?.Invoke(root);
                }
            }
        }

        return IntPtr.Zero;
    }

    private async Task ConfirmArrivalAsync(string root)
    {
        for (int attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(attempt switch { 0 => 400, < 4 => 500, _ => 800 }).ConfigureAwait(false);
            if (_disposed) return;

            try
            {
                var di = new DriveInfo(root);
                if (!di.IsReady) continue;
                if (di.DriveType is not (DriveType.Removable or DriveType.Fixed)) return;

                // 能枚举到根目录才算真的可用
                _ = di.RootDirectory.GetFiles();
                DriveArrived?.Invoke(root);
                return;
            }
            catch
            {
                // 还没准备好，继续等
            }
        }
    }

    private static IEnumerable<string> DecodeVolumes(int mask)
    {
        for (int i = 0; i < 26; i++)
        {
            if ((mask & (1 << i)) == 0) continue;
            yield return $"{(char)('A' + i)}:\\";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_HDR
    {
        public int dbch_size;
        public int dbch_devicetype;
        public int dbch_reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_VOLUME
    {
        public int dbcv_size;
        public int dbcv_devicetype;
        public int dbcv_reserved;
        public int dbcv_unitmask;
        public short dbcv_flags;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _source?.RemoveHook(WndProc); } catch { }
    }
}
