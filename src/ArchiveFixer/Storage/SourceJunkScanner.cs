using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Storage
{
    /// <summary>一个"可能的无用物"（打包者附带的说明 / 网址 / 工具 / 广告之类）。</summary>
    public sealed class SourceJunkItem
    {
        /// <summary>它所在的那个源目录（提示里按目录归组，用户一眼知道去哪儿看）。</summary>
        public string DirectoryPath { get; init; } = string.Empty;

        /// <summary>文件名（只给名字不给完整路径：提示要短，目录那一行已经说明了在哪）。</summary>
        public string FileName { get; init; } = string.Empty;

        /// <summary>完整路径（日志与排障用）。</summary>
        public string FullPath { get; init; } = string.Empty;

        /// <summary>
        /// 它是个**目录**（2026-09-25 第 32 条加）：真实资源包常把密码提示做成一个文件夹
        /// （用户的素材就是 <c>解压密码为：example.com</c>），那种条目要标出来给人看，
        /// 免得提示里"说明.txt"和"某个文件夹"混在一起分不清。
        /// </summary>
        public bool IsDirectory { get; init; }
    }

    /// <summary>一次"无用物"扫描的结论。</summary>
    public sealed class SourceJunkScanResult
    {
        /// <summary>要报出来的条目（最多 <see cref="SourceJunkScanner.MaxReportedItems"/> 条）。</summary>
        public IReadOnlyList<SourceJunkItem> Items { get; init; } = Array.Empty<SourceJunkItem>();

        /// <summary>超过上限、只报了个数的那部分（提示里写成"还有 N 个"）。</summary>
        public int ExtraCount { get; init; }

        /// <summary>这次一共认出多少个（= <see cref="Items"/> + <see cref="ExtraCount"/>）。</summary>
        public int TotalCount => Items.Count + ExtraCount;

        /// <summary>真的要报给用户吗（false = 这一段是空的，提示里整段不出现）。</summary>
        public bool HasAnything => Items.Count > 0;

        /// <summary>实际扫过的源目录数。</summary>
        public int DirectoryCount { get; init; }

        /// <summary>因为"目录数上限"没扫的目录数（0 = 全扫了）。</summary>
        public int SkippedDirectoryCount { get; init; }

        /// <summary>
        /// 撞到某个硬上限（每个目录 2000 个文件 / 魔数体检预算）而只核对了前一部分。
        /// **必须如实说明**：不写这一条，用户会以为"就这么几个"。
        /// </summary>
        public bool Truncated { get; init; }

        /// <summary>供日志用的"前几条名字"（最多 <see cref="SourceJunkScanner.MaxNamesInLog"/> 个）。</summary>
        public IReadOnlyList<string> SampleNames =>
            Items.Take(SourceJunkScanner.MaxNamesInLog).Select(item => item.FileName).ToList();
    }

    /// <summary>
    /// 解压前的"无用物"扫描（用户 2026-09-22 需求第 8 条 A 段）。
    ///
    /// <para><b>它回答的问题</b>：这批任务的**源目录**里，有哪些文件很可能是打包者附带的东西
    /// （说明、网址、工具、广告），用户解压完可能想顺手删掉 —— 程序自己不删、也不动它们，
    /// 只是**提前说一声**。</para>
    ///
    /// <para><b>三条判据（判据要窄，宁可漏报不可误报）</b>：</para>
    /// <list type="number">
    /// <item><description>文件名落在已知的"打包者常放的那几类"里（<see cref="JunkExtensions"/> 与
    /// <see cref="JunkFileNames"/>）。这是**唯一**的入口条件 —— 不在这个名单里的文件，无论看起来多像垃圾，
    /// 一个都不报。</description></item>
    /// <item><description>**不属于本批任何任务**：源包本身与它的分卷（<c>CurrentPath</c> + <c>VolumePaths</c>）
    /// 一律排除。它们本来就是要解压的东西，把它们叫成"无用物"是灾难性的误导。</description></item>
    /// <item><description>**魔数认不出是压缩包**（见 <see cref="IArchiveProber"/>）。这一条是本程序最拿手的场景：
    /// 后缀写着 <c>.jpg</c> / <c>.txt</c>、里面其实是压缩包的伪装包（含"前面是视频、尾部藏着 ZIP"的双面文件）
    /// **绝不能**被叫成无用物 —— 它恰恰是用户拖进来要处理的那个包。</description></item>
    /// </list>
    ///
    /// <para><b>硬上限</b>（都要如实落在结论里，不许静默截断）：每个目录最多扫
    /// <see cref="MaxFilesPerDirectory"/> 个文件、最多报 <see cref="MaxReportedItems"/> 条（其余只报个数）、
    /// 最多扫 <see cref="MaxDirectories"/> 个目录、整批最多做 <see cref="MaxMagicProbesPerBatch"/> 次魔数体检。
    /// 后两条是**性能护栏**：体检要读文件（先 34 KB 文件头，认不出来再读尾部 128 KB），
    /// 一个真实相册目录里几千张 .jpg 会被逐个读一遍 —— 那是"每次解压前"都要付的代价，必须有界。</para>
    ///
    /// <para><b>不引用 WPF</b>（AGENTS.md §4 分层铁律）：它只依赖 <see cref="IArchiveProber"/>（Detection 层）
    /// 与 <see cref="ArchiveTask"/>（Models 层），因此可以在无界面宿主里直接跑。</para>
    /// </summary>
    public static class SourceJunkScanner
    {
        /// <summary>每个源目录最多扫这么多个文件（按枚举顺序，超出即停并标 <c>Truncated</c>）。</summary>
        public const int MaxFilesPerDirectory = 2000;

        /// <summary>提示里最多列这么多条（多出来的写"还有 N 个"）。</summary>
        public const int MaxReportedItems = 10;

        /// <summary>最多扫这么多个源目录（用户一次拖进来的目录数远小于它）。</summary>
        public const int MaxDirectories = 50;

        /// <summary>整批最多做这么多次魔数体检（性能护栏，见类注释）。</summary>
        public const int MaxMagicProbesPerBatch = 2000;

        /// <summary>日志里"例如 …"最多列几个名字（日志要能一行看完）。</summary>
        public const int MaxNamesInLog = 5;

        /// <summary>
        /// 打包者常放的那几类文件的扩展名（**唯一来源**，别在别处再写一份）。
        ///
        /// <para>为什么只有这些：这是"判据要窄"的落点。像 <c>.pdf</c>、<c>.mp4</c>、<c>.zip</c> 这些
        /// 既可能是诱饵也可能是用户真正要的东西，一律不报 —— 宁可漏报，也不要让用户在一堆
        /// "疑似无用物"里找自己真正需要的文件。</para>
        /// </summary>
        public static readonly IReadOnlyList<string> JunkExtensions = new[]
        {
            ".txt", ".url", ".html", ".htm", ".lnk", ".exe", ".bat", ".cmd", ".ps1", ".vbs", ".js",
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".db", ".ini"
        };

        /// <summary>没有扩展名、但一看就是系统/工具留下的那两个名字（大小写不敏感）。</summary>
        public static readonly IReadOnlyList<string> JunkFileNames = new[] { "Thumbs.db", ".DS_Store" };

        /// <summary>
        /// **名字里带这些字**的条目（文件或**目录**）也算"打包者附带的东西"（2026-09-25 第 32 条加）。
        ///
        /// <para>来由（用户真机素材）：他的资源包里没有"说明.txt"这种文件，而是把提示做成了
        /// <b>一个文件夹</b>：<c>解压密码为：example.com</c>。旧判据只看扩展名 →
        /// 一个条目都认不出来 → 他说"为什么我用了这么久还是没有看到有关无用物的任何提醒"。</para>
        ///
        /// <para>判据仍然窄：只认这些"一眼就是给人看的提示"词，绝不把 <c>1-29+4 IF线</c>
        /// 这类**内容文件夹**叫成无用物（那是用户真正要的东西）。</para>
        /// </summary>
        public static readonly IReadOnlyList<string> JunkNameKeywords = new[]
        {
            "解压密码", "密码是", "密码为", "密码：", "密码:",
            "说明", "必看", "公告", "广告", "网址", "更多资源", "公众号", "微信", "QQ群",
            "防和谐", "防删", "失效", "补档", "下载说明", "使用说明", "readme"
        };

        /// <summary>
        /// 这个名字看起来像"给人看的提示"吗（密码提示 / 说明 / 公告那一类；大小写不敏感）。
        ///
        /// <para>它同时适用于**文件与目录**：目录名在真实资源包里经常就是那个密码提示。</para>
        /// </summary>
        public static bool LooksLikeHintName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            string plain = Path.GetFileName(name.TrimEnd('\\', '/'));

            if (plain.Length == 0)
            {
                return false;
            }

            return JunkNameKeywords.Any(keyword =>
                plain.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 这个文件名"看起来像"打包者附带的东西吗（**只看名字**，不含"是不是压缩包"那一步）。
        /// </summary>
        public static bool LooksLikeJunkCandidate(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            string name = Path.GetFileName(fileName);

            if (JunkFileNames.Any(known => string.Equals(name, known, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            // 名字里带"解压密码 / 说明 / 公告"这类字样的一律算（第 32 条）：真实资源包把密码提示
            // 做成 `解压密码为：xxx.txt` 甚至一个**文件夹**，只按扩展名认会一条都认不出来。
            if (LooksLikeHintName(name))
            {
                return true;
            }

            string extension = Path.GetExtension(name);

            return !string.IsNullOrEmpty(extension) &&
                   JunkExtensions.Any(known => string.Equals(extension, known, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 密码候选里**有没有一个能用的**（B 段判据）。
        ///
        /// <para>为什么要单独判"非空"：<c>PasswordService.GetPasswordCandidates</c> 保证**至少给一个空密码**
        /// （无密码包必须能解）。所以"候选表非空"是个恒真命题，拿它当判据等于永远不提醒。
        /// 真正要问的是"除了那个兜底的空密码，还有没有别的" —— 没有就说明密码本为空、
        /// 映射式没命中、也没设统一密码，这个加密包**这一批必然打不开**。</para>
        /// </summary>
        public static bool HasUsablePasswordCandidate(IEnumerable<PasswordItem?>? candidates)
        {
            if (candidates == null)
            {
                return false;
            }

            return candidates.Any(candidate => !string.IsNullOrEmpty(candidate?.Value));
        }

        /// <summary>
        /// 扫一遍这批任务的源目录，给出"可能的无用物"。
        ///
        /// <para><paramref name="prober"/> 为 null 时**返回空结论**（而不是"全都算无用物"）：
        /// 没有魔数体检就没法排除伪装包，而误报一个真压缩包比漏报严重得多。</para>
        ///
        /// <para>任何 IO 异常都吞掉（目录不存在、被权限挡住、枚举到一半目录被删）：这是"解压前的提醒"，
        /// **绝不允许**因为它让整批解压开不了头。</para>
        /// </summary>
        public static async Task<SourceJunkScanResult> ScanAsync(
            IReadOnlyList<ArchiveTask>? tasks,
            IArchiveProber? prober,
            CancellationToken cancellationToken = default)
        {
            if (tasks == null || tasks.Count == 0 || prober == null)
            {
                return new SourceJunkScanResult();
            }

            List<string> directories = CollectDirectories(tasks, out int skippedDirectories);

            var protectedPaths = CollectProtectedPaths(tasks);

            var items = new List<SourceJunkItem>();
            int extra = 0;
            int scannedDirectories = 0;
            int probesLeft = MaxMagicProbesPerBatch;
            bool truncated = false;
            bool budgetExhausted = false;

            foreach (string directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (budgetExhausted)
                {
                    break;
                }

                if (!Directory.Exists(directory))
                {
                    continue;
                }

                scannedDirectories++;

                /*
                 * 先收**目录**（第 32 条）：只看直属子目录的名字，不做魔数体检、也不递归 ——
                 * 目录不可能是压缩包，而名字里带"解压密码 / 说明 / 公告"的那种一眼就是给人看的提示。
                 * 这一步是廉价的纯名字判断，代价可以忽略。
                 */
                foreach (string childDirectory in CollectHintDirectories(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (items.Count < MaxReportedItems)
                    {
                        items.Add(new SourceJunkItem
                        {
                            DirectoryPath = directory,
                            FileName = Path.GetFileName(childDirectory),
                            FullPath = childDirectory,
                            IsDirectory = true
                        });
                    }
                    else
                    {
                        extra++;
                    }
                }

                List<string> candidates = CollectJunkCandidates(
                    directory,
                    protectedPaths,
                    out bool directoryTruncated);

                truncated |= directoryTruncated;

                /*
                 * 体检顺序按文件名排一遍：目录枚举顺序由文件系统给，不稳定；
                 * 而"前 10 条"是要给用户看的，稳定输出才谈得上"每次看到的是同一批"。
                 * （排序发生在 2000 条以内，代价可以忽略。）
                 */
                foreach (string path in candidates
                             .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                             .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (probesLeft <= 0)
                    {
                        budgetExhausted = true;
                        truncated = true;
                        break;
                    }

                    probesLeft--;

                    if (await IsArchiveAsync(prober, path, cancellationToken).ConfigureAwait(false))
                    {
                        // 魔数认得出是压缩包（含"伪装成 .jpg/.txt 的真包"与双面文件）→ 绝不是无用物。
                        continue;
                    }

                    if (items.Count < MaxReportedItems)
                    {
                        items.Add(new SourceJunkItem
                        {
                            DirectoryPath = directory,
                            FileName = Path.GetFileName(path),
                            FullPath = path
                        });
                    }
                    else
                    {
                        extra++;
                    }
                }
            }

            return new SourceJunkScanResult
            {
                Items = items,
                ExtraCount = extra,
                DirectoryCount = scannedDirectories,
                SkippedDirectoryCount = skippedDirectories,
                Truncated = truncated
            };
        }

        /// <summary>
        /// 本批所有任务的源目录（按出现顺序去重，超过 <see cref="MaxDirectories"/> 的部分不扫）。
        /// </summary>
        private static List<string> CollectDirectories(
            IReadOnlyList<ArchiveTask> tasks,
            out int skippedDirectories)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ArchiveTask? task in tasks)
            {
                string directory = ResolveDirectory(task);

                if (directory.Length == 0 || !seen.Add(directory))
                {
                    continue;
                }

                result.Add(directory);
            }

            skippedDirectories = Math.Max(0, result.Count - MaxDirectories);

            return skippedDirectories == 0 ? result : result.Take(MaxDirectories).ToList();
        }

        /// <summary>
        /// 一个任务的源目录：优先用它自己记着的 <see cref="ArchiveTask.DirectoryPath"/>，
        /// 空的时候（探针任务 / 手工构造的任务）现算一次。
        /// </summary>
        private static string ResolveDirectory(ArchiveTask? task)
        {
            if (task == null)
            {
                return string.Empty;
            }

            string directory = task.DirectoryPath;

            if (string.IsNullOrWhiteSpace(directory) && !string.IsNullOrWhiteSpace(task.CurrentPath))
            {
                try
                {
                    directory = Path.GetDirectoryName(task.CurrentPath) ?? string.Empty;
                }
                catch
                {
                    return string.Empty;
                }
            }

            if (string.IsNullOrWhiteSpace(directory))
            {
                return string.Empty;
            }

            try
            {
                // TrimEndingDirectorySeparator 而不是 TrimEnd：盘根（"E:\"）不能被削成 "E:"。
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 本批任何**是压缩包**的任务的源包与其分卷（<c>CurrentPath</c> + <c>VolumePaths</c>）——
        /// 这些文件**永远不算无用物**（判据 2）。
        ///
        /// <para>⚠ 2026-09-24 第 15 条之后这里多了一个限定：只保护 <see cref="ArchiveTask.IsArchive"/>
        /// 为真的任务。原因是那条需求把提醒提前到了**导入之后**，而"添加文件夹"默认会把文件夹里的
        /// **所有**文件都收进任务列表（<c>ScanAllFiles</c>）—— 打包者附带的 <c>说明.txt</c> / <c>网址.url</c>
        /// 同样会成为任务。按旧口径它们全都被"属于本批任务"保护起来，于是提醒永远列不出任何东西，
        /// 而用户要的恰恰是"列表里把这些删掉"。</para>
        ///
        /// <para>安全性没有降低：真正的包由**判据 3**（魔数体检，认得出是归档就不算无用物）兜着，
        /// 而且判据 1（名字）本来就不含 <c>.7z</c>/<c>.rar</c>/<c>.zip</c> 这些归档后缀。</para>
        /// </summary>
        private static HashSet<string> CollectProtectedPaths(IReadOnlyList<ArchiveTask> tasks)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ArchiveTask? task in tasks)
            {
                if (task == null || !task.IsArchive)
                {
                    continue;
                }

                AddProtectedPath(result, task.CurrentPath);

                foreach (string volumePath in task.VolumePaths)
                {
                    AddProtectedPath(result, volumePath);
                }

                /*
                 * ⛔ **同目录里同组的兄弟卷也要保护**（用户 2026-09-28 真机）：
                 * 分卷组的后续卷**没有文件头魔数**（裸切块），所以 `IsArchive == false`、
                 * 也不会出现在 `VolumePaths` 里（归组当时没成组）—— 于是它被判成"疑似无用物"，
                 * 提醒框一出现用户就会把它从列表里删掉，只剩第一卷，**整组再也解不开**。
                 *
                 * 判据用**名字**（`FileNameHelper.IsVolumePartFileName` + 同组），不看内容 ——
                 * 后续卷本来就没东西可看。
                 */
                AddSiblingVolumePaths(result, task.CurrentPath);
            }

            return result;
        }

        /// <summary>
        /// 把"和 <paramref name="sourcePath"/> 同一分卷组的兄弟卷"也加进保护集。
        ///
        /// <para><b>判据走唯一出口 <see cref="VolumeGroupResolver"/></b>（2026-09-30 分卷组装算法）：
        /// 老写法只保护"名字里带卷标记、且同族同基名"的兄弟，于是
        /// 「一卷丢了后缀变成 <c>111</c>」「末卷被改名成 <c>一只顶美.z删除ip</c>」
        /// 「续卷伪装成 <c>.mp4</c>」这一类**名字看不出来**的兄弟一个都保护不到 ——
        /// 而它们恰恰是最容易被当成无用物删掉、删掉就再也解不开的那一批。</para>
        ///
        /// <para>判定器用"基名段 + 体积 + 位置"把它们认出来（**不试开**：导入期不调引擎，
        /// 也不该为了列个提醒去起进程）。读不到目录就安静跳过 —— 保护集少几个不会造成破坏，
        /// 多保护几个只是"提醒少列一条"。</para>
        /// </summary>
        private static void AddSiblingVolumePaths(HashSet<string> target, string? sourcePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sourcePath))
                {
                    return;
                }

                string selfName = Path.GetFileName(sourcePath);

                if (!FileNameHelper.IsVolumePartFileName(selfName))
                {
                    return;
                }

                string directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;

                foreach (string sibling in Directory.EnumerateFiles(directory))
                {
                    string siblingName = Path.GetFileName(sibling);

                    if (string.Equals(siblingName, selfName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (FileNameHelper.IsVolumePartFileName(siblingName) &&
                        VolumeGroupDetector.BelongsToSameGroup(selfName, siblingName))
                    {
                        AddProtectedPath(target, sibling);
                    }
                }

                AddResolverGroupPaths(target, sourcePath);
            }
            catch
            {
                // 读目录失败就少保护几个：这里的方向是"宁可少列一条提醒"，不是破坏性动作。
            }
        }

        /// <summary>
        /// 判定器认出来的**整组**也进保护集（含名字里没有卷标记的那些兄弟）。
        ///
        /// <para>⛔ 这一条与"可删残留名单"是同一件事的两半（AGENTS §9.5）：判定器说"属于某个分卷组"，
        /// 这里就一个都不许进"疑似无用物"提醒 —— 用户 2026-09-30 那 25 GB 就是被提醒框送进删除的。</para>
        /// </summary>
        private static void AddResolverGroupPaths(HashSet<string> target, string sourcePath)
        {
            var resolver = new VolumeGroupResolver();

            VolumeGroupResolution resolution = resolver.Resolve(new VolumeGroupQuery
            {
                AnchorPath = sourcePath,
                AllowTrialOpen = false
            });

            /*
             * ⛔ 判据是"判定器有没有认出组成员"，**不是**"结论是不是完整"：
             * 结论 `Undetermined`（判不出）时 `GroupFilePaths` 也可能非空（例如"这几卷不在同一个目录 /
             * 同一个卷"那一档 —— 组是认出来了，只是不敢据此装配）。那一档恰恰**更**不该把成员列成无用物，
             * 所以这里只在"一个成员都没认出来"时才放手。
             *
             * 方向永远是安全的：保护集多几个人，只会让提醒里少列几条（⛔ 不会多删任何东西）。
             */
            if (resolution.GroupFilePaths.Count == 0)
            {
                return;
            }

            string selfFull = SafePathHelper.GetFullPathSafe(sourcePath);

            foreach (string member in resolution.GroupFilePaths)
            {
                if (!string.Equals(member, selfFull, StringComparison.OrdinalIgnoreCase))
                {
                    AddProtectedPath(target, member);
                }
            }
        }

        private static void AddProtectedPath(HashSet<string> target, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                target.Add(Path.GetFullPath(path));
            }
            catch
            {
                // 路径形状不合法：它本来也不可能出现在目录枚举结果里，忽略即可。
            }
        }

        /// <summary>
        /// 一个目录里"名字像密码提示 / 说明公告"的**直属子目录**（第 32 条；不递归、不体检）。
        /// 任何 IO 异常都吞掉：提醒不允许让整批解压开不了头。
        /// </summary>
        private static List<string> CollectHintDirectories(string directory)
        {
            var result = new List<string>();

            try
            {
                foreach (string path in Directory.EnumerateDirectories(directory))
                {
                    if (LooksLikeHintName(Path.GetFileName(path)))
                    {
                        result.Add(path);
                    }
                }
            }
            catch
            {
                // 读不了当"这里没有"。
            }

            return result;
        }

        /// <summary>
        /// 一个目录里"文件名看起来像无用物"的文件（已排除本批源包 / 分卷）。
        /// </summary>
        private static List<string> CollectJunkCandidates(
            string directory,
            HashSet<string> protectedPaths,
            out bool truncated)
        {
            var result = new List<string>();
            truncated = false;
            int scanned = 0;

            IEnumerator<string>? enumerator = null;

            try
            {
                enumerator = Directory.EnumerateFiles(directory).GetEnumerator();

                while (true)
                {
                    bool moved;

                    try
                    {
                        moved = enumerator.MoveNext();
                    }
                    catch
                    {
                        // 枚举到一半目录被删 / 权限变化：拿到的这些照样算，剩下的不再追。
                        break;
                    }

                    if (!moved)
                    {
                        break;
                    }

                    scanned++;

                    if (scanned > MaxFilesPerDirectory)
                    {
                        truncated = true;
                        break;
                    }

                    string path = enumerator.Current;

                    if (protectedPaths.Contains(path))
                    {
                        continue;
                    }

                    if (LooksLikeJunkCandidate(Path.GetFileName(path)))
                    {
                        result.Add(path);
                    }
                }
            }
            catch
            {
                // 目录读不了一律当"这里没有无用物"：提醒不允许让整批解压开不了头。
            }
            finally
            {
                enumerator?.Dispose();
            }

            return result;
        }

        /// <summary>
        /// 魔数体检：认得出是归档才算"不是无用物"。
        ///
        /// <para>体检本身出意外（文件被占用、坏扇区、路径过长）时返回 <c>true</c> ——
        /// **按"可能是压缩包"处理**：宁可漏报一个说明文件，也不要冒"把用户的真包叫成垃圾"的风险。</para>
        /// </summary>
        private static async Task<bool> IsArchiveAsync(
            IArchiveProber prober,
            string path,
            CancellationToken cancellationToken)
        {
            try
            {
                return await prober.IsArchiveAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return true;
            }
        }
    }
}
