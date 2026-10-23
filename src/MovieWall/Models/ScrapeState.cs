namespace MovieWall.Models;

/// <summary>元数据刮削状态。持久化到库里，避免每次启动都重试必然失败的条目。</summary>
public enum ScrapeState
{
    /// <summary>还没尝试过刮削。</summary>
    Pending = 0,
    /// <summary>已成功拿到海报/元数据。</summary>
    Success = 1,
    /// <summary>尝试过但没匹配到，或者网络失败。默认不再自动重试。</summary>
    Failed = 2,
    /// <summary>用户手动忽略，永远不刮。</summary>
    Ignored = 3,
    /// <summary>
    /// 根本不是影片（宣传片、花絮、录制、剧集片段……），直接跳过。
    /// 和 Failed 分开存是为了两件事：不吃豆瓣的请求配额，
    /// 也不会在界面上冒充「刮削失败」。
    /// </summary>
    Skipped = 4
}
