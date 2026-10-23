using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using MovieWall.Models;

namespace MovieWall.Services;

/// <summary>settings.json 的读写。单例，全局共享一份当前配置。</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };

    private static readonly Lazy<SettingsStore> Lazy = new(() => new SettingsStore());
    public static SettingsStore Instance => Lazy.Value;

    public AppSettings Current { get; private set; } = new();

    private SettingsStore() => Load();

    public void Load()
    {
        try
        {
            Paths.EnsureAll();
            if (File.Exists(Paths.SettingsPath))
            {
                var json = File.ReadAllText(Paths.SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (loaded is not null)
                {
                    Current = loaded;
                    Sanitize(Current);
                }
            }
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Paths.EnsureAll();
            var json = JsonSerializer.Serialize(Current, JsonOpts);
            var tmp = Paths.SettingsPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, Paths.SettingsPath, overwrite: true);
        }
        catch { }
    }

    /// <summary>修掉手工改配置时容易写错的字段。</summary>
    private static void Sanitize(AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.Language)) s.Language = "zh-CN";
        if (string.IsNullOrWhiteSpace(s.TmdbApiBase)) s.TmdbApiBase = "https://api.themoviedb.org";
        if (string.IsNullOrWhiteSpace(s.TmdbImageBase)) s.TmdbImageBase = "https://image.tmdb.org";
        s.TmdbApiBase = s.TmdbApiBase.TrimEnd('/');
        s.TmdbImageBase = s.TmdbImageBase.TrimEnd('/');
        if (s.MinFileSizeMb < 0) s.MinFileSizeMb = 0;
        if (s.MinRemovableFileSizeMb < 0) s.MinRemovableFileSizeMb = 0;
        if (s.HttpTimeoutSeconds is < 5 or > 120) s.HttpTimeoutSeconds = 15;
        s.ScanRoots ??= new List<string>();
        s.ExcludedFolders ??= new List<string>();
        if (string.IsNullOrWhiteSpace(s.PlayerArgs)) s.PlayerArgs = "{file}";
    }
}
