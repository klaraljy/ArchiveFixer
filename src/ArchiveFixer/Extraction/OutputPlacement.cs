using System;
using System.IO;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 输出的落点模式（规格 <c>docs/输出与整理模型.md</c> §1.1）。**只剩两档**。
    ///
    /// <para>
    /// <b>用户 2026-09-24 第 13 条亲自拍板删掉两档</b>（原话："我们之前不是还有解压到当前目录吗，
    /// 现在那条删掉了，因为我怕如果 222 里面有很多的子文件夹，我们 111 里面就彻底乱掉了"）：
    /// </para>
    /// <list type="bullet">
    /// <item><description>删除 <c>SourceDirectoryFlat</c>（解压到压缩包所在目录 / 摊平到当前目录）→ 迁移到
    /// <see cref="PerArchiveSubfolder"/>；</description></item>
    /// <item><description>删除 <c>CustomRootFlat</c>（直接解到指定目录、不建子文件夹）→ 迁移到
    /// <see cref="CustomRootPerArchive"/>。</description></item>
    /// </list>
    ///
    /// <para>
    /// 剩下的两档 + "选中对象是文件还是文件夹"（<see cref="SourceSelectionKind"/>）一起给出用户要的四条规则：
    /// </para>
    /// <list type="table">
    /// <item><description>文件 <c>111\222.rar</c> + 未指定 → <c>111\222\</c>；</description></item>
    /// <item><description>文件夹 <c>111\222\</c> + 未指定 → 就在 <c>222\</c> 里面，不新建同名层、不往外扔；</description></item>
    /// <item><description>文件 <c>222.rar</c> + 指定 <c>BBB</c> → <c>BBB\222\</c>（名字 = 去掉后缀的包名）；</description></item>
    /// <item><description>文件夹 <c>222\</c> + 指定 <c>BBB</c> → <c>BBB\222\</c>（名字 = 选中的文件夹名）。</description></item>
    /// </list>
    ///
    /// <para>
    /// ⚠ 枚举值**刻意保留 0 与 2**（不重新编号成 0/1）：旧版本、旧配置、序列化过的整数里
    /// 1 = 摊平到当前目录、3 = 直接解到指定目录 —— 编号一变，那些值会**静默**对应到别的落点。
    /// 迁移由 <see cref="OutputPlacement.NormalizeLegacyMode"/> 负责。
    /// </para>
    /// </summary>
    public enum OutputPlacementMode
    {
        /// <summary>同名子文件夹（默认，未指定位置那一档）：<c>111\222.rar</c> → <c>111\222\内容物</c>。</summary>
        PerArchiveSubfolder = 0,

        /// <summary>指定位置 + 同名子文件夹：<c>&lt;指定位置&gt;\222\内容物</c>。</summary>
        CustomRootPerArchive = 2
    }

    /// <summary>
    /// 这次导入时用户选中的是**文件**还是**文件夹**（用户 2026-09-24 第 13 条四条规则的第 1 个维度）。
    ///
    /// <para>
    /// 为什么必须显式记在任务上：用户在"添加文件"里选的是一个个包，在"添加文件夹"里选的是**一个容器**。
    /// 后者指定位置时，落点用的是**那个文件夹的名字**（<c>BBB\222\</c>），而不是里面每个包各自的名字；
    /// 未指定位置时则要求产物全部留在所选文件夹**里面**。这两件事光看任务自己的路径是推不出来的
    /// （任务只是文件夹里的某一个包），所以导入那一刻就把这个事实记下来。
    /// </para>
    /// </summary>
    public enum SourceSelectionKind
    {
        /// <summary>用户直接选中的是文件（<c>添加文件…</c> / 拖放单个包）。</summary>
        File = 0,

        /// <summary>用户选中的是文件夹（<c>添加文件夹…</c>），任务来自这个文件夹里的包。</summary>
        Folder = 1
    }

    /// <summary>
    /// 终端落法（规格 §3.1 的可选项）：内容物最里面那一层文件夹叫什么。
    ///
    /// <para>
    /// <see cref="KeepLastFolder"/>（默认）保留归档内部最后一层文件夹名（<c>111\222\666\…</c>）；
    /// <see cref="UseArchiveName"/> 改用归档基名（<c>111\222\…</c>，"少一层点击"）。
    /// </para>
    /// <para>
    /// ⚠️ 本选项只管**套出来的那一层文件夹叫什么**。判定表情形 1（终端就是单个文件）本来就没有
    /// 文件夹层，两种取值结果相同 —— 见 <see cref="ResultFinalizer"/>。
    /// </para>
    /// </summary>
    public enum TerminalLayoutMode
    {
        /// <summary>保留归档内最后一层文件夹名（默认，更保守，不丢信息）。</summary>
        KeepLastFolder = 0,

        /// <summary>用归档基名当最后那一层文件夹名。</summary>
        UseArchiveName = 1
    }

    /// <summary>
    /// 落点解析的失败原因。**机器可判**（不要去比中文文案）。
    /// </summary>
    public enum OutputPlacementError
    {
        None = 0,

        /// <summary>源包路径为空。</summary>
        EmptySourcePath,

        /// <summary>源包路径里没有目录部分（例如只给了个文件名），推不出"当前目录"。</summary>
        SourceDirectoryUnavailable,

        /// <summary>自定义根为空。</summary>
        EmptyCustomRoot,

        /// <summary>自定义根是相对路径（<c>out\sub</c>）—— 会落到进程当前目录，用户完全没指定过那里。</summary>
        CustomRootNotAbsolute,

        /// <summary>自定义根就是盘符根本身（<c>D:\</c>）—— 等于把内容物直接摊在盘根上。</summary>
        CustomRootIsRootDirectory,

        /// <summary>自定义根的盘符不存在（U 盘拔了 / 盘符写错）。</summary>
        CustomRootDriveMissing,

        /// <summary>路径拼接失败（名字里带 Path.Combine 处理不了的字符）。</summary>
        InvalidDestinationPath
    }

    /// <summary>
    /// 落点解析结果。失败时 <see cref="DestinationDirectory"/> 为空，**绝不静默回退到别的目录**。
    /// </summary>
    public sealed class OutputPlacementResult
    {
        /// <summary>是否解析成功。</summary>
        public bool Success { get; init; }

        /// <summary>失败原因（成功时为 <see cref="OutputPlacementError.None"/>）。</summary>
        public OutputPlacementError Error { get; init; }

        /// <summary>给用户看的一句话（失败原因 / 落点说明）。</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>目标根：未指定位置那一档是源包所在目录，指定位置那一档是自定义根。</summary>
        public string DestinationRoot { get; init; } = string.Empty;

        /// <summary>最终落点目录（规格 §1.1 的 <c>destDir</c>）。</summary>
        public string DestinationDirectory { get; init; } = string.Empty;

        /// <summary>包基名（已清洗，可直接当文件夹名）。</summary>
        public string ArchiveBaseName { get; init; } = string.Empty;

        /// <summary>最终落点那一层文件夹的名字（就是上面那条规则算出来的"包名"）。</summary>
        public string PackageFolderName { get; init; } = string.Empty;

        /// <summary>
        /// 落点那一层用的是**用户选中的文件夹名**（而不是包基名）—— 四条规则里的"文件夹 + 指定位置"。
        /// </summary>
        public bool UsesSelectedFolderName { get; init; }

        /// <summary>
        /// 这个落点目录是**同一次导入里的多个包共用**的（"文件夹 + 指定位置"那一档：
        /// 文件夹里每个包都落进同一个 <c>BBB\222\</c>）。
        ///
        /// <para>
        /// 调用方靠它决定两件事（都只有这一处判据，别在管线里再推一遍）：
        /// ① 别的包刚往这个目录里定稿过，**不能**因为"目录已存在且非空"就改名成 <c>222(1)</c>
        ///    —— 那会把用户要的"都放进 BBB\222\"当场拆开；
        /// ② 定稿时其余物要按包基名再分一层（决策 D-10 的原判据是"多个包共用根"）。
        /// </para>
        /// </summary>
        public bool SharesDestinationWithOtherPackages { get; init; }

        /// <summary>是否命中了场景 B 的"重复一层塌缩"（规格 §3.3）。</summary>
        public bool CollapsedRepeatedFolderLayer { get; init; }
    }

    /// <summary>
    /// 输出落点解析（规格 §1，**唯一实现处**；不变量 §6.6：禁止在别处再拼一遍输出路径）。
    ///
    /// <para>
    /// 用户 2026-09-24 第 13 条之后只剩**四条规则**（<c>选文件/选文件夹</c> × <c>未指定/指定位置</c>），
    /// 全部由 <see cref="ResolveDestinationDirectory"/> 一处实现；界面上再也选不到"摊平"那两档。
    /// </para>
    ///
    /// <para>
    /// 三条设计约束：
    /// ① **纯函数**：输入输出都是字符串/枚举，不建目录、不写文件、不排除列目录；
    /// ② **失败要显式**：自定义根为空/相对路径/盘符不存在/是盘根本身时返回带原因的失败结果，
    ///    不抛异常，也不"悄悄换一个目录"—— 静默落到别处会让用户的东西出现在他从没指定的地方；
    /// ③ **名字清洗只有一处**：一律走既有的 <see cref="FileNameHelper"/>（Windows 保留名、结尾空格/点、非法字符），
    ///    不在这里另写一套。
    /// </para>
    /// </summary>
    public static class OutputPlacement
    {
        /// <summary>终端落法的设置默认值（<see cref="AppSettings.TerminalLayoutMode"/> 的默认字符串）。</summary>
        public const string DefaultTerminalLayoutSetting = "KeepLastFolder";

        /// <summary>
        /// 设置里的终端落法字符串 → 枚举（**唯一解析处**）。
        ///
        /// 为什么解析要放这里而不是 ViewModel / 设置层各写一份：落点解析是本类的事，
        /// "什么算合法的终端落法"只应该有一个答案。设置加载时（<c>AppSettings.Normalize</c>）
        /// 与解压时（<c>ExtractionCoordinator</c>）都调它，两处口径不会分叉。
        ///
        /// 容错规则：空 / 非法 / 大小写不符 / 旧配置缺字段（反序列化后为 null）一律回落
        /// <see cref="TerminalLayoutMode.KeepLastFolder"/> —— 这是"最不意外"的那一档（规格 §4），
        /// 绝不因为一个读不懂的字符串就把落点算成别的东西。
        /// </summary>
        public static TerminalLayoutMode ParseTerminalLayoutMode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return TerminalLayoutMode.KeepLastFolder;
            }

            return Enum.TryParse(value.Trim(), ignoreCase: true, out TerminalLayoutMode parsed)
                   && Enum.IsDefined(parsed)
                ? parsed
                : TerminalLayoutMode.KeepLastFolder;
        }

        /// <summary>枚举 → 设置字符串（与 <see cref="ParseTerminalLayoutMode"/> 严格互逆）。</summary>
        public static string ToSettingValue(TerminalLayoutMode mode)
        {
            return mode.ToString();
        }

        /// <summary>
        /// 落点是不是**源包自己所在的那个目录**。
        ///
        /// <para>
        /// 现在只剩一种情形会成立：场景 B 塌缩之后（<c>111\222\名字\名字.rar</c> → 落点 <c>111\222\名字</c>）。
        /// 旧版本还有"解压到压缩包所在目录"那一档会走这里，但它已被用户 2026-09-24 第 13 条删除。
        /// </para>
        /// <para>
        /// 为什么需要这个判断：解压管线有一条"输出目录已存在且非空 → 自动改名成 <c>xxx(1)</c>"的规则
        /// （避免把产物倒进一个已有内容的目录）。而源包所在目录**必然非空** —— 源包自己就躺在里面。
        /// 不留这个例外，用户选的"就地整理"和场景 B 的塌缩会当场被抵消，
        /// 产物落到旁边的 <c>名字(1)\</c>，正是要根治的"凭空多一层"。
        /// </para>
        /// </summary>
        public static bool LandsInSourceDirectory(string? sourceArchivePath, string? destinationDirectory)
        {
            if (string.IsNullOrWhiteSpace(sourceArchivePath) || string.IsNullOrWhiteSpace(destinationDirectory))
            {
                return false;
            }

            string sourceDirectory = FileNameHelper.GetDirectoryName(GetFullPathSafe(sourceArchivePath));

            return !string.IsNullOrWhiteSpace(sourceDirectory)
                   && SafePathHelper.PathEquals(sourceDirectory, destinationDirectory);
        }

        /// <summary><c>Path.GetFullPath</c> 的安全版（取不到就返回空串，绝不抛）。</summary>
        private static string GetFullPathSafe(string? path)
        {
            return SafePathHelper.GetFullPathSafe(path);
        }

        /// <summary>
        /// 是不是"以自定义根为目标"的模式。
        ///
        /// ⚠ 先过一遍 <see cref="NormalizeLegacyMode"/>：被删掉的两档（旧编号 1/3）也可能被传进来，
        /// 不能因为"读到的是旧值"就把它算成源目录那一档。
        /// </summary>
        public static bool UsesCustomRoot(OutputPlacementMode mode)
        {
            return NormalizeLegacyMode(mode) == OutputPlacementMode.CustomRootPerArchive;
        }

        /// <summary>
        /// 旧枚举值 / 旧枚举名 → 现在这两档（**唯一迁移处**，用户 2026-09-24 第 13 条删掉两档之后）。
        ///
        /// <para>
        /// 为什么要有它：删掉的两档在旧版本里是 1（摊平到当前目录）与 3（直接解到指定目录），
        /// 枚举名是 <c>SourceDirectoryFlat</c> / <c>CustomRootFlat</c>。任何一个还留着旧值的配置、
        /// 缓存体或手写的 json 读进来时**不许炸、也不许落到一个没人想过的目录**：
        /// </para>
        /// <list type="bullet">
        /// <item><description><c>SourceDirectoryFlat</c> / <c>1</c> → <see cref="OutputPlacementMode.PerArchiveSubfolder"/>
        /// （摊平会让源目录乱掉，正是用户删它的理由）；</description></item>
        /// <item><description><c>CustomRootFlat</c> / <c>3</c> → <see cref="OutputPlacementMode.CustomRootPerArchive"/>
        /// （指定位置保留，但必须建同名子文件夹）；</description></item>
        /// <item><description>读不懂的值 → 默认档。</description></item>
        /// </list>
        /// </summary>
        public static OutputPlacementMode NormalizeLegacyMode(OutputPlacementMode mode)
        {
            return mode switch
            {
                OutputPlacementMode.PerArchiveSubfolder => OutputPlacementMode.PerArchiveSubfolder,
                OutputPlacementMode.CustomRootPerArchive => OutputPlacementMode.CustomRootPerArchive,

                // 旧值 1 = SourceDirectoryFlat（已删除）→ 回到"建同名子文件夹"。
                (OutputPlacementMode)1 => OutputPlacementMode.PerArchiveSubfolder,

                // 旧值 3 = CustomRootFlat（已删除）→ 保留"指定位置"，但补上同名子文件夹。
                (OutputPlacementMode)3 => OutputPlacementMode.CustomRootPerArchive,

                _ => OutputPlacementMode.PerArchiveSubfolder
            };
        }

        /// <summary>
        /// 旧枚举名（字符串）→ 现在这两档。空 / 非法 / 认不出的名字一律回落默认档，**不抛异常**。
        /// </summary>
        public static OutputPlacementMode ParsePlacementMode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return OutputPlacementMode.PerArchiveSubfolder;
            }

            string trimmed = value.Trim();

            // 认不出名字时**先按旧名字迁移**，再看是不是新名字：旧配置里存的正是那两个名字。
            if (string.Equals(trimmed, "SourceDirectoryFlat", StringComparison.OrdinalIgnoreCase))
            {
                return OutputPlacementMode.PerArchiveSubfolder;
            }

            if (string.Equals(trimmed, "CustomRootFlat", StringComparison.OrdinalIgnoreCase))
            {
                return OutputPlacementMode.CustomRootPerArchive;
            }

            if (Enum.TryParse(trimmed, ignoreCase: true, out OutputPlacementMode parsed) && Enum.IsDefined(parsed))
            {
                return NormalizeLegacyMode(parsed);
            }

            // 纯数字（旧版本可能把它当整数存过）：按旧编号迁移。
            if (int.TryParse(trimmed, out int numeric))
            {
                return NormalizeLegacyMode((OutputPlacementMode)numeric);
            }

            return OutputPlacementMode.PerArchiveSubfolder;
        }

        /// <summary>
        /// 枚举 → 旧的"两个布尔"（<c>ExtractToOriginalDirectory</c> + <c>KeepArchiveNameFolder</c>），
        /// 用于把旧调用点（<c>PathService.BuildOutputPath</c> 只有这两个布尔）平滑接到本实现上。
        ///
        /// <code>
        /// PerArchiveSubfolder  → (true,  true )
        /// CustomRootPerArchive → (false, true )
        /// </code>
        ///
        /// ⚠ 第二个布尔**永远是 true**（用户 2026-09-24 第 13 条删掉了两档"不建子文件夹"的落点）。
        /// 它仍然写出来，是为了让设置文件保持"旧版本也读得懂"的形状（回退旧版本不会炸）。
        /// </summary>
        public static (bool ExtractToOriginalDirectory, bool KeepArchiveNameFolder) ToLegacyFlags(OutputPlacementMode mode)
        {
            // 先迁移：旧值 1/3 进来时也要得到"保留的那一档"对应的布尔，别把 3（指定位置）算成源目录。
            return (!UsesCustomRoot(NormalizeLegacyMode(mode)), true);
        }

        /// <summary>
        /// 旧的两个布尔 + 自定义根 → 新枚举（**旧配置迁移的唯一实现处**，规格 §1.3）。
        ///
        /// <para>
        /// 用户 2026-09-24 第 13 条删掉"摊平"两档之后，这里只剩一个判断题：
        /// **"是不是指定了位置"**（旧代码里就是 <c>ExtractToOriginalDirectory == false</c> + 自定义根非空）。
        /// </para>
        /// <list type="table">
        /// <item><description><c>(true, true)</c> → <see cref="OutputPlacementMode.PerArchiveSubfolder"/>；</description></item>
        /// <item><description><c>(true, false)</c>（旧的"摊平到当前目录"）→ <see cref="OutputPlacementMode.PerArchiveSubfolder"/>；</description></item>
        /// <item><description><c>(false, true, 根)</c> → <see cref="OutputPlacementMode.CustomRootPerArchive"/>；</description></item>
        /// <item><description><c>(false, false, 根)</c>（旧的"直接解到指定目录"）→ <see cref="OutputPlacementMode.CustomRootPerArchive"/>；</description></item>
        /// <item><description>空根 → 源目录家族（<see cref="OutputPlacementMode.PerArchiveSubfolder"/>）——
        /// 旧代码在这种情形会落到**程序目录**（<c>PathService.BuildOutputPath</c> 的 <c>?? AppBaseDirectory</c>），
        /// 那是"用户从来没指定过"的兜底，不是一种落点，绝不沿用。</description></item>
        /// </list>
        ///
        /// <para>
        /// <paramref name="keepArchiveNameFolder"/> 已经**不参与判断**（两档都没了），
        /// 参数保留只为不改旧调用点的签名 —— 旧配置读进来照样得出新档，绝不抛异常。
        /// </para>
        /// </summary>
        public static OutputPlacementMode FromLegacyFlags(
            bool extractToOriginalDirectory,
            bool keepArchiveNameFolder,
            string? customRoot = null)
        {
            _ = keepArchiveNameFolder;

            bool hasCustomRoot = !string.IsNullOrWhiteSpace(customRoot);

            return !extractToOriginalDirectory && hasCustomRoot
                ? OutputPlacementMode.CustomRootPerArchive
                : OutputPlacementMode.PerArchiveSubfolder;
        }

        /// <summary>
        /// 解析目标根（规格 §1.2 的 <c>destRoot</c>）。
        ///
        /// 返回的结果里只填 <see cref="OutputPlacementResult.DestinationRoot"/>；
        /// 失败原因同样有效（自定义根的四类非法写法都在这里拦下）。
        /// </summary>
        /// <param name="sourceArchivePath">源包路径（可以只是文件名，但那样推不出"当前目录"）。</param>
        /// <param name="mode">落点模式。</param>
        /// <param name="customRoot">自定义根（仅自定义模式用得上）。</param>
        /// <param name="driveExists">
        /// 盘符存在性探针，收到的是盘根（如 <c>D:\</c>）。
        /// 默认走真实查询 —— 这是本类唯一一处碰环境的地方，抽成参数是为了让测试不依赖本机盘符。
        /// </param>
        public static OutputPlacementResult ResolveDestinationRoot(
            string? sourceArchivePath,
            OutputPlacementMode mode,
            string? customRoot = null,
            Func<string, bool>? driveExists = null)
        {
            if (string.IsNullOrWhiteSpace(sourceArchivePath))
            {
                return Failure(OutputPlacementError.EmptySourcePath, "源包路径为空，无法判断输出落点");
            }

            if (!UsesCustomRoot(mode))
            {
                string sourceDirectory = FileNameHelper.GetDirectoryName(sourceArchivePath);

                if (string.IsNullOrWhiteSpace(sourceDirectory))
                {
                    return Failure(
                        OutputPlacementError.SourceDirectoryUnavailable,
                        "源包路径里没有目录部分，无法判断\"当前目录\"");
                }

                return new OutputPlacementResult
                {
                    Success = true,
                    DestinationRoot = sourceDirectory,
                    Message = "目标根：" + sourceDirectory
                };
            }

            return ResolveCustomRoot(customRoot, driveExists);
        }

        /// <summary>
        /// 解析最终落点 <c>destDir</c>（规格 §1.1 的四条规则 + §3.3 场景 B 塌缩）。
        ///
        /// <code>
        /// destRoot = 未指定位置 ? dir(源包) : 指定位置
        /// 包名     = 指定位置 且 选中对象是文件夹 ? SafeName(选中的文件夹名)
        ///            : SafeName(包基名)          // 分卷组 = 整组基名；伪装后缀/内嵌归档都剥对
        /// destDir  = destRoot \ 包名              // 两档都建这一层（"摊平"两档已被用户删除）
        /// </code>
        ///
        /// <para>
        /// <b>未指定位置 + 选中文件夹</b>那一条要特别看：这时 <c>destRoot</c> 是**源包自己的目录**
        /// （它在所选文件夹里面），包名是**各个包自己的基名** —— 于是产物落在 <c>111\222\a\</c>，
        /// 既没有凭空多出一层 <c>111\222\222\</c>（"不新建"），也没有把内容摊进 <c>222\</c> 或扔到 <c>111\</c>
        /// （用户原话："我们就在 222 文件夹里面操作，不将东西往外面扔"）。
        /// 所选文件夹里只有与它同名的那个包时（<c>111\222\222.7z.001</c>），下面的塌缩规则会让产物
        /// 直接落进 <c>111\222\</c> 本身。
        /// </para>
        /// </summary>
        /// <param name="sourceArchivePath">源包路径。</param>
        /// <param name="mode">落点模式（只剩两档）。</param>
        /// <param name="customRoot">自定义根（仅 <see cref="OutputPlacementMode.CustomRootPerArchive"/> 用得上）。</param>
        /// <param name="collapseRepeatedFolderLayer">
        /// 场景 B 开关（规格 §3.3，默认开）："包名 == 其所在目录名"且该目录下只有这一个包时，
        /// 塌缩掉重复的一层，产物直接落在该包自己的目录里（<c>111\222\名字\内容物</c>）。
        /// 只在未指定位置那一档有意义 —— 指定位置时"所在目录名"根本不参与运算。
        /// </param>
        /// <param name="sourceDirectoryContainsOnlyThisArchive">
        /// "这个目录下只有这一个包"这个事实**由调用方告知**：
        /// 本类是纯函数，不去扫目录。传 false 就等于关掉塌缩（宁可多一层，也不把多个包的产物混到一个目录里）。
        /// </param>
        /// <param name="volumeGroupBaseName">
        /// 分卷组基名（<see cref="Detection.VolumeGroupDetector"/> 的结果，如 <c>222.7z</c>）。
        /// 给了就用它替代"从文件名剥分卷标记"这一步 —— 上游已经算过就别再算一遍。
        /// </param>
        /// <param name="selectionKind">
        /// 这次导入用户选中的是文件还是文件夹（四条规则的第 1 个维度）。默认"文件"= 最保守的那一档。
        /// </param>
        /// <param name="selectionRoot">
        /// 用户选中的那个根（文件夹选择时是文件夹全路径）。空的文件夹根**不参与运算**，
        /// 免得把"某次没记全的导入"当成"用户选了一个叫空字符串的文件夹"。
        /// </param>
        /// <param name="driveExists">盘符存在性探针，见 <see cref="ResolveDestinationRoot"/>。</param>
        public static OutputPlacementResult ResolveDestinationDirectory(
            string? sourceArchivePath,
            OutputPlacementMode mode,
            string? customRoot = null,
            bool collapseRepeatedFolderLayer = true,
            bool sourceDirectoryContainsOnlyThisArchive = false,
            string? volumeGroupBaseName = null,
            Func<string, bool>? driveExists = null,
            SourceSelectionKind selectionKind = SourceSelectionKind.File,
            string? selectionRoot = null)
        {
            mode = NormalizeLegacyMode(mode);

            OutputPlacementResult root = ResolveDestinationRoot(sourceArchivePath, mode, customRoot, driveExists);

            if (!root.Success)
            {
                return root;
            }

            string rawBaseName = ComputeRawBaseName(sourceArchivePath, volumeGroupBaseName);
            string safeBaseName = FileNameHelper.SanitizeFileName(rawBaseName);

            /*
             * "指定位置 + 添加文件夹"：落点那一层用**选中的文件夹名**（用户原话：
             * "我们就在 BBB 里面创建一个和我们选中文件夹名字相同的子文件夹，然后再里面操作"）。
             *
             * 文件夹名取不到（没记下根 / 根是空的）时回落到包基名 —— 宁可多一层"包名"目录，
             * 也绝不把产物摊进指定位置的根上（那正是被删掉的那一档）。
             */
            string selectedFolderName = ResolveSelectedFolderName(selectionKind, selectionRoot);
            bool useSelectedFolderName = UsesCustomRoot(mode) && selectedFolderName.Length > 0;
            string packageName = useSelectedFolderName ? selectedFolderName : safeBaseName;

            if (packageName.Length == 0)
            {
                return Failure(OutputPlacementError.InvalidDestinationPath, "目标目录路径拼不出来：" + root.DestinationRoot);
            }

            string perArchiveDirectory = SafeCombine(root.DestinationRoot, packageName);

            if (perArchiveDirectory.Length == 0)
            {
                return Failure(OutputPlacementError.InvalidDestinationPath, "目标目录路径拼不出来：" + root.DestinationRoot);
            }

            /*
             * 场景 B：111\222\名字\名字.rar。
             *
             * 不塌缩时 destDir = dir(源包) + 包名 = 111\222\名字\名字，比用户期望的
             * 111\222\名字\内容物 多出重复的一层。判据是"包名 == 它所在目录名"，
             * 但**还要**目录下只有这一个包 —— 否则同一个目录里两个包的产物会并在一起，
             * 用户再也分不清哪份内容来自哪个包（所以这一条由调用方告知，猜不得）。
             *
             * 塌缩就是把刚算出来的那一层去掉，即 destDir 回到"源包自己的目录"。
             * ⚠ 用 rawBaseName 比，不拿 selectedFolderName 比：塌缩判的是"包自己的名字"，
             * 而"添加文件夹 + 指定位置"那种落点跟源包所在目录名没有关系。
             */
            if (collapseRepeatedFolderLayer
                && mode == OutputPlacementMode.PerArchiveSubfolder
                && sourceDirectoryContainsOnlyThisArchive)
            {
                string sourceDirectory = FileNameHelper.GetDirectoryName(sourceArchivePath);
                string sourceDirectoryName = FileNameHelper.GetFileName(sourceDirectory);

                bool sameName = !string.IsNullOrWhiteSpace(sourceDirectoryName)
                                && (string.Equals(sourceDirectoryName, rawBaseName, StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(sourceDirectoryName, safeBaseName, StringComparison.OrdinalIgnoreCase));

                if (sameName)
                {
                    string collapsed = TryGetParentDirectory(perArchiveDirectory);

                    // 盘根不安全：把内容物直接摊在 D:\ 上比多一层糟得多，宁可不动。
                    if (collapsed.Length > 0 && !IsPathRoot(collapsed))
                    {
                        return new OutputPlacementResult
                        {
                            Success = true,
                            DestinationRoot = root.DestinationRoot,
                            DestinationDirectory = collapsed,
                            ArchiveBaseName = safeBaseName,
                            PackageFolderName = packageName,
                            CollapsedRepeatedFolderLayer = true,
                            Message = $"包名与目录同名，已塌缩重复的一层，落点：{collapsed}"
                        };
                    }
                }
            }

            return new OutputPlacementResult
            {
                Success = true,
                DestinationRoot = root.DestinationRoot,
                DestinationDirectory = perArchiveDirectory,
                ArchiveBaseName = safeBaseName,
                PackageFolderName = packageName,
                UsesSelectedFolderName = useSelectedFolderName,

                /*
                 * "多个包共用这个落点"只在**指定位置 + 添加文件夹**时成立：
                 * 那个文件夹里可能有好几个包，它们都落进同一个 <c>BBB\222\</c>。
                 * 其余三档的落点每包一个目录（同名包撞车由既有冲突档处理），不是"共用根"。
                 */
                SharesDestinationWithOtherPackages = useSelectedFolderName,

                Message = "落点：" + perArchiveDirectory
            };
        }

        /// <summary>
        /// 包基名：<c>222.rar</c> / <c>222.7z.001</c> / <c>222.part1.rar</c> / <c>222.z01</c> → <c>222</c>。
        ///
        /// <para>
        /// 为什么不能直接用 <see cref="FileNameHelper.GetArchiveBaseName"/>：
        /// 它只剥一层后缀，<c>222.7z.001</c> 会得到 <c>222.7z</c> —— 于是落点变成 <c>111\222.7z\</c>，
        /// 分卷组里每一卷还会各建一个目录。
        /// </para>
        /// <para>
        /// 为什么也不能用 <see cref="Detection.VolumeGroupDetector"/> 的 <c>BaseName</c> 直接当结果：
        /// 那个基名是"分卷组"的基名（<c>222.7z.001</c> → <c>222.7z</c>，它保留了内层格式段，这是它该做的），
        /// 还要再剥掉归档后缀才是包基名。给了 <paramref name="volumeGroupBaseName"/> 就省掉剥分卷标记那一步。
        /// </para>
        /// <para>返回值已经清洗过，可以直接当文件夹名。</para>
        /// </summary>
        public static string ResolveArchiveBaseName(string? sourceArchivePath, string? volumeGroupBaseName = null)
        {
            return FileNameHelper.SanitizeFileName(ComputeRawBaseName(sourceArchivePath, volumeGroupBaseName));
        }

        /// <summary>
        /// "指定位置 + 添加文件夹"那一档落点那一层的名字：**用户选中的那个文件夹的名字**（已清洗）。
        ///
        /// <para>
        /// 拿不到就返回空串（调用方回落到包基名）：这是"没记下这次导入的根"这种缺信息的情形，
        /// 空串绝不能变成"一个叫空字符串的文件夹"或者"直接摊在根上"。
        /// 名字清洗照样走既有的 <see cref="FileNameHelper.SanitizeFileName"/>（唯一实现，不新写一套）。
        /// </para>
        /// </summary>
        public static string ResolveSelectedFolderName(SourceSelectionKind selectionKind, string? selectionRoot)
        {
            if (selectionKind != SourceSelectionKind.Folder || string.IsNullOrWhiteSpace(selectionRoot))
            {
                return string.Empty;
            }

            string full = SafePathHelper.GetFullPathSafe(selectionRoot);

            if (string.IsNullOrWhiteSpace(full))
            {
                return string.Empty;
            }

            // 用户可能选了 D:\ 这种盘根："末段名"取不到，这时同样回落到包基名。
            return FileNameHelper.SanitizeFileName(FileNameHelper.GetFileName(full));
        }

        /// <summary>清洗前的包基名（场景 B 的"包基名 == 目录名"要用原样名字比，不能拿清洗后的比）。</summary>
        private static string ComputeRawBaseName(string? sourceArchivePath, string? volumeGroupBaseName)
        {
            string fileName = !string.IsNullOrWhiteSpace(volumeGroupBaseName)
                ? FileNameHelper.GetFileName(volumeGroupBaseName)
                : FileNameHelper.GetFileName(sourceArchivePath);

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return string.Empty;
            }

            /*
             * 分卷标记这一层**转调 FileNameHelper.StripVolumeMarkers**（体检报告 §2 第 2 条）：
             * 这里原来有一份与它逐字相同的 40 行副本。两份必须永远剥得一样 ——
             * 归档基名与包基名对同一个包算出不同的名字，落点就会指到两个不同的目录。
             */
            return StripArchiveExtensions(FileNameHelper.StripVolumeMarkers(fileName));
        }

        /// <summary>
        /// 剥掉末尾的归档后缀（<c>.rar</c> / <c>.7z</c> / <c>.zip</c> / <c>.tar.gz</c> …），最多 3 段。
        ///
        /// 两条经验规则：
        /// ① 只剥**已知归档后缀**。用 <c>Path.GetFileNameWithoutExtension</c> 无脑剥会把
        ///    <c>movie.2024</c> 变成 <c>movie</c> —— 数字结尾的名字太常见了；
        /// ② 若第一段剥掉的是**伪装后缀**（<c>222.rar.jpg</c>），允许继续剥下一段，
        ///    但只在"还没剥到归档后缀"时允许一次，避免把 <c>movie.mkv.rar</c> 的名字特征也啃掉。
        /// </summary>
        private static string StripArchiveExtensions(string name)
        {
            string current = name;
            bool strippedArchiveExtension = false;

            for (int guard = 0; guard < 3; guard++)
            {
                int lastDot = current.LastIndexOf('.');

                if (lastDot <= 0)
                {
                    break;
                }

                string extension = "." + current[(lastDot + 1)..];

                if (ExtensionHelper.IsKnownArchiveExtension(extension))
                {
                    current = current[..lastDot];
                    strippedArchiveExtension = true;
                    continue;
                }

                if (!strippedArchiveExtension && ExtensionHelper.IsSuspiciousFakeExtension(extension))
                {
                    current = current[..lastDot];
                    continue;
                }

                break;
            }

            return current;
        }

        /// <summary>校验自定义根：空 / 相对路径 / 盘根本身 / 盘符不存在，四类都返回明确原因。</summary>
        private static OutputPlacementResult ResolveCustomRoot(string? customRoot, Func<string, bool>? driveExists)
        {
            if (string.IsNullOrWhiteSpace(customRoot))
            {
                return Failure(OutputPlacementError.EmptyCustomRoot, "自定义输出位置为空");
            }

            string trimmed = customRoot.Trim();

            if (!Path.IsPathRooted(trimmed))
            {
                return Failure(
                    OutputPlacementError.CustomRootNotAbsolute,
                    $"自定义输出位置必须是绝对路径：{trimmed}");
            }

            string full = SafePathHelper.GetFullPathSafe(trimmed);
            string pathRoot = SafeGetPathRoot(full);

            if (pathRoot.Length == 0)
            {
                return Failure(
                    OutputPlacementError.CustomRootNotAbsolute,
                    $"自定义输出位置认不出盘符：{trimmed}");
            }

            // 顺序要紧：盘根本身的判据先于盘符存在性 —— 否则 "Z:\" 会被报成"盘符不存在"，
            // 用户照着改还是错（真正的问题是"不许直接摊在盘根上"）。
            if (IsPathRoot(full) || IsBareDriveSpec(trimmed, pathRoot))
            {
                return Failure(
                    OutputPlacementError.CustomRootIsRootDirectory,
                    $"自定义输出位置不能是盘根本身：{trimmed}");
            }

            Func<string, bool> probe = driveExists ?? DefaultDriveExists;

            if (!probe(pathRoot))
            {
                return Failure(
                    OutputPlacementError.CustomRootDriveMissing,
                    $"自定义输出位置的盘符不存在：{pathRoot}");
            }

            return new OutputPlacementResult
            {
                Success = true,
                DestinationRoot = full,
                Message = "目标根：" + full
            };
        }

        /// <summary>默认探针：盘根目录在不在。只在这里碰一次环境，且失败一律当"不存在"。</summary>
        private static bool DefaultDriveExists(string pathRoot)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(pathRoot) && Directory.Exists(pathRoot);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>路径是不是盘根 / UNC 根的本身（<c>D:\</c>、<c>\\server\share</c>）。</summary>
        private static bool IsPathRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string normalized = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string root = SafeGetPathRoot(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return root.Length > 0 && string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// <c>D:</c> 这种"只有盘符"的写法：<see cref="Path.IsPathRooted"/> 认它，但它是**盘当前目录**，
        /// 会随进程当前目录漂移 —— 当成"盘根本身"一样拒掉最安全。
        /// </summary>
        private static bool IsBareDriveSpec(string trimmed, string pathRoot)
        {
            return trimmed.Length == 2
                   && trimmed[1] == ':'
                   && pathRoot.Length >= 2
                   && char.ToUpperInvariant(trimmed[0]) == char.ToUpperInvariant(pathRoot[0]);
        }

        private static string SafeGetPathRoot(string path)
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

        /// <summary>取父目录；取不出来（已在根上）返回空串。</summary>
        private static string TryGetParentDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                       ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string SafeCombine(string directory, string name)
        {
            return SafePathHelper.Combine(directory, name);
        }

        private static OutputPlacementResult Failure(OutputPlacementError error, string message)
        {
            return new OutputPlacementResult
            {
                Success = false,
                Error = error,
                Message = message
            };
        }
    }
}
