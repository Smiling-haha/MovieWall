using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MovieWall.Services;

/// <summary>
/// 单实例：第二次双击不要再开一个窗口，而是把已经在跑的那个叫到前面来。
///
/// 两件事分开做：
///   1) 互斥体判重 —— 只回答「是不是已经有实例」。进程崩了系统会自动释放，不会留死锁。
///   2) 命名管道唤醒 —— 后启动的那个把「SHOW」发给先启动的，先启动的再把窗口叫出来。
///      如果只做 1)，用户双击后什么都不会发生，看起来像程序坏了。
///
/// 用 Local\ 而不是 Global\：只限制当前登录会话，多用户/远程桌面下互不打扰。
/// 带调试参数启动（--shot / --batch-scrape 等）时会跳过互斥，否则没法同时跑脚本。
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Local\MovieWall.SingleInstance.v1";
    private const string PipeName = "MovieWall.Activate.v1";

    /// <summary>这些参数是无人值守的诊断模式，不应该受单实例限制。</summary>
    private static readonly string[] ToolSwitches =
    {
        "--selftest", "--probe-scrape", "--probe-tmdb", "--parse-test",
        "--batch-scrape", "--classify-test", "--shot", "--demo-detail", "--demo-spec"
    };

    public static bool IsToolRun(string[] args)
        => args.Any(a => ToolSwitches.Contains(a, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// 尝试成为唯一实例。返回 null 表示已经有别人在跑（调用方应该通知它后退出）。
    /// </summary>
    public static Mutex? TryAcquire()
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (createdNew) return mutex;

            mutex.Dispose();
            return null;
        }
        catch (AbandonedMutexException)
        {
            // 上一个进程是被强杀的，锁已经归我们了
            return new Mutex(initiallyOwned: true, MutexName, out _);
        }
        catch
        {
            return null;   // 拿不到锁就当单实例失败，宁可多开也不能打不开
        }
    }

    /// <summary>
    /// 通知已经在跑的实例把窗口叫出来。管道连不上（比如正在启动中）就退回找同名窗口。
    /// </summary>
    public static bool NotifyExisting(int timeoutMs = 2500)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                pipe.Connect(Math.Max(300, (int)(deadline - Environment.TickCount64)));

                using var writer = new StreamWriter(pipe) { AutoFlush = true };
                writer.WriteLine("SHOW");
                return true;
            }
            catch
            {
                Thread.Sleep(250);
            }
        }

        return BringProcessWindowToFront();
    }

    /// <summary>管道不行时的兜底：按进程名找到那个窗口，直接 Win32 还原并置前。</summary>
    private static bool BringProcessWindowToFront()
    {
        try
        {
            int self = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName("MovieWall"))
            {
                if (p.Id == self) continue;
                if (p.MainWindowHandle == IntPtr.Zero) continue;

                ShowWindow(p.MainWindowHandle, SW_RESTORE);
                SetForegroundWindow(p.MainWindowHandle);
                return true;
            }
        }
        catch { }

        return false;
    }

    /// <summary>在已有实例里启动监听：收到唤醒信号就把窗口还原、置前。</summary>
    public static void Listen(Dispatcher dispatcher, Window window)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None);

                    server.WaitForConnection();

                    using var reader = new StreamReader(server);
                    if (reader.ReadLine()?.Trim() == "SHOW")
                        dispatcher.BeginInvoke(new Action(() => ShowWindowFront(window)));
                }
                catch (Exception ex)
                {
                    App.Trace("single-instance: 监听异常 " + ex.GetType().Name);
                    Thread.Sleep(500);   // 别把线程烧掉
                }
            }
        })
        {
            IsBackground = true,
            Name = "MovieWall.SingleInstance"
        };

        thread.Start();
    }

    private static void ShowWindowFront(Window window)
    {
        try
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();

            // Activate() 在别的程序占着前台时会失败，补一次 Win32 强制置前
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero)
            {
                SetForegroundWindow(handle);
                FlashWindow(handle, true);
            }

            App.Trace("single-instance: 收到唤醒，窗口已置前");
        }
        catch { }
    }

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool FlashWindow(IntPtr hWnd, bool bInvert);
}
