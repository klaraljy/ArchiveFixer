using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;

namespace ArchiveFixer.Storage
{
    /// <summary>
    /// 工作区根的**来源**（三档，日志里必须能看出这一批落在哪一档）。
    /// </summary>
    public enum WorkspaceRootOrigin
    {
        /// <summary>用户显式设过缓存根目录 → <c>&lt;它&gt;\work</c>（老行为，一个字都不改用户的选择）。</summary>
        ConfiguredCacheRoot = 0,

        /// <summary>
        /// 默认档：跟着**这一单的目标目录**（<c>&lt;目标目录&gt;\.ArchiveFixer.work</c>，用户 2026-09-30 明确指示）。
        /// 目标目录与工作区在同一棵树里 → **天然同卷**，定稿仍是同卷改名。
        /// </summary>
        OutputDirectory = 1,

        /// <summary>
        /// 拿不到目标目录（没有算得出落点的任务 / 目标目录建不出来）。
        ///
        /// <para>⛔ 这一档**不回落任何位置**（<see cref="WorkspaceRootResolution.RootDirectory"/> 为空）：
        /// 用户 2026-09-30 点名"可能固定到了 D 盘，甚至危险操作固定到了 C 盘" ——
        /// 悄悄换个盘写中间产物是**不可逆**的错误方向，必须报错并告诉他去哪儿设。</para>
        /// </summary>
        Unresolved = 2
    }

    /// <summary>
    /// 一次"本批工作区放哪"的结论。
    ///
    /// <para>它只描述结论（路径 + 哪一档 + 为什么），不碰任何界面 —— 分层铁律：
    /// <c>Storage</c> 不引用 WPF。</para>
    /// </summary>
    public sealed class WorkspaceRootResolution
    {
        /// <summary>
        /// 本批工作区根目录。**解析不出来时是空串**（<see cref="WorkspaceRootOrigin.Unresolved"/>）——
        /// 调用方必须据此停下并报错，⛔ 不许拿空串去拼路径、更不许自己补一个"兜底位置"。
        /// </summary>
        public string RootDirectory { get; init; } = string.Empty;

        /// <summary>这个根是怎么定出来的。</summary>
        public WorkspaceRootOrigin Origin { get; init; }

        /// <summary>
        /// 工作区落在**哪个目标目录**里面（那一档才有；显式缓存根那一档为空串）。
        /// 它就是"这一单成品要落的目录"，也是 <see cref="RootDirectory"/> 的父目录。
        /// </summary>
        public string TargetDirectory { get; init; } = string.Empty;

        /// <summary>本批落点涉及到的盘（去重、按首次出现顺序）。只有一个盘时是单元素。</summary>
        public IReadOnlyList<string> BatchDrives { get; init; } = Array.Empty<string>();

        /// <summary>本批落点跨了不止一个盘（跨盘时**必须如实说明**，不许假装同盘）。</summary>
        public bool CrossDrive => BatchDrives.Count > 1;

        /// <summary>为什么是它（中文，含真实路径）—— 进日志，用户与排障都读得到。</summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>真的定下来了（<see cref="RootDirectory"/> 非空）。false = 必须报错停手。</summary>
        public bool Resolved => Origin != WorkspaceRootOrigin.Unresolved;

        /// <summary>这条结论该用的日志级别：定不下来是 ERROR（不许降级成 WARN 让它看起来没事）。</summary>
        public string LogLevel => Resolved ? "INFO" : "ERROR";
    }

