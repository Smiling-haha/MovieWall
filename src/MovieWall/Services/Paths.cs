using System.IO;

namespace MovieWall.Services;

/// <summary>应用数据目录。全部落在 %LocalAppData%\MovieWall，不碰用户任何原始文件。</summary>
public static class Paths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MovieWall");

    public static string PosterDir { get; } = Path.Combine(Root, "posters");
    public static string DbPath { get; } = Path.Combine(Root, "library.db");
    public static string SettingsPath { get; } = Path.Combine(Root, "settings.json");
    public static string LogPath { get; } = Path.Combine(Root, "moviewall.log");

    public static void EnsureAll()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(PosterDir);
    }

    /// <summary>
    /// 海报文件的实际位置。
    /// 刮削下来的图存在海报缓存目录里，传进来的是纯文件名；
    /// 用户自己放在片子旁边的 poster.jpg 传进来的是绝对路径，直接用。
    /// </summary>
    public static string PosterPath(string fileName)
        => Path.IsPathRooted(fileName) ? fileName : Path.Combine(PosterDir, fileName);
}
