using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction
{    /// <summary>
     /// 递归的硬上限（AGENTS.md §6 第 8 条：层数 / 文件数 / 总大小 / 展开比 / 密码尝试次数）。
     ///
     /// 这些值是"安全阀"而不是"性能参数"：命中任何一个都必须**停下并报告**，
     /// 不允许"再试一层看看"。压缩炸弹靠的就是把其中某一条顶穿。
     /// </summary>
    public sealed class RecursionLimits
    {
        /// <summary>最多解几层（含第 0 层）。3 层足够覆盖 外层.zip → 内层.7z → data.tar 这种真实包。</summary>
        public int MaxDepth { get; init; } = 3;

        /// <summary>
        /// 手动「只解压」：第 0 层成功后不再展开内层包（用户 2026-10-25，WinRAR 语义）。
        /// 与 MaxDepth=1 的区别是**停因是 Completed 而不是 MaxDepthReached** ——
        /// 后者会把这一单算成部分完成并留下工作区；手动单层是正常的终点。
        /// </summary>
        public bool ManualSingleLayer { get; init; }

        /// <summary>所有层累计的输出文件数上限。</summary>
        public int MaxTotalFiles { get; init; } = 100_000;

        /// <summary>所有层累计的输出总字节数上限（默认 50 GiB）。</summary>
        public long MaxTotalSize { get; init; } = 50L * 1024 * 1024 * 1024;

        /// <summary>
        /// 单个条目**解压后**的字节上限（默认 64 GiB —— 与⑥设置「安全上限」里那一格的默认值同一口径）。
        ///
        /// <para>为什么要单列一条（2026-10-05 只读审计）：递归这条路（出厂默认档 = 展开所有分支）
        /// 过去**一处都没引用 ResourceBudget**，于是"单文件"这道闸门在默认档下等于不存在 ——
        /// 一个含 200 GiB 单文件的包既不会被拦、事后报的还不是这一条。
        /// 判据与文案都转调解压前预算那一份（<see cref="Security.ResourceBudget.DescribeSingleFileOverLimit"/>），
        /// ⛔ 不许在这里再拼一句。</para>
        /// </summary>
        public long MaxSingleFileSize { get; init; } = 64L * 1024 * 1024 * 1024;

        /// <summary>
        /// 空间不足模式：成功收尾时把**最深一层的过程物**留到最终输出目录，
        /// 让失败时至少还有一份已解出的现场。默认 false。
        /// </summary>
        public bool KeepDeepestProcessArtifact { get; init; }

        /// <summary>
        /// 单层展开比上限：该层解压后总大小 / 该层归档文件大小。
        /// 500 倍对正常资源包足够宽松（视频、图片再压也到不了），超了就是"疑似压缩炸弹"。
        /// </summary>
        public double MaxExpansionRatio { get; init; } = 500d;

        /// <summary>单层里最多允许出现多少个内层归档；超了说明这不是"套娃"而是"归档集合"，应当让用户单独处理。</summary>
        public int MaxInnerArchivesPerLayer { get; init; } = 50;

        /// <summary>
        /// 每层最多试几个密码候选。
        ///
        /// 为什么和"密码错误"必须分开（AGENTS.md §9.2）：
        /// 密码错误 = "试遍了所有候选都不对"，用户该去补密码本；
        /// 达到上限 = "候选还有，只是我们不再试了"，用户该做的是缩小候选或调高上限。
        /// 两种情况给用户的下一步动作完全不同，混成一个会让人白折腾。
        /// </summary>
        public int MaxPasswordAttemptsPerLayer { get; init; } = 8;

        /// <summary>默认上限。写成静态单例是为了让"默认值"只有一处，便于核对与调整。</summary>
        public static RecursionLimits Default { get; } = new();
    }
}
