using System.IO;

namespace MovieWall.Services;

/// <summary>极简文件日志。排查刮削问题时最有用，默认开启。</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static bool Enabled { get; set; } = true;

    public static void Write(string message)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                File.AppendAllText(Paths.LogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
