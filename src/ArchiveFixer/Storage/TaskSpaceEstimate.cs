using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ArchiveFixer.Engines;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;

namespace ArchiveFixer.Storage
{
    /// <summary>
    /// 一个解压任务的**空间需求**（用户 2026-09-22 需求："空间核算必须包含内容物 + 本次会产生的过程物
    /// （分卷、内层包）+ 去重后的峰值需求"）。
    ///
    /// <para><b>为什么需要它</b>：<c>Security/ResourceBudget</c> 回答的是"这个包该不该解"（硬上限），
    /// 而用户真正会撞上的是"解到一半盘满了"。要提前拦住，就必须有一个**任务级的空间账面**。</para>
    ///
    /// <para><b>三个组成（去重后）</b>：</para>
    /// <list type="number">
    /// <item><description><see cref="SourceBytes"/> —— 本任务源包**整组**（分卷求和）。
    /// 解压期间它一直占着盘：成功之后只是**搬进 `其余物`**（同盘移动不释放空间），
    /// 只有①页「空间不足」模式才会在定稿 + 校验通过之后真的把它删掉（那份空间才回到可用空间里）。</description></item>
    /// <item><description><see cref="ContentBytes"/> —— 内容物。清单拿得到时是解压后总大小（与
    /// <c>ResourceBudget</c> 同一份 list、同一套累加口径）；拿不到时按源包体积估**下界**并标注
    /// <see cref="ContentEstimated"/>。</description></item>
    /// <item><description><see cref="ProcessArtifactBytes"/> —— 本次会**额外**产生的过程物：
    /// 内嵌归档抠出来的过程物（源文件尾部那一段的副本）+ 内容物里的内层包**再展开**的增量
    /// （内层包自身已经算在 <see cref="ContentBytes"/> 里，这里只算它展开后多出来的那一份）。</description></item>
    /// </list>
    ///
    /// <para><b>去重规则（三条，缺一条就会把需求算大一倍）</b>：</para>
    /// <list type="number">
    /// <item><description>同一个**全路径**只算一次：源包与它的各卷按路径去重（改名前后的同一个文件不重复计）。</description></item>
    /// <item><description>分卷各卷只进 <see cref="SourceBytes"/>，**绝不**再按"过程物"算第二遍
    /// （旧 `解压7z分卷文件.bat` 的场景里，分卷既被当成源包又被当成过程物，很容易重复计）。</description></item>
    /// <item><description>内层归档自身已在 <see cref="ContentBytes"/> 里，<see cref="ProcessArtifactBytes"/>
    /// 只计它的**净增量**（展开后 − 包本体 ≈ 0，见 <see cref="SpaceEstimator.NestedExpansionAllowance"/>；
    /// 用户 2026-10-03 第 ③ 条把老口径的"整份再扣一遍"改掉了）。</description></item>
    /// </list>
    ///
    /// <para><b>峰值（描述）</b>：<see cref="PeakBytes"/> = 源包 + 过程物 + 内容物，即"这一切同时存在"的那一刻。
    /// ⛔ 它是**描述**不是**判据**：盘上"还能用多少"（可用空间）本来就不含源包，
    /// 拿峰值去比可用空间等于把源包算两遍（2026-09-29 真机事故，见 <see cref="FreeSpaceDemandBytes"/>）。</para>
    ///
    /// <para><b>判据（唯一出口）</b>：<see cref="FreeSpaceDemandBytes"/> = 内容物 + 过程物 ——
    /// 这一次真正要从可用空间里**新写**的字节数。放行 / 拦下、并发累计、日志里的"需要多少"全用它。</para>
    ///
    /// <para><b>峰值与"同卷 / 跨卷"的关系（用户 2026-09-30 拍板改口径）</b>：工作区默认落在**这一单的
    /// 目标目录里面**（<c>&lt;目标目录&gt;\.ArchiveFixer.work</c>），于是"暂存内容物"与"成品"落在
    /// **同一棵树的同一块盘**上。
    /// 这一件事必须如实算进去，因为旧注释里那句"定稿搬运是同盘移动（不额外占）"当时只是**假设**
    /// （工作区在程序盘、成品在输出盘时它并不成立，只是恰好数字还能对上）：</para>
    /// <list type="bullet">
    /// <item><description><b>同卷</b>（默认档）：暂存与成品是同一份字节 —— 定稿走的是同卷**改名**
    /// （<c>File.Move</c> 同卷 = rename），所以内容物在峰值里**只算一份**；峰值那一刻是
    /// "内容物已解进工作区、源包还在这块盘上"。</description></item>
    /// <item><description><b>跨卷</b>（这一批的目标目录跨盘：工作区跟着第一个目标目录，
    /// 落在别的盘上的那些任务定稿就要跨盘）：
    /// 定稿那一步是**复制 + 删原件**，成品盘在那一刻会多出一份内容物；而**工作区盘**另外还要留得下
    /// <see cref="StagingBytes"/>（内容物 + 过程物）。当前账本只按成品盘核算 ——
    /// 工作区盘那一半是**已知限制**，日志里必须说明，不许假装它也被核过。</description></item>
    /// </list>
    /// <para><see cref="WorkspaceSharesTargetVolume"/> 为 null（取不到落点或工作区根）时**不做同卷假设**，
    /// 说明文字也照实写"未知"。</para>
    /// </summary>
    public sealed class TaskSpaceEstimate
    {
        /// <summary>任务路径（源包路径，或分卷组第一卷）。只用于日志与去重，不参与判断。</summary>
        public string TaskPath { get; init; } = string.Empty;

