# LUMEN · Private Cinema

本地电影海报墙。把散落在硬盘和 U 盘里的电影收成一面墙，点开能看完整的媒体参数，一键唤起你指定的播放器。

单文件程序，双击即用。不装服务端、不注册账号、不上传任何路径。

---

## 三个页面

**片库** — 大标题 + 磁盘清单 + 筛选（全部 / 本机 / 外挂 / 收藏）+ 搜索排序。
每张海报左上角标分辨率规格，右上角标 HDR 和所属磁盘，卡片下方是片名和「年份 · 时长 · 画质」。

**详情** — 豆瓣评分与评分人数、导演、主演、类型、制片国家、上映日期，
四个关键数据（时长 / 分辨率 / 码率 / 文件大小），一排规格 chips，
以及故事简介。背景是海报放大模糊后的色调。

**技术规格** — 三栏（视频 / 文件 / 音频·字幕）+ 底部「完整媒体参数」双列键值表，
字段完全对照 MediaInfo 的原始输出：容器格式、Profile/Level、Codec ID、色彩原色、
传递特性、色彩矩阵、母版亮度、每条音轨与字幕轨。

---

## 快速开始

1. 双击 `dist/MovieWall.exe`（需要 .NET 8 Desktop Runtime）
2. 首次启动自动扫描固定磁盘（默认跳过 C 盘系统盘，可在设置里改）
3. 想刮得又快又准 → 设置里填一个 TMDB API Key（见下）
4. 想让「播放」按钮用你自己的播放器 → 设置里选播放器 exe，按钮文案会跟着变

`dist/` 目录必须整体保留——`tools/MediaInfo.exe` 是媒体参数探测的核心，不能单独拷走 exe。

---

## 关于媒体参数：为什么必须带 MediaInfo

设计里那些字段（Profile/Level、Codec ID、色彩原色、传递特性、母版亮度、色度抽样、
封装软件、编码库、总帧数）**Windows 属性系统一个都给不了**。它们来自 MediaInfo ——
业界标准的媒体分析库，设计稿右下角那个 `MEDIAINFO 26.05` 就是它的版本号水印。

本程序带的是 MediaInfo CLI 26.05（`tools/` 目录，9.3MB）。调用方式：

```
MediaInfo.exe --Output=JSON --Language=raw --Details=0 "电影.mkv"
```

程序解析它的 JSON 输出，映射成那套双列参数表。

**找不到 MediaInfo 会怎样**：自动降级到 Windows 属性系统，只能拿到时长、分辨率、
码率这几项，Profile/色彩/HDR/音轨这些会显示 `—`，技术规格页顶部会提示缺少 MediaInfo。
不会崩，只是信息少。

---

## 数据源

### 豆瓣（免 Key，默认）

不填 TMDB Key 时自动使用。中文片名匹配最准，海报质量好，还带评分人数、导演、主演、
制片国家、上映日期。

**2026 年起豆瓣的几个老搜索接口已经停用了**，现在按顺序走三条通道：

1. `m.douban.com/rexxar/api/v2/search/subjects` —— 一次请求就给齐标题/年份/评分/
   大图海报/国家/类型/导演/主演，是目前最省事也最稳的一条
2. `search.douban.com/movie/subject_search` —— 结果以明文 JSON 内联在 HTML 里
3. 都不行就交给限流判定

**它也会拦 IP**。表现分两种，处理方式完全不同：

- **静默返回空数组**：多半只是片名不对。程序会用一部确定存在的片名做探针
  （「阿甘正传」），探针也空才算真限流
- **直接 403**：出口 IP 被明确拉黑了，再等 60 秒毫无意义，直接暂停 20 分钟

其它配套策略：

- 全局串行 + 最小 1300ms 间隔（命中时自动放缓到 2200ms）
- 每个片名拆成「中文名」「英文名」「去冠词」多个候选逐个试
- 非影片（宣传片、花絮、录制、纯编号）在本地就跳过，不消耗请求配额
- 待刮削队列按「有年份 > 正常时长」排序，先保证最像电影的那些出海报
- 被限流的条目保留「待重试」状态，下次启动自动补

### TMDB（推荐，需要 Key）

