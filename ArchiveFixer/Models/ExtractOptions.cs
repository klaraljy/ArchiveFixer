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

            if (MaxParallelExtractCount > 8)
            {
                MaxParallelExtractCount = 8;
            }
        }
    }

    /// <summary>
    /// 「一键处理 · 本次选项」的**运行期快照**（规格 <c>docs/输出与整理模型.md</c> §9）。
    ///
    /// <para><b>它是什么</b>：一个**不可变**的值对象，装"这一次一键处理"用哪一档落点 / 终端落法 /
    /// 源包处理。面板问完用户就造一个出来，交给解压协调器用**这一次**，然后就丢掉 ——
    /// 不落盘、不进全局状态、不放进 <see cref="AppSettings"/>。</para>
    ///
    /// <para><b>为什么必须经由它、而不是直接改设置</b>（规格 §9.2 硬要求①/②）：
    /// 不勾"把本次选择存为默认"时，<c>appsettings.json</c> **一个字节都不许改**；
    /// 而"本次落点"又必须仍然由 <see cref="OutputPlacement"/> **唯一实现**算出来 ——
    /// 所以这里只做一件事：把用户选的模式**翻译成 <see cref="ExtractOptions"/> 上那三个既有字段**，
    /// 后面的路径推导一个字都不重复（<c>PathService.BuildOutputPath</c> → <c>OutputPlacement</c>）。
    /// 别在这里拼任何路径。</para>
    ///
    /// <para><b>为什么落在 <c>Models/ExtractOptions.cs</c> 这个文件里</b>：它与
    /// <see cref="ExtractOptions"/> 是同一件事的两半（"这次按什么选项解压"），
    /// 而本次授权只允许新建 <c>Views/</c> 下的窗口文件与 <c>Engines/EngineRouter.cs</c>，
    /// 没有给它单开一个文件的位置。</para>
    /// </summary>
    public sealed class OneClickRunOptions
    {
        /// <summary>本次落点（规格 §1.1，**只剩两档**：以包名命名的子文件夹 / 指定位置 + 同名子文件夹）。</summary>
        public OutputPlacementMode PlacementMode { get; init; } = OutputPlacementMode.PerArchiveSubfolder;

        /// <summary>指定位置（只有 <c>CustomRootPerArchive</c> 用得上；另一档下它是惰性的）。</summary>
        public string CustomRoot { get; init; } = string.Empty;

        /// <summary>终端落法（规格 §3.1 的那个可选项）。</summary>
        public TerminalLayoutMode TerminalLayout { get; init; } = TerminalLayoutMode.KeepLastFolder;

        /// <summary>源包处理档（规格 §3.4 的三档）。</summary>
        public SourceHandlingMode SourceHandling { get; init; } = SourceHandlingMode.MoveToRest;

        /// <summary>面板上勾了「把本次选择存为默认」——**只有它为 true 时才允许写设置文件**。</summary>
        public bool SaveAsDefault { get; init; }

        /// <summary>
        /// 面板上勾了「以后不再询问」。
        ///
        /// ⚠ 它**只对本次运行有效**，不落盘：<see cref="AppSettings"/> 不在本次授权范围内，
        /// 没地方存它；而且"按设置走"本来就是默认行为，所以不存也不丢功能。
        /// 勾上之后，本次运行内再点一键处理不再弹面板。
        /// </summary>
        public bool SuppressPanelNextTime { get; init; }

        /// <summary>
        /// 落点这一项是不是**可用**的：选了"指定位置"却没给路径时不可用。
        ///
        /// <para>
        /// 为什么必须挡：<see cref="OutputPlacement.FromLegacyFlags"/> 对"空根"的口径是
        /// **回落源目录家族**（§1.3 的旧配置迁移规则）—— 也就是说，一个空根的"指定位置"
        /// 会被静默解释成"未指定位置"。用户明确选了另一个位置却写进源目录，
        /// 正是最不该发生的那种"悄悄改了落点"。所以空根时**整条落点不生效**（回落设置值），
        /// 并由调用方写一条 WARN 说明。
        /// </para>
        /// </summary>
        public bool IsPlacementValid =>
            !OutputPlacement.UsesCustomRoot(PlacementMode) || !string.IsNullOrWhiteSpace(CustomRoot);

        /// <summary>
        /// 从设置造一个快照（面板打开时的初值 / 没弹面板时的降级值）。
        ///
        /// ⚠ 只**读**设置，一个字都不写回去。
        /// </summary>
        /// <param name="selectedOutputDirectory">
        /// 主界面"输出目录"那一格的实际值。它通常等于 <see cref="AppSettings.CustomOutputDirectory"/>
        /// （那个 setter 会同步写设置），但用户关掉「记住上次输出目录」时两者可能不同 ——
        /// 传进来时以它为准，因为解压管线用的就是它（<c>ExtractionCoordinator</c> 的
        /// <c>extractOptions.CustomOutputDirectory</c>）。
        /// </param>
        public static OneClickRunOptions FromSettings(
            AppSettings? settings,
            string? selectedOutputDirectory = null)
        {
            settings ??= AppSettings.CreateDefault();
            settings.Normalize();

            string customRoot = !string.IsNullOrWhiteSpace(selectedOutputDirectory)
                ? selectedOutputDirectory!
                : settings.CustomOutputDirectory ?? string.Empty;

            return new OneClickRunOptions
            {
                PlacementMode = OutputPlacement.FromLegacyFlags(
                    settings.ExtractToOriginalDirectory,
                    settings.KeepArchiveNameFolder,
                    customRoot),
                CustomRoot = customRoot,
                TerminalLayout = OutputPlacement.ParseTerminalLayoutMode(settings.TerminalLayoutMode),
                SourceHandling = AppSettings.ParseSourceHandling(settings.SourceHandling)
            };
        }

        /// <summary>
        /// 把本次选项**翻译成解压选项上的既有字段**（唯一入口，规格 §9.2 硬要求①）。
        ///
        /// <para>
        /// 落点那三个字段的翻译只有一种写法：<see cref="OutputPlacement.ToLegacyFlags"/>。
        /// 之后 <c>PathService.BuildOutputPath</c> 会用 <see cref="OutputPlacement.FromLegacyFlags"/>
        /// 反推回模式、再由 <see cref="OutputPlacement.ResolveDestinationDirectory"/> 算路径 ——
        /// 全项目就这一条推导链，面板不新增第二条。
        /// </para>
        /// <para>
        /// 落点无效（<see cref="IsPlacementValid"/> 为 false）时**不碰**那三个字段：
        /// 宁可整条回落设置值，也不把"空根的指定位置"解释成"解压到源目录"。
        /// 终端落法 / 源包处理与落点无关，照常生效。
        /// </para>
        /// </summary>
        public void ApplyTo(ExtractOptions? options)
        {
            if (options == null)
            {
                return;
            }

            if (IsPlacementValid)
            {
                (bool extractToOriginalDirectory, bool keepArchiveNameFolder) =
                    OutputPlacement.ToLegacyFlags(PlacementMode);

                options.ExtractToOriginalDirectory = extractToOriginalDirectory;
                options.KeepArchiveNameFolder = keepArchiveNameFolder;

                // 源目录家族的模式下这个字段是惰性的（FromLegacyFlags 会忽略它），
                // 但仍然写上：万一将来有人只看字段不看模式，看到的是用户真正填的那个根。
                options.CustomOutputDirectory = CustomRoot ?? string.Empty;
            }

            options.Normalize();
        }

        /// <summary>
        /// 一句话说清"这一次按什么在跑"（规格 §9.2 硬要求⑥：日志与任务详情里要能回答
        /// "这次为什么解到这里"）。**不含任何路径推导**，只是把枚举翻译成人话。
        /// </summary>
        public string Describe()
        {
            string placement = IsPlacementValid
                ? DescribePlacement(PlacementMode, CustomRoot)
                : $"{DescribePlacement(PlacementMode, CustomRoot)}（未填指定位置，本次落点按设置值）";

            return $"落点：{placement}；终端落法：{DescribeTerminalLayout(TerminalLayout)}；" +
                   $"源包处理：{DescribeSourceHandling(SourceHandling)}";
        }

        /// <summary>落点模式的中文说明（含一个例子；与设置窗口里那两档的措辞同一口径）。</summary>
        public static string DescribePlacement(OutputPlacementMode mode, string? customRoot)
        {
            return OutputPlacement.NormalizeLegacyMode(mode) switch
            {
                OutputPlacementMode.CustomRootPerArchive =>
                    $"指定位置 + 同名子文件夹（{DescribeRoot(customRoot)}\\222\\内容物；选中文件夹时用该文件夹的名字）",
                _ => "以包名命名的子文件夹（111\\222.rar → 111\\222\\内容物）"
            };
        }

        public static string DescribeTerminalLayout(TerminalLayoutMode mode)
        {
            return mode == TerminalLayoutMode.UseArchiveName
                ? "多文件时用包名当最后一层（少一层点击）"
                : "多文件时保留最后一层文件夹名（默认）";
        }

        public static string DescribeSourceHandling(SourceHandlingMode mode)
        {
            return mode switch
            {
                SourceHandlingMode.KeepInPlace => "留在原地（一个字节都不搬）",
                SourceHandlingMode.DeleteAfterVerify => "校验通过后删除（走既有的清理源包规则）",
                _ => "移入其余物（默认）"
            };
        }

        private static string DescribeRoot(string? customRoot)
        {
            return string.IsNullOrWhiteSpace(customRoot) ? "<未指定>" : customRoot!.Trim();
        }
    }
}