        /// <summary>给用户看的名字（文件名）。</summary>
        public string DisplayName { get; init; } = string.Empty;

        /// <summary>源包整组字节数（分卷求和；路径去重后）。</summary>
        public long SourceBytes { get; init; }

        /// <summary>内容物字节数。清单拿不到时是按源包体积估的下界（见 <see cref="ContentEstimated"/>）。</summary>
        public long ContentBytes { get; init; }

        /// <summary>本次会额外产生的过程物字节数（内嵌归档过程物 + 内层包再展开的增量）。</summary>
        public long ProcessArtifactBytes { get; init; }

        /// <summary>是不是"清单读不到、内容物按源包体积估的"。</summary>
        public bool ContentEstimated { get; init; }

        /// <summary>估算用没用引擎给的条目清单。</summary>
        public bool HasListing { get; init; }

        /// <summary>估算依据的一句话（进日志与任务详情，让用户能自己复核这个数字怎么来的）。</summary>
        public string Basis { get; init; } = string.Empty;

        /// <summary>
        /// 任务完成前**一直占着盘**的那部分（源包 + 过程物）。
        ///
        /// <para>①页「空间不足」模式"边解边删源包"能立刻收回的就是它 —— 这也是那个模式能解决"空间不够"的
        /// 全部原因：普通档下这些字节只是从源目录搬进了 `其余物`，净占用一点没变。</para>
        ///
        /// <para>⛔ **它不进放行判据**（判据是 <see cref="FreeSpaceDemandBytes"/>）：源包本来就在盘上、
        /// 从来不在"可用空间"里，把它加进"需要"就是算两遍。</para>
        /// </summary>
        public long RetainedBytes => SaturatingSum(SourceBytes, ProcessArtifactBytes);

        /// <summary>
        /// 本任务会写进**工作区**的字节数（暂存内容物 + 过程物）。
        ///
        /// <para>它是"跨卷时工作区那块盘至少要留得下多少"的那个数（见类注释里的同卷 / 跨卷说明）。
        /// 同卷（默认档）时它已经包含在 <see cref="PeakBytes"/> 里，**不额外再加一遍** ——
        /// 加一遍等于凭空把需求算大一倍。</para>
        ///
        /// <para>它同时就是**放行判据**那个数（<see cref="FreeSpaceDemandBytes"/> 直接引它）：
        /// 工作区默认跟着输出盘走，于是"新写进工作区"= "从目标盘可用空间里吃掉"。</para>
        /// </summary>
        public long StagingBytes => SaturatingSum(ContentBytes, ProcessArtifactBytes);

        /// <summary>
        /// 工作区根与成品落点是不是**同一个卷**（null = 取不到落点或工作区根，未知）。
        ///
        /// <para>true = 暂存与成品同盘（定稿是同卷改名，内容物只算一份）；
        /// false = 跨卷（定稿是复制，见类注释）。这个事实由调用方给出
        /// （<c>ExtractionCoordinator</c> 拿当前生效的工作区根与实际落点比盘根），
        /// 估算器本身不碰文件系统、也不读设置。</para>
        /// </summary>
        public bool? WorkspaceSharesTargetVolume { get; init; }

        /// <summary>盘上**总占地**：源包 + 过程物 + 内容物同时存在的那一刻（描述用，⛔ 不是放行判据）。</summary>
        public long PeakBytes => SaturatingSum(RetainedBytes, ContentBytes);

