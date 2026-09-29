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

            if (MaxParallelExtractCount > 8)
            {
                MaxParallelExtractCount = 8;
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

        /// <summary>
        /// 源包处理档（2026-09-25 第 32 条起**只剩两档**：留在原地 / 放入其余物当中）。
        ///
        /// <para>默认 = <see cref="SourceHandlingMode.KeepInPlace"/>，与 <see cref="AppSettings.SourceHandling"/>
        /// 的默认档一致（一个什么都不做的兜底，绝不能让"没填这一格"变成搬走用户的源包）。</para>
        /// </summary>
        public SourceHandlingMode SourceHandling { get; init; } = SourceHandlingMode.KeepInPlace;

        /// <summary>
        /// 其余物怎么处理（2026-09-25 第 33 条补进"本次选项"；③页那一栏是同一个词表）。
        ///
        /// <para>取值见 <see cref="RestHandlingModes"/>：<c>Keep</c>（默认，不动其余物）/
        /// <c>RecycleBin</c>（移入回收站，可还原）/ <c>Delete</c>（彻底删除，省空间）。
        /// 默认 = <see cref="RestHandlingModes.Keep"/>，与 <see cref="AppSettings.RestHandlingAfterVerify"/>
        /// 的默认档一致。</para>
        ///
        /// <para>⚠ 用户 2026-09-25 的原话："一键处理的弹窗也是要随着现在的设置进行更新的" ——
        /// 所以"本次选项"必须**完整覆盖**③页那两栏（源包操作 + 删除操作），
        /// 不能只有源包操作、让"其余物到底删不删"在弹窗里无从选择也无从看到。</para>
        /// </summary>
        public string RestHandling { get; init; } = RestHandlingModes.Keep;

        /// <summary>面板上勾了「把本次选择存为默认」——**只有它为 true 时才允许写设置文件**。</summary>
        public bool SaveAsDefault { get; init; }

        /// <summary>
        /// 面板上勾了「以后不再询问」。
        ///
        /// <para>⚠ 2026-09-24 第 17 条之后**它会写进设置**（<see cref="AppSettings.SkipOneClickConfirm"/>）：
        /// 用户原话是「这个可以选中以后不弹出」，而"以后"显然不止这一次运行。设置里留着一个开关
        /// 可以再打开（存了却收不回来的开关等于把用户锁死）。</para>
        ///
        /// <para>勾上之后，本次运行内再点一键处理不再弹确认框（也不弹旧的面板）。</para>
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
                SourceHandling = AppSettings.ParseSourceHandling(settings.SourceHandling),
                RestHandling = RestHandlingModes.Normalize(settings.RestHandlingAfterVerify)
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

            return $"落点：{placement}；" +
                   $"源包处理：{DescribeSourceHandling(SourceHandling)}；" +
                   $"其余物：{DescribeRestHandling(RestHandling)}";
        }

        /// <summary>
        /// 落点模式的中文说明（含一个例子；与②「解压方式」页那两档的措辞同一口径）。
        ///
        /// <para>⚠ 例子里的占位符一律写「包名」，**不许写具体数字**（用户 2026-09-25 第 36 条）：
        /// 这里原来写的是 <c>{指定位置}\222\内容物</c>，而那串字会原样进日志与失败清单的
        /// 「本次选项」—— 他真机上看到 <c>…\BBB\222\内容物</c> 时，会以为程序打算往一个
        /// 他并没有的 <c>222</c> 目录里写东西（他的真实目录叫 <c>BBB\新建文件夹_…</c>）。
        /// 例子要一眼看出是例子。</para>
        /// </summary>
        public static string DescribePlacement(OutputPlacementMode mode, string? customRoot)
        {
            return OutputPlacement.NormalizeLegacyMode(mode) switch
            {
                OutputPlacementMode.CustomRootPerArchive =>
                    $"指定位置 + 同名子文件夹（{DescribeRoot(customRoot)}\\包名\\内容物；选中文件夹时用该文件夹的名字）",
                _ => "以包名命名的子文件夹（111\\222.rar → 111\\222\\内容物）"
            };
        }

        public static string DescribeSourceHandling(SourceHandlingMode mode)
        {
            return mode switch
            {
                SourceHandlingMode.KeepInPlace => "留在原地（默认，一个字节都不搬）",
                _ => "放入其余物当中"
            };
        }

        /// <summary>
        /// 「其余物」那一档的中文说法。
        ///
        /// <para>⚠ 措辞与确认框正文（<c>StatusText.OneClickConfirmRest*</c>）**同一口径**：
        /// 都是"用户会看到的那句话"，只是详情不同 —— 一个给折叠区的单选框用，一个给正文那一行用。</para>
        /// </summary>
        public static string DescribeRestHandling(string? mode)
        {
            return RestHandlingModes.Normalize(mode) switch
            {
                RestHandlingModes.RecycleBin => "移入回收站（可还原）",
                RestHandlingModes.Delete => "彻底删除（直接省空间，不可恢复）",
                _ => "不动其余物（默认）"
            };
        }

        private static string DescribeRoot(string? customRoot)
        {
            return string.IsNullOrWhiteSpace(customRoot) ? "<未指定>" : customRoot!.Trim();
        }
    }

    /// <summary>
    /// 一键处理确认框正文里的那几行事实（用户 2026-09-24 第 17 条）。
    ///
    /// <para><b>为什么要单独一个类型</b>：确认框只负责**显示**，它既不算落点、也不扫无用物 ——
    /// 这两个结论分别来自落点的唯一实现（<c>PathService.ResolveOutputPlacement</c>）与
    /// 无用物的唯一实现（<c>SourceJunkScanner</c>）。把结论装在这里传进去，
    /// 窗口就没有任何"自己再算一遍"的机会（§7：界面文案不许出现第二份判据）。</para>
    ///
    /// <para>四行都是**可空的纯文本**：空 = 那一行不显示（比如没有无用物、也没有缺密码的包时，
    /// 提醒那一段整块收起，而不是显示一句"没有"）。</para>
    /// </summary>
    public sealed class OneClickConfirmFacts
    {
        /// <summary>「内容物会生成在…」——由落点唯一实现按本次快照算出来（含多包时的后缀说明）。</summary>
        public string DestinationEcho { get; init; } = string.Empty;

        /// <summary>「其余物…」——自动彻底删除 / 不自动删除（危险模式是否真的生效）。</summary>
        public string RestEcho { get; init; } = string.Empty;

        /// <summary>「源包…」——移入其余物 / 留在原地 / 校验通过后删除。</summary>
        public string SourceEcho { get; init; } = string.Empty;

        /// <summary>
        /// 「特定解压：&lt;规则名&gt;」——开了特定解压时才非空（用户 2026-09-24："我以为它按默认跑的"
        /// 是他最恨的一件事，所以这一行必须出现在**动手前**的那一个框里）。
        /// </summary>
        public string SpecialExtractionEcho { get; init; } = string.Empty;

        /// <summary>需要时的提醒两行（疑似无用物 / 没有可用密码的包）；空 = 不显示。</summary>
        public string NoticeEcho { get; init; } = string.Empty;

        /// <summary>
        /// 「源包：」那一行**不许**被确认框按折叠区里的单选按钮重算（用户 2026-09-27 真机逮到）。
        ///
        /// <para>为什么要这个开关：确认框打开时会用折叠区那几个单选框的值**重算**「源包：」那一行
        /// （那是"你在本框里改了这一档"的正常反馈）。可「空间不足」模式是**运行期覆盖**，
        /// 折叠区里的单选框仍然显示设置里的值（源包=留在原地）—— 于是在同一个框里就出现了
        /// "源包：留在原地" 与红字"会自动覆盖为「源包 → 放入其余物」"两句互相矛盾的话，
        /// 而那正是用户最不能接受的一种形态。</para>
        ///
        /// <para>为 true 时：正文那一行用调用方算好的值（= 覆盖后的真实行为），
        /// 折叠区里的单选框照旧只反映设置。</para>
        /// </summary>
        public bool SourceEchoLocked { get; init; }

        /// <summary>
        /// 「空间不足」模式那条**红字**（用户 2026-09-27 要求：这个模式会在动手前覆盖源包/其余物两档，
        /// 而且会永久删源包，必须在点「开始处理」之前看见）。空 = 整行不显示（模式没开）。
        ///
        /// <para>它与 <see cref="NoticeEcho"/> 分开：那一段是灰字提醒（"顺便说一声"），
        /// 这一段是红字（"这次会删你的源包"）—— 两种分量不能共用一个控件。</para>
        /// </summary>
        public string SpaceTightEcho { get; init; } = string.Empty;
    }
}
