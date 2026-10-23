using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using MovieWall.Models;

namespace MovieWall.Services;

/// <summary>
/// 补全队列。扫描只负责把片子「点亮」成一张卡，这个队列在后台补三样东西：
///   1) MediaInfo 全量技术参数（容器、Profile、色彩、HDR、音轨、字幕轨）
///   2) 网络元数据（海报、简介、评分、导演、主演）
///   3) 落库
///
/// 用户看到的效果：墙先出现，然后一张张从灰块变成海报，角标和卡片下方的
/// 「4K UHD · HDR10」也是这时候才补齐的。
/// </summary>
public sealed class EnrichmentQueue : IDisposable
{
    private readonly Channel<Movie> _channel = Channel.CreateUnbounded<Movie>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });

    private readonly MetadataService _meta;
    private readonly LibraryDatabase _db;
    private readonly ConcurrentDictionary<long, byte> _inFlight = new();

    /// <summary>因为豆瓣冷却而跳过的条目，冷却结束后重新排队。</summary>
    private readonly ConcurrentQueue<Movie> _deferred = new();

    private CancellationTokenSource? _cts;
    private Task[] _workers = Array.Empty<Task>();
    private int _pending;

    public event Action<Movie>? Updated;

    public int Pending => Volatile.Read(ref _pending);

    public EnrichmentQueue(MetadataService meta, LibraryDatabase db)
    {
        _meta = meta;
        _db = db;
    }

    public void Start(int workerCount = 3)
    {
        if (_workers.Length > 0) return;
        _cts = new CancellationTokenSource();
        _workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() => WorkerLoopAsync(_cts.Token)))
            .ToArray();
        _ = Task.Run(() => RetryDeferredLoopAsync(_cts.Token));
    }

    /// <summary>
    /// 豆瓣冷却结束后，把「当时被跳过」的条目重新排进队列。
    ///
    /// 这里以前是在工作线程里 `while (IsThrottled) await Task.Delay(2000)`：
    /// 冷却 8 分钟，整条流水线（包括 TMDB 明明能秒回的条目）就干等 8 分钟。
    /// 现在冷却只影响豆瓣那一路，其他照常跑，被跳过的条目在这里补回来。
    /// </summary>
    private async Task RetryDeferredLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(10_000, ct).ConfigureAwait(false);
                if (_deferred.IsEmpty || Pending > 0) continue;
                if (DoubanClient.IsThrottled) continue;

                int n = 0;
                while (_deferred.TryDequeue(out var m))
                {
                    m.ScrapeState = ScrapeState.Pending;
                    Enqueue(m);
                    n++;
                }
                if (n > 0) Log.Write($"enrich: 豆瓣冷却结束，重新排队 {n} 条");
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Enqueue(Movie movie)
    {
        if (movie.Id == 0) return;
        Interlocked.Increment(ref _pending);
        _channel.Writer.TryWrite(movie);
    }

    public void EnqueueRange(IEnumerable<Movie> movies)
    {
        foreach (var m in movies) Enqueue(m);
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var movie in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _pending);

                if (!_inFlight.TryAdd(movie.Id, 0)) continue;
                try
                {
                    await ProcessAsync(movie, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log.Write($"enrich error «{movie.DisplayTitle}»: {ex.Message}"); }
                finally
                {
                    _inFlight.TryRemove(movie.Id, out _);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessAsync(Movie movie, CancellationToken ct)
    {
        bool changed = false;
        Log.Write($"enrich: start «{movie.DisplayTitle}»");

        // ---- 1) 技术参数 ----
        if (!movie.ProbeDone || movie.Detail is null)
        {
            var path = movie.FilePath;
            bool exists;
            try { exists = File.Exists(path); } catch { exists = false; }

            if (exists)
            {
                var detail = await Task.Run(() => MediaProbe.Probe(path, ct), ct).ConfigureAwait(false);

                if (detail is not null)
                {
                    movie.Detail = detail;
                    movie.ProbeDone = true;

                    try { movie.ProbeJson = JsonSerializer.Serialize(detail); }
                    catch { }

                    // 同步扁平字段，让不依赖 Detail 的地方（排序、筛选）也能用
                    if (detail.Duration is { TotalSeconds: > 0 })
                        movie.DurationMs = (long)detail.Duration.Value.TotalMilliseconds;
                    if (detail.Width is > 0) movie.VideoWidth = detail.Width;
                    if (detail.Height is > 0) movie.VideoHeight = detail.Height;
                    if (detail.VideoBitRate is > 0) movie.BitrateBps = detail.VideoBitRate;
                    if (!string.IsNullOrWhiteSpace(detail.VideoFormat)) movie.VideoCodec = detail.VideoFormat;
                    if (detail.AudioTracks.Count > 0) movie.AudioCodec = detail.AudioSummary;

                    changed = true;
                }
                else
                {
                    // MediaInfo 不可用 / 失败 → 退回 Windows 属性系统（字段少但不会空白）
                    var info = await Task.Run(() => MediaMetadataReader.Read(path), ct).ConfigureAwait(false);
                    if (info.Duration is { TotalSeconds: > 1 })
                    {
                        movie.DurationMs = (long)info.Duration.Value.TotalMilliseconds;
                        changed = true;
                    }
                    if (info.Width is > 0 && info.Height is > 0)
                    {
                        movie.VideoWidth = info.Width;
                        movie.VideoHeight = info.Height;
                        changed = true;
                    }
                    if (info.Bitrate is > 0) { movie.BitrateBps = info.Bitrate; changed = true; }
                    if (!string.IsNullOrWhiteSpace(info.VideoCodec)) { movie.VideoCodec = info.VideoCodec; changed = true; }
                    if (!string.IsNullOrWhiteSpace(info.AudioCodec)) { movie.AudioCodec = info.AudioCodec; changed = true; }
                }

                if (changed) Updated?.Invoke(movie);
            }
        }

        Log.Write($"enrich: probe done «{movie.DisplayTitle}» state={movie.ScrapeState}");

        // ---- 2) 网络元数据 ----
        if (movie.ScrapeState == ScrapeState.Pending)
        {
            // 冷却期里不在这里干等：MetadataService 会跳过豆瓣但照常问 TMDB，
            // 所以 TMDB 能命中的条目该立刻拿海报，不能陪豆瓣一起停摆。
            var stateBefore = movie.ScrapeState;
            var ok = await _meta.EnrichAsync(movie, ct).ConfigureAwait(false);
            if (ok) changed = true;
            if (movie.ScrapeState != stateBefore) changed = true;

            // 因为冷却被跳过、什么都没问成的，等冷却结束再排一次（否则它就永远停在 Pending）。
            if (!ok && movie.ScrapeState == ScrapeState.Pending && DoubanClient.IsThrottled)
                _deferred.Enqueue(movie);
        }

        // ---- 3) 落库 ----
        if (changed || movie.Id == 0)
        {
            try { await _db.UpsertAsync(movie).ConfigureAwait(false); }
            catch (Exception ex) { Log.Write($"db upsert «{movie.DisplayTitle}» FAILED: {ex.GetType().Name} {ex.Message}"); }
        }

        if (changed) Updated?.Invoke(movie);
    }

    public async Task RescrapeAsync(Movie movie, CancellationToken ct)
    {
        movie.ScrapeState = ScrapeState.Pending;
        movie.PosterFile = null;
        movie.BackdropFile = null;
        movie.ProbeDone = false;
        movie.Detail = null;
        movie.ProbeJson = null;
        await ProcessAsync(movie, ct).ConfigureAwait(false);
    }

    /// <summary>只重跑 MediaInfo，不碰海报和网络元数据。</summary>
    public async Task ReproceAsync(Movie movie, CancellationToken ct)
    {
        movie.ProbeDone = false;
        movie.Detail = null;
        movie.ProbeJson = null;
        await ProcessAsync(movie, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try
        {
            _channel.Writer.TryComplete();
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch { }
    }
}
