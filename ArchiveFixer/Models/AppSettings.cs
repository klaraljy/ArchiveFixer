using System;

namespace ArchiveFixer.Models
{
    /// <summary>
    /// 源包处理档（决策 D-9，2026-09-22 用户拍板）。
    ///
    /// <para>
    /// ⚠ **只作用于「一键处理」这条整理路径**。手动「只解压」是地基路径，
    /// **永远不动源包**（不变量 1 的例外范围被用户显式限定在这两条里，见 AGENTS.md §6 第 1 条）。
    /// </para>
    /// </summary>
    public enum SourceHandlingMode
    {
        /// <summary>把整组源包（分卷组 = 全部卷）移入其余物。**默认档**：整理完删一个目录就干净了。</summary>
        MoveToRest = 0,

        /// <summary>源包留在原地，一个字节都不动。</summary>
        KeepInPlace = 1,

        /// <summary>
        /// 沿用既有 §9.5 的清理（不可逆）：只有"解压成功 + 输出校验通过 + 属于本任务分卷组"才删，
        /// 回收站 / 彻底删除按既有设置走。
        /// </summary>
        DeleteAfterVerify = 2
    }

    /// <summary>
    /// 应用程序设置。
    /// 对应 appsettings.json。
    /// </summary>
    public class AppSettings
    {
        public bool RecursiveScan { get; set; } = true;

        public string ScanMode { get; set; } = "ScanAllFiles";

        public string UnknownFormatAction { get; set; } = "MarkUnknown";

        public string DefaultExtension { get; set; } = ".7z";

        public bool ExtractToOriginalDirectory { get; set; } = true;

        public string CustomOutputDirectory { get; set; } = string.Empty;

        public bool KeepArchiveNameFolder { get; set; } = true;

        public string ConflictAction { get; set; } = "AutoRename";

        public bool TestBeforeExtract { get; set; } = false;

        public bool EnableLog { get; set; } = true;

        public bool AutoScanAfterDrop { get; set; } = true;

        public bool PreviewBeforeRename { get; set; } = true;

        public string OverwriteMode { get; set; } = "SkipExisting";

        public bool TryEmptyPasswordFirst { get; set; } = true;

        public bool UseGlobalPasswordForAllTasks { get; set; } = true;

        public int MaxParallelExtractCount { get; set; } = 1;

        public bool RememberLastOutputDirectory { get; set; } = true;

        public bool IncludeHiddenFiles { get; set; } = false;

        public bool IncludeSystemFiles { get; set; } = false;

        public long MaxFileSizeLimit { get; set; } = 0;

        public bool PreservePasswordLeadingTrailingSpaces { get; set; } = true;

        /// <summary>
        /// 从归档同目录的说明文件（.txt/.bat/…）里提取密码候选。
        /// **默认关闭**：这是"猜"出来的候选，必须由用户显式开启（AGENTS.md §9.4）。
        /// </summary>
        public bool EnableSidecarPassword { get; set; } = false;

        /// <summary>用户自定义的 7z.exe 路径；空表示用程序目录内置的。路径只由 ToolLocator 解析（AGENTS.md §3.1）。</summary>
        public string CustomSevenZipExePath { get; set; } = string.Empty;

        /// <summary>
        /// 解压成功且校验通过后删除源压缩包/源分卷。
        /// **默认关闭**：删除不可逆，必须用户显式开启（AGENTS.md §9.5、用户 2026-09-21 指示）。
        /// </summary>
        public bool DeleteSourceAfterExtract { get; set; } = false;

        /// <summary>把多个包的产物归集（移动）到一个目标目录（AGENTS.md §9.5）。</summary>
        public bool CollectResultsToDirectory { get; set; } = false;

        /// <summary>
        /// 递归解压模式（AGENTS.md §6 第 8 条、设计.md §十）：
        /// SingleLayer = 只解当前层；SingleChain = 只有一个主要内层归档时自动继续（默认）；
        /// AllBranches = 展开所有内层归档（必须由用户显式选择）。
        /// </summary>
        /// 注意：**默认已改成 SingleLayer**。
        /// 递归解压在 2026-09-21 出现"点了就整机无响应"的故障，
        /// 排查期间先让默认流程走单层（单层已用真实文件验证通过），
        /// 递归修好后再改回来 —— 不能让用户替我的 bug 买单。
        public string RecursionMode { get; set; } = "SingleLayer";

        /// <summary>递归最大层数。到顶就停并报告，不做无限展开。</summary>
        public int MaxRecursionDepth { get; set; } = 3;

        /// <summary>
        /// 每一层（每个归档）最多真的试几个密码候选。
        ///
        /// 为什么必须有上限（不变量 8）：密码本可能有几百条，一个包逐条试过去会烧掉整晚；
        /// 到上限时状态是「达到密码尝试上限」，**不是**"密码错误"（AGENTS.md §9.2）——
        /// 前者是"还没试完就停了"，后者是"试过的都不对"，两者的处置方式完全不同。
        ///
        /// 范围 1–1000：下限 1 保证至少试一个候选（否则等于不解压），
        /// 上限 1000 是兜底 —— 再大就不是"帮用户省事"，而是把时间烧在一个可能失败的包上。
        /// </summary>
        public int MaxPasswordAttemptsPerLayer { get; set; } = 10;

