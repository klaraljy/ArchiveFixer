using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;


namespace ArchiveFixer.ViewModels
{
    /// <summary>批首改名归一 + 改后任务表同步（第 11 块，从 Password 大杂烩搬出；同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {

        /// <summary>
        /// 解压前把"名字被伪装的整组卷名"改回标准名（用户 2026-09-28 三层方案的第 3 步）。
        ///
        /// <para>判据只有一处（<see cref="VolumeNameRepair.Plan"/>），执行体也只有一处
        /// （<see cref="VolumeNameRepair.TryApply"/>）—— 手动档的「修复分卷名并重试」调的是**同一对**。
        /// 这里把它挪到"每单开工前"自动做一次：一键处理与手动解压都从这个入口走，两条路行为一致。</para>
        ///
        /// <para>⛔ 底线：只改名字，不动内容；目标名被占 / 证据不足 → 一个字节都不动，写清为什么。</para>
        /// </summary>

        /// <summary>
        /// **批级整组改名**：在**任何任务开工之前**，把这一批里"名字被改坏的分卷组"改回标准名。
        ///
        /// <para>为什么必须串行、必须在批首（而不是每个任务开工前各改各的）：并发跑 N 个任务时，
        /// "改名 → 归组 → 解压"三步不是原子的，而同一个包的多卷是**多个任务** ——
        /// 谁先开工不定，另一单就可能拿着**旧名字**判完归组（认不出组）再往下跑，
        /// 于是同一组被解两遍、第二棵产物树落成 `名字(1)`、后续卷那一单报「分卷缺失」，
        /// 连整组的链尾处理也被它拦住（用户 2026-10-01 真机现场）。</para>
        ///
        /// <para>位置要求两条：① 工作区根**已经定下来**（名字路不用试开，但内容级推断要拿工作区
        /// 做硬链接试开，拿不到就只能降级成"无法确认"）；② **并发启动之前**。
        /// 调用点只有 <c>StartExtractForOneClickAsync</c> 里、<c>ApplyBatchWorkspaceRoot</c> 之后那一处。</para>
        /// </summary>

        /// <summary>
        /// **这一单是不是"同一分卷组的后续卷"** —— 是就跳过，不重复解一遍（一组只解一次）。
        ///
        /// <para>判据只读事实，两步：① 先按**当前真实文件名**重新归组
        /// （<see cref="OneClickCoordinator.ApplyVolumeGroupingFromDirectory"/>，与扫描期、
        /// 与手动档补救走的是同一条路 —— 扫描期那一份是按改名**前**的名字算的，已经过期）；
        /// ② 再看这一组的**第一卷**是不是任务表里**另一个任务**在负责。</para>
        ///
        /// <para>⛔ 首卷不在任务表里时**不跳**（用户只把 `.002` 拖进来那一档）：照旧让引擎去报缺卷，
        /// 绝不因为"组里没人负责"就把这一单静默吞掉 —— 那等于把一个真失败说成"已跳过"。</para>
        ///
        /// <para>跳过的这一单**不拍快照、不碰盘、不写任何产物**；它的源包跟着整组一起被处理
        /// （首卷那一单的 <c>VolumePaths</c> 记的是整组）。终态落 <see cref="TaskOutcome.Skipped"/>
        /// （既不是成功也不是失败）：批末汇总与批末诊断读的都是它。</para>
        /// </summary>
        /// <returns>true = 已经跳过（调用方必须立刻 return，什么都不许做）。</returns>
        /// <summary>
        /// 这一趟**真解出来的东西**够不够证明"密码是对的"（带这一趟引擎的结构化事实）。
        ///
        /// <para>判据本体**唯一出口** <see cref="ProducedContentGate"/> —— 单层这条路与递归那条路
        /// （<c>RecursiveExtractor</c>）问的是同一个方法，⛔ 不许各写一份（2026-10-01 只接了单层那条路，
        /// 而真机 `giu910` 走的是递归那条 ⇒ 修复等于没生效，用户白等 13 分钟）。</para>
        ///
        /// <para>为什么必须把引擎事实带上（2026-10-12 真机 `2222`）：RAR4 + 存储 + 数据加密的包上，
        /// **错密码**也会把垃圾明文按原尺寸写满盘，光数文件数与字节数会把错密码判成"密码已证实"
        /// ⇒ 候选循环当场 break、正确密码（密码本第 7 位）永远没机会被试。这里只负责把两个计数递过去。</para>
        /// </summary>

