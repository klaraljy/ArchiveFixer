using System.Collections.Generic;

namespace ArchiveFixer.Models;

/// <summary>
/// 出厂默认值的**唯一赋值表**：<see cref="AppSettings.CreateDefault"/> 转到这里。
///
/// <para>为什么单独成类：出厂默认必须与 <see cref="AppSettingsNormalizer"/> 里那张
/// 「系统固定值」钉子表逐项一致（新装 = 默认，老配置 = 归一值，两条路不能给出不同行为），
/// 判据由 <c>FixedDefaultsTests.出厂默认本来就是系统值</c> 逐项比对。把赋值集中在一个文件里，
/// 改默认值时不会漏项，也不必去动属性声明。</para>
///
/// <para>⛔ 这里只写「值」，⛔ 不写归一化逻辑 —— 归一化在 <see cref="AppSettingsNormalizer"/>。</para>
/// </summary>
internal static class AppSettingsDefaults
{
    /// <summary>按出厂默认值创建一份设置（逐项列出，便于一眼看到出厂值；与属性初始化器保持一致）。</summary>
    internal static AppSettings Create()
    {
        return new AppSettings
        {
            RecursiveScan = true,
            ScanMode = "ScanAllFiles",
            UnknownFormatAction = "MarkUnknown",
            DefaultExtension = ".7z",
            ExtractToOriginalDirectory = true,
            CustomOutputDirectory = string.Empty,
            KeepArchiveNameFolder = true,
            ConflictAction = ConflictActions.AutoRename,
            TestBeforeExtract = false,
            EnableLog = true,

            // 详细日志默认关：成功只留一行摘要（第 44 条追加）。显式写出来是为了让
            // "出厂默认"一眼可见，别让人以为它是漏掉的。
            VerboseLog = false,
            AutoScanAfterDrop = true,
            PreviewBeforeRename = true,

            /*
             * ⚠ 2026-10-14（用户最终设计 + 当场逐项拍板）：这一批已经是**系统固定值**、
             * 界面上没有入口 ⇒ 出厂默认必须跟 `Normalize` 里那张钉子表**逐项一致**
             * （否则"新装 = 默认"，老配置 = 归一值，两条路给出不同行为）。
             * ⛔ 判据只有一处实现：`FixedDefaultsTests.出厂默认本来就是系统值` 会逐项比对两边。
             * ⚠ 本块里只写**原先不是这个值**的那一项（OverwriteMode）；其余各项（RecursionMode /
             *    KeepFailedWorkspace / RememberPasswordList …）
             *    原本就是系统值，各自在下面写着，⛔ 不许在这里再写一遍（CS1912 会当场打回）。
             */
            OverwriteMode = "AutoRenameExisting",
            TryEmptyPasswordFirst = true,
            UseGlobalPasswordForAllTasks = true,
            MaxParallelExtractCount = AppSettings.DefaultMaxParallelExtractCount,
            RunAtFullSpeed = false,
            LowProcessPriority = false,
            OpenOutputFolderWhenDone = false,
            ReportDangerousEntries = true,
            RemindBeforeExtract = true,

            // 第 42 条的全自动拼装：出厂**关**（它会读源目录、并在盘上造名字 / 复制卷）。
            AssembleSplitVolumesFromContainer = true,

            // 打包（第 46 条）：落点默认本地、原包不动、其余物（装分卷的文件夹）默认彻底删除。
            PackTargetMode = "Local",
            PackCustomOutputDirectory = string.Empty,
            PackSourceHandling = "KeepInPlace",
            PackRestHandling = "Delete",
            RememberLastOutputDirectory = true,
            IncludeHiddenFiles = false,
            IncludeSystemFiles = false,
            MaxFileSizeLimit = 0,
            EnableSidecarPassword = false,
            CustomSevenZipExePath = string.Empty,
            CustomUnRarExePath = string.Empty,
            CustomRarExePath = string.Empty,

            // 用户 2026-09-22 指示："先是 winrar、7z、然后就是后面的引擎"。
            EnginePriority = new List<string>(EngineIds.DefaultPriority),
            KeepBrokenFiles = false,
            RestHandlingAfterVerify = RestHandlingModes.Keep,
            CollectResultsToDirectory = false,
            CollectTargetDirectory = string.Empty,
            PasswordBookPath = string.Empty,
            PasswordBookPaths = new List<string>(),
            RememberPasswordList = true,
            SkipOneClickConfirm = false,
            RemindJunkAfterImport = true,
            /*
             * 递归模式出厂默认（用户 2026-10-04 当场推翻旧口径）：
             * 「那你为什么会将这个**单链解压设为默认**……现在**将默认设为全文件解压**」——
             * 出厂默认 = 「展开所有分支」（有压缩包就解压），②页那三档照旧都可选。
             *
             * ⛔ 只改**出厂默认**：用户自己存过的档一个字都不动（`Normalize` 里也不做迁移）——
             * 他机器上那份 appsettings.json 里选的是哪一档，就还是哪一档。
             */

            /*
             * 默认层数 = **7**（用户 2026-10-14 拍板：层数/轮数**保持一个设置**、默认从 5 调到 7，
             * 不再有"单层路 / 单链档"，一律展开到底）。
             * ⚠ 上限没动：`Normalize` 里仍然是 1..10，`OneClickCoordinator.MaxRoundsCeiling` = 10（不变量 8）⇒ 7 在闸内。
             * ⛔ 只改**出厂默认**：用户自己存过的档一个字都不动。
             */
            MaxRecursionDepth = 7,
            RecursionDefaultUnifiedToTen = true,
            MaxPasswordAttemptsPerLayer = 10,

            // 解压前的资源预算上限（用户 2026-09-25 第 36 条：四条都落进设置，默认值调到真实包碰不到的量级）。
            MaxSingleExtractedFileGiB = AppSettings.DefaultMaxSingleExtractedFileGiB,
            MaxExtractedTotalGiB = AppSettings.DefaultMaxExtractedTotalGiB,
            MaxExtractedFileCount = AppSettings.DefaultMaxExtractedFileCount,
            MaxExtractionRatio = AppSettings.DefaultMaxExtractionRatio,

            // 内容物保留关键词（用户 2026-10-04）：出厂**空** = 功能不生效，行为与以前一字不差。
            ContentKeepKeywords = new List<string>(),

            SourceHandling = nameof(SourceHandlingMode.KeepInPlace),

            // 失败 / 取消不留中间产物（用户 2026-09-25 第 25 条追加）：默认关闭，
            // 老配置里没有这个字段时反序列化出来也是 false —— 与默认档一致，不需要迁移标记。
            KeepFailedWorkspace = false,

            /*
             * 部分完成发布（用户 2026-10-02 拍板，口径见 StatusText 那一组常量与
             * `docs/部分完成发布方案.md` §6）：**默认关**。
             *
             * 为什么默认关、而且不能顺手打开：它让"跑完之后盘上从 1 份变成 2 份"
             * （源包 + 已解出的内容物，而且源包一定不删）—— 用户点名的顾虑就是这条
             * （"空间不足加上大容量多相同文件一起开"）。要开是他自己的选择，程序不替他决定。
             */
            PartialPublishEnabled = false
        };
    }
}
