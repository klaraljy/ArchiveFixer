using ArchiveFixer.Extraction;

namespace ArchiveFixer.Models
{
    /// <summary>
    /// 解压选项。
    /// 由 AppSettings 和界面选项转换而来，传给归档引擎（IArchiveEngine.ExtractAsync）使用。
    /// </summary>
    public class ExtractOptions
    {
        /// <summary>
        /// 是不是"未指定位置"那一档（落点在**源包同目录家族**：源包旁边建同名子文件夹）。
        /// true：使用压缩包当前所在目录。
        /// false：使用 CustomOutputDirectory。
        /// </summary>
        public bool ExtractToOriginalDirectory { get; set; } = true;

        /// <summary>
        /// 自定义输出目录。
        /// ExtractToOriginalDirectory = false 时使用。
        /// </summary>
        public string CustomOutputDirectory { get; set; } = string.Empty;

        /// <summary>
        /// 是否保留压缩包同名文件夹。
        /// 
        /// 例如：
        /// test.7z -> test\
        /// </summary>
        public bool KeepArchiveNameFolder { get; set; } = true;

        /// <summary>
        /// 新落点计划的直接输入；非空时优先于上面两个旧布尔。
        /// 旧布尔只留给旧调用方/旧配置读入，新写入只写这一格。
        /// </summary>
        public OutputPlacementMode? PlacementMode { get; set; }

        /// <summary>
        /// 手动档「**解压到当前文件夹**」（用户 2026-09-27 新加的那一档，语义 = WinRAR 的"解压到当前文件夹"）：
        /// 内容物直接落在**压缩包所在的那一层**，⛔ 不套包名文件夹（<c>111\222.rar</c> → <c>111\内容物</c>）。
        ///
        /// <para>⛔ 只有手动操作会把它置 true —— 一键处理 / 批量**永远**是"外面裹一层包名目录"。
        /// 同一层里有多个包时它们的内容物会混进同一层（同名文件走冲突档）——这是用户自己点的，
        /// 日志里会提醒一句。</para>
        /// </summary>
        public bool ExtractIntoSourceFolder { get; set; }

        /// <summary>
        /// 解压前是否先执行 7z t 测试。
        /// </summary>
        public bool TestBeforeExtract { get; set; } = false;

        /// <summary>
        /// 覆盖策略。
        /// 
        /// SkipExisting        -> -aos
        /// OverwriteAll        -> -aoa
        /// AutoRenameExtracted -> -aou
        /// AutoRenameExisting  -> -aot
        /// </summary>
        public string OverwriteMode { get; set; } = "SkipExisting";

        /// <summary>
        /// 是否使用统一密码。
        /// </summary>
        public bool UseGlobalPassword { get; set; } = true;

        /// <summary>
        /// 统一密码。
        /// 仅内存使用，不能写入日志。
        /// </summary>
        public string GlobalPassword { get; set; } = string.Empty;

        /// <summary>
        /// 是否尝试密码列表。
        /// </summary>
        public bool TryPasswordList { get; set; } = true;

        /// <summary>
        /// 是否优先尝试空密码。
        /// </summary>
        public bool TryEmptyPasswordFirst { get; set; } = true;

        /// <summary>
        /// 密码成功后是否停止继续尝试。
        /// 默认 true。
        /// </summary>
        public bool CancelOnFirstSuccess { get; set; } = true;

        /// <summary>
        /// 遇到 Unknown 格式时是否尝试解压。
        /// 默认 false，更安全。
        /// </summary>
        public bool TryExtractUnknownFormat { get; set; } = false;

        /// <summary>
        /// 最大并发解压数。
        /// 第十一版默认串行，值为 1。
        /// </summary>
        public int MaxParallelExtractCount { get; set; } = 1;

        /// <summary>
        /// 从 AppSettings 创建解压选项。
        /// </summary>
        public static ExtractOptions FromSettings(AppSettings settings, string globalPassword = "")
        {
            settings ??= AppSettings.CreateDefault();
            settings.Normalize();

            return new ExtractOptions
            {
                ExtractToOriginalDirectory = settings.ExtractToOriginalDirectory,
                CustomOutputDirectory = settings.CustomOutputDirectory,
                KeepArchiveNameFolder = settings.KeepArchiveNameFolder,
                TestBeforeExtract = settings.TestBeforeExtract,
                OverwriteMode = settings.OverwriteMode,
                UseGlobalPassword = settings.UseGlobalPasswordForAllTasks,
                GlobalPassword = globalPassword ?? string.Empty,
                TryPasswordList = true,
                TryEmptyPasswordFirst = settings.TryEmptyPasswordFirst,
                CancelOnFirstSuccess = true,
                TryExtractUnknownFormat = settings.UnknownFormatAction == "TryExtract",
                MaxParallelExtractCount = settings.MaxParallelExtractCount < 1
                    ? 1
                    : settings.MaxParallelExtractCount
            };
        }

        /*
         * 这里**不允许**再出现"覆盖策略 → 7z 参数"的映射（-aos/-aoa/-aou/-aot）。
         *
         * 历史：本类曾经有一个 GetOverwriteArgument()，与 Engines\SevenZip\SevenZipProcessRunner
         * 里的同名映射是同一张表的两次实现（当时两处都没有调用方，属潜伏债）。
         * 7z 参数只允许存在于 Engines\SevenZip\ 内部（AGENTS.md §3.1 四条禁止项①、铁律 2），
         * 所以那个方法已删除，OverwriteMode 只作为"策略名"往上传。
         */

        /// <summary>
        /// 修正非法配置。
        /// </summary>
        public void Normalize()
        {
            CustomOutputDirectory ??= string.Empty;
            GlobalPassword ??= string.Empty;

            if (string.IsNullOrWhiteSpace(OverwriteMode))
            {
                OverwriteMode = "SkipExisting";
            }

            if (MaxParallelExtractCount < 1)
            {
                MaxParallelExtractCount = 1;
            }

            if (MaxParallelExtractCount > 4)
            {
                MaxParallelExtractCount = 4;
            }
        }

        /// <summary>
        /// **只解这些条目**（空 = 全解）。密码预检用它只解"最小的那一个条目"来验密码，
        /// 而不是拿一个可能不对的候选去跑整包（用户 2026-09-25 第 37 条）。
        ///
        /// <para>为什么是可写属性而不是 <c>init</c>：预检要在同一个 <see cref="ExtractOptions"/> 上临时换条目、
        /// 跑完再换回来（它还带着覆盖档等一整套口径，不能为一次预检再造一份）。
        /// 调用点只有 <c>ExtractionCoordinator</c> 的候选循环一处，且那是顺序执行的。</para>
        /// </summary>
        public IReadOnlyList<string> IncludeEntries { get; set; } = Array.Empty<string>();
    }
}
