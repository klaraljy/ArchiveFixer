using ArchiveFixer.Engines;
using System;
using System.Collections.Generic;

namespace ArchiveFixer.Models
{
    /// <summary>
    /// 源包处理档（决策 D-9，2026-09-22 用户拍板）。
    ///
    /// <para>
    /// ⚠ <b>两条路径都照这一档走</b>（用户 2026-09-22 的版本二指示，**推翻**了早先"地基路径永远不动源包"的说法）：
    /// 「一键处理」与手动「只解压」都在"成功 + 输出校验通过 + 未取消 + 属于本任务分卷组"之后
    /// 按本档处理源包（见 AGENTS.md §0 / §6 第 1 条的例外、`docs/输出与整理模型.md` §3.4）。
    /// <see cref="SourceHandlingMode.KeepInPlace"/> 才是"一个字节都不搬"的出口。
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
    /// 同名冲突处理档（设置项 <see cref="AppSettings.ConflictAction"/>）的**唯一词表**。
    ///
    /// <para>
    /// 为什么要有这个词表：这四个字符串同时出现在三个地方 —— 设置界面的 <c>ComboBox</c> Tag、
    /// appsettings.json 里的字符串、以及冲突处理的分支判断。以前它们是各写各的字面量，
    /// 于是出现了本项目最不能接受的那类缺陷：界面给了「询问」这一档，
    /// 而 <c>PathService</c> 把它和 <c>AutoRename</c> 并到同一个分支 —— **用户选了询问，程序静默自动重命名**。
    /// 现在四处都引用这里的常量，字面量只允许在这里出现一次。
    /// </para>
    ///
    /// <para>
    /// 四档语义（<c>docs/WinRAR功能参考.md</c> §B：照 WinRAR 的六档精神裁剪到我们的批量场景）：
    /// <list type="bullet">
    /// <item><description><see cref="Skip"/>：同名就不动，已存在的文件一个字节都不改（产物留在暂存目录）。</description></item>
    /// <item><description><see cref="Overwrite"/>：顶掉已存在的同名项；走"先挪到临时名 → 落位 → 再删"两阶段（不变量 3）。</description></item>
    /// <item><description><see cref="AutoRename"/>：新产物落成 <c>名字(1)</c>，**绝不覆盖**。默认档。</description></item>
    /// <item><description><see cref="Ask"/>：第一次遇到同名冲突时**暂停该任务**弹一次聚合询问
    /// （覆盖 / 跳过 / 自动重命名，可升级成"整批都照此办理"）；无界面宿主时降级为自动重命名并写日志。</description></item>
    /// </list>
    /// </para>
    /// </summary>
    public static class ConflictActions
    {
        /// <summary>跳过（不动已存在的文件）。</summary>
        public const string Skip = "Skip";

        /// <summary>覆盖（两阶段落位，先挪开旧的再落位）。</summary>
        public const string Overwrite = "Overwrite";

        /// <summary>自动重命名（<c>名字(1)</c>）。**默认档**：不丢产物、不动旧文件。</summary>
        public const string AutoRename = "AutoRename";

        /// <summary>询问（第一次冲突时暂停该任务，弹一次聚合询问）。</summary>
        public const string Ask = "Ask";

        /// <summary>这个档是不是"询问"（唯一判定处，禁止在别处再写 <c>== "Ask"</c>）。</summary>
        public static bool IsAsk(string? value) =>
            string.Equals(Normalize(value), Ask, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 归一化：空 / 非法一律回落 <see cref="AutoRename"/>。
        ///
        /// 容错口径与 <c>ParseSourceHandling</c> / <c>ParseTerminalLayoutMode</c> 一致：
        /// 这个字符串可能来自旧配置（缺字段）、用户手改的 json，或将来改名的枚举。
        /// 读不懂时**退回最不意外、也最不可能丢数据的那一档**，而不是到解压那一刻才报错或猜一个别的行为。
        /// </summary>
        public static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return AutoRename;
            }

            string trimmed = value.Trim();

            if (string.Equals(trimmed, Skip, StringComparison.OrdinalIgnoreCase))
            {
                return Skip;
            }