    /// <summary>
    /// **默认工作区根落在这一单的目标目录里**（用户 2026-09-30 明确指示，取代 2026-09-24 的"跟着输出盘"）。
    ///
    /// <para><b>用户原话（三条诉求）</b>：① 「我不喜欢现在这样的模式就是专门在一个盘里面去开一个工作区……
    /// 我这个 H 盘总共才三个最顶层文件夹，但是你这个第四个就会占一个」；
    /// ② 「要么是在解压的地方临时创一个，然后弄完就直接全部清除什么都不留，原地就在原地创，
    /// 指定就在指定创，不要有固定一个位置的思想，因为用户可能忘了固定，可能固定到了 D 盘，
    /// 甚至危险操作固定到了 C 盘……外界盘有 200G，但是他 D 盘可能只有 40G 剩余，
    /// 这种情况下你难道要给他报空间不足的错误吗」；
    /// ③ 「就弄到工作区，而且是隐形的，什么都不要创，或者要创的话就隐秘文件」。</para>
    ///
    /// <para><b>改成 <c>&lt;目标目录&gt;\.ArchiveFixer.work</c> 之后</b>：① 不再在盘根造第四个顶层目录；
    /// ② 中间产物与成品落在**同一棵树**里 → 天然同卷，定稿仍是同卷改名（快、也不占双份）；
    /// ③ 目录名点开头 + <see cref="FileAttributes.Hidden"/>，用户翻自己的目录时看不见它。</para>
    ///
    /// <para><b>三条规则（与用户的三条诉求一一对应）</b>：</para>
    /// <list type="number">
    /// <item><description><b>用户显式设过 <c>CacheRootDirectory</c> → 永远以它为准</b>：工作区 = <c>&lt;它&gt;\work</c>，
    /// 一个字都不改用户的选择（这条口径不许改）。</description></item>
    /// <item><description>留空（默认）→ 跟着**这一批第一个算得出落点的目标目录**：<c>&lt;它&gt;\.ArchiveFixer.work</c>；
    /// 目标目录还不存在就**先建它**（用户原话"原地就在原地创"）。</description></item>
    /// <item><description>⛔ 拿不到目标目录 → <see cref="WorkspaceRootOrigin.Unresolved"/>：
    /// <b>报错并明确告诉用户去哪儿设</b>，<b>绝不</b>悄悄回落到 C 盘 / 程序目录
    /// （用户点名"危险操作固定到了 C 盘"）。</description></item>
    /// </list>
    ///
    /// <para>⚠ <b>落进目标目录之后必须排除自己那棵树</b>：那边有唯一出口
    /// <see cref="WorkspaceTree.ShouldSkipEntry"/> / <see cref="WorkspaceTree.EnumerateFiles"/>，
    /// 凡"扫产物 / 数内容物 / 找内层包 / 校验落点"的判据都要走它。</para>
    ///
    /// <para><b>目录名是点开头的 <c>.ArchiveFixer.work</c></b>（常量 <see cref="DefaultWorkspaceDirectoryName"/>，
    /// 只允许在这里出现一次）。名字、位置都不许在别处再拼一遍 —— 落点唯一实现仍是
    /// <c>OutputPlacement.ResolveDestinationDirectory</c>，本类只消费**它算出来的落点**。</para>
    /// </summary>
    public static class WorkspaceRootResolver
    {
        /// <summary>
        /// 默认工作区目录名（**点开头 = Windows 默认隐藏**，用户翻盘时不至于把它当成自己的东西）。
        /// 全项目只在这里出现一次；改名要连测试与文档一起改。
        /// </summary>
        public const string DefaultWorkspaceDirectoryName = ".ArchiveFixer.work";

        /// <summary>用户显式设过缓存根目录时用的子目录名（老行为：<c>&lt;缓存根&gt;\work</c>）。</summary>
        public const string ConfiguredCacheWorkspaceSubDirectoryName = "work";

