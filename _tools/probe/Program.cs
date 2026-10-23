using MovieWall.Services;

Console.OutputEncoding = System.Text.Encoding.UTF8;

string[] names =
{
    "[DBD-Raws][4K_HDR][蜘蛛侠：英雄远征][IMAX版][2160P][WebRip][HEVC-10bit][FLAC].mkv",
    "[No.88]摩登时代 BD720P.mp4",
    "[No.60]搏击俱乐部 BD720P.mp4",
    "[黑豹].Black.Panther.2018.BD.1080P.x265.10bit.DD5.1.英国双语.内封特效中英双字-FFansBD.mkv",
    "[钢铁侠].Iron.Man.2008.BD.1080P.x265.10bit.AAC.英国双语.内封特效中英双字-FFansBD.mkv",
    "精灵旅社3.1080p.国英台粤四语.BD中英双字[最新电影www.66ys.tv].mp4",
    "哪吒之魔童闹海.Ne.Zha.2.2025.1080p.WEB-DL.H264.AAC.mkv",
    "Captain America BD DD5 英国双语内封特效中英双字-FFansBD.mkv",
    "The.Matrix.1999.2160p.BluRay.x265.10bit.HDR.mkv",
    "1917.2019.1080p.BluRay.x264.mkv",
    "Blade.Runner.2049.2017.2160p.UHD.BluRay.mkv",
    "千与千寻.2001.1080p.国日双语.mkv",
    "窗中女孩 BD中英双字.mkv",
    "让子弹飞 HD国语中字.2010.mkv",
    "S01E10v2.mp4",
    "与客户共发展Loop.mov",
    "宣传片.mp4"
};

Console.WriteLine("=== 解析结果 ===");
foreach (var n in names)
{
    var p = FileNameParser.Parse(n);
    var flag = p.LooksLikeEpisode ? "[剧集]" : "      ";
    Console.WriteLine($"{flag} {p.Title,-32} | {p.Year,4}  <- {n}");
}

Console.WriteLine();
Console.WriteLine("=== 豆瓣实测 ===");
var db = new DoubanClient();
foreach (var n in names.Take(12))
{
    var p = FileNameParser.Parse(n);
    var m = await db.SearchAsync(p.Title, p.Year, CancellationToken.None);
    Console.WriteLine(m is null
        ? $"  x {p.Title}"
        : $"  OK {p.Title}  ->  {m.Title} ({m.Year})  rating={m.Rating}  poster={(m.PosterUrl is null ? "NO" : "YES")}");
    await Task.Delay(900);
}