            if (string.Equals(trimmed, Overwrite, StringComparison.OrdinalIgnoreCase))
            {
                return Overwrite;
            }

            if (string.Equals(trimmed, AutoRename, StringComparison.OrdinalIgnoreCase))
            {
                return AutoRename;
            }

            if (string.Equals(trimmed, Ask, StringComparison.OrdinalIgnoreCase))
            {
                return Ask;
            }

            return AutoRename;
        }
    }

    /// <summary>
    /// 「其余物」清理默认档（设置项 <see cref="AppSettings.RestRemovalDefaultMode"/>）的**唯一词表**。
    ///
    /// <para>
    /// 语义：它只是删除确认框里那个勾选框的**默认状态**，不是"跳过确认直接删"。
    /// 无论哪一档，都照 <c>docs/输出与整理模型.md</c> §3.2 的清理表办 ——
    /// 先预览条目数与总大小 → 红色确认 → 彻底删除还要**二次确认**（勾"我知道不可恢复"）；
    /// 回收站不可用时**一律不删**，绝不因为这一项就降级成永久删除。
    /// </para>
    ///
    /// <para>
    /// 为什么用字符串词表而不是直接存 <c>Storage.DeleteMode</c> 的枚举：与
    /// <see cref="ConflictActions"/> 同一口径 —— 落盘的是**枚举名**（用户手改 json 也看得懂），
    /// 空 / 非法一律回落最保守的 <see cref="RecycleBin"/>，旧配置缺字段不报错。
    /// </para>
    /// </summary>
    public static class RestRemovalModes
    {
        /// <summary>移入回收站（**默认档**）：可恢复；回收站不可用时一律不删。</summary>
        public const string RecycleBin = "RecycleBin";

        /// <summary>彻底删除（激进档）：不可恢复，界面上必须红色标识 + 二次确认。</summary>
        public const string Permanent = "Permanent";

        /// <summary>这一档是不是"彻底删除"（唯一判定处，禁止在别处再写 <c>== "Permanent"</c>）。</summary>
        public static bool IsPermanent(string? value) =>
            string.Equals(Normalize(value), Permanent, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 归一化：空 / 非法一律回落 <see cref="RecycleBin"/>。
        ///
        /// 容错口径与 <c>ConflictActions.Normalize</c> / <c>ParseSourceHandling</c> 一致：
        /// 这个字符串可能来自旧配置（缺字段）、用户手改的 json，或将来改名后的枚举。
        /// 读不懂时**退回最保守的那一档**（回收站），而不是到确认框那一刻才报错，
        /// 更不是"读不懂就按激进档办"。
        /// </summary>
        public static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return RecycleBin;
            }

            string trimmed = value.Trim();

            if (string.Equals(trimmed, Permanent, StringComparison.OrdinalIgnoreCase))
            {
                return Permanent;
            }

            return RecycleBin;
        }
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

        /// <summary>
        /// 同名冲突处理档（<see cref="ConflictActions"/> 的四个值之一）。
        ///
        /// **默认 <c>AutoRename</c>**：同名时把新产物落成 <c>名字(1)</c>，不丢产物、不动旧文件，
        /// 也绝不默认覆盖（AGENTS.md §6 第 3 条）。
        ///
        /// 它管两处：**解压落位**（输出目录已存在且非空 / 定稿搬运时同名）与**改名冲突**
        /// （改名预览里逐条显示、确认后才落盘）。<c>Ask</c> 档在解压落位时真的会问
        /// （<c>Services/DialogService.ShowConflictDecisionAsync</c>），不再是"写着一个询问、跑起来自动改名"。
        /// </summary>
        public string ConflictAction { get; set; } = ConflictActions.AutoRename;

        public bool TestBeforeExtract { get; set; } = false;

        public bool EnableLog { get; set; } = true;

        public bool AutoScanAfterDrop { get; set; } = true;

        public bool PreviewBeforeRename { get; set; } = true;

        public string OverwriteMode { get; set; } = "SkipExisting";

        public bool TryEmptyPasswordFirst { get; set; } = true;

        public bool UseGlobalPasswordForAllTasks { get; set; } = true;

        public int MaxParallelExtractCount { get; set; } = 1;

        /// <summary>
        /// 低运行优先级（**默认开**）：启动时把本进程设成 <c>BelowNormal</c>，
        /// 解压子进程（7z.exe）继承这个优先级，于是批量解压不再和桌面抢 CPU / 磁盘。
        ///
        /// <para>
        /// 为什么升成设置项（依据 <c>docs/WinRAR功能参考.md</c> §2 D 组 / §3 第 9 条）：
        /// 这一段以前是 <c>App.xaml.cs</c> 里的**硬编码**，用户觉得慢的时候**没有任何可调项**
        /// （WinRAR 把它放在"设置 / 常规 / 系统"里，并且写明"通常常规优先级是最优选择"）。
        /// </para>
        ///
        /// <para>
        /// ⚠ <b>它与 <see cref="MaxParallelExtractCount"/> 是两个互相独立的旋钮</b>：
        /// 这一项决定"让不让出 CPU / 磁盘优先级"（快慢与是否拖慢别的程序），
        /// 并发数决定"同时跑几个包"（吞吐与内存/磁盘压力）。
        /// 改其中一个**绝不会**顺手改另一个 —— 抄的正是 WinRAR 6.02 修过的那个坑
        /// （"以前版本忽略了 <c>-ri</c>，并在存在 <c>-ibck</c> 时设置了较低优先级"：
        /// 两个调节项互相覆盖，用户调了 A 却被 B 决定）。
        /// </para>
        /// </summary>
        public bool LowProcessPriority { get; set; } = true;

        /// <summary>
        /// 定稿完成后在资源管理器里打开输出目录（**默认关**）。
        ///
        /// <para>
        /// ⚠ 这是"**会动用户桌面**"的行为（依据 <c>docs/WinRAR功能参考.md</c> §2 C 组 / §3 第 7 条）：
        /// ① 只有用户**显式打开**这一项才执行，绝不做成默认；
        /// ② 打开时**只打开文件夹** —— 复用既有 <c>SafePathHelper.OpenDirectory</c>
        ///    （<c>explorer.exe &lt;目录&gt;</c>），**不得**把主窗口置前 / 最大化 / 抢焦点，
        ///    也不得切换前台窗口（AGENTS.md §13 同精神：只打开文件夹，不抢焦点）。
        /// </para>
        ///
        /// <para>
        /// 只在本任务**真正定稿成功**（内容物已落 <c>destDir</c>、输出校验通过）之后触发；
        /// 失败 / 部分完成 / 取消时一律不打开 —— 那时候打开一个空的或半截的目录只会误导用户。
        /// </para>
        /// </summary>
        public bool OpenOutputFolderWhenDone { get; set; } = false;

        /// <summary>
        /// 「其余物」清理的默认档（取值见 <see cref="RestRemovalModes"/>）：<c>RecycleBin</c>（**默认**）
        /// 或 <c>Permanent</c>。
        ///
        /// <para>
        /// 与 WinRAR"删除压缩包"那一组的"永不 / 询问确认 / 总是 / 移动到回收站"（<c>docs/WinRAR功能参考.md</c> §1.4）
        /// 对齐的是**默认值思路**，不是"不问就删"：我们**永远**先预览 + 确认，
        /// 这一项只决定确认框里那个"改为彻底删除"勾选框**默认勾不勾**。
        /// 默认档 = <see cref="RestRemovalModes.RecycleBin"/>（可恢复），
        /// 危险的那一档必须用户主动勾选，勾了还有二次确认（规格 §3.2 清理表）。
        /// </para>
        /// </summary>
        public string RestRemovalDefaultMode { get; set; } = RestRemovalModes.RecycleBin;

        /// <summary>
        /// 危险条目统计（**默认开**）：在**既有那一遍**解压前条目预检里顺便数出可执行 / 脚本类条目
        /// （<c>.exe/.scr/.lnk/.bat/.cmd/.ps1/.vbs</c>），把"本包含 N 个可执行文件"写进任务详情与失败清单。
        ///
        /// <para>
        /// **只提示、绝不阻断**（依据 <c>docs/WinRAR功能参考.md</c> §2 F 组 / §3 第 5 条）：
        /// 我们的场景里安装器 / 补丁**经常就是内容物**，所以**不做** WinRAR 那种全局硬排除掩码
        /// —— 那会把内容物一起丢掉，而且全局开关容易被遗忘、排障时变成隐形行为。
        /// </para>
        ///
        /// <para>
        /// 关掉它只是"不统计、不提示"，**落盘行为一模一样**（不变量 4 的路径清洗与落点校验与它无关，照旧执行）。
        /// </para>
        /// </summary>
        public bool ReportDangerousEntries { get; set; } = true;

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
        /// 用户自定义的 UnRAR.exe 路径；空表示按"**已装的 WinRAR 目录 → 内置 tools\unrar**"自动解析。
        ///
        /// 与 <see cref="CustomSevenZipExePath"/> 同一口径：路径**只**由 <c>ToolLocator</c> 解析，
        /// 别的文件里不许再拼一遍（AGENTS.md §3.1）。
        /// </summary>
        public string CustomUnRarExePath { get; set; } = string.Empty;

        /// <summary>
        /// **引擎优先级**（用户 2026-09-22 指示："先是 winrar、7z、然后就是后面的引擎"）。
        ///
        /// <para>
        /// 存的是引擎 id 的**有序列表**（<c>winrar</c> = RARLAB UnRAR，<c>sevenzip</c> = 7-Zip 命令行，
        /// 词表见 <see cref="EngineIds"/>）。默认 <c>["winrar", "sevenzip"]</c>。
        /// </para>
        ///
        /// <para>
        /// ⚠ 它是**同能力时的先后**，不是"只能用第一个"：选择规则是
        /// **先按能力筛（谁能干这活），再用优先级做 tiebreaker**，不可用的引擎直接跳过 ——
        /// 不许因为"排第一但没装"就打不开包（AGENTS.md §3.1）。
        /// 所以 zip/7z 包永远走 7-Zip（UnRAR 不支持这些格式），RAR 包默认先走 UnRAR。
        /// </para>
        ///
        /// <para>
        /// 旧配置里没有这个字段 → 反序列化后是 null → <see cref="Normalize"/> 补成默认值，
        /// 不需要额外的兼容分支（与 MaxPasswordAttemptsPerLayer 同一套容错口径）。
        /// </para>
        /// </summary>
        public List<string>? EnginePriority { get; set; }

        /// <summary>
        /// 保留受损的文件（**默认关**）：解压时给**RAR 引擎**加"保留校验和不符的半成品"的开关
        /// （UnRAR 的 <c>-kb</c>，来源 <c>docs/WinRAR功能参考.md</c> §2 C 组）。
        ///
        /// <para>
        /// ⚠ <b>它只对 UnRAR 生效</b>，对本机 7-Zip 26.01 **不加任何参数**，依据是实测：
        /// <c>7z x -kb</c> 直接报 <c>Command Line Error: Unknown switch: -kb</c>（退出码 7）——
        /// <c>-kb</c> 是 RAR / UnRAR 的开关；而 7-Zip **本来就保留**校验失败的半成品
        /// （实测截断包里被截断的文件仍留在输出目录），也就不需要这个开关。
        /// 若照文档建议给 7-Zip 加上，用户一开这项，**所有解压都会以命令行错误失败**。
        /// </para>
        ///
        /// <para>
        /// ⚠ <b>它绝不改变任务成败的判定</b>（AGENTS.md §6 不变量 6）：
        /// 校验和不符的包仍然是**失败 / 部分完成**，只是磁盘上会留下那个半成品，
        /// 让用户还能试着抢救半个视频 / 半张图。状态由引擎退出码与错误分类决定，与本开关无关。
        /// </para>
        /// </summary>
        public bool KeepBrokenFiles { get; set; } = false;

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
        /// ⚠ **两条路径行为一致**（用户 2026-09-22 版本二，推翻早先"地基路径永远不动源包"）：
        /// 手动「只解压」与一键处理读的是同一个档位，成功后同样把源包移入其余物；
        /// <c>KeepInPlace</c> 档才是"一个字节都不搬"的出口。
        /// <see cref="DeleteSourceAfterExtract"/> 仍然只管 <c>KeepInPlace</c> 档下"传统解压器 + 解压后清理"
        /// 那个老组合（见 <c>ExtractionCoordinator</c> 里 deleteSource 的算法）。
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
                ConflictAction = ConflictActions.AutoRename,
                TestBeforeExtract = false,
                EnableLog = true,
                AutoScanAfterDrop = true,
                PreviewBeforeRename = true,
                OverwriteMode = "SkipExisting",
                TryEmptyPasswordFirst = true,
                UseGlobalPasswordForAllTasks = true,
                MaxParallelExtractCount = 1,
                LowProcessPriority = true,
                OpenOutputFolderWhenDone = false,
                RestRemovalDefaultMode = RestRemovalModes.RecycleBin,
                ReportDangerousEntries = true,
                RememberLastOutputDirectory = true,
                IncludeHiddenFiles = false,
                IncludeSystemFiles = false,
                MaxFileSizeLimit = 0,
                PreservePasswordLeadingTrailingSpaces = true,
                EnableSidecarPassword = false,
                CustomSevenZipExePath = string.Empty,
                CustomUnRarExePath = string.Empty,

                // 用户 2026-09-22 指示："先是 winrar、7z、然后就是后面的引擎"。
                EnginePriority = new List<string>(EngineIds.DefaultPriority),
                KeepBrokenFiles = false,
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

            /*
             * 同名冲突处理档：空 / 非法一律回落 AutoRename（**绝不默认覆盖**，不变量 3）。
             *
             * 归一化放在设置层，与 RecursionMode / TerminalLayoutMode / SourceHandling 同一口径：
             * 到了冲突那一刻才发现"这个档读不懂"是最糟的 —— 用户已经点了解压，程序却要临时猜一个处置方式。
             * 判定本身只有一处实现（ConflictActions.Normalize），这里不另写一套字符串比较。
             */
            ConflictAction = ConflictActions.Normalize(ConflictAction);

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

            /*
             * 「其余物」清理默认档：空 / 非法一律回落 RecycleBin（**可恢复的那一档**），旧配置不报错。
             *
             * 归一化必须放在设置层，与 ConflictAction / SourceHandling 同一口径：
             * 到用户点下"删除其余物"的那一刻才发现"这个档读不懂"是最糟的 ——
             * 那时程序要么临时猜一个处置方式，要么把用户晾在确认框前。
             * 判定只有一处实现（RestRemovalModes.Normalize），这里不另写一套字符串比较。
             */
            RestRemovalDefaultMode = RestRemovalModes.Normalize(RestRemovalDefaultMode);

            if (MaxFileSizeLimit < 0)
            {
                MaxFileSizeLimit = 0;
            }

            CustomOutputDirectory ??= string.Empty;
            CustomSevenZipExePath ??= string.Empty;
            CustomUnRarExePath ??= string.Empty;
            CollectTargetDirectory ??= string.Empty;
            CacheRootDirectory ??= string.Empty;
            PasswordBookPath ??= string.Empty;

            /*
             * 引擎优先级：空 / 缺字段 / 手改坏的值一律收拾成"规范写法 + 补全所有已知引擎"。
             *
             * 归一化放在设置层，与其它设置项同一口径：到引擎选择那一刻才发现"这个列表读不懂"
             * 是最糟的 —— 用户已经点了处理，程序却要临时猜用哪个引擎。
             * 判定只有一处实现（EngineIds.Normalize），这里不另写一套字符串比较。
             */
            EnginePriority = EngineIds.Normalize(EnginePriority);

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

            /*
             * 自定义 UnRAR 路径同理；但**兜底方向不同**：UnRAR 还有"已装 WinRAR 目录"与"内置"两档，
             * 所以清空它只是回到"自动解析"，不会让引擎不可用（ToolLocator 里两级回落）。
             */
            if (!string.IsNullOrWhiteSpace(CustomUnRarExePath) && !System.IO.File.Exists(CustomUnRarExePath))
            {
                CustomUnRarExePath = string.Empty;
            }
        }
    }
}