        /// <summary>
        /// 缓存根目录（日志 / 临时 / 递归工作区 / 配置文件都放这里）。
        ///
        /// 默认留空 = 用**程序目录下的 data**。
        /// 用户明确要求：缓存绝不能默认写到 C 盘（%AppData%），绿色软件跟着安装位置走；
        /// 要换盘就在这里填绝对路径。
        /// </summary>
        public string CacheRootDirectory { get; set; } = string.Empty;

        /// <summary>
        /// 上次导入的密码本文件路径（用户 2026-09-21 反复要求：导入一次就够了，不要每次重导）。
        /// 启动时若文件仍在就自动加载；文件没了就只写一条 WARN，不打扰用户。
        /// </summary>
        public string PasswordBookPath { get; set; } = string.Empty;
        /// <summary>归集目标目录。</summary>
        public string CollectTargetDirectory { get; set; } = string.Empty;

        /// <summary>
        /// 终端落法（规格 <c>docs/输出与整理模型.md</c> §3.1 的可选项）：内容物最里面那一层文件夹叫什么。
        ///
        /// 存的是 <see cref="ArchiveFixer.Extraction.TerminalLayoutMode"/> 的**枚举名**
        /// （<c>KeepLastFolder</c> / <c>UseArchiveName</c>），与其它设置项（RecursionMode / OverwriteMode）
        /// 一样用字符串落盘 —— 枚举名字比数字抗改，用户手改配置文件也能看懂。
        ///
        /// 默认 <c>KeepLastFolder</c>：保留归档内最后一层文件夹名（<c>111\222\666\内容物</c>），
        /// 更保守、不丢信息（规格 §7 决策 D-1）。
        /// </summary>
        public string TerminalLayoutMode { get; set; } = "KeepLastFolder";

        /// <summary>
        /// 场景 B 塌缩（规格 §3.3）：<c>111\222\名字\名字.rar</c> 且该目录下只有这一个包时，
        /// 产物直接落在 <c>111\222\名字\</c>，不再套一层重复的 <c>名字</c>。
        ///
        /// **默认开**（规格 §3.3 明确"此规则必须可关（设置项），默认开"）。
        /// 只在"包基名 == 所在目录名"且目录里没有别的归档时才生效，其余情况一律不动 ——
        /// 否则同一个目录里两个包的产物会并在一起，用户再也分不清哪份内容来自哪个包。
        /// </summary>
        public bool CollapseRepeatedFolderLayer { get; set; } = true;

        /// <summary>
        /// 「一键处理」里怎么处理源包（决策 D-9，2026-09-22 用户拍板）。
        ///
        /// 存的是 <see cref="SourceHandlingMode"/> 的**枚举名**（<c>MoveToRest</c> / <c>KeepInPlace</c> /
        /// <c>DeleteAfterVerify</c>），与其它设置项（RecursionMode / OverwriteMode / TerminalLayoutMode）
        /// 一样用字符串落盘 —— 枚举名比数字抗改，用户手改配置文件也看得懂。
        ///
        /// 默认 <c>MoveToRest</c>：源包跟着进其余物，用户在那个目录里一次删掉就干净了
        /// （用户原话："其余物/源包+过程物，这样删除对用户就更方便一点"）。可关。
        ///
        /// ⚠ **不影响手动「只解压」**（地基路径永远不动源包）；
        /// 也不影响 <see cref="DeleteSourceAfterExtract"/> —— 那个开关管的是地基路径的清理。
        /// </summary>
        public string SourceHandling { get; set; } = nameof(SourceHandlingMode.MoveToRest);

        /// <summary>
        /// 解析源包处理档：空 / 非法一律回落 <see cref="SourceHandlingMode.MoveToRest"/>。
        ///
        /// 容错放在这里（与 <c>ParseTerminalLayoutMode</c> 同一口径）：这个字符串可能来自
        /// 旧配置（缺字段 → 反序列化后是默认值）、用户手改的 json，或将来改名后的枚举。
        /// 读不懂时**退回最不意外的那一档**，而不是到解压那一刻才报错或猜一个别的行为。
        /// </summary>
        public static SourceHandlingMode ParseSourceHandling(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                Enum.TryParse(value.Trim(), ignoreCase: true, out SourceHandlingMode mode) &&
                Enum.IsDefined(mode))
            {
                return mode;
            }

            return SourceHandlingMode.MoveToRest;
        }

        /// <summary>反解成落盘字符串（界面 ↔ 解压管线共用同一份口径）。</summary>
        public static string ToSourceHandlingValue(SourceHandlingMode mode)
        {
            return mode.ToString();
        }

