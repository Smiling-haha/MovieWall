using System.Diagnostics;
using System.IO;
using MovieWall.Models;

namespace MovieWall.Services;

/// <summary>
/// 唤起播放器。默认交给系统关联程序，也可以在设置里指定（参考设计里按钮写的是 Yaoplayer）。
/// </summary>
public static class PlayerLauncher
{
    /// <summary>当前生效的播放器名字，按钮文案用它。</summary>
    public static string CurrentPlayerName
    {
        get
        {
            var s = SettingsStore.Instance.Current;
            if (!string.IsNullOrWhiteSpace(s.PlayerPath) && File.Exists(s.PlayerPath))
            {
                return string.IsNullOrWhiteSpace(s.PlayerName)
                    ? Path.GetFileNameWithoutExtension(s.PlayerPath)
                    : s.PlayerName;
            }
            return "本机播放器";
        }
    }

    public static (bool ok, string message) Play(Movie movie)
    {
        if (!File.Exists(movie.FilePath))
            return (false, "文件不在了——可能这块磁盘没接上，或者片子被移动过。");

        var s = SettingsStore.Instance.Current;

        try
        {
            if (!string.IsNullOrWhiteSpace(s.PlayerPath) && File.Exists(s.PlayerPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = s.PlayerPath,
                    Arguments = BuildArguments(s.PlayerArgs, movie.FilePath),
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(s.PlayerPath) ?? ""
                });
                return (true, "");
            }

            // 没指定播放器：交给 Shell，等价于双击
            Process.Start(new ProcessStartInfo
            {
                FileName = movie.FilePath,
                UseShellExecute = true
            });
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, "启动播放器失败：" + ex.Message);
        }
    }

    private static string BuildArguments(string template, string filePath)
    {
        var quoted = $"\"{filePath}\"";
        if (string.IsNullOrWhiteSpace(template)) return quoted;

        if (template.Contains("{file}", StringComparison.OrdinalIgnoreCase))
            return template.Replace("{file}", quoted, StringComparison.OrdinalIgnoreCase);

        return template.TrimEnd() + " " + quoted;
    }

    public static void RevealInExplorer(Movie movie)
    {
        try
        {
            if (!File.Exists(movie.FilePath)) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{movie.FilePath}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    /// <summary>
    /// 探测常见播放器，包括 Yaoplayer（会扫 Program Files / LocalAppData 下名字带 yao 的目录）。
    /// </summary>
    public static List<(string name, string path)> DetectPlayers()
    {
        var result = new List<(string, string)>();

        void Probe(string name, params string[] candidates)
        {
            foreach (var c in candidates)
            {
                try
                {
                    var expanded = Environment.ExpandEnvironmentVariables(c);
                    if (File.Exists(expanded))
                    {
                        if (!result.Any(x => string.Equals(x.Item2, expanded, StringComparison.OrdinalIgnoreCase)))
                            result.Add((name, expanded));
                        return;
                    }
                }
                catch { }
            }
        }

        // Yaoplayer —— 名字不固定，先扫目录
        foreach (var (name, path) in ScanForYaoPlayer()) result.Add((name, path));

        Probe("Yaoplayer",
            @"%ProgramFiles%\Yaoplayer\Yaoplayer.exe",
            @"%ProgramFiles%\YaoPlayer\YaoPlayer.exe",
            @"%LOCALAPPDATA%\Programs\Yaoplayer\Yaoplayer.exe",
            @"%LOCALAPPDATA%\Yaoplayer\Yaoplayer.exe");

        Probe("PotPlayer",
            @"%ProgramFiles%\DAUM\PotPlayer\PotPlayerMini64.exe",
            @"%ProgramFiles(x86)%\DAUM\PotPlayer\PotPlayerMini.exe");

        Probe("VLC",
            @"%ProgramFiles%\VideoLAN\VLC\vlc.exe",
            @"%ProgramFiles(x86)%\VideoLAN\VLC\vlc.exe");

        Probe("MPC-HC",
            @"%ProgramFiles%\MPC-HC\mpc-hc64.exe",
            @"%ProgramFiles(x86)%\MPC-HC\mpc-hc.exe");

        Probe("MPV",
            @"%ProgramFiles%\mpv\mpv.exe",
            @"%LOCALAPPDATA%\Programs\mpv\mpv.exe");

        return result;
    }

    private static IEnumerable<(string, string)> ScanForYaoPlayer()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            string[] dirs;
            try { dirs = Directory.GetDirectories(root); }
            catch { continue; }

            foreach (var dir in dirs)
            {
                var name = Path.GetFileName(dir);
                if (!name.Contains("yao", StringComparison.OrdinalIgnoreCase)) continue;

                string[] exes;
                try { exes = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly); }
                catch { continue; }

                // 优先挑名字里也带 yao 的 exe，否则挑最大的（通常是主程序）
                var exe = exes.FirstOrDefault(x => Path.GetFileName(x).Contains("yao", StringComparison.OrdinalIgnoreCase))
                          ?? exes.OrderByDescending(x => new FileInfo(x).Length).FirstOrDefault();

                if (exe is not null) yield return (name, exe);
            }
        }
    }
}