        /// <summary>
        /// **放行判据（唯一出口）**：这一次真正要从**目标盘可用空间**里吃掉多少字节
        /// = 内容物 + 过程物（= <see cref="StagingBytes"/>）。
        ///
        /// <para><b>⛔ 绝不加源包。</b>可用空间这个数**已经**把盘上现有的东西（含源包）排除在外：
        /// 源包占着的字节从来不在"可用"里，再把它算进"需要"就是把源包算两遍。
        /// 盘上塞得下的恒等式是"本次新写 ≤ 可用 − 要保留的余量"，而"本次新写"只有内容物与过程物
        /// （源包原地不动、或者搬进**同盘**的 `其余物`，都不产生新字节）。</para>
        ///
        /// <para><b>为什么 2026-09-29 之前是错的</b>（用户真机当场报"普通人随便都能解压"）：
        /// 老口径拿 <see cref="PeakBytes"/>（含源包）去比可用空间 ——
        /// 三卷共 19,008,319,874 字节、目标盘可用 33.45 GiB 的那一组，明明只要 17.70 GiB 就能解出来
        /// （余 15.25 GiB），却被按 35.41 GiB 判成"整盘都放不下"（差 2.46 GiB），一个字节都没解。</para>
        ///
        /// <para>并发的账也按它记：同时在跑的几个任务，各自"新写"的量之和才是那一刻盘上多出来的量。
        /// ①页「空间不足」模式**不需要另立一套判据** —— 源包在两种档下都不进需求；
        /// 那个模式的差别只体现在"跑完把源包收回来、下一个包更宽"（账本每个任务收尾都真实重探可用空间）。</para>
        ///
        /// <para><b>唯一的例外 = 部分完成发布（<see cref="CountsSourceAsNewOccupancy"/>）</b>：
        /// 那一档跑完源包**一定还在盘上**（用户定的红线），也就是说这一单跑完盘上会多留一整个源包的占用
        /// ⇒ 需求要加上它。⛔ 不加就等于把"两份"的承诺按"一份"排计划（用户 2026-10-02 顾虑 2 点名的
        /// 危险组合：空间不足 + 大容量 + 多份相同文件一起开）。</para>
        /// </summary>
        public long FreeSpaceDemandBytes =>
            CountsSourceAsNewOccupancy ? SaturatingSum(StagingBytes, SourceBytes) : StagingBytes;

        /// <summary>
        /// **部分完成发布开着**：这一单跑完源包仍然占着盘（那一档一律不搬不删源包）
        /// ⇒ <see cref="FreeSpaceDemandBytes"/> 要把整份源包算进来。
        ///
        /// <para>它只影响"要不要启动这个任务"这一个判断；<see cref="PeakBytes"/> /
        /// <see cref="ReclaimableBytes"/> 这些描述性的数一个都不改（口径仍然是"盘上同时有什么"）。</para>
        /// </summary>
        public bool CountsSourceAsNewOccupancy { get; init; }

        /// <summary>
        /// ①页「空间不足」模式在定稿 + 校验通过之后能收回的字节数（源包 + 过程物）。
        ///
        /// <para>它**不参与放行判断**（那一刻这些字节还在盘上），只用于两处：
        /// 建议文案里的"开那个模式可以少要多少"，以及空间不足模式自己的排序口径
        /// （净占用 = <see cref="PeakBytes"/> − 它，见 <c>ExtractionScheduler.Build</c> 的 sortKey）。</para>
        /// </summary>
        public long ReclaimableBytes => RetainedBytes;

        /// <summary>
        /// **净占用**：这个任务跑完（源包与过程物都已按模式回收）之后真正留在盘上的字节数。
        ///
        /// <para>= <see cref="PeakBytes"/> − <see cref="ReclaimableBytes"/> = <see cref="ContentBytes"/>。
        /// 空间不足模式按它**从小到大**排执行顺序：先解"解完占地最少"的包，盘上越跑越宽。</para>
        /// </summary>
        public long NetOccupancyBytes => ContentBytes;

