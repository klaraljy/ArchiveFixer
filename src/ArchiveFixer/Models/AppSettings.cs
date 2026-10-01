using ArchiveFixer.Engines;
using System;
using System.Collections.Generic;
using System.Linq;

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
        KeepInPlace = 1
    }

    /// <summary>
    /// 「其余物」在**解压成功 + 输出校验通过 + 未取消**之后怎么处理（设置项
    /// <see cref="AppSettings.RestHandlingAfterVerify"/>）的**唯一词表**。
    ///
    /// <para><b>用户 2026-09-25 亲自定的三档</b>（原话：「删除操作，1_不动其余物，2_动，但是只删除在回收站的位置，
    /// 3_动，而且是彻底删除，直接节约空间（这个就可以字体变红了，就不用再弄其他的没用的注释了）」）：</para>
    /// <list type="number">
    /// <item><description><see cref="Keep"/>：不动其余物（**默认**）。</description></item>
    /// <item><description><see cref="RecycleBin"/>：移入回收站 —— 可还原；⚠ 空间要等清空回收站才真正释放。</description></item>
    /// <item><description><see cref="Delete"/>：彻底删除、直接省空间 —— **不可恢复**，界面上整条标红。</description></item>
    /// </list>
    ///
    /// <para>⚠ 它同时取代了 2026-09-22 那套「危险模式 + 自测凭证」（用户 2026-09-25 明确要求整块删掉）：
    /// 不再有自测、风险四条与红横幅 —— **红字就是警告**。而"失败 / 部分完成 / 取消一个字节都不删"这条红线
    /// 由 <c>Storage/RestItemPurger</c> 的五道门槛继续钉着（与哪一档无关）。</para>
    /// </summary>
    public static class RestHandlingModes
    {
        /// <summary>不动其余物（默认）。</summary>
        public const string Keep = "Keep";

        /// <summary>移入回收站（可还原；空间要等清空回收站才释放）。</summary>
        public const string RecycleBin = "RecycleBin";

        /// <summary>彻底删除（不可恢复，直接省空间）。</summary>
        public const string Delete = "Delete";

        /// <summary>三档的唯一判据（认不出的值一律回落 <see cref="Keep"/>，绝不回落成"删"）。</summary>
        public static bool IsKnown(string? value) =>
            string.Equals(value, Keep, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, RecycleBin, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, Delete, StringComparison.OrdinalIgnoreCase);

        /// <summary>归一化：认不出的值 → <see cref="Keep"/>（保守方向：宁可不动，绝不误删）。</summary>
        public static string Normalize(string? value)
        {
            if (string.Equals(value, RecycleBin, StringComparison.OrdinalIgnoreCase))
            {
                return RecycleBin;
            }

            if (string.Equals(value, Delete, StringComparison.OrdinalIgnoreCase))
            {
                return Delete;
            }

            return Keep;
        }
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
        /// 容错口径与 <c>ParseSourceHandling</c> / <c>ParsePlacementMode</c> 一致：
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

        /// <summary>
        /// **详细日志（排查用）**：打开后，成功的任务也把全过程写进日志（用户 2026-09-25 第 44 条追加拍板）。
        ///
        /// <para>默认 <c>false</c> = 成功只留一行摘要（几十个同形包时日志小一个数量级）；
        /// 打开 = 回到"每步都写"（进度、落点、入仓、空间门、结果校验、定稿……）。
        /// ⚠ **失败 / 取消的任务不受这个开关影响** —— 它们永远保留全部细节
        /// （用户原话："如果失败的话，你就可以多一点"）。</para>
        /// </summary>
        public bool VerboseLog { get; set; } = false;

        public bool AutoScanAfterDrop { get; set; } = true;

        /// <summary>
        /// 改名前是否先弹预览。
        ///
        /// <para>
        /// ⚠ <b>这个配置键对本程序的行为没有任何影响，而且这是刻意的</b>（AGENTS.md §6 不变量 3）：
        /// 「改名必须先预览」是红线，主流程 <c>RenameCoordinator</c> 的五个入口**一律硬编码**
        /// <c>PreviewBeforeRename = true</c>，<c>RenameOptions.Normalize</c> 也会把它强制回 true。
        /// 设置窗口里那个复选框早已删除（留着就是"改了没用"的开关）。
        /// </para>
        /// <para>
        /// 之所以还留着这个属性：<c>Models/RenameOptions.cs</c>（本次不在授权文件清单里）
        /// 的 <c>FromSettings</c> 仍会读它，删掉属性会编译不过。
        /// **将来能一并改的时候，正确做法是连属性带 <c>RenameOptions</c> 那一行一起删掉**，
        /// 而不是让它变成"看起来能关掉预览"的假开关。
        /// </para>
        /// </summary>
        public bool PreviewBeforeRename { get; set; } = true;

        public string OverwriteMode { get; set; } = "SkipExisting";

        public bool TryEmptyPasswordFirst { get; set; } = true;

        public bool UseGlobalPasswordForAllTasks { get; set; } = true;

        /// <summary>
        /// 同时解压几个包（1~8，**默认 4**）。
        ///
        /// <para>
        /// 默认值 2026-09-22 由 1 改成 4，依据是**并行实测**（真样本、三档、逐字节校验）：
        /// 并发 1→4 的吞吐是 **3.08×**（保守口径 2.70×），而 4→8 只再快 **1.9%** ——
        /// 但单包耗时从 6.63 s 涨到 10.91 s（+108%）、整机 CPU 均值从 48.7% 涨到 72.7%。
        /// 根因是**6 物理核超订**：第 1 波 8 个并发的单包耗时 11.9–14.2 s，降到 4 个立刻回到 5.9–6.8 s。
        /// 所以"4"是这台机器上的拐点：拿满了并行收益，又不把机器拖到超订。
        /// 正确性三档 **36/36 逐字节一致**，界面心跳三档都没有 &gt;400 ms 的卡顿，取消语义 6/6 通过。
        /// </para>
        ///
        /// <para>
        /// ⚠ 它与空间无关：**并发越高，同时在盘上的峰值越大** —— 空间不够时仍由空间门在启动前拦下
        /// （见 <c>Storage/SpaceGate.cs</c> 与 <c>Storage/SpaceReservationLedger.cs</c>）。
        /// 设置界面在它旁边给一句"建议不超过物理核心数（当前检测到 N 核）"。
        /// </para>
        /// </summary>
        public int MaxParallelExtractCount { get; set; } = DefaultMaxParallelExtractCount;

        /// <summary>
        /// 「全速」：不再按 <see cref="MaxParallelExtractCount"/> 节流（能并行多少就并行多少）。
        ///
        /// <para><b>用户 2026-09-26 明确要求它必须记住</b>：原话"我刚刚测试当点击并发操作的时候，
        /// 这个开关就没有保存……我现在是想他们一个要自动保存……而且要有记忆性，下次重启也要有，
        /// 这点要非常重视"。以前它只是主视图模型上的一个字段（语义刻意写成"只看这一次运行"），
        /// 于是他勾了、重启又回到节流档 —— 那在他眼里就是"没保存"。</para>
        ///
        /// <para>现在的语义：**记住**（写进 appsettings.json）。想回到节流档就取消勾选（同样会被记住）。</para>
        /// </summary>
        public bool RunAtFullSpeed { get; set; }

        /// <summary>
        /// 并发数的出厂默认值（**4**，见 <see cref="MaxParallelExtractCount"/> 的实测依据）。
        ///
        /// <para>单独提出来是为了让"默认值"在代码里只有一个来源：
        /// <see cref="CreateDefault"/> 与属性初始化器都引用它，改一处就够了。</para>
        /// </summary>
        public const int DefaultMaxParallelExtractCount = 4;

        /// <summary>
        /// 「其余物」在**解压成功 + 输出校验通过 + 未取消**之后怎么处理
        /// （取值见 <see cref="RestHandlingModes"/>：<c>Keep</c> / <c>RecycleBin</c> / <c>Delete</c>）。
        ///
        /// <para><b>用户 2026-09-25 亲自定的三档</b>，取代了原先那套「危险模式 + 自测凭证 + 风险四条 + 红横幅」
        /// （他明确要求"全部删掉，字体变红就是最好的操作"）：这一项就是 ③「清理与删除」页那个"删除操作"
        /// 单选框存的值，**默认 <see cref="RestHandlingModes.Keep"/>（不动其余物）**。</para>
        ///
        /// <para>红线（与哪一档无关，由 <c>Storage/RestItemPurger</c> 的五道门槛执行）：
        /// 只有"解压成功 + 输出校验通过 + 未取消 + 落在本任务输出根之内 + 路径就是定稿那一刻记下来的那条"
        /// 才动；**失败 / 部分完成 / 取消 → 一个字节都不删**（不变量 1）。</para>
        /// </summary>
        public string RestHandlingAfterVerify { get; set; } = RestHandlingModes.Keep;

        /// <summary>
        /// ⛔ **已退役**（2026-09-25 用户第 32 条：「危险模式……全部删掉」）：
        /// 原来这是那套"红色按钮 + 自测凭证 + 风险四条 + 红横幅"的总开关。
        ///
        /// <para>现在保留这个属性只有两个用途：①读得懂旧 <c>appsettings.json</c>（不然那一行会被当成未知字段丢掉，
        /// 我们也就无从迁移）；②<see cref="Normalize"/> 里把它一次性迁移成
        /// <see cref="RestHandlingAfterVerify"/> = <see cref="RestHandlingModes.Delete"/>，
        /// 随后一律写回 <c>false</c>。程序里**没有任何地方**再读它做决策。</para>
        /// </summary>
        public bool DangerousSpaceModeEnabled { get; set; } = false;

        /// <summary>⛔ **已退役**（见 <see cref="DangerousSpaceModeEnabled"/>）：旧的自测凭证，读取时一律清空。</summary>
        public string DangerModeSelfTestStamp { get; set; } = string.Empty;

        /// <summary>
        /// **容器里装的是分卷的第 1 卷时，自动把同目录的后续卷接上**（用户 2026-09-25 第 42 条，**默认关**）。
        ///
        /// <para>他遇到的形状：一个文件（例如 <c>封面.jpg</c>）里装着的正是 <c>set.7z.001</c>，
        /// 而 <c>set.7z.002/.003</c> 就躺在**同一个目录**里。引擎按**文件名**在**同一个目录**里找同组的其他卷，
        /// 而抠出来的那一段既不在那个目录、名字也不带卷号 → 报"分卷缺失"，两边永远凑不上。</para>
        ///
        /// <para>打开之后：程序在工作区里摆一套"名字成套、同在一个目录"的卷
        /// （首卷 = 容器里那一段，改成标准首卷名；后续卷 = 同盘**硬链接**、跨盘才复制且先查空间），
        /// 拿拼好的那一套去解压。**用户的源文件一个字节都不动**（见 <c>Extraction/SplitVolumeAssembler</c>）。</para>
        ///
        /// <para>⛔ 为什么默认关：它会读用户的源目录、并在盘上造名字 / 复制卷。
        /// 判不准的场合（同族两组以上缺首卷、族认不出来且不止一组）**一律不动**，
        /// 照旧如实报"分卷缺失"并给出改名建议。</para>
        /// </summary>
        public bool AssembleSplitVolumesFromContainer { get; set; } = false;

        // ==================== 打包（用户 2026-09-26 第 46 条：与解压那套**完全独立**）====================

        /// <summary>
        /// 最终 <c>.rar</c> 放哪：<c>Local</c>（默认，源旁边）/ <c>Custom</c>（指定位置）。
        ///
        /// <para>取值见 <c>Packing/PackingOptions.cs</c> 的 <c>PackingTargetMode</c>。
        /// ⛔ 与解压侧的落点（<see cref="ExtractToOriginalDirectory"/> / <see cref="CustomOutputDirectory"/>）
        /// **各存各的**：用户明确要求"两者绝对不能同步"。</para>
        /// </summary>
        public string PackTargetMode { get; set; } = "Local";

        /// <summary>打包的指定位置（<see cref="PackTargetMode"/> = Custom 时用；留空 = 回落本地）。</summary>
        public string PackCustomOutputDirectory { get; set; } = string.Empty;

        /// <summary>
        /// 打包的**原包操作**：<c>KeepInPlace</c>（默认，一个字节都不碰）/ <c>MoveToRest</c>
        /// （移入其余物 = 装 7z 分卷的那个文件夹里面）。
        /// </summary>
        public string PackSourceHandling { get; set; } = "KeepInPlace";

        /// <summary>
        /// 打包的**其余物操作**（其余物 = 装 7z 分卷的那个文件夹）：
        /// <c>Keep</c> / <c>RecycleBin</c> / <c>Delete</c>（**默认 Delete** —— 用户原话："这个对用户来说一点用没有"）。
        ///
        /// <para>⛔ 只有 <c>.rar</c> 生成且校验通过之后才动；失败 / 取消一个字节都不动。</para>
        /// </summary>
        public string PackRestHandling { get; set; } = "Delete";

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
        /// ⚠ 这是"会动用户桌面"的行为（依据 <c>docs/WinRAR功能参考.md</c> §2 C 组 / §3 第 7 条）：
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

        /// <summary>
        /// 解压前的提醒（**默认开**）：扫一遍源目录，把"可能的无用物"与"一个可用密码都没有"的包列出来，
        /// 让用户先看一眼再决定（AGENTS.md §9.7；用户 2026-09-24 第 15 条要求这一项可关）。
        ///
        /// <para>
        /// 关掉它 = **不扫、不弹**，直接开始解压。它与弹窗里那个"本次运行不再提示"是两件事：
        /// 后者只记内存、进程一退就失效，这一项是落盘的长期选择。
        /// </para>
        /// </summary>
        public bool RemindBeforeExtract { get; set; } = true;

        public bool RememberLastOutputDirectory { get; set; } = true;

        /// <summary>
        /// 「导出日志」对话框**上次成功导出到的目录**（用户 2026-10-01 真机 `giu910`：导出落进了其余物）。
        ///
        /// <para>只服务一件事：下次打开保存对话框时从这儿开始（⛔ 不是"默认导出到这儿"、⛔ 不是任何自动行为）。
        /// 落进其余物 / 工作区的目录**不记**——那两个地方程序自己会整份删掉，日志落那儿哪天就跟着没了。</para>
        /// </summary>
        public string LastLogExportDirectory { get; set; } = string.Empty;

        /// <summary>
        /// 扫描时是否包含隐藏文件（默认否）。设置界面在⑥设置页的「扫描与识别」里。
        /// </summary>
        public bool IncludeHiddenFiles { get; set; } = false;

        /// <summary>
        /// 扫描时是否包含系统文件（默认否）。设置界面在⑥设置页的「扫描与识别」里。
        /// </summary>
        public bool IncludeSystemFiles { get; set; } = false;

        /// <summary>
        /// 扫描时的单文件大小上限，单位 **MB**（0 = 不限制，默认）。
        /// 设置界面在⑥设置页的「扫描与识别」里。超过上限的文件不进任务列表。
        /// </summary>
        public long MaxFileSizeLimit { get; set; } = 0;

        /*
         * 这里曾经有一个 PreservePasswordLeadingTrailingSpaces（默认 true）。
         *
         * 它 2026-09-22 被**删除**了，原因是它是一句谎话：全项目**没有任何一处读它**，
         * 而界面上也没有这一项 —— 用户（或将来接手的人）在 appsettings.json 里把它改成 false，
         * 以为密码会被 Trim，实际一个字节的行为都不会变。
         *
         * "密码不 Trim"本来就已经是硬行为（AGENTS.md §9.1：密码可能就带首尾空格），
         * 需要的是"想 Trim 就得先想清楚为什么"，不需要一个做不到的开关。
         * 旧配置里若还留着这个键，System.Text.Json 会**忽略**未知成员，不影响启动。
         */

        /// <summary>
        /// 从归档同目录的说明文件（.txt/.bat/…）里提取密码候选。
        /// **默认关闭**：这是"猜"出来的候选，必须由用户显式开启（AGENTS.md §9.4）。
        /// </summary>
        public bool EnableSidecarPassword { get; set; } = false;

        /// <summary>用户自定义的 7z.exe 路径；空表示用程序目录内置的。路径只由 ToolLocator 解析（AGENTS.md §3.1）。</summary>
        public string CustomSevenZipExePath { get; set; } = string.Empty;

        /// <summary>
        /// 用户自定义的 UnRAR.exe 路径；空表示按"已装的 WinRAR 目录 → 内置 tools\unrar"自动解析。
        ///
        /// 与 <see cref="CustomSevenZipExePath"/> 同一口径：路径**只**由 <c>ToolLocator</c> 解析，
        /// 别的文件里不许再拼一遍（AGENTS.md §3.1）。
        /// </summary>
        public string CustomUnRarExePath { get; set; } = string.Empty;

        /// <summary>
        /// 用户自定义的 <c>Rar.exe</c> 路径（**打包**功能做外层 rar 时用；用户 2026-09-23 决定加这一格）。
        ///
        /// <para>空表示按 <c>ToolLocator</c> 的解析链自动找：本机已装 WinRAR 目录里的 <c>Rar.exe</c>，
        /// 再退 <c>WinRAR.exe</c>。</para>
        ///
        /// <para>⚠ <b>许可边界（AGENTS.md §3.1）</b>：<c>Rar.exe</c> / <c>WinRAR.exe</c> 是**共享软件**，
        /// RARLAB 的 EULA 明确禁止随其它软件包分发（§3.1 / §3.2 / §3.3 / §10），所以这一格指向的
        /// 只能是**用户自己安装 / 下载的**那一份 —— 程序只检测与调用，绝不复制、绝不内置、绝不随包分发。
        /// 这也正是这一格存在的理由：不提供自选路径，用户把 WinRAR 装到非默认目录时就只能去改
        /// 系统环境变量。</para>
        /// </summary>
        public string CustomRarExePath { get; set; } = string.Empty;

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
        /// ⛔ **已退役**（2026-09-25 用户第 32 条）：原来这一格是"手动「只解压」成功后删源包"的独立开关。
        ///
        /// <para>用户原话：「其实手动档和我们这个一键操作可以看成是同一批，只不过，一键处理不会有那么多的操作，
        /// 手动档的操作就和选项卡里面的一致」—— 所以手动档不再有自己的删除开关，
        /// 统一读 <see cref="SourceHandling"/>(源包操作) + <see cref="RestHandlingAfterVerify"/>(删除操作)。
        /// 这个属性只为**读得懂旧配置文件**而保留：旧值 <c>true</c> 在
        /// <see cref="Normalize"/> 里被迁移成"源包移入其余物 + 其余物移入回收站"（可还原的那一档），
        /// 之后一律写回 <c>false</c>。</para>
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

        /// <summary>
        /// 递归最大层数（1~10）。到顶就停并报告，不做无限展开（不变量 8）。
        ///
        /// <para>⚠ 2026-09-24 用户拍板把默认值从 3 提到 **10**，与一键处理的轮数上限统一：
        /// 他原话是"你为什么只弄了两层，我要的一键解压时多重解压"——
        /// 两个上限一个 3 一个 10 只会让人以为"有一处没生效"。</para>
        ///
        /// <para>⚠ <b>2026-09-26 又调回 5</b>（他原话："现在将默认的最大的解压层数从 10 改到 5 吧，
        /// 用户有需要自己会改的"）：理由是他想明白了真实形状 —— 里面的压缩包**可能是用户想留的东西**
        /// （游戏包里的 mod 压缩包不止一个，程序判不出来，我们只做提醒、不做判定）。
        /// 默认少解几层 = 少一次"把你想要的东西拆了"的机会；真要深挖的人在②页改成 10 就行。
        /// 一键处理每一批的轮数**跟着这一格走**（见 <c>OneClickCoordinator.RoundLimit</c>），
        /// 所以"界面写 5、程序跑到 10"这种不一致不会发生。</para>
        /// </summary>
        public int MaxRecursionDepth { get; set; } = 5;

        /// <summary>
        /// 一次性迁移标记：旧的"最大层数默认 3"已经统一成 10（用户 2026-09-24 拍板）。
        ///
        /// <para>为什么要标记而不是每次都判 <c>== 3</c>：用户完全可能在②页把 3 改回来当自己的选择，
        /// 那时每次都"纠正"成 10 就变成了"设置改不动"。标记落盘之后迁移只发生一次。</para>
        /// </summary>
        public bool RecursionDefaultUnifiedToTen { get; set; }

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

        // ================================================================
        // 解压前的资源预算上限（不变量 8；用户 2026-09-25 第 36 条：必须看得见、改得动）
        // ================================================================

        /*
         * 为什么这四条从"写死在代码里"变成"设置项"（用户真机报的第 36 条）：
         *
         * 他拿一个 12.22 GiB 的包跑一键处理，里面是一组 5 GB 的分卷（`Code Complete-BZ.7z.001` 那类），
         * 失败清单上写着"单个文件解压后 5242880000 字节（4.88 GiB）超过单文件上限 4294967296 字节（4 GiB）"。
         * 那四条上限当时是**硬编码**的（`ResourceBudgetOptions` 的 4 GiB / 20 GiB），界面上一个字都没有，
         * 于是他看到的是一句像"这个包有问题"的判决 —— 而实际上是程序自己的安全阀，而且他改不了。
         *
         * 处置（与"危险模式"退役同一口径：不要仪式，要看得见、改得动）：
         * ① 默认值调到真实资源包绝不会碰到的量级（单文件 64 GiB / 总大小 512 GiB）；
         * ② 四条全部落进设置（⑥设置 →「安全上限」），超范围就夹回并**当场告诉他**；
         * ③ 拒绝文案必须自己说清"这是程序的上限、不是包坏了、去哪儿调"（`ResourceBudget` 的 `CapHint`）。
         *
         * ⛔ 不许再退回"写死"：不变量 8 要求的是**上限存在**，不是"上限不许用户改"。
         */

        /// <summary>解压后单个文件大小上限的默认值（64 GiB）。</summary>
        public const int DefaultMaxSingleExtractedFileGiB = 64;

        /// <summary>解压后总大小上限的默认值（512 GiB）。</summary>
        public const int DefaultMaxExtractedTotalGiB = 512;

        /// <summary>解压后文件数上限的默认值（20 万个）。</summary>
        public const int DefaultMaxExtractedFileCount = 200_000;

        /// <summary>展开比上限的默认值（1000 倍，超过视为压缩炸弹）。</summary>
        public const double DefaultMaxExtractionRatio = 1000d;

        /// <summary>两个大小上限允许的取值范围（GiB）：下限 1，上限 4096（4 TiB）。</summary>
        public const int MinExtractionCapGiB = 1;

        /// <summary>两个大小上限允许的取值范围（GiB）的上界。</summary>
        public const int MaxExtractionCapGiB = 4096;

        /// <summary>
        /// 解压后**单个文件**的大小上限（GiB，默认 64）。
        ///
        /// <para>判的是"归档里最大的那一个条目"（`ResourceBudget` 规则 2a），超了就**不解这个包**。
        /// 默认值取 64 GiB：任何真实视频 / 镜像都够，而"一个 1 KB 的文件声称要写 4 TiB"这种
        /// 单条目炸弹仍然会被拦住（另有展开比与目标盘空间两道）。</para>
        /// </summary>
        public int MaxSingleExtractedFileGiB { get; set; } = DefaultMaxSingleExtractedFileGiB;

        /// <summary>
        /// 解压后**总大小**上限（GiB，默认 512）。永远不小于单文件那一档（见
        /// <see cref="NormalizeTotalCapGiB"/>），否则它形同虚设。
        /// </summary>
        public int MaxExtractedTotalGiB { get; set; } = DefaultMaxExtractedTotalGiB;

        /// <summary>解压后**文件数**上限（默认 20 万）。</summary>
        public int MaxExtractedFileCount { get; set; } = DefaultMaxExtractedFileCount;

        /// <summary>**展开比**上限（默认 1000 倍）：解压后总大小 ÷ 压缩包体积，超了按压缩炸弹拒绝。</summary>
        public double MaxExtractionRatio { get; set; } = DefaultMaxExtractionRatio;

        /// <summary>把"单文件上限"夹进合法区间（≤0 视为没配 → 用默认值）。</summary>
        public static int NormalizeSingleFileCapGiB(int value)
        {
            if (value <= 0)
            {
                return DefaultMaxSingleExtractedFileGiB;
            }

            return Math.Clamp(value, MinExtractionCapGiB, MaxExtractionCapGiB);
        }

        /// <summary>
        /// 把"总大小上限"夹进合法区间，并保证它**不小于**单文件上限 ——
        /// 否则单文件那一档永远先命中，用户以为调大了总量却什么都没变。
        /// </summary>
        public static int NormalizeTotalCapGiB(int value, int singleFileGiB)
        {
            int single = NormalizeSingleFileCapGiB(singleFileGiB);
            int total = value <= 0
                ? DefaultMaxExtractedTotalGiB
                : Math.Clamp(value, MinExtractionCapGiB, MaxExtractionCapGiB);

            return Math.Max(single, total);
        }

        /// <summary>把"文件数上限"夹进合法区间（≤0 视为没配 → 用默认值）。</summary>
        public static int NormalizeExtractedFileCountCap(int value)
        {
            if (value <= 0)
            {
                return DefaultMaxExtractedFileCount;
            }

            return Math.Clamp(value, 1, 50_000_000);
        }

        /// <summary>把"展开比上限"夹进合法区间（NaN / 无穷 / &lt;1 视为没配 → 用默认值）。</summary>
        public static double NormalizeExtractionRatioCap(double value)
        {
            if (!double.IsFinite(value) || value < 1d)
            {
                return DefaultMaxExtractionRatio;
            }

            return Math.Min(value, 1_000_000d);
        }

        /*
         * ⛔ 这里原来有一个 `CacheRootDirectory`（"缓存根目录"）设置项，2026-09-30 **已彻底删除**。
         *
         * 用户原话："这个彻底取消，用户没有定工作区的权力，就是在解压的地方设立隐形的工作区，
         * 这就完全不存在跨盘的操作"。
         *
         * 于是：① 工作区、数据根都不再由用户指定 —— 数据根固定是程序目录下的 data，
         * 工作区固定由**这一单的目标目录**派生（<目标目录>\.ArchiveFixer.work）；
         * ② 旧 appsettings.json 里残留的 `CacheRootDirectory` 键被**安静忽略**
         * （System.Text.Json 默认不认未知成员也不抛），⛔ 不迁移、不报错、不警告 ——
         * 它曾经代表的那个"用户自己挑位置"的权限已经不存在了。
         */

        /// <summary>
        /// 上次导入的密码本文件路径（用户 2026-09-21 反复要求：导入一次就够了，不要每次重导）。
        /// 启动时若文件仍在就自动加载；文件没了就只写一条 WARN，不打扰用户。
        ///
        /// <para>⚠ 2026-09-24 起它**不再承载"记住哪一本"**：那件事改由**有序的**
        /// <see cref="PasswordBookPaths"/> 承担（用户要求多本密码本）。
        /// 这个老字段刻意**保留、不清空**（见 <see cref="Normalize"/> 的迁移）：
        /// ① 旧的 <c>appsettings.json</c> 里只有它，要能迁进列表；
        /// ② 用户回退到旧版本时，那一边还认得它（保留 = 回退不会炸）。</para>
        /// </summary>
        public string PasswordBookPath { get; set; } = string.Empty;

        /// <summary>
        /// **记住的密码本路径（有序，可多本）**，启动时按这个顺序逐本合并 —— 用户 2026-09-24 要求。
        ///
        /// <para>为什么顺序重要：合并是"只补列表里还没有的值、各本的新条目按书顺序追加"，
        /// 所以书的先后会影响新增条目的落位。列表本身也是设置界面上"已记住的密码本"
        /// 那一组的唯一数据源（可逐项移除）。</para>
        ///
        /// <para>旧配置里没有这个字段 → 反序列化后是 null → <see cref="Normalize"/> 用老的
        /// <see cref="PasswordBookPath"/> 补一条（迁移），老用户升级后行为不变。</para>
        /// </summary>
        public List<string>? PasswordBookPaths { get; set; }

        /// <summary>
        /// **记住密码列表**（默认 **开**，用户 2026-09-24 拍板）。
        ///
        /// <para>开着：列表的内容 / 顺序 / 启用状态 / 手工条目 / 记住的密码本一起被
        /// **机器范围 DPAPI 加密**存进 <c>&lt;程序目录&gt;\data\password-list.dat</c>，
        /// 重启后原样恢复（哪些密码本已经加载过也不再是"C 盘还是 D 盘"那种一次性状态）。</para>
        ///
        /// <para>关掉：**既不写、也不读**那个文件（一个字节都不动，旧文件留在原地不删），
        /// 列表退回"只在本次运行内有效"的老行为 —— 界面上必须把这句话说清楚，
        /// 否则用户会以为"关掉只是不加密"。</para>
        ///
        /// <para>⚠ 代价（用户已知并接受，实现与文档都必须如实写明）：机器范围 = 同机任何本机用户
        /// 都可能解开；换机器 / 重装系统解不开，那时程序**忽略并提示**（不崩、不覆盖、不删），
        /// 用户可以点「写回密码本」把列表带走。</para>
        /// </summary>
        public bool RememberPasswordList { get; set; } = true;

        /// <summary>
        /// 一键处理的那**一个**确认框还要不要弹（用户 2026-09-24 第 17 条）。
        ///
        /// <para>用户原话：「点击完一键处理，就只能有一个弹窗提醒，**而且这个可以选中以后不弹出**」。
        /// 勾上之后写进设置、跨重启有效，界面上（② 解压方式 页）留着一个开关可以再打开 ——
        /// 存了却收不回来的开关等于把用户锁在"再也不问"里。</para>
        ///
        /// <para>它与 <see cref="RemindJunkAfterImport"/> 是两件事：这一条管**动手前的那一次确认**，
        /// 那一条管**导入之后的无用物提醒**。两个都关掉才是"完全静默"（那时日志照样写清"本次按什么在跑"）。</para>
        /// </summary>
        public bool SkipOneClickConfirm { get; set; }

        /// <summary>
        /// 导入文件夹 / 文件之后要不要弹一次「无用物提醒」（用户 2026-09-24 第 15 条）。
        ///
        /// <para>用户原话：「每次操作的选完文件夹，就要出一个无用物提醒，**用户可以选中关闭以后就不用触发了**」。
        /// 默认开（他要求的就是"每次选完文件夹就提醒"），关掉之后导入**一次都不弹**
        /// （解压前那一次合并提醒里也不再出现无用物那一段 —— 说了不看还反复说就是噪声）。</para>
        ///
        /// <para>⚠ 这个开关爱**不**影响两件事：① 程序对无用物依旧**一个都不动**（不删/不改名/不搬）；
        /// ② 无用物扫描的判据与上限一个字都不变。</para>
        /// </summary>
        public bool RemindJunkAfterImport { get; set; } = true;

        /// <summary>
        /// 失败 / 取消 / 部分完成时**保留**中间产物（用户 2026-09-25 第 25 条追加，**默认关**）。
        ///
        /// <para><b>用户原话</b>：「我不希望有这么多的失败残留，还是这么说如果解压 40G，两层，
        /// 解压失败有 80G 的卸载残留，用户不得气死，你为什么要弄卸载残留，有一个导出失败列表
        /// 不就可以了吗，而且对于用户来说，失败了就失败了，成功了就成功了」。</para>
        ///
        /// <para>默认 <c>false</c> = 没成功就**一个中间产物都不留**：任务工作区（暂存目录、抠出来的内嵌
        /// 归档副本、已解出的过程物）与递归核心的逐层工作区整份删掉，只留日志与失败清单。
        /// 打开它才是老行为（留着现场便于排查，③「清理与删除」页能扫到、能清）。</para>
        ///
        /// <para>⚠ 这条设置**只影响"没成功"的收尾**：成功路径的清理口径一个字没变
        /// （仍然只有"解压成功 + 输出校验通过 + 未取消"才清），源包仍然一个字节都不动
        /// （不变量 1），已经定稿搬出去的内容物也早就不在工作区里了。</para>
        /// </summary>
        public bool KeepFailedWorkspace { get; set; }

        /// <summary>归集目标目录。</summary>
        public string CollectTargetDirectory { get; set; } = string.Empty;

        /// <summary>
        /// 续解时**省略中间层**（用户 2026-09-27 定的「简洁档」，他原话叫"压缩空白目录"）。
        ///
        /// <para>
        /// 链条：<c>111\222.rar</c>（最外层）里套 <c>333</c>→<c>444</c>→<c>555</c>→<c>666</c>（<c>666</c> 里才是内容物）：
        /// </para>
        /// <list type="bullet">
        /// <item><description><b>关（默认＝忠实档）</b>：<c>111\222\333\444\555\666\内容物</c> —— 每层包名都留；</description></item>
        /// <item><description><b>开（简洁档）</b>：<c>111\222\666\内容物</c> —— 只留**第一层**与**最后一层**，
        /// 中间那些"只产出下一个包、自己没有内容物"的层不建目录。</description></item>
        /// </list>
        ///
        /// <para>
        /// ⛔ 两条边界（用户当面确认）：①**只在单链时省略** —— 某一层里出现**并列的多个**内层包（分支）时，
        /// 那一层照建（退化成忠实档）并写一条日志说明，否则几个包的内容物会并到同一层；
        /// ②⛔ **永远不许省略第一层（源包名）与最后一层（真正装内容物的那个包）**。
        /// </para>
        ///
        /// <para>中间层里夹带的非包文件（小 .txt / 无用物）**收进其余物**（一个字节都不丢，日志写明）。</para>
        /// </summary>
        public bool OmitMiddleContinuationLayers { get; set; }

        /// <summary>
        /// 「特定解压」总开关（用户 2026-09-24 拍板：**①「任务」页、一键处理旁边**那一个开关）。
        ///
        /// <para>
        /// 用户原话："我是想在一键解压旁边弄一个特定解压开关，这样要弄特定解压你就在选项卡里面有解压方式，
        /// 在解压方式里面就可以去添加一个特定解压这一栏，也就是以后可能会经常加的东西，
        /// 因为每个人的特定的解压方式不同"。
        /// </para>
        ///
        /// <para>
        /// <b>默认关</b>（最不意外）：关着时 <see cref="SpecialExtractionRules"/> 里写什么都不参与运算，
        /// 一键处理与手动「只解压」的行为与加这条功能之前**逐字相同**（有测试钉住）。
        /// 规则本身在②「解压方式」页那一栏里逐条挑。
        /// </para>
        /// </summary>
        public bool UseSpecialExtraction { get; set; }

        /// <summary>
        /// 启用的**特定解压规则 Id**（②「解压方式」页那一栏里每条规则的开关）。
        ///
        /// <para>
        /// 存的是注册表（<see cref="ArchiveFixer.Extraction.SpecialExtractionRules"/>）里的稳定 Id
        /// （第一条 = <c>SingleContentLayer</c>）—— 与其它列表设置（<see cref="EnginePriority"/>、
        /// <see cref="PasswordBookPaths"/>）一样用字符串落盘：Id 比序号抗改，用户手改配置文件也看得懂。
        /// </para>
        ///
        /// <para>
        /// 容错口径（<see cref="Normalize"/> 里收口，唯一实现在
        /// <c>SpecialExtractionRules.Normalize</c>）：认不出的 Id **丢掉**（绝不因此报错、
        /// 也绝不把它当另一条规则跑）、去重、统一成注册表里的规范写法。
        /// <c>null</c>（旧配置里没有这个字段）→ 取默认集；**空列表**（用户把规则全关掉）→ 保持空
        /// （否则"关掉全部 = 与现在完全一样"存不住）。
        /// </para>
        /// </summary>
        public List<string>? SpecialExtractionRules { get; set; }

        /// <summary>
        /// 「源包操作」（决策 D-9，2026-09-22 用户拍板；**2026-09-25 收敛成两档**）。
        ///
        /// 存的是 <see cref="SourceHandlingMode"/> 的**枚举名**（<c>MoveToRest</c> / <c>KeepInPlace</c>），
        /// 与其它设置项（RecursionMode / OverwriteMode / TerminalLayoutMode）一样用字符串落盘 ——
        /// 枚举名比数字抗改，用户手改配置文件也看得懂。
        ///
        /// <para><b>默认 <c>KeepInPlace</c></b>（2026-09-25 第 32 条用户拍板："默认是源包原来位置不动"）：
        /// 程序默认**一个字节都不搬用户的源包**；要整理的人自己选"放入其余物当中"，
        /// 再由「删除操作」（<see cref="RestHandlingAfterVerify"/>）决定那份其余物是留着、进回收站还是彻底删。</para>
        ///
        /// <para><b>2026-09-25 第 32 条：原来的第三档 <c>DeleteAfterVerify</c> 被删掉</b>
        /// （用户原话：「源包操作里的第 3 项……删掉」）。要删源包请用
        /// "源包操作 = 放入其余物" + "删除操作 = 回收站 / 彻底删除"（见 <see cref="RestHandlingAfterVerify"/>）——
        /// 两档组合起来语义更清楚，选项也少一个。旧值在 <see cref="Normalize"/> 里迁移成
        /// "移入其余物 + 移入回收站"（可还原的那一档）。</para>
        ///
        /// <para>⚠ <b>两条路径行为一致</b>（用户 2026-09-22 版本二，推翻早先"地基路径永远不动源包"；
        /// 2026-09-25 再次确认手动档与一键档是同一批）：手动「只解压」与一键处理读的是同一套设置；
        /// <c>KeepInPlace</c> 才是"一个字节都不搬"的出口。</para>
        /// </summary>
        public string SourceHandling { get; set; } = nameof(SourceHandlingMode.KeepInPlace);

        /// <summary>
        /// 解析源包处理档：空 / 非法一律回落 <see cref="SourceHandlingMode.KeepInPlace"/>。
        ///
        /// 容错放在这里（与 <c>ParsePlacementMode</c> 同一口径）：这个字符串可能来自
        /// 旧配置（缺字段 → 反序列化后是默认值）、用户手改的 json，或将来改名后的枚举。
        /// 读不懂时**退回最不意外的那一档**，而不是到解压那一刻才报错或猜一个别的行为。
        ///
        /// <para>⚠ 回落档必须与 <see cref="SourceHandling"/> 的默认值 / <c>CreateDefault</c> **一致**
        /// （2026-09-25 第 32 条把默认档改成「留在原地」时，这里漏改过一处：一个手改坏的字符串会把
        /// 用户从没同意过的"搬走源包"打开 —— 而这一档是**不可逆**的）。读不懂 = 什么都不做。</para>
        /// </summary>
        public static SourceHandlingMode ParseSourceHandling(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                Enum.TryParse(value.Trim(), ignoreCase: true, out SourceHandlingMode mode) &&
                Enum.IsDefined(mode))
            {
                return mode;
            }

            return SourceHandlingMode.KeepInPlace;
        }

        /// <summary>反解成落盘字符串（界面 ↔ 解压管线共用同一份口径）。</summary>
        public static string ToSourceHandlingValue(SourceHandlingMode mode)
        {
            return mode.ToString();
        }

        /// <summary>
        /// 把"记住的密码本清单"收拾干净：去掉空项、去掉重复（大小写不敏感，Windows 路径口径），
        /// 并把老字段 <paramref name="legacyBookPath"/> 里那一本**补进清单**（迁移）。
        ///
        /// <para>顺序保留（第一本在最前）：合并密码本时"各本书的新条目按书顺序追加"依赖它。</para>
        /// </summary>
        public static List<string> NormalizeBookPaths(IEnumerable<string>? paths, string? legacyBookPath)
        {
            var result = new List<string>();

            void TryAdd(string? candidate)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    return;
                }

                string trimmed = candidate.Trim();

                foreach (string existing in result)
                {
                    if (string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }

                result.Add(trimmed);
            }

            if (paths != null)
            {
                foreach (string path in paths)
                {
                    TryAdd(path);
                }
            }

            // 迁移：旧配置只有 PasswordBookPath 一个字段（清空它会让用户回退版本后"没配过"）。
            TryAdd(legacyBookPath);

            return result;
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

                // 详细日志默认关：成功只留一行摘要（第 44 条追加）。显式写出来是为了让
                // "出厂默认"一眼可见，别让人以为它是漏掉的。
                VerboseLog = false,
                AutoScanAfterDrop = true,
                PreviewBeforeRename = true,
                OverwriteMode = "SkipExisting",
                TryEmptyPasswordFirst = true,
                UseGlobalPasswordForAllTasks = true,
                MaxParallelExtractCount = DefaultMaxParallelExtractCount,
                RunAtFullSpeed = false,
                LowProcessPriority = true,
                OpenOutputFolderWhenDone = false,
                RestRemovalDefaultMode = RestRemovalModes.RecycleBin,
                ReportDangerousEntries = true,
                RemindBeforeExtract = true,

                // 第 42 条的全自动拼装：出厂**关**（它会读源目录、并在盘上造名字 / 复制卷）。
                AssembleSplitVolumesFromContainer = false,

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
                DeleteSourceAfterExtract = false,
                RestHandlingAfterVerify = RestHandlingModes.Keep,
                CollectResultsToDirectory = false,
                CollectTargetDirectory = string.Empty,
                PasswordBookPath = string.Empty,
                PasswordBookPaths = new List<string>(),
                RememberPasswordList = true,
                SkipOneClickConfirm = false,
                RemindJunkAfterImport = true,
                RecursionMode = "SingleLayer",

                // 默认层数 = 5（用户 2026-09-26："从 10 改到 5 吧，用户有需要自己会改的"）。
                MaxRecursionDepth = 5,
                RecursionDefaultUnifiedToTen = true,
                MaxPasswordAttemptsPerLayer = 10,

                // 解压前的资源预算上限（用户 2026-09-25 第 36 条：四条都落进设置，默认值调到真实包碰不到的量级）。
                MaxSingleExtractedFileGiB = DefaultMaxSingleExtractedFileGiB,
                MaxExtractedTotalGiB = DefaultMaxExtractedTotalGiB,
                MaxExtractedFileCount = DefaultMaxExtractedFileCount,
                MaxExtractionRatio = DefaultMaxExtractionRatio,
                // 简洁档默认**关**（忠实档：每层包名都留）—— 用户 2026-09-27："我们有一个按钮可以让用户是否打开"
                OmitMiddleContinuationLayers = false,

                /*
                 * 特定解压（用户 2026-09-24）：总开关默认**关**，规则清单预置默认集
                 * （由注册表自己说哪几条默认勾上 —— 以后加规则时这里一个字都不用改）。
                 * 两者都要落到新配置里：只写默认值而不写清单，"关掉全部"与"从没配过"就分不开了。
                 */
                UseSpecialExtraction = false,
                SpecialExtractionRules = ArchiveFixer.Extraction.SpecialExtractionRules.DefaultEnabledIds.ToList(),
                SourceHandling = nameof(SourceHandlingMode.KeepInPlace),

                // 失败 / 取消不留中间产物（用户 2026-09-25 第 25 条追加）：默认关闭，
                // 老配置里没有这个字段时反序列化出来也是 false —— 与默认档一致，不需要迁移标记。
                KeepFailedWorkspace = false
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
             * 危险模式退役（2026-09-25 第 32 条）：旧配置里那个开关与自测凭证**一次性迁移**成新的
             * 「删除操作」档位，然后两个旧字段一律写回关闭/空 —— 从此它们只是"读得懂旧 json"的占位。
             *
             * 迁移方向（保守优先）：开过危险模式的用户，意图就是"成功后彻底删掉其余物" →
             * 迁移成 <see cref="RestHandlingModes.Delete"/>；没开过的保持默认 Keep。
             * 旧的手动删源开关 <see cref="DeleteSourceAfterExtract"/>=true → "源包移入其余物 + 移入回收站"
             * （**可还原的那一档**：它原来管的是不可逆删除，但我们不能替用户把不可逆的选择重新做一遍）。
             */
            if (DangerousSpaceModeEnabled)
            {
                RestHandlingAfterVerify = RestHandlingModes.Delete;
            }

            if (DeleteSourceAfterExtract)
            {
                SourceHandling = nameof(SourceHandlingMode.MoveToRest);

                if (!string.Equals(RestHandlingAfterVerify, RestHandlingModes.Delete, StringComparison.OrdinalIgnoreCase))
                {
                    RestHandlingAfterVerify = RestHandlingModes.RecycleBin;
                }
            }

            DangerousSpaceModeEnabled = false;
            DangerModeSelfTestStamp = string.Empty;
            DeleteSourceAfterExtract = false;

            // 源包处理档：旧值 DeleteAfterVerify（第三档，2026-09-25 已删）→ 移入其余物 + 移入回收站。
            if (string.Equals(SourceHandling?.Trim(), "DeleteAfterVerify", StringComparison.OrdinalIgnoreCase))
            {
                SourceHandling = nameof(SourceHandlingMode.MoveToRest);

                if (string.Equals(RestHandlingAfterVerify, RestHandlingModes.Keep, StringComparison.OrdinalIgnoreCase))
                {
                    RestHandlingAfterVerify = RestHandlingModes.RecycleBin;
                }
            }

            /*
             * 「删除操作」三档：空 / 非法一律回落 **Keep（不动其余物）** —— 保守方向是"宁可不动，绝不误删"。
             * 判定只有一处实现（RestHandlingModes.Normalize），这里不另写一套字符串比较。
             */
            RestHandlingAfterVerify = RestHandlingModes.Normalize(RestHandlingAfterVerify);

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

            /*
             * 落点旧配置迁移（用户 2026-09-24 第 13 条：删掉"解压到当前目录"与"直接解到指定目录"两档）。
             *
             * 落盘一直是**两个布尔**（ExtractToOriginalDirectory + KeepArchiveNameFolder），
             * 旧的那两档就是 (true,false) 与 (false,false)。它们现在已经没有对应档位了，
             * 所以这里把 KeepArchiveNameFolder 归一成 true：
             * · 旧配置读进来**不炸**，行为落到用户要的四条规则上（源目录家族 → 111\222\；
             *   指定位置 → 顺手保留那个根，但补上同名子文件夹）；
             * · 归一化放在设置层，与 ConflictAction / SourceHandling 同一口径 ——
             *   到解压那一刻才发现"这个组合读不懂"是最糟的，那时用户已经点了一键处理。
             * ⚠ 刻意**不**动 ExtractToOriginalDirectory：它决定"有没有指定位置"，是两档的判据本身。
             */
            KeepArchiveNameFolder = true;

            CustomOutputDirectory ??= string.Empty;
            CustomSevenZipExePath ??= string.Empty;
            CustomUnRarExePath ??= string.Empty;
            CustomRarExePath ??= string.Empty;
            CollectTargetDirectory ??= string.Empty;
            PasswordBookPath ??= string.Empty;

            /*
             * 「失败时保留中间产物」（用户 2026-09-25 第 25 条追加）：**bool 不需要归一化**。
             *
             * 它只有开 / 关两种取值，读不出第三种；而旧配置里没有这个字段时，
             * System.Text.Json 反序列化后就是属性默认值 false —— 与"默认档 = 失败不留残留"完全一致。
             * 所以这里**一个字都不写**：刻意留这段说明，免得以后有人顺手加一句
             * `KeepFailedWorkspace = false;` 把用户打开的那一档每次启动都顶掉。
             */

            /*
             * 密码本清单（用户 2026-09-24：多本密码本）。
             *
             * 迁移放在这里，与其它设置项的容错同一口径：旧配置里只有单个 PasswordBookPath，
             * 读出来是个 null 列表 —— 这里把它补进去，老用户升级后的"自动加载哪一本"行为不变。
             *
             * ⚠ 刻意**不清空老字段** PasswordBookPath：用户回退到旧版本时，那一边仍然靠它认路
             *（清空 = 回退后"密码本没配过"，等于把用户的配置吃掉一次）。
             * 于是这份清单在每次 Normalize 时都会把老字段的值并进来 —— 用 Contains 去重，幂等。
             */
            PasswordBookPaths = NormalizeBookPaths(PasswordBookPaths, PasswordBookPath);
            PasswordBookPath = PasswordBookPaths.Count > 0 ? PasswordBookPaths[PasswordBookPaths.Count - 1] : string.Empty;

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
             * 简洁档（续解省略中间层，用户 2026-09-27）：布尔项，非法值只可能是"缺字段" ——
             * 缺字段时保持属性默认值 false（忠实档），这里不需要额外归一化。
             */
            OmitMiddleContinuationLayers = OmitMiddleContinuationLayers;

            /*
             * 源包处理档（决策 D-9）：空 / 非法一律回落默认档 MoveToRest，**旧配置不报错**。
             *
             * 这里刻意**不**把旧的 DeleteSourceAfterExtract=true 迁移成 DeleteAfterVerify：
             * 那个布尔管的是"手动只解压"这条地基路径的清理，与一键处理的整理档是两件事；
             * 迁移它会让升级后的一键处理从"移动"变成"不可逆删除"，方向正好反了。
             * （一键处理的新默认值是"移动"——比删除保守，用户随时可以在设置里改成删除。）
             */
            SourceHandling = ParseSourceHandling(SourceHandling).ToString();

            /*
             * 特定解压规则清单（用户 2026-09-24）：认不出的 Id 丢掉、去重、统一写法、按注册表顺序排。
             *
             * 归一化放在设置层，与 EnginePriority / SourceHandling 同一口径：到定稿那一刻才发现
             * "这个 Id 读不懂"是最糟的 —— 用户已经点了一键处理，规则却要临时猜一条来跑。
             * 判定只有一处实现（SpecialExtractionRules.Normalize），这里不另写一套字符串比较。
             *
             * ⚠ null 与空列表是**两件事**：null = 旧配置里没有这个字段 → 取默认集；
             *   空列表 = 用户把规则全关掉了 → 保持空（"关掉全部 = 与现在完全一样"必须存得住）。
             */
            SpecialExtractionRules = ArchiveFixer.Extraction.SpecialExtractionRules
                .Normalize(SpecialExtractionRules);

            /*
             * 一次性迁移（用户 2026-09-24 拍板"两个上限统一成 10"）：
             *
             * 旧默认值是 3，而**从没主动选过 3** 的用户读到的那个 3 其实是"旧默认"，不是他的选择 ——
             * 光把默认值改成 10 对他没用（他的设置文件里存着 3）。所以这里迁一次，并落一个标记：
             * 迁移只发生一次，他之后在②页把 3 改回来就是他的选择，不会被再次顶掉。
             */
            if (!RecursionDefaultUnifiedToTen)
            {
                if (MaxRecursionDepth == 3)
                {
                    MaxRecursionDepth = 10;
                }

                RecursionDefaultUnifiedToTen = true;
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

            /*
             * 解压前的资源预算上限（用户 2026-09-25 第 36 条）：四条都夹回合法区间。
             * 夹的规则只有一处实现（上面那四个 NormalizeXxxCap），`ResourceBudgetOptions.FromSettings`
             * 读的时候用的是同一批函数 —— 界面、落盘、真正判包三处口径不许分叉。
             */
            MaxSingleExtractedFileGiB = NormalizeSingleFileCapGiB(MaxSingleExtractedFileGiB);
            MaxExtractedTotalGiB = NormalizeTotalCapGiB(MaxExtractedTotalGiB, MaxSingleExtractedFileGiB);
            MaxExtractedFileCount = NormalizeExtractedFileCountCap(MaxExtractedFileCount);
            MaxExtractionRatio = NormalizeExtractionRatioCap(MaxExtractionRatio);

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

            /*
             * 自定义 Rar.exe 路径同理（打包的外层 rar 用）。清空它只是回到"自动解析"，
             * 不会让打包不可用 —— 界面上还可以把外层容器改成 7z 或"不做外层"。
             *
             * ⚠ 这里**不做任何"帮用户找一份 Rar.exe 塞进来"的事**：那是分发共享软件的第一步
             * （AGENTS.md §3.1）。这一格永远是用户自己填的。
             */
            if (!string.IsNullOrWhiteSpace(CustomRarExePath) && !System.IO.File.Exists(CustomRarExePath))
            {
                CustomRarExePath = string.Empty;
            }
        }
    }
}
