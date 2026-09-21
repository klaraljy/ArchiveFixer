using System;

namespace ArchiveFixer.Models
{
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
                MaxRecursionDepth = 3
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

            // 层数下限 1（只解当前层），上限 10：再深就不是"帮用户省事"而是失控了。
            if (MaxRecursionDepth < 1)
            {
                MaxRecursionDepth = 1;
            }

            if (MaxRecursionDepth > 10)
            {
                MaxRecursionDepth = 10;
            }

            // 自定义 7z 路径要么是有效文件，要么当没填 —— 留一个失效路径会让整个程序找不到引擎。
            if (!string.IsNullOrWhiteSpace(CustomSevenZipExePath) && !System.IO.File.Exists(CustomSevenZipExePath))
            {
                CustomSevenZipExePath = string.Empty;
            }
        }
    }
}