        /// <summary>
        /// 把"同卷 / 跨卷"这个事实补进这一份估算（返回**新的一份**，不原地改）。
        ///
        /// <para>为什么要单独一步：粗估发生在排计划的时候（那时还没有精确落点），
        /// 精估发生在解压前的预检里（那时落点已经定下来了并按冲突档让过位）。
        /// 两处都要带上这个事实，而事实的口径只有一处实现（<see cref="DescribeVolumeLayout"/>）。</para>
        /// </summary>
        public TaskSpaceEstimate WithVolumeLayout(bool? workspaceSharesTargetVolume)
        {
            string note = DescribeVolumeLayout(workspaceSharesTargetVolume);

            // 事实一样、依据里也已经写着这句：原样返回（两处估算会重复调用它）。
            if (WorkspaceSharesTargetVolume == workspaceSharesTargetVolume &&
                Basis.Contains(note, StringComparison.Ordinal))
            {
                return this;
            }

            return new TaskSpaceEstimate
            {
                TaskPath = TaskPath,
                DisplayName = DisplayName,
                SourceBytes = SourceBytes,
                ContentBytes = ContentBytes,
                ProcessArtifactBytes = ProcessArtifactBytes,
                ContentEstimated = ContentEstimated,
                HasListing = HasListing,
                CountsSourceAsNewOccupancy = CountsSourceAsNewOccupancy,
                WorkspaceSharesTargetVolume = workspaceSharesTargetVolume,
                Basis = string.IsNullOrWhiteSpace(Basis) ? note : Basis + "；" + note
            };
        }

        /// <summary>
        /// "工作区与成品同不同卷"的一句话（**口径的唯一来源**，估算依据、空间门日志、文档都引它）。
        /// 未知的一档照样要说清"未知"，绝不默认成同卷。
        /// </summary>
        public static string DescribeVolumeLayout(bool? workspaceSharesTargetVolume)
        {
            if (workspaceSharesTargetVolume == true)
            {
                return "工作区与成品同卷：暂存与成品是同一份字节（定稿走同卷改名），"
                       + "内容物在峰值里只算一份；峰值那一刻 = 内容物已解进工作区、源包还在这块盘上";
            }

            if (workspaceSharesTargetVolume == false)
            {
                return "工作区与成品跨卷：定稿那一步是复制 + 删原件，成品盘会多出一份内容物；"
                       + "工作区盘另外还要留得下 内容物 + 过程物（当前账本只按成品盘核算，"
                       + "工作区盘那一半是已知限制）";
            }

            return "工作区与成品是否同卷未知（取不到落点或工作区根）：不做同卷假设，按最保守的口径算";
        }

        /// <summary>饱和加法：回绕成负数等于把空间判断整个废掉（"需要 -2G"永远放行）。</summary>
        public static long SaturatingSum(long left, long right)
        {
            long a = left > 0 ? left : 0L;
            long b = right > 0 ? right : 0L;

            return a > long.MaxValue - b ? long.MaxValue : a + b;
        }

        /// <summary>
        /// 一句话说法（日志/确认框用；数字一律走与 <see cref="SpaceChecker"/> 一致的口径）。
        ///
        /// <para>两个数都要说出来，而且要说清哪个是**判据**：只写"峰值"时用户会拿它去对可用空间，
        /// 然后得出"17.7 GiB 的包怎么说需要 35.41 GiB"（2026-09-29 真机就是这么被看出来的）。</para>
        /// </summary>
        public string Describe()
        {
            string name = string.IsNullOrWhiteSpace(DisplayName) ? TaskPath : DisplayName;

            return $"{name}：源包 {FormatSize(SourceBytes)} + 内容物 {FormatSize(ContentBytes)}"
                   + (ContentEstimated ? "（估）" : string.Empty)
                   + $" + 过程物 {FormatSize(ProcessArtifactBytes)}"
                   + $"（盘上总占地 {FormatSize(PeakBytes)}；"
                   + $"本次要从可用空间里新写 {FormatSize(FreeSpaceDemandBytes)}）"
                   + (WorkspaceSharesTargetVolume == false
                       ? $"（工作区与成品跨卷：工作区那块盘另需 {FormatSize(StagingBytes)}）"
                       : string.Empty);
        }

        /// <summary>
        /// 把字节数说成人话。**口径必须与 <c>SpaceChecker</c> / <c>ResourceBudget</c> 一致** ——
        /// 三处各写一套换算时，同一块盘在界面上会出现两个不同的数字（历史上因此被投诉过）。
        /// </summary>
        internal static string FormatSize(long bytes)
        {
            long value = bytes > 0 ? bytes : 0L;
            double gib = value / 1024d / 1024d / 1024d;

            if (gib >= 1d)
            {
                return gib.ToString("0.##", CultureInfo.InvariantCulture) + " GiB";
            }

            double mib = value / 1024d / 1024d;

            if (mib >= 1d)
            {
                return mib.ToString("0.##", CultureInfo.InvariantCulture) + " MiB";
            }

            double kib = value / 1024d;

            if (kib >= 1d)
            {
                return kib.ToString("0.##", CultureInfo.InvariantCulture) + " KiB";
            }

            return value.ToString(CultureInfo.InvariantCulture) + " 字节";
        }
    }