        private static bool TryMeasureProducedContent(
            string? directory,
            ArchiveOperationResult engineResult,
            out int files,
            out long bytes) =>
            ProducedContentGate.TryMeasure(
                directory,
                engineResult.ReportedSubItemErrors,
                engineResult.FailedEntryNames.Count,
                out files,
                out bytes);

        /// <summary>两份路径名单是不是**逐位相同**（大小写不敏感，与 Windows 上其它路径比较同一口径）。</summary>
        private static bool SamePaths(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null || left.Count != right.Count)
            {
                return false;
            }

            for (int i = 0; i < left.Count; i++)
            {
                if (!string.Equals(left[i], right[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// **一次改名之后，谁负责把任务路径 + 源文件快照更新到新名字** —— 全仓唯一出口。
        ///
        /// <para><b>为什么必须覆盖整个任务表、而不是"驱动改名的那一单"</b>（用户 2026-09-30 真机）：
        /// <see cref="VolumeNameRepair.TryApply"/> 改的是**整组每一卷**（<c>VolumeNameRepair.cs</c> 里
        /// `plan.Items` 逐项 <c>File.Move</c>），而"组里别的卷"完全可能是**同一批里另一个任务**指着的文件。
        /// 只同步自己那一单，别的任务就会继续指着**已经不在磁盘上**的旧名字 ——
        /// 紧接着的不变量 11 比对（<see cref="StopIfSourceChanged"/>）当场报「源文件已变化（文件不见了）」，
        /// 那一单连解压都没开始（真机：一键档 5 个任务里 2 个因此失败，报的是"文件不见了"，
        /// 而文件其实好端端地躺在同一个目录里、只是改了名）。</para>
        ///
        /// <para><b>为什么快照也必须重拍</b>：路径不是不变量 11 要保护的东西（大小与修改时间才是），
        /// 但"改名"这件事实必须由程序自己记账 —— 重拍之后基准与新名字对齐，后面各层比对的才是同一份文件。
        /// ⛔ 这**不是**"放宽成文件不在也算没变"：比对口径一个字没动，变的只是"谁的名字"。</para>
        ///
        /// <para>返回被同步到的任务（调用方要重新识别的那条手动路会用到）。名字没变的项不重拍快照
        /// （<c>BuildPlanFromOrder</c> 会把"本来就标准"的那一卷也放进 Items）。</para>
        /// </summary>
        /// <param name="items">改名计划里的每一卷（旧路径 → 新路径）。</param>
        /// <param name="primary">驱动这次改名的任务（可能在任务表里、也可能在表外，比如测试直接造的）。</param>
        internal IReadOnlyList<ArchiveTask> SyncTasksAfterVolumeRename(
            IEnumerable<VolumeRepairItem>? items,
            ArchiveTask? primary = null)
        {
            var affected = new List<ArchiveTask>();

            List<VolumeRepairItem> moves = (items ?? Enumerable.Empty<VolumeRepairItem>())
                .Where(item => item != null &&
                               !string.IsNullOrWhiteSpace(item.CurrentPath) &&
                               !string.IsNullOrWhiteSpace(item.TargetPath) &&
                               !string.Equals(item.CurrentPath, item.TargetPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (moves.Count == 0)
            {
                return affected;
            }

            /*
             * 任务表的快照：这一步跑在后台线程上（解压管线），而任务表归 UI 线程写 ——
             * 必须用**下标**拷贝，不能用 LINQ 的 ToArray：List 的枚举器带版本号检查，
             * UI 线程正在导入 / 移除时枚举会抛 "Collection was modified"
             * （AGENTS.md §11.2 记过同形状的假红），而这一抛会连带把"改名已做完"的同步丢掉。
             * 下标拷贝最坏只是拿到一份稍微过期的名单，绝不会抛。
             */
            ArchiveTask[] table = SnapshotTaskTable(Tasks);

            foreach (ArchiveTask candidate in EnumerateDistinct(primary, table))
            {
                bool touched = false;

                foreach (VolumeRepairItem move in moves)
                {
                    if (string.Equals(candidate.CurrentPath, move.CurrentPath, StringComparison.OrdinalIgnoreCase))
                    {
                        // CurrentPath 的 setter 会顺手刷新文件名 / 目录 / 后缀 / 体积。
                        candidate.CurrentPath = move.TargetPath;

                        /*
                         * **这一步变换也要记进那一行的链里**（唯一写入出口）：整组改名走的是这条路，
                         * 只有"改名记录"本身的两个名字 —— ⛔ 不许靠账上字段比对推
                         * （用户 2026-10-07：「最简单的改名操作你都要有一个变换的过程」）。
                         */
                        candidate.AppendTransformationStep(
                            $"{Path.GetFileName(move.CurrentPath)} → {Path.GetFileName(move.TargetPath)}");

                        touched = true;
                    }

                    for (int i = 0; i < candidate.VolumePaths.Count; i++)
                    {
                        if (string.Equals(candidate.VolumePaths[i], move.CurrentPath, StringComparison.OrdinalIgnoreCase))
                        {
                            candidate.VolumePaths[i] = move.TargetPath;
                            touched = true;
                        }
                    }
                }

                if (!touched)
                {
                    continue;
                }

                // 基准跟着新名字重拍（不变量 11 的"改名不算变化"就落在这里，而不是落在比对里放宽判据）。
                candidate.CaptureSourceSnapshot();
                affected.Add(candidate);

                if (!ReferenceEquals(candidate, primary))
                {
                    /*
                     * 别的任务被顺带改了名必须**说出来**：用户看到那一行的文件名变了、
                     * 而日志里一个字都没有的话，会以为程序动错了东西（AGENTS.md §9.5"值 + 通知"）。
                     */
                    AppendLog(
                        "INFO",
                        $"{candidate.LogName}：这一卷在同一个改名批次里被改成了标准名，"
                        + "任务路径与源文件快照已同步到新名字（内容一个字节没动）。");
                }
            }

            return affected;
        }

        /// <summary>把"驱动改名的任务"与任务表合成一个**不重复**的枚举（表里已有就不重复处理）。</summary>
        private static IEnumerable<ArchiveTask> EnumerateDistinct(ArchiveTask? primary, IEnumerable<ArchiveTask> table)
        {
            var seen = new HashSet<ArchiveTask>();

            if (primary != null && seen.Add(primary))
            {
                yield return primary;
            }

            foreach (ArchiveTask candidate in table)
            {
                if (candidate != null && seen.Add(candidate))
                {
                    yield return candidate;
                }
            }
        }

        /// <summary>
        /// 任务表的**不会抛**的快照（下标拷贝；理由见调用处）。
        /// 中途被 UI 线程改了就以已经拷到的那部分为准 —— 少同步一个任务，下一次改名还会再同步一遍。
        /// </summary>
        private static ArchiveTask[] SnapshotTaskTable(ObservableCollection<ArchiveTask> tasks)
        {
            var copy = new List<ArchiveTask>();
            int count = tasks.Count;

            try
            {
                for (int i = 0; i < count; i++)
                {
                    copy.Add(tasks[i]);
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                // 名单在拷贝途中变短了：用已经拷到的部分。
            }

            return copy.ToArray();
        }


        /// <summary>同目录里有没有同组的兄弟卷（判据唯一实现在 <see cref="VolumeSiblingProbe"/>，这里只转一行给用例）。</summary>
        internal static bool HasSiblingVolumeBeside(string? path) => VolumeSiblingProbe.HasSiblingVolumeBeside(path);
    }
}
