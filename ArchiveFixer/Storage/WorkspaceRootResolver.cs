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

        /// <summary>默认档：跟着**输出盘**（<c>&lt;输出盘&gt;\.ArchiveFixer.work</c>）。</summary>
        OutputDrive = 1,

        /// <summary>拿不到盘（形状不合法 / 盘不存在 / 不可写）→ 回落 <c>&lt;程序目录&gt;\data\work</c>（老行为）。</summary>
        ProgramDirectoryFallback = 2
    }

    /// <summary>
    /// 一次"本批工作区放哪"的结论。
    ///
    /// <para>它只描述结论（路径 + 哪一档 + 为什么），不碰任何界面 —— 分层铁律：
    /// <c>Storage</c> 不引用 WPF。</para>
    /// </summary>
    public sealed class WorkspaceRootResolution
    {
        /// <summary>本批工作区根目录（一定非空：实在定不下来时是程序目录下的老位置）。</summary>
        public string RootDirectory { get; init; } = string.Empty;

        /// <summary>这个根是怎么定出来的。</summary>
        public WorkspaceRootOrigin Origin { get; init; }

        /// <summary>跟着输出盘时那个盘根（如 <c>D:\</c>）；其它档为空串。</summary>
        public string DriveRoot { get; init; } = string.Empty;

        /// <summary>本批落点涉及到的盘（去重、按首次出现顺序）。只有一个盘时是单元素。</summary>
        public IReadOnlyList<string> BatchDrives { get; init; } = Array.Empty<string>();

        /// <summary>本批落点跨了不止一个盘（跨盘时**必须如实说明**，不许假装同盘）。</summary>
        public bool CrossDrive => BatchDrives.Count > 1;

        /// <summary>为什么是它（中文，含真实路径）—— 进日志，用户与排障都读得到。</summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>回落档（拿不到盘）＝ 要写 WARN，其余两档是 INFO。</summary>
        public bool IsFallback => Origin == WorkspaceRootOrigin.ProgramDirectoryFallback;

        /// <summary>这条结论该用的日志级别。</summary>
        public string LogLevel => IsFallback ? "WARN" : "INFO";
    }

    /// <summary>
    /// **默认工作区根跟着输出盘走**（用户 2026-09-24 拍板）。
    ///
    /// <para><b>为什么改</b>：老行为把工作区放在 <c>&lt;程序目录&gt;\data\work</c>，于是</para>
    /// <list type="number">
    /// <item><description>失败 / 取消留下的残留（用户实测 5.7 GB，他问的原话是"那要是 40G 的东西"）
    /// 全都堆在**程序所在的那块盘**上；</description></item>
    /// <item><description>定稿（暂存 → 成品目录）在输出盘与程序盘**不同卷**时是"复制 + 删原件"，
    /// 又慢又要在两块盘上同时放得下。</description></item>
    /// </list>
    /// <para>改成 <c>&lt;输出盘&gt;\.ArchiveFixer.work</c> 之后：① 中间产物不再堆在程序盘；
    /// ② 定稿变成**同盘改名**（快、也不占双份）。两条都要如实写进日志，用户才知道东西在哪。</para>
    ///
    /// <para><b>四条规则（与用户拍板的方案一一对应）</b>：</para>
    /// <list type="number">
    /// <item><description><b>用户显式设过 <c>CacheRootDirectory</c> → 永远以它为准</b>：工作区 = <c>&lt;它&gt;\work</c>，
    /// 与改这一条之前的行为逐字相同（一个字都不改用户的选择）。</description></item>
    /// <item><description>留空（默认）时跟着**这一批的落点盘**：指定了位置 → 那个盘的根；
    /// 没指定位置 → 源包所在盘（成品就落在源目录旁边）。取"这一批第一个真正算得出落点的任务"的盘。</description></item>
    /// <item><description>一批里可能跨盘：工作区固定在第一个盘上，**并在日志里如实说明跨了几个盘**
    /// （跨盘时那些任务的定稿会是复制，不能假装它们同盘）。</description></item>
    /// <item><description>拿不到盘（路径形状不合法 / 盘不存在 / 建不出目录＝不可写）→ **回落老行为**
    /// <c>&lt;程序目录&gt;\data\work</c> 并写 WARN 说明原因，**绝不因此让整批开不了工**。</description></item>
    /// </list>
    ///
    /// <para><b>目录名是点开头的 <c>.ArchiveFixer.work</c></b>（常量 <see cref="DefaultWorkspaceDirectoryName"/>，
    /// 只允许在这里出现一次）：Windows 上点开头的目录默认隐藏，用户翻输出盘时不会把它当成自己的东西。
    /// 名字、位置都不许在别处再拼一遍 —— 落点唯一实现仍是
    /// <c>OutputPlacement.ResolveDestinationDirectory</c>，本类只在**它算出来的落点**上取盘符。</para>
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
        /// <param name="dataRootDirectory">
        /// 数据根目录（<c>&lt;程序目录&gt;\data</c>，或用户设过的缓存根）—— 回落目标
        /// <c>&lt;它&gt;\work</c> 就是**改这一条之前的老位置**。
        /// </param>
        /// <param name="batchDestinationDirectories">
        /// 本批**按处理顺序**排好的落点目录（由调用方走唯一实现
        /// <c>PathService.ResolveOutputPlacement</c> 算出来，本类不自己拼路径）。
        /// 算不出落点的任务由调用方跳过 —— 这里只认"能算出盘"的那些。
        /// </param>
        /// <param name="driveOf">
        /// "一个落点目录在哪块盘上"（收到的是目录，返回盘根如 <c>D:\</c>）。
        /// 抽成参数是为了让测试不依赖本机真有 D 盘（默认 = <see cref="Path.GetPathRoot(string)"/>）。
        /// </param>
        /// <param name="driveExists">
        /// 盘根存在性探针（默认 = <c>Directory.Exists</c>）。同 <c>OutputPlacement</c> 的口径：
        /// 这是本类唯一碰环境的地方，抽出来给单测注入假盘。
        /// </param>
        /// <param name="ensureDirectory">按需建目录（默认 = <c>SafePathHelper.EnsureDirectoryExists</c>）。</param>
        public static WorkspaceRootResolution Resolve(
            string? configuredCacheRoot,
            string dataRootDirectory,
            IReadOnlyList<string>? batchDestinationDirectories,
            Func<string, string>? driveOf = null,
            Func<string, bool>? driveExists = null,
            Func<string, bool>? ensureDirectory = null)
        {
            Func<string, bool> ensure = ensureDirectory ?? SafePathHelper.EnsureDirectoryExists;

            string fallbackRoot = string.IsNullOrWhiteSpace(dataRootDirectory)
                ? string.Empty
                : Path.Combine(dataRootDirectory, ConfiguredCacheWorkspaceSubDirectoryName);

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

                ensure(configuredRoot);

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

            Func<string, string> resolveDrive = driveOf ?? DefaultDriveOf;
            Func<string, bool> probe = driveExists ?? DefaultDriveExists;

            string? firstFailure = null;
            List<string> drives = CollectDrives(batchDestinationDirectories, driveOf);

            foreach (string? destination in batchDestinationDirectories ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(destination))
                {
                    continue;
                }

                string full = SafePathHelper.GetFullPathSafe(destination);

                if (string.IsNullOrWhiteSpace(full))
                {
                    firstFailure ??= $"落点路径形状不合法：{destination}";
                    continue;
                }

                string drive = resolveDrive(full);

                if (string.IsNullOrWhiteSpace(drive))
                {
                    firstFailure ??= $"落点里认不出盘符：{full}";
                    continue;
                }

                if (!probe(drive))
                {
                    firstFailure ??= $"输出盘不存在或未就绪：{drive}";
                    continue;
                }

                string root = Path.Combine(drive, DefaultWorkspaceDirectoryName);

                if (!ensure(root))
                {
                    firstFailure ??= $"输出盘上建不出工作区目录（不可写）：{root}";
                    continue;
                }

                return new WorkspaceRootResolution
                {
                    RootDirectory = root,
                    Origin = WorkspaceRootOrigin.OutputDrive,
                    DriveRoot = drive,
                    BatchDrives = drives,
                    Reason = BuildOutputDriveReason(drive, root, drives)
                };
            }

            /*
             * 规则 4：拿不到盘 → 回落老行为 + WARN 说明原因。
             *
             * "绝不因此让整批开不了工"是硬要求：工作区定不下来只是慢一点、多占一点，
             * 而"整批开不了工"是用户什么都干不了。
             */
            ensure(fallbackRoot);

            return new WorkspaceRootResolution
            {
                RootDirectory = fallbackRoot,
                Origin = WorkspaceRootOrigin.ProgramDirectoryFallback,
                BatchDrives = drives,
                Reason = string.Format(
                    CultureInfo.CurrentCulture,
                    StatusText.WorkspaceRootFallbackFormat,
                    fallbackRoot,
                    firstFailure ?? "这一批没有任何算得出落点的任务")
            };
        }

        /// <summary>跟着输出盘那一档的日志正文（跨盘时如实补一句）。</summary>
        private static string BuildOutputDriveReason(string drive, string root, IReadOnlyList<string> drives)
        {
            var text = new StringBuilder();

            text.Append(string.Format(
                CultureInfo.CurrentCulture,
                StatusText.WorkspaceRootOnOutputDriveFormat,
                drive.TrimEnd('\\', '/'),
                root));

            if (drives.Count > 1)
            {
                text.Append(string.Format(
                    CultureInfo.CurrentCulture,
                    StatusText.WorkspaceRootCrossDriveFormat,
                    drives.Count,
                    string.Join("、", drives),
                    drive.TrimEnd('\\', '/')));
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

        /// <summary>默认探针：盘根在不在。失败一律当"不存在"（与 <c>OutputPlacement</c> 同一口径）。</summary>
        private static bool DefaultDriveExists(string driveRoot)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(driveRoot) && Directory.Exists(driveRoot);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// **用过的工作区根**的小账本（<c>&lt;数据根&gt;\workspace-roots.txt</c>，一行一个绝对路径，最新的在最前）。
    ///
    /// <para><b>为什么必须有它</b>：工作区默认跟输出盘之后，根会**随输出盘变**
    /// （今天 <c>D:\.ArchiveFixer.work</c>、明天 <c>E:\.ArchiveFixer.work</c>）。
    /// 而"③ 清理与删除"页与启动那条日志要能继续找到**真实的残留**（用户 2026-09-24 第 23 条：
    /// 位置变了也要继续能扫到、能清）—— 不记住的话，换过盘之后上一次那批的残留就再也列不出来，
    /// 用户只能自己去盘根翻隐藏目录。这与"残留看得见"是同一条要求的两半。</para>
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