        /// <summary>
        /// 解析本批的工作区根。
        /// </summary>
        /// <param name="configuredCacheRoot">
        /// 设置项 <see cref="AppSettings.CacheRootDirectory"/>；非空 = 用户显式设过 → 以它为准（规则 1）。
        /// </param>
        /// <param name="batchDestinationDirectories">
        /// 本批**按处理顺序**排好的目标目录（由调用方走唯一实现
        /// <c>PathService.ResolveOutputPlacement</c> 算出来，本类不自己拼路径）。
        /// 算不出落点的任务由调用方跳过 —— 这里只认"能算出目标目录"的那些。
        /// </param>
        /// <param name="driveOf">
        /// "一个目标目录在哪块盘上"（收到的是目录，返回盘根如 <c>D:\</c>）。
        /// 抽成参数是为了让测试不依赖本机真有 D 盘（默认 = <see cref="Path.GetPathRoot(string)"/>）；
        /// 它只影响"跨了几个盘"这句**描述**，不影响工作区落在哪。
        /// </param>
        /// <param name="ensureDirectory">按需建目录（默认 = <c>SafePathHelper.EnsureDirectoryExists</c>）。</param>
        public static WorkspaceRootResolution Resolve(
            string? configuredCacheRoot,
            IReadOnlyList<string>? batchDestinationDirectories,
            Func<string, string>? driveOf = null,
            Func<string, bool>? ensureDirectory = null)
        {
            Func<string, bool> ensure = ensureDirectory ?? SafePathHelper.EnsureDirectoryExists;

            /*
             * 规则 1：用户显式设过缓存根目录 → **永远以它为准**（老行为一个字都不改）。
             *
             * 这里刻意不判"盘在不在 / 能不能写"：那是用户自己的选择，换盘、改组策略是他的自由；
             * 我们只在他的目录下建 work 子目录（建不出来由后面的任务级错误如实报出来）。
             */
            if (!string.IsNullOrWhiteSpace(configuredCacheRoot))
            {
                string configured = configuredCacheRoot.Trim();
                string configuredRoot = Path.Combine(configured, ConfiguredCacheWorkspaceSubDirectoryName);

                // 工作区那棵树一律"建完立刻标隐藏"（用户诉求③：单看着看不出来的隐秘文件）。
                // 建目录走注入进来的那一个（单测要能把真实盘换成记账替身）。
                if (ensure(configuredRoot))
                {
                    WorkspaceTree.MarkHidden(configuredRoot);
                }

                return new WorkspaceRootResolution
                {
                    RootDirectory = configuredRoot,
                    Origin = WorkspaceRootOrigin.ConfiguredCacheRoot,
                    BatchDrives = CollectDrives(batchDestinationDirectories, driveOf),
                    Reason = string.Format(
                        CultureInfo.CurrentCulture,
                        StatusText.WorkspaceRootConfiguredFormat,
                        configuredRoot)
                };
            }

            List<string> drives = CollectDrives(batchDestinationDirectories, driveOf);
            string? firstFailure = null;

            foreach (string? destination in batchDestinationDirectories ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(destination))
                {
                    continue;
                }

                string full = SafePathHelper.GetFullPathSafe(destination);

                if (string.IsNullOrWhiteSpace(full))
                {
                    firstFailure ??= $"目标目录路径形状不合法：{destination}";
                    continue;
                }

                /*
                 * 目标目录还不存在就**先建它**（用户原话"原地就在原地创"）：工作区要落在它里面，
                 * 连它都没有就无从谈"落在目标目录里"。建不出来说明那个位置不可写，
                 * 换下一个能算出落点的任务继续试。
                 */
                if (!ensure(full))
                {
                    firstFailure ??= $"目标目录建不出来（不可写）：{full}";
                    continue;
                }

                string root = Path.Combine(full, DefaultWorkspaceDirectoryName);

                if (!ensure(root))
                {
                    firstFailure ??= $"目标目录里建不出工作区（不可写）：{root}";
                    continue;
                }

                // 建完立刻标隐藏（用户诉求③）。设不上只当没发生，绝不影响这一批能不能开工。
                WorkspaceTree.MarkHidden(root);

                return new WorkspaceRootResolution
                {
                    RootDirectory = root,
                    Origin = WorkspaceRootOrigin.OutputDirectory,
                    TargetDirectory = full,
                    BatchDrives = drives,
                    Reason = BuildOutputDirectoryReason(full, root, drives, driveOf)
                };
            }