    /// <summary>
    /// 任务空间需求的**唯一**估算处（<c>Storage\</c>；不引用 WPF）。
    ///
    /// <para>两级精度，刻意分开：</para>
    /// <list type="number">
    /// <item><description><b>粗估</b>（<see cref="FromSourceFiles"/>，只 stat 文件，不跑引擎）：
    /// 调度排序、并发建议、启动前的第一道空间门用它 —— 排 50–200 个包时绝不能为排序去逐个列目录。</description></item>
    /// <item><description><b>精估</b>（<see cref="RefineWithListing"/>，解压前那一遍 list 的产物）：
    /// 拿到清单后用真实的内容物大小替换估算值，这是**硬门**用的数字（同一份 list 也喂给
    /// <c>ResourceBudget</c>，绝不为了它多跑一次 7z —— 加密包每多列一次目录就多一次失败机会）。</description></item>
    /// </list>
    /// </summary>
    public static class SpaceEstimator
    {
        /// <summary>
        /// 清单读不到时，内容物按源包体积的几倍估。
        ///
        /// <para>取 1.0：资源包绝大多数是"已经压过一遍的东西再打包一次"（视频 / 图片 / 已压缩的内层包），
        /// 解压后总量与压缩包同量级。这是**下界估计**，不是承诺 —— 所以：</para>
        /// <list type="bullet">
        /// <item><description>它只用于**排序与并发建议**，以及"启动前"那道门；</description></item>
        /// <item><description>真正的硬门是解压前那一遍 list 算出来的精确值（<see cref="RefineWithListing"/>）；</description></item>
        /// <item><description>盘上还要求保留 <c>ResourceBudgetOptions.MinFreeSpaceReserveBytes</c>（默认 512 MiB）余量。</description></item>
        /// </list>
        /// </summary>
        public const double UnknownListingContentAllowance = 1.0d;

        /// <summary>
        /// 内层包**再展开**的增量按它自身体积的几倍估（0 = 不计）。
        ///
        /// <para><b>取 0 = 按「净增量」估（用户 2026-10-03 拍板，第 ③ 条）</b>：要的是
        /// <b>展开后 − 包本体</b>那一份，而不是"整份再扣一遍"。内层包**自身**已经算在
        /// <see cref="TaskSpaceEstimate.ContentBytes"/> 里，而它展开出来的内容与它同量级
        /// （资源包本来就是"已经压过一遍的东西再打包一次"）⇒ 净增量 ≈ 0。</para>
        ///
        /// <para><b>为什么老口径（1.0）是错的</b>：等于把内层包算了两遍（内容物里一遍 + 增量里一遍），
        /// 四层链的需求会被抬到"层数 × 单层"；朋友那台真机就是这么被抬高的
        /// （程序要 57.29 GiB、真实峰值 42.85 GiB —— 见 <c>_tmp\方案-普通档逐层回收其余物.md</c> §1）。</para>
        ///
        /// <para><b>为什么敢取 0</b>：普通档从 2026-10-03 起**逐层回收**（<c>ExtractionCoordinator</c>
        /// 在每一层定稿 + 校验通过之后当场删掉这一层的过程物），盘上同一时刻只有"当前层 + 下一层"
        /// ⇒ 展开内层包时，上一层的那些字节已经还回来了，不需要为"所有内层包同时展开"留余量。</para>
        ///
        /// <para>⚠ 下界仍然守得住：放行判据 <see cref="TaskSpaceEstimate.FreeSpaceDemandBytes"/>
        /// 是"内容物 + 过程物"的饱和加法，⛔ **永远 ≥ 内容物**；真正的硬门照旧是解压前那一遍
        /// list 算出来的精确值（<see cref="RefineWithListing"/>）与 <c>ResourceBudget</c>。</para>
        /// </summary>
        public const double NestedExpansionAllowance = 0.0d;

