
namespace ArchiveFixer.Models
{
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
    /// <para><b>它与 <see cref="ExtractOptions"/> 是同一件事的两半</b>（「这次按什么选项解压」）：
    /// 面板问完用户就造一个快照，交给解压协调器用这一次。
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
            !OutputPlacementRules.UsesCustomRoot(PlacementMode) || !string.IsNullOrWhiteSpace(CustomRoot);

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
                // 落点只按用户界面的两个布尔 + 自定义根派生；旧 PlacementMode 不允许再压回来。
                PlacementMode = OutputPlacementRules.FromLegacyFlags(
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
        /// 新写入只写 <see cref="ExtractOptions.PlacementMode"/>；旧两个布尔只留给旧调用方/旧配置读入，
        /// 不再由这里回写。之后 <see cref="OutputPlacement.ResolveDestinationDirectory"/> 仍算唯一落点公式。
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
                options.PlacementMode = PlacementMode;
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
            return OutputPlacementRules.NormalizeLegacyMode(mode) switch
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
                // ⚠ 2026-10-15：源包那一档只剩「留下 / 彻底删除（就地）」——「放入其余物」整条删除。
                SourceHandlingMode.DeleteInPlace => "彻底删除（就地删掉源包，不可恢复；⛔ 不搬进其余物）",
                _ => "留下（默认，原来的位置一个字节都不动）"
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
                // 只作用于失败 / 部分完成时链停下那一层要解的包。
                RestHandlingModes.Delete => "彻底删除最后一层过程物（仅失败 / 部分完成，不可恢复）",
                _ => "留下最后一层过程物（仅失败 / 部分完成，默认）"
            };
        }

        private static string DescribeRoot(string? customRoot)
        {
            return string.IsNullOrWhiteSpace(customRoot) ? "<未指定>" : customRoot!.Trim();
        }
    }
}
