using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Storage
{
    /// <summary>
    /// 一条"源文件当时长什么样"的记录：路径 + 大小 + 修改时间 + <b>记的时候还在不在</b>。
    ///
    /// <para>
    /// "当时在不在"必须记下来，不能靠"文件不见了"倒推：文件在上一次快照时就已经不在时，
    /// 正确的结论是"两次都一样（都不在）"，而不是"它刚刚消失了"。
    /// 少了这个字段，一个从来就不存在的路径会被每次都报成"源文件已变化"。
    /// </para>
    /// </summary>
    public sealed class SourceFileState
    {
        /// <summary>记录时的完整路径（<see cref="Path.GetFullPath(string)"/> 规范化过；取不到时原样保留）。</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>记录时文件是否存在。<b>false</b> 时后面两项没有意义。</summary>
        public bool Exists { get; init; }

        /// <summary>记录时的字节数（不存在时为 0）。</summary>
        public long Length { get; init; }

        /// <summary>记录时的最后写入时间（UTC；不存在时为 <see cref="DateTime.MinValue"/>）。</summary>
        public DateTime LastWriteTimeUtc { get; init; }
    }

    /// <summary>一个文件上比对出来的差异（一项差异 = 一个文件上的一类变化，不叠加）。</summary>
    public sealed class SourceFileChange
    {
        /// <summary>差异那一项的类型。</summary>
        public SourceChangeKind Kind { get; init; }

        /// <summary>出问题的那个文件（完整路径）。分卷组里就是**具体哪一卷**。</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>显示用的文件名（报错文案里点名用）。</summary>
        public string FileName { get; init; } = string.Empty;

        /// <summary>
        /// 这一项是不是**分卷**（而不是主文件）。
        ///
        /// <para>文案上必须分得清：分卷组里"第二卷不见了"时，只说"文件不见了"用户会以为整个包没了；
        /// 程序得点名"是哪一个卷"（AGENTS.md §9.3 对缺卷的同一口径）。</para>
        /// </summary>
        public bool IsVolume { get; init; }

        /// <summary>当时的大小（<see cref="SourceChangeKind.Missing"/> 时为 0）。</summary>
        public long PreviousLength { get; init; }

        /// <summary>现在的大小（<see cref="SourceChangeKind.Missing"/> 时为 0）。</summary>
        public long CurrentLength { get; init; }

        /// <summary>当时的最后写入时间。</summary>
        public DateTime PreviousLastWriteTimeUtc { get; init; }

        /// <summary>现在的最后写入时间。</summary>
        public DateTime CurrentLastWriteTimeUtc { get; init; }

        /// <summary>
        /// 这一项差异怎么念给用户听（`大小 1.2 GiB → 1.3 GiB` / `修改时间 … → …` / `文件不见了`）。
        /// 文案模板的唯一来源是 <see cref="StatusText"/>（AGENTS.md §7：界面文案不许手写散落）。
        /// </summary>
        public string Describe()
        {
            return Kind switch
            {
                SourceChangeKind.Size =>
                    string.Format(
                        StatusText.SourceChangeSizeFormat,
                        TaskSpaceEstimate.FormatSize(PreviousLength),
                        TaskSpaceEstimate.FormatSize(CurrentLength)),

                SourceChangeKind.LastWriteTime =>
                    string.Format(
                        StatusText.SourceChangeTimeFormat,
                        FormatTime(PreviousLastWriteTimeUtc),
                        FormatTime(CurrentLastWriteTimeUtc)),

                _ => IsVolume ? StatusText.SourceChangeVolumeMissingText : StatusText.SourceChangeMissingText
            };
        }

        /// <summary>
        /// 时间按**本地时间**显示。
        ///
        /// 存的是 UTC（跨时区比较不会因为夏令时跳变而误判），但给用户看的一律是本地时间 ——
        /// 用户在资源管理器里看到的"修改时间"就是本地时间，直接给他 UTC 等于让他自己换算。
        /// </summary>
        private static string FormatTime(DateTime utc)
        {
            if (utc == DateTime.MinValue)
            {
                return StatusText.SourceChangeUnknownTimeText;
            }

            return utc.ToLocalTime().ToString(StatusText.SourceChangeTimeDisplayFormat);
        }
    }

    /// <summary>差异类型。顺序即文案里的列举顺序（大小 → 修改时间 → 不见）。</summary>
    public enum SourceChangeKind
    {
        Size = 0,
        LastWriteTime = 1,
        Missing = 2
    }

    /// <summary>
    /// 一次比对的结论。**Changed = false 时其余字段一律为空**（调用方只需要看这一个布尔）。
    /// </summary>
    public sealed class SourceChangeResult
    {
        /// <summary>源文件（分卷则整组里任意一个）发生了变化。</summary>
        public bool Changed { get; init; }

        /// <summary>逐个文件的差异（按记录顺序；"文件不见了"是那个文件上唯一的一项）。</summary>
        public IReadOnlyList<SourceFileChange> Changes { get; init; } = Array.Empty<SourceFileChange>();

        /// <summary>
        /// 有没有"快照压根不存在"这回事（老任务 / 测试直接 new 出来的任务）。
        ///
        /// <para>它**不是**"变化了"：没有基准就无从比对，正确的做法是**在开工前补拍一次**
        /// 而不是拦下任务（拦下等于把"我们没记录"说成"你的文件变了"）。</para>
        /// </summary>
        public bool SnapshotMissing { get; init; }

        /// <summary>没有变化的那一份（省得每处都 new 一个）。</summary>
        public static SourceChangeResult Unchanged { get; } = new();
    }

    /// <summary>
    /// 源文件快照（<b>不变量 11</b>：源文件变化后不得继续使用旧识别结果）。
    ///
    /// <para><b>它回答的问题</b>：识别（扫描）那一刻记下来的"这个包多大、什么时候改的"，
    /// 在真正调引擎之前还对得上吗？对不上就说明<b>手上这份识别结果已经不是这个文件的结果了</b> ——
    /// 继续解压等于拿旧结论处理新文件，结局通常是密码错误 / 文件损坏这种**指错方向**的结论，
    /// 更糟的是把用户刚换进去的那份文件当成原来那份解出来（不变量 1 的红线：源包原地不动）。</para>
    ///
    /// <para><b>为什么落在 <c>Storage/</c> 而不是 <c>Models/</c></b>（AGENTS.md §4 分层铁律）：
    /// Domain / Models 只放纯模型，<b>碰磁盘的事（stat）归 Storage</b>。
    /// 本类只依赖 <see cref="TaskSpaceEstimate"/>（同层）与 <c>SafePathHelper</c>（Helpers），
    /// <b>不引用 WPF</b>，因此可以在无界面宿主里直接跑。</para>
    ///
    /// <para><b>口径（刻意保守：变了就算）</b>：只比 <see cref="FileInfo.Length"/> 与
    /// <see cref="FileInfo.LastWriteTimeUtc"/>，<b>不比对内容哈希</b> ——
    /// 全量读一遍几百 GB 的分卷组是不可接受的代价（那正是我们想避免的 IO）。
    /// 代价是"只是被别的工具摸了一下时间戳（甚至只读打开过）也会触发"，
    /// 这一条如实写进 <c>README.md</c> 与 <c>docs/使用说明.md</c> 的已知限制，不假装能分辨"内容变了"。</para>
    ///
    /// <para><b>触发之后一律"停下"</b>：不启动引擎、不生成 <c>其余物</c>、不发布任何产物、不留中间品
    /// （不变量 1 的红线在这一种情况下照旧成立）。出路只有一条：右键「重新扫描此文件」，
    /// 让识别结果重新对上这个文件，再处理 —— 与"分卷缺失"给出的出路是同一口径。</para>
    /// </summary>
    public sealed class SourceFileSnapshot
    {
        private SourceFileSnapshot(IReadOnlyList<SourceFileState> files, DateTime capturedAt)
        {
            Files = files;
            CapturedAt = capturedAt;
        }

        /// <summary>记了哪些文件（第一项永远是 <c>ArchiveTask.CurrentPath</c>，其余是分卷）。</summary>
        public IReadOnlyList<SourceFileState> Files { get; }

        /// <summary>这份快照是什么时候拍的（UTC）。</summary>
        public DateTime CapturedAt { get; }

        /// <summary>
        /// 按"主文件 + 分卷整组"的顺序拍一份快照。
        ///
        /// <para>顺序必须与 <see cref="Compare"/> 传进来的一模一样：比对是**按位**做的，
        /// 这样"改名 / 源包搬进其余物之后路径变了"不会被误判成"源文件被换了"
        /// （路径不是我们要保护的东西，大小与修改时间才是）。</para>
        ///
        /// <para>文件不存在**不是失败**：记成"当时不在"照样是一份有效基准，
        /// 于是"后来它出现了"也会被如实报出来。</para>
        /// </summary>
        public static SourceFileSnapshot Capture(IEnumerable<string> paths, DateTime capturedAtUtc)
        {
            var files = new List<SourceFileState>();

            foreach (string path in paths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                files.Add(ReadState(path));
            }

            return new SourceFileSnapshot(files, capturedAtUtc);
        }

        /// <summary>拍一份快照，时间戳取当前 UTC。</summary>
        public static SourceFileSnapshot Capture(IEnumerable<string> paths) =>
            Capture(paths, DateTime.UtcNow);

        /// <summary>
        /// 比对：把快照与**现在**的磁盘状况逐位比一遍。
        ///
        /// <para><paramref name="paths"/> 必须与 <see cref="Capture"/> 时同形同序
        /// （主文件 + 分卷）。长度对不上时，多出来的那一项算"新出现的文件"（记成当时不存在），
        /// 少掉的那一项算"不见了" —— 两种都说得出话，不会静默放过。</para>
        /// </summary>
        public SourceChangeResult Compare(IEnumerable<string> paths)
        {
            var current = (paths ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(ReadState)
                .ToList();

            var changes = new List<SourceFileChange>();

            int count = Math.Max(Files.Count, current.Count);

            for (int i = 0; i < count; i++)
            {
                SourceFileState? before = i < Files.Count ? Files[i] : null;
                SourceFileState? after = i < current.Count ? current[i] : null;

                SourceFileChange? change = CompareOne(i, before, after);

                if (change != null)
                {
                    changes.Add(change);
                }
            }

            if (changes.Count == 0)
            {
                return SourceChangeResult.Unchanged;
            }

            return new SourceChangeResult { Changed = true, Changes = changes };
        }

        /// <summary>
        /// 一个文件上的比对。**"文件不见了"是那个文件上唯一的一项** ——
        /// 文件都没了，再说"大小也变了"只是噪音（而且"现在的大小"根本无从谈起）。
        /// </summary>
        private static SourceFileChange? CompareOne(int index, SourceFileState? before, SourceFileState? after)
        {
            string path = after?.Path ?? before?.Path ?? string.Empty;
            string fileName = DisplayName(path);

            // 第 0 项永远是主文件，其余是分卷（见 ArchiveTask.GetSnapshotPaths 的顺序约定）。
            bool isVolume = index > 0;

            /*
             * "当时就不在、现在还是不在" = **没有变化**。
             *
             * ⚠ 这一条必须在"现在不在 → 文件不见了"之前判掉（否则它会被那一支吃掉）：
             * 一个**一开始就不存在**的路径会被每一次比对都报成"文件不见了"，
             * 于是任务永远开工不了、用户做什么都没用。这正是"判据缺失 ≠ 判据不通过"那条原则的落点 ——
             * 快照老老实实录了"当时不在"，比对就必须认这个账。
             */
            if (before is { Exists: false } && (after == null || !after.Exists))
            {
                return null;
            }

            if (before == null)
            {
                // 快照里没有这一项（分卷组后来多了/少了一卷）：按"当时不存在"处理，说得出来。
                return new SourceFileChange
                {
                    Kind = after is { Exists: true } ? SourceChangeKind.LastWriteTime : SourceChangeKind.Missing,
                    Path = path,
                    FileName = fileName,
                    IsVolume = isVolume,
                    PreviousLastWriteTimeUtc = DateTime.MinValue,
                    CurrentLastWriteTimeUtc = after?.LastWriteTimeUtc ?? DateTime.MinValue,
                    CurrentLength = after?.Length ?? 0
                };
            }

            if (after == null || !after.Exists)
            {
                return new SourceFileChange
                {
                    Kind = SourceChangeKind.Missing,
                    Path = path,
                    FileName = fileName,
                    IsVolume = isVolume,
                    PreviousLength = before.Length,
                    PreviousLastWriteTimeUtc = before.LastWriteTimeUtc
                };
            }

            if (!before.Exists)
            {
                // 拍快照时不在、现在在了：同样是"源文件变了"（基准不成立），照样拦下。
                return new SourceFileChange
                {
                    Kind = SourceChangeKind.LastWriteTime,
                    Path = path,
                    FileName = fileName,
                    IsVolume = isVolume,
                    PreviousLastWriteTimeUtc = DateTime.MinValue,
                    CurrentLastWriteTimeUtc = after.LastWriteTimeUtc,
                    CurrentLength = after.Length
                };
            }

            if (before.Length != after.Length)
            {
                return new SourceFileChange
                {
                    Kind = SourceChangeKind.Size,
                    Path = path,
                    FileName = fileName,
                    IsVolume = isVolume,
                    PreviousLength = before.Length,
                    CurrentLength = after.Length,
                    PreviousLastWriteTimeUtc = before.LastWriteTimeUtc,
                    CurrentLastWriteTimeUtc = after.LastWriteTimeUtc
                };
            }

            /*
             * 大小一样、时间戳不一样 → **也算变化**（不变量 11 就是这么写的："记录大小/修改时间快照，
             * 处理中变化即停下"）。刻意不去"聪明地"认为"大小没变大概就没变"：
             * 那种聪明正是"用旧识别结果处理新文件"的入口 —— 一个字节都没多、内容却换了的包真实存在
             *（同尺寸重新打包、加密包换密码重打）。
             */
            if (before.LastWriteTimeUtc != after.LastWriteTimeUtc)
            {
                return new SourceFileChange
                {
                    Kind = SourceChangeKind.LastWriteTime,
                    Path = path,
                    FileName = fileName,
                    IsVolume = isVolume,
                    PreviousLength = before.Length,
                    CurrentLength = after.Length,
                    PreviousLastWriteTimeUtc = before.LastWriteTimeUtc,
                    CurrentLastWriteTimeUtc = after.LastWriteTimeUtc
                };
            }

            return null;
        }

        /// <summary>
        /// 把结论说成一句给用户看的话（点名**哪一个文件**、**哪一项变了**；分卷则点名"是哪一个卷"）。
        ///
        /// 形如：<c>源文件已变化：222.7z（大小 1.2 GiB → 1.3 GiB）。…</c> /
        /// <c>源文件已变化：222.7z.002（分卷 · 修改时间 … → …）。…</c>
        /// 多项差异时全部列出来（分卷组里"同时动了两个卷"必须都说清）。
        /// </summary>
        public static string DescribeChange(SourceChangeResult result)
        {
            if (result == null || !result.Changed)
            {
                return string.Empty;
            }

            string items = string.Join(
                StatusText.SourceChangeItemSeparator,
                result.Changes.Select(change =>
                {
                    string what = change.IsVolume
                        ? StatusText.SourceChangeVolumePrefix + change.Describe()
                        : change.Describe();

                    return $"{change.FileName}（{what}）";
                }));

            return string.Format(StatusText.SourceChangeMessageFormat, items);
        }

        /// <summary>读一个文件现在的样子。<b>只 stat，不读内容</b>。</summary>
        private static SourceFileState ReadState(string path)
        {
            string full = SafePathHelper.GetFullPathSafe(path);

            try
            {
                var info = new FileInfo(full);

                if (!info.Exists)
                {
                    return new SourceFileState { Path = full, Exists = false };
                }

                return new SourceFileState
                {
                    Path = full,
                    Exists = true,
                    Length = info.Length,
                    LastWriteTimeUtc = info.LastWriteTimeUtc
                };
            }
            catch (Exception)
            {
                /*
                 * 读不到（路径畸形 / 权限 / 设备离线）：记成"当时不在"。
                 *
                 * 为什么不像别处那样报错：这是**在拍快照**，任何失败都不该让任务开不了工
                 * （快照只是记账，不是门槛）。记成"不在"之后，下一次比对会如实说"文件不见了"，
                 * 用户拿到的仍然是一句能行动的话。
                 */
                return new SourceFileState { Path = full, Exists = false };
            }
        }

        /// <summary>路径的显示名（取不到文件名时退回整条路径，绝不给出空串）。</summary>
        private static string DisplayName(string path)
        {
            try
            {
                string name = Path.GetFileName(path);
                return string.IsNullOrWhiteSpace(name) ? path : name;
            }
            catch
            {
                return path;
            }
        }
    }
}