        /// <summary>
        /// 粗估：只读文件大小，不碰引擎。分卷组按 <see cref="ArchiveTask.VolumePaths"/> 求和（路径去重）。
        /// </summary>
        /// <param name="directReadAvailable">
        /// 这个任务的内嵌归档能不能走**直读**（<c>Extraction/EmbeddedZipStreamExtractor</c>）。
        /// <para>为 true 时**不记**那笔抠取副本 —— 直读路线一个字节的过程物都不产生
        /// （用户 2026-09-24 需求第 7 条：账面上必须与真实发生的动作一致，
        /// 不许一边说"省了副本"一边又把它预留出来）。判别由调用方给
        /// （<c>ArchiveTask.EmbeddedDirectReadSupported</c> + 这一批会不会走递归模式），
        /// 估算器本身不碰文件、不读设置。真回落时由抠取器自己那道空间预检兜底：
        /// 那一刻会如实报"取出内嵌归档需要约 X MB 临时空间"，而不是把盘写满。</para>
        /// </param>
        /// <param name="countsSourceAsNewOccupancy">
        /// 本批开着「部分完成发布」（③页那个开关）—— 那一档跑完源包**一定还在盘上**，
        /// 所以放行判据要按"多留一整个源包"算（见 <see cref="TaskSpaceEstimate.CountsSourceAsNewOccupancy"/>）。
        /// 精估（<see cref="RefineWithListing"/>）从这一份**继承**这个事实，⛔ 不需要也不许再传一次
        /// （同一件事的判据只允许有一个来源）。
        /// </param>
        public static TaskSpaceEstimate FromSourceFiles(
            ArchiveTask? task,
            bool directReadAvailable = false,
            bool countsSourceAsNewOccupancy = false)
        {
            if (task == null)
            {
                return new TaskSpaceEstimate { Basis = "没有任务，无法估算" };
            }

            IReadOnlyList<string> files = CollectSourceFiles(task);
            long sourceBytes = SumFileSizes(files, out int measuredCount, out int missingCount);

            /*
             * 内容物的下界估计 = 源包体积 × 1.0（见 UnknownListingContentAllowance）。
             * 用"下界"这三个字是认真的：它不保证够，只保证不会把明显放不下的任务排上去。
             */
            long contentEstimate = (long)Math.Min(
                long.MaxValue,
                sourceBytes * UnknownListingContentAllowance);

            long carvedBytes = EstimateCarvedBytes(task, sourceBytes, directReadAvailable);

            string basis =
                $"扫到 {measuredCount} 个源文件（{TaskSpaceEstimate.FormatSize(sourceBytes)}）"
                + (missingCount > 0 ? $"，其中 {missingCount} 个读不到大小" : string.Empty)
                + $"，内容物按源包 {UnknownListingContentAllowance:0.##} 倍估（清单还没读，属于下界）"
                + DescribeCarvedBytes(carvedBytes, task, directReadAvailable);

            return new TaskSpaceEstimate
            {
                TaskPath = task.CurrentPath,
                DisplayName = string.IsNullOrWhiteSpace(task.FileName)
                    ? Path.GetFileName(task.CurrentPath)
                    : task.FileName,
                SourceBytes = sourceBytes,
                ContentBytes = contentEstimate,
                ProcessArtifactBytes = carvedBytes,
                ContentEstimated = true,
                HasListing = false,
                CountsSourceAsNewOccupancy = countsSourceAsNewOccupancy,
                Basis = basis
            };
        }