            /*
             * 规则 3：拿不到目标目录 → **报错，不回落任何位置**（用户 2026-09-30 点名的红线）。
             *
             * 老实现这里回落到 <程序目录>\data\work 并写一条 WARN"整批照常开工"——
             * 用户明确否掉了那个方向：他可能把输出位置固定到了只剩 40G 的 D 盘、甚至固定到 C 盘，
             * 程序"悄悄换个盘"写中间产物正是他说"危险"的那件事。
             * 所以这一档 RootDirectory 留空，由调用方停手并把"去哪儿设"告诉他。
             */
            return new WorkspaceRootResolution
            {
                RootDirectory = string.Empty,
                Origin = WorkspaceRootOrigin.Unresolved,
                BatchDrives = drives,
                Reason = string.Format(
                    CultureInfo.CurrentCulture,
                    StatusText.WorkspaceRootUnresolvedFormat,
                    firstFailure ?? "这一批里没有任何算得出目标目录的任务")
            };
        }

        /// <summary>跟着目标目录那一档的日志正文（跨盘时如实补一句）。</summary>
        private static string BuildOutputDirectoryReason(
            string targetDirectory,
            string root,
            IReadOnlyList<string> drives,
            Func<string, string>? driveOf)
        {
            var text = new StringBuilder();

            text.Append(string.Format(
                CultureInfo.CurrentCulture,
                StatusText.WorkspaceRootInOutputDirectoryFormat,
                targetDirectory,
                root));

            if (drives.Count > 1)
            {
                string ownDrive = (driveOf ?? DefaultDriveOf)(targetDirectory);

                text.Append(string.Format(
                    CultureInfo.CurrentCulture,
                    StatusText.WorkspaceRootCrossDriveFormat,
                    drives.Count,
                    string.Join("、", drives),
                    ownDrive.TrimEnd('\\', '/')));
            }

            return text.ToString();
        }

        /// <summary>本批落点涉及到的盘（去重、保持首次出现的顺序）—— 跨盘说明与自检读它。</summary>
        private static List<string> CollectDrives(
            IReadOnlyList<string>? destinations,
            Func<string, string>? driveOf)
        {
            var drives = new List<string>();

            if (destinations == null)
            {
                return drives;
            }

            Func<string, string> resolveDrive = driveOf ?? DefaultDriveOf;

            foreach (string? destination in destinations)
            {
                if (string.IsNullOrWhiteSpace(destination))
                {
                    continue;
                }

                string drive = resolveDrive(SafePathHelper.GetFullPathSafe(destination));

                if (string.IsNullOrWhiteSpace(drive))
                {
                    continue;
                }

                bool seen = false;

                foreach (string existing in drives)
                {
                    if (string.Equals(existing, drive, StringComparison.OrdinalIgnoreCase))
                    {
                        seen = true;
                        break;
                    }
                }

                if (!seen)
                {
                    drives.Add(drive);
                }
            }

            return drives;
        }