- 免费注册：<https://www.themoviedb.org/settings/api> → 申请 API Key（选 Developer）
- 海报规范、中文简介全、**没有这种 IP 限流**
- **国内直连 `api.themoviedb.org` 被 DNS 污染**，需要挂代理（程序走系统代理，Clash 开着就行）
  或改 hosts（参考 [CheckTMDB](https://github.com/cnwikee/CheckTMDB)）

如果你发现海报一直刮不到，先看设置页「测试」按钮的返回，然后配一个 TMDB Key ——
这条路的稳定性远高于免 Key 方案。

---

## 隐私

| 发出去的东西 | 用途 |
|---|---|
| 「片名 + 年份」 | 查询元数据 |
| 海报图片 URL | 下载海报 |

**不会**上传：完整路径、盘符、文件体积、观看记录、任何文件内容。
数据库、海报缓存、设置全部在 `%LocalAppData%\MovieWall\`，只在本机。

技术参数完全由本地 MediaInfo 分析，全程不联网。

---

## U 盘

监听 `WM_DEVICECHANGE`，插上移动硬盘/U 盘后自动识别盘符、开始扫描，
**扫到一部就立刻出现在墙上**（不是等全部扫完）。

拔掉后该盘的影片打上「磁盘已断开」标记，海报和元数据全部保留，重新插上立刻恢复。

---

## 支持的格式

`.mp4 .mkv .avi .mov .wmv .flv .m4v .ts .m2ts .mpg .mpeg .rmvb .rm .webm .vob .ogv .divx .asf .3gp`

电视剧单集会按 `S01E05` / `第01集` 自动识别并跳过。默认只收录 ≥150MB 的文件。

---

## 快捷键

| 按键 | 作用 |
|---|---|
| `Ctrl + F` | 回到片库并聚焦搜索 |
| `Esc` | 逐级返回（规格 → 详情 → 片库）/ 清空搜索 |
| `Enter` | 详情页 → 打开技术规格 |
| `空格` | 播放当前影片 |
| 滚轮 | 带惯性的平滑滚动 |

---

## 配置与数据

全部在 `%LocalAppData%\MovieWall\`：

```
settings.json     配置（可直接编辑）
library.db        SQLite（路径、元数据、MediaInfo JSON、刮削状态）
posters\          海报缓存
moviewall.log     运行日志
```

想彻底重来：关掉程序，删掉这个目录。

---

## 技术要点

- **.NET 8 + WPF**，单文件 2.4MB（不含 tools），无 Node/Electron 依赖
- **虚拟化海报网格**：自写 `VirtualizingPanel + IScrollInfo`，按行列算位置，
  只生成可视区 + 1 行缓冲的容器。库里有 1000 部还是 10000 部，滚动开销基本不变
- **平滑滚动**：`CompositionTarget.Rendering` 每帧指数插值
- **数据库演进**：`PRAGMA table_info` 检查缺列后 `ALTER TABLE ADD COLUMN`，升级不丢已有数据
- **海报解码**：后台线程用 `BitmapDecoder` + `TransformedBitmap`
  （`BitmapImage` 在非 UI 线程配 `StreamSource` 会抛 `ArgumentNullException(key)`）
- **悬停态**：用 XAML `DataTrigger` 绑 `IsMouseOver`（Enter/ExitActions 严格配对），
  避免虚拟化复用容器时 `MouseLeave` 丢失导致一堆卡片卡在悬停态
- **自检**：`MovieWall.exe --selftest` 会跑一遍数据库读写校验，结果写进 `moviewall.log`

代码结构：

```
src/MovieWall/
  Models/     Movie、MediaDetail（技术参数）、AppSettings
  Services/   MediaProbe（MediaInfo）、扫描、文件名解析、豆瓣/TMDB 客户端、SQLite、播放器
  Controls/   VirtualizingWrapPanel、SmoothScroll、PosterCard
  ViewModels/ MainViewModel（导航、磁盘分组、筛选）
  Views/      LibraryView、DetailView、SpecView
  Themes/     极简深色主题
```

---

## 已知限制

- **豆瓣刮削慢是刻意的**（限流保护）。配了 TMDB Key 会快很多
- **首次全盘扫描较慢**，取决于硬盘文件数量。第二次起是增量的
- **U 盘插拔**在部分 USB 移动硬盘上有 1~2 秒延迟（系统挂载本身需要时间）
- 文件名完全无意义（`movie1.mkv`）时刮不到
- 非电影视频（项目宣传片、录音等）也会被收进来，用排除目录或最小体积过滤

---

## 调试参数

```
MovieWall.exe --selftest                  数据库读写自检
MovieWall.exe --demo-detail 奥本海默       启动后直接打开指定影片详情
MovieWall.exe --demo-spec 奥本海默         启动后直接打开该片的技术规格页
```

---

## 仓库结构

| 目录 | 是否入库 | 说明 |
|---|---|---|
| `src/MovieWall/` | ✅ | 全部源码、XAML、图标 |
| `docs/`、`dist/使用说明.md` | ✅ | 使用文档 |
| `tools/` | ✅ | MediaInfo CLI 与图标生成脚本（构建时拷到输出目录） |
| `dist/` | ✅ 部分 | 可直接运行的成品：`MovieWall.exe` + `e_sqlite3.dll` + `tools/`；其中的 `.7z` 打包件不入库 |
| `src/MovieWall/bin`、`obj` | ❌ | 构建产物，`dotnet build` 重新生成 |
| `_shots/` | ❌ | 开发过程截图（60 张），属开发工件 |
| `release/` | ❌ | 发布压缩包 |

## 构建

```bash
cd src/MovieWall
dotnet build -c Release
# 或发布单文件
dotnet publish -c Release -r win-x64 --self-contained false
```

需要 **.NET 8 SDK**；运行需要 **.NET 8 Desktop Runtime**。

## 许可与致谢

- 本项目代码：MIT（如需改为其它许可请自行调整本节）
- **MediaInfo**（`tools/MediaInfo.exe`、`tools/LIBCURL.DLL`）：© MediaArea.net，
  采用 **BSD-2-Clause / LGPL** 双许可，二进制原样分发，未做修改。
  详见 <https://mediaarea.net/MediaInfo>。
- 影片元数据与海报来自 **豆瓣** / **TMDB** 的公开接口，版权归各自权利人所有，
  本项目仅作个人影片库的本地展示用途。

> 仓库内不包含任何 TMDB API Key、豆瓣 Cookie 或账号凭据。这类信息由使用者在
> 程序设置里自行填写，只保存在本机。