        /// <summary>
        /// 精估：用解压前那一遍 list 替换内容物与过程物，源包大小用**真正交给引擎的那个文件**
        /// （内嵌归档抠出来之后它比源文件小得多，拿源文件当分母会把展开比算小）。
        /// </summary>
        /// <param name="cheap">粗估（提供源包字节数；为 null 时只按清单算）。</param>
        /// <param name="list">引擎给的条目清单；null / Success=false 表示读不到 —— **不假装知道**。</param>
        public static TaskSpaceEstimate RefineWithListing(
            TaskSpaceEstimate? cheap,
            ArchiveListResult? list,
            long carvedBytes = 0,
            bool carvedBytesNotNeeded = false)
        {
            TaskSpaceEstimate basis = cheap ?? new TaskSpaceEstimate();

            if (list == null || !list.Success)
            {
                /*
                 * 清单读不到（加密头 / 引擎拒绝列目录 / 流式）：**保留粗估**，一个字都不许假装知道。
                 * 只把"已经量出来的内嵌归档过程物"补进去 —— 那个字节数是真量出来的。
                 */
                return new TaskSpaceEstimate
                {
                    TaskPath = basis.TaskPath,
                    DisplayName = basis.DisplayName,
                    SourceBytes = basis.SourceBytes,
                    ContentBytes = basis.ContentBytes,
                    ProcessArtifactBytes = Math.Max(basis.ProcessArtifactBytes, carvedBytes > 0 ? carvedBytes : 0L),
                    ContentEstimated = true,
                    HasListing = false,
                    CountsSourceAsNewOccupancy = basis.CountsSourceAsNewOccupancy,
                    Basis = basis.Basis + "；本次仍没拿到条目清单，内容物维持估算值"
                };
            }

            (long contentBytes, long innerArchiveBytes) = SumListing(list);

            /*
             * 内层包再展开的**净增量**（用户 2026-10-03 第 ③ 条）：内层包**自身**已经在 contentBytes 里，
             * 这里只加"展开后 − 包本体"那一份 —— 见 NestedExpansionAllowance 的说明。
             * 没有内层包条目时是 0 —— 不凭空加。
             */
            long nestedExtra = (long)Math.Min(
                long.MaxValue,
                innerArchiveBytes * NestedExpansionAllowance);

            long processBytes = TaskSpaceEstimate.SaturatingSum(carvedBytes, nestedExtra);

            string basis2 =
                $"清单 {list.FileCount} 个文件 / 解压后 {TaskSpaceEstimate.FormatSize(contentBytes)}"
                + (innerArchiveBytes > 0
                    ? $"，其中内层包 {TaskSpaceEstimate.FormatSize(innerArchiveBytes)}"
                      + "（它本体已算在内容物里，再展开按「净增量」估、不再按整份扣一遍）"
                    /*
                     * ⛔ 这里**只能**说"按名字看不出"，不能说"没有内层包"（用户 2026-10-05 真机：
                     * 那句"没有内层包"是假话 —— 那个包里有 3 层内层包，只是第 0 层清单里那三个
                     * `.mp4` 的名字看不出是归档，它们其实是"尾部藏着归档"的双面文件）。
                     * ⛔ 只改措辞，预算口径一个字都不动（这一档本来就是 0 净增量）。
                     */
                    : "，按名字看不出内层包（真正的内层包要解出来才知道）")
                + DescribeCarvedBytes(carvedBytes, null, carvedBytesNotNeeded);

            return new TaskSpaceEstimate
            {
                TaskPath = basis.TaskPath,
                DisplayName = basis.DisplayName,
                SourceBytes = basis.SourceBytes,
                ContentBytes = contentBytes,
                ProcessArtifactBytes = processBytes,
                ContentEstimated = false,
                HasListing = true,
                CountsSourceAsNewOccupancy = basis.CountsSourceAsNewOccupancy,
                Basis = basis2
            };
        }

        /// <summary>
        /// 内嵌归档抠出来会多占多少字节：源文件里 <c>[偏移, 归档终点)</c> 那一段的副本。
        /// 偏移为 0（不是内嵌归档）时返回 0 —— 绝不凭空加一份。
        ///
        /// 终点缺失（0 或 ≤ 偏移）时按"抠到文件末尾"算，与抠取侧的回落完全同口径；
        /// 真实资源包在 EOCD 之后还有十几 KB 正常数据，那截不会被抠出来，所以这里也**不能**算进去。
        ///
        /// <para><paramref name="directReadAvailable"/> 为 true 时返回 0：
        /// 这个任务会走 ZIP 直读，**不会**产生那份等大的临时副本（用户 2026-09-24 需求第 7 条）。</para>
        /// </summary>
        public static long EstimateCarvedBytes(ArchiveTask? task, long sourceBytes, bool directReadAvailable = false)
        {
            if (task == null || task.EmbeddedArchiveOffset <= 0)
            {
                return 0;
            }

            if (directReadAvailable)
            {
                return 0;
            }

            long tail = 0;

            try
            {
                long length = new FileInfo(task.CurrentPath).Length;

                tail = task.EmbeddedArchiveEnd > task.EmbeddedArchiveOffset &&
                       task.EmbeddedArchiveEnd <= length
                    ? task.EmbeddedArchiveEnd - task.EmbeddedArchiveOffset
                    : length - task.EmbeddedArchiveOffset;
            }
            catch
            {
                // 量不到就退回源包总量作上界（宁可高估）：抠包是真实的字节拷贝。
                tail = sourceBytes;
            }

            return tail > 0 ? tail : 0;
        }

