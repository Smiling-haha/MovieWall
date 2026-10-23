using System.IO;
using System.Windows;
using System.Windows.Threading;
using MovieWall.Services;

namespace MovieWall;

public partial class App : Application
{
    /// <summary>单实例互斥体。整个进程生命周期都握着，退出时系统自动释放。</summary>
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Paths.EnsureAll();
        Trace("OnStartup begin");

        // ---- 单实例 ----
        // 双击第二次时不要开出第二个窗口：让已经在跑的那个前置，然后本进程直接退出。
        // 调试参数（--shot / --batch-scrape 之类）不参与，否则脚本没法并行跑。
        if (!SingleInstance.IsToolRun(e.Args))
        {
            _singleInstanceMutex = SingleInstance.TryAcquire();
            if (_singleInstanceMutex is null)
            {
                Trace("single-instance: 已有实例在运行，请求它前置后退出");
                SingleInstance.NotifyExisting();
                Shutdown(0);
                return;
            }
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Trace("DispatcherUnhandledException: " + args.Exception);
            MessageBox.Show(
                "程序遇到了一个问题：\n\n" + args.Exception.Message +
                "\n\n日志：" + Paths.LogPath,
                "光影墙", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Trace("AppDomain.UnhandledException: " + args.ExceptionObject);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Trace("UnobservedTaskException: " + args.Exception);
            args.SetObserved();
        };

        try
        {
            Trace("creating MainWindow");
            var window = new MainWindow();
            Trace("MainWindow constructed, showing");
            MainWindow = window;
            window.Show();
            Trace("MainWindow shown, visible=" + window.IsVisible);

            // 开始监听唤醒请求：以后再双击 exe，由这里把窗口叫出来
            if (!SingleInstance.IsToolRun(e.Args))
                SingleInstance.Listen(Dispatcher, window);
        }
        catch (Exception ex)
        {
            Trace("STARTUP FAILED: " + ex);
            MessageBox.Show("启动失败：\n\n" + ex, "光影墙",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        try { _singleInstanceMutex?.Dispose(); } catch { }
        base.OnExit(e);
    }

    public static void Trace(string message)
    {
        try
        {
            File.AppendAllText(Paths.LogPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}