        public static AppSettings CreateDefault()
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
                ConflictAction = "AutoRename",
                TestBeforeExtract = false,
                EnableLog = true,
                AutoScanAfterDrop = true,
                PreviewBeforeRename = true,
                OverwriteMode = "SkipExisting",
                TryEmptyPasswordFirst = true,
                UseGlobalPasswordForAllTasks = true,
                MaxParallelExtractCount = 1,
                RememberLastOutputDirectory = true,
                IncludeHiddenFiles = false,
                IncludeSystemFiles = false,
                MaxFileSizeLimit = 0,
                PreservePasswordLeadingTrailingSpaces = true,
                EnableSidecarPassword = false,
                CustomSevenZipExePath = string.Empty,
                DeleteSourceAfterExtract = false,
                CollectResultsToDirectory = false,
                CollectTargetDirectory = string.Empty,
                CacheRootDirectory = string.Empty,
                PasswordBookPath = string.Empty,
                RecursionMode = "SingleLayer",
                MaxRecursionDepth = 3,
                MaxPasswordAttemptsPerLayer = 10,
                TerminalLayoutMode = "KeepLastFolder",
                CollapseRepeatedFolderLayer = true,
                SourceHandling = nameof(SourceHandlingMode.MoveToRest)
            };
        }

        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(ScanMode))
            {
                ScanMode = "ScanAllFiles";
            }

            if (string.IsNullOrWhiteSpace(UnknownFormatAction))
            {
                UnknownFormatAction = "MarkUnknown";
            }

            if (string.IsNullOrWhiteSpace(DefaultExtension))
            {
                DefaultExtension = ".7z";
            }

            if (!DefaultExtension.StartsWith(".", StringComparison.Ordinal))
            {
                DefaultExtension = "." + DefaultExtension;
            }

            if (string.IsNullOrWhiteSpace(ConflictAction))
            {
                ConflictAction = "AutoRename";
            }

            if (string.IsNullOrWhiteSpace(OverwriteMode))
            {
                OverwriteMode = "SkipExisting";
            }

            if (MaxParallelExtractCount < 1)
            {
                MaxParallelExtractCount = 1;
            }

            if (MaxParallelExtractCount > 8)
            {
                MaxParallelExtractCount = 8;
            }

            if (MaxFileSizeLimit < 0)
            {
                MaxFileSizeLimit = 0;
            }

            CustomOutputDirectory ??= string.Empty;
            CustomSevenZipExePath ??= string.Empty;
            CollectTargetDirectory ??= string.Empty;
            CacheRootDirectory ??= string.Empty;
            PasswordBookPath ??= string.Empty;

            if (string.IsNullOrWhiteSpace(RecursionMode))
            {
                RecursionMode = "SingleLayer";
            }

            /*
             * 终端落法：非法值一律回落默认。
             *
             * 为什么放在设置层做容错（而不是等到解压时再判）：
             * 这个字符串可能来自旧配置（缺字段）、用户手改的 json，或将来改名后的枚举。
             * 到解压那一刻才发现"读不懂"是最糟的 —— 用户已经点了一键处理，落点却是猜出来的。
             *
             * 判断口径与运行时解析完全一致（都走 OutputPlacement.ParseTerminalLayoutMode），
             * 不在这里另写一套字符串比较，免得两处对"什么算合法"产生分歧。
             */
            TerminalLayoutMode = ArchiveFixer.Extraction.OutputPlacement
                .ParseTerminalLayoutMode(TerminalLayoutMode)
                .ToString();

            /*
             * 源包处理档（决策 D-9）：空 / 非法一律回落默认档 MoveToRest，**旧配置不报错**。
             *
             * 这里刻意**不**把旧的 DeleteSourceAfterExtract=true 迁移成 DeleteAfterVerify：
             * 那个布尔管的是"手动只解压"这条地基路径的清理，与一键处理的整理档是两件事；
             * 迁移它会让升级后的一键处理从"移动"变成"不可逆删除"，方向正好反了。
             * （一键处理的新默认值是"移动"——比删除保守，用户随时可以在设置里改成删除。）
             */
            SourceHandling = ParseSourceHandling(SourceHandling).ToString();

            // 层数下限 1（只解当前层），上限 10：再深就不是"帮用户省事"而是失控了。
            if (MaxRecursionDepth < 1)
            {
                MaxRecursionDepth = 1;
            }

            if (MaxRecursionDepth > 10)
            {
                MaxRecursionDepth = 10;
            }

            // 密码尝试上限：下限 1（至少试一个候选），上限 1000（再大就不是省事而是烧时间）。
            // 旧配置文件里没有这一项 → 反序列化后拿到的是默认值 10，不需要额外兼容分支。
            if (MaxPasswordAttemptsPerLayer < 1)
            {
                MaxPasswordAttemptsPerLayer = 1;
            }

            if (MaxPasswordAttemptsPerLayer > 1000)
            {
                MaxPasswordAttemptsPerLayer = 1000;
            }

            // 自定义 7z 路径要么是有效文件，要么当没填 —— 留一个失效路径会让整个程序找不到引擎。
            if (!string.IsNullOrWhiteSpace(CustomSevenZipExePath) && !System.IO.File.Exists(CustomSevenZipExePath))
            {
                CustomSevenZipExePath = string.Empty;
            }
        }
    }
}