        /// <summary>默认的"这个落点在哪块盘上"：<c>Path.GetPathRoot</c>（UNC 路径也会给出 <c>\\server\share</c>）。</summary>
        private static string DefaultDriveOf(string path)
        {
            try
            {
                return Path.GetPathRoot(path) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

    }

    /// <summary>
    /// **用过的工作区根**的小账本（<c>&lt;数据根&gt;\workspace-roots.txt</c>，一行一个绝对路径，最新的在最前）。
    ///
    /// <para><b>为什么必须有它</b>：工作区默认落在**这一单的目标目录**里之后，根会**随目标目录变**
    /// （今天 <c>D:\out\包A\.ArchiveFixer.work</c>、明天 <c>E:\别的\包B\.ArchiveFixer.work</c>）。
    /// 而"③ 清理与删除"页与启动那条日志要能继续找到**真实的残留**（用户 2026-09-24 第 23 条：
    /// 位置变了也要继续能扫到、能清）—— 不记住的话，换过目标目录之后上一次那批的残留就再也列不出来，
    /// 用户只能自己去翻隐藏目录。这与"残留看得见"是同一条要求的两半。</para>
    ///
    /// <para><b>它是尽力而为的账本</b>：读不到 / 写不进只当"没记住"，绝不抛、也绝不因此拦住任何流程
    /// （它管的只是"界面上列不列得出来"，不是解压本身）。</para>
    ///
    /// <para>文件名固定、内容是人类可读的纯文本：用户打开 <c>data</c> 目录时一眼能看懂这是什么，
    /// 删掉它也只是"下次少列一个根"（工作区本身一个字节都不会动）。</para>
    /// </summary>
    public static class WorkspaceRootIndex
    {
        /// <summary>账本文件名（放在数据根下，与日志 / 设置 / 密码列表同一个目录）。</summary>
        public const string FileName = "workspace-roots.txt";

        /// <summary>最多记几个根（再多也没有意义：用户不会同时用几十块盘）。</summary>
        public const int MaxRoots = 8;

        /// <summary>账本路径（数据根为空时返回空串）。</summary>
        public static string GetFilePath(string? dataRootDirectory) =>
            string.IsNullOrWhiteSpace(dataRootDirectory)
                ? string.Empty
                : Path.Combine(dataRootDirectory, FileName);

        /// <summary>读账本（读不到就是空列表 —— 绝不让它变成启动失败）。</summary>
        public static List<string> Load(string? dataRootDirectory)
        {
            var roots = new List<string>();
            string path = GetFilePath(dataRootDirectory);

            if (string.IsNullOrWhiteSpace(path))
            {
                return roots;
            }

            try
            {
                if (!File.Exists(path))
                {
                    return roots;
                }

                foreach (string? line in File.ReadAllLines(path))
                {
                    AddUnique(roots, line);
                }
            }
            catch
            {
                // 账本坏了 / 被占用：只当没记住。它是"界面上能不能列出来"的辅助信息，不是数据。
            }

            return roots;
        }

        /// <summary>
        /// 记住一个根（放最前）并写回账本。
        /// </summary>
        /// <returns>写回之后的清单（写不进去也返回内存里那一份，调用方照样能用）。</returns>
        public static List<string> Remember(string? dataRootDirectory, string? rootDirectory)
        {
            List<string> roots = Load(dataRootDirectory);

            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                return roots;
            }

            // 先把它从旧位置摘掉：同一个根重复出现只会把别的根挤出上限。
            roots.RemoveAll(existing => string.Equals(
                existing,
                rootDirectory.Trim(),
                StringComparison.OrdinalIgnoreCase));

            roots.Insert(0, rootDirectory.Trim());

            while (roots.Count > MaxRoots)
            {
                roots.RemoveAt(roots.Count - 1);
            }

            string path = GetFilePath(dataRootDirectory);

            if (string.IsNullOrWhiteSpace(path))
            {
                return roots;
            }

            try
            {
                string? directory = Path.GetDirectoryName(path);

                if (!string.IsNullOrWhiteSpace(directory))
                {
                    SafePathHelper.EnsureDirectoryExists(directory);
                }

                File.WriteAllLines(path, roots);
            }
            catch
            {
                // 写不进去（只读盘 / 被占用）只影响"下次启动还记不记得"，不影响这一批的解压。
            }

            return roots;
        }

        private static void AddUnique(List<string> roots, string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            string trimmed = candidate.Trim();

            if (trimmed.StartsWith('#'))
            {
                return;
            }

            foreach (string existing in roots)
            {
                if (string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            roots.Add(trimmed);
        }
    }
}