        /// <summary>
        /// 空间账面上"那份临时副本"的一句话。**口径必须与真实发生的动作一致**（用户 2026-09-24 需求第 7 条）：
        /// 直读时明说"不需要副本"，抠取时给出字节数 —— 不许一边说省了、一边又预留。
        /// </summary>
        private static string DescribeCarvedBytes(long carvedBytes, ArchiveTask? task, bool directReadAvailable)
        {
            bool isEmbedded = task == null
                ? carvedBytes > 0 || directReadAvailable
                : task.EmbeddedArchiveOffset > 0;

            if (!isEmbedded)
            {
                return string.Empty;
            }

            if (directReadAvailable)
            {
                return "，内嵌归档走 ZIP 直读（不需要那份等大的临时副本，不记这一笔）";
            }

            return carvedBytes > 0
                ? $"，内嵌归档过程物（抠取副本）{TaskSpaceEstimate.FormatSize(carvedBytes)}"
                : string.Empty;
        }

        /// <summary>逐条累加清单（与 <c>ResourceBudget.CheckBeforeExtract</c> 同一口径），顺便挑出内层包条目。</summary>
        private static (long ContentBytes, long InnerArchiveBytes) SumListing(ArchiveListResult list)
        {
            long total = 0;
            long inner = 0;

            foreach (ArchiveEntry? entry in list.Entries ?? Array.Empty<ArchiveEntry>())
            {
                if (entry == null || entry.IsDirectory)
                {
                    continue;
                }

                // 负数是解析异常：按 0 计，但绝不用它冲减已累计的量（同一个理由见 ResourceBudget）。
                long size = entry.Size > 0 ? entry.Size : 0L;

                total = TaskSpaceEstimate.SaturatingSum(total, size);

                if (LooksLikeNestedArchive(entry.Path))
                {
                    inner = TaskSpaceEstimate.SaturatingSum(inner, size);
                }
            }

            /*
             * 引擎自报的总量与逐条累加取更大的那个：两边不一致时按更保守的一侧算
             * （解析器少报几条就能把超限的包放过去，那是空间判断最不能出的错）。
             */
            return (Math.Max(total, list.TotalUncompressedSize > 0 ? list.TotalUncompressedSize : 0L), inner);
        }

        /// <summary>
        /// 清单里的一个条目是不是"解出来还要再解一层"的内层包。
        ///
        /// <para>后缀词表**不在这里重写**：<see cref="ExtensionHelper.IsKnownArchiveExtension"/> 与
        /// <see cref="ExtensionHelper.IsVolumePartExtension"/> 才是既有资产
        /// （`MaintenanceCleanupService.LooksLikeArchiveFile` 用的是同一套判断，只是它是私有的）。
        /// 这里只做"空间估算要不要给内层包留增量"这一个用途。</para>
        /// </summary>
        internal static bool LooksLikeNestedArchive(string? entryPath)
        {
            if (string.IsNullOrWhiteSpace(entryPath))
            {
                return false;
            }

            string last = ExtensionHelper.GetLastExtension(entryPath);

            if (ExtensionHelper.IsKnownArchiveExtension(last))
            {
                return true;
            }

            // 分卷段（.001 / .z01 / .r00 / .part1）：它是"一组内层包"的一段，同样要再解一层。
            return ExtensionHelper.IsVolumePartExtension(last);
        }

        /// <summary>本任务的源文件集合（当前路径 + 最初路径 + 分卷各卷），按全路径去重。</summary>
        private static IReadOnlyList<string> CollectSourceFiles(ArchiveTask task)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<string>();

            foreach (string? candidate in new[] { task.CurrentPath, task.OriginalPath }.Concat(task.VolumePaths))
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                string full = SafePathHelper.GetFullPathSafe(candidate);

                if (string.IsNullOrWhiteSpace(full))
                {
                    full = candidate;
                }

                if (seen.Add(full))
                {
                    files.Add(full);
                }
            }

            return files;
        }

        /// <summary>把一组文件的大小求和（读不到的计入 <paramref name="missingCount"/>，按 0 计但如实报数）。</summary>
        private static long SumFileSizes(IReadOnlyList<string> files, out int measuredCount, out int missingCount)
        {
            long total = 0;
            int measured = 0;
            int missing = 0;

            foreach (string file in files)
            {
                try
                {
                    total = TaskSpaceEstimate.SaturatingSum(total, new FileInfo(file).Length);
                    measured++;
                }
                catch
                {
                    // 文件没了 / 被占用：**不抛** —— 空间估算跑在解压前的准备阶段，
                    // 一次抛异常会把"这个包我还没开始解"变成"整个任务崩了"。
                    missing++;
                }
            }

            measuredCount = measured;
            missingCount = missing;

            return total;
        }
    }
}
