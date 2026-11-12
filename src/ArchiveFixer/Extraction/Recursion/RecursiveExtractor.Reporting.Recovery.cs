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
{
    public sealed partial class RecursiveExtractor
    {        /// <summary>
             /// 写 report.json（给"崩溃后可恢复"用）。
             /// 报告里**只有**结构和数量：路径、层号、状态、计数、脱敏后的密码标记。
             /// 密码策略在这里只是**声明**（"每层最多试 N 个候选"），不含任何明文。
             /// </summary>
        private void WriteRecoveryReport(
            RecursionStopReason stopReason,
            List<RecursionLayerReport> layers,
            ExtractionWorkspace workspace,
            string finalOutputPath,
            bool completed,
            bool partiallyCompleted)
        {
            var report = new
            {
                SchemaVersion = 1,
                TaskDirectory = workspace.TaskDirectory,
                RootDirectory = workspace.RootDirectory,
                StopReason = stopReason.ToString(),
                Completed = completed,
                PartiallyCompleted = partiallyCompleted,
                FinalOutputPath = finalOutputPath,
                Engine = new
                {
                    Id = _engine.Id,
                    Version = _engine.Version
                },
                Limits = new
                {
                    _limits.MaxDepth,
                    _limits.MaxTotalFiles,
                    _limits.MaxTotalSize,
                    _limits.MaxExpansionRatio,
                    _limits.MaxInnerArchivesPerLayer,
                    _limits.MaxPasswordAttemptsPerLayer
                },
                PasswordPolicy = "每层按调用方给的候选顺序逐个尝试；报告不记录任何密码内容",
                Layers = layers.Select(layer => new
                {
                    layer.Depth,
                    layer.ArchivePath,
                    layer.OutputPath,
                    layer.Success,
                    layer.Status,
                    layer.Message,
                    layer.OutputFileCount,
                    layer.OutputSize,
                    layer.UsedPasswordMasked,
                    InnerArchives = layer.InnerArchives.ToArray()
                }).ToArray()
            };

            workspace.WriteReport(report);
        }

        private static bool IsZeroByteFile(string path)
        {
            try
            {
                return new FileInfo(path).Length == 0;
            }
            catch
            {
                // 读不到大小就当作非 0，交给探测器去判断（宁可多问一次，也别漏掉真的归档）。
                return false;
            }
        }

        private static long SafeFileLength(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return 0;
                }

                return new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        private static bool TryGetAttributes(string path, out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(path);
                return true;
            }
            catch
            {
                attributes = default;
                return false;
            }
        }

        /// <summary>
        /// 入队这一步的可变状态。用一个对象而不是三个 out 参数，
        /// 是为了让"这一层还有哪几个内层包没展开"在每条提前返回的分支上都必须显式写一次 ——
        /// 忘了写就是空的，而空的会让 Summary 少掉那句提示，所以每处都写清楚。
        ///
        /// <para>⚠ 2026-10-04（真机）：过去这里只记**个数**，于是 Summary 里只有"该层还有 1 个内层包未展开"，
        /// 用户看不出**是哪一个**（他要自己去工作区里翻）。现在**名单是唯一事实**，个数由名单长度推出来
        /// （<see cref="UnexpandedCount"/>）—— 数与名不可能对不上。</para>
        /// </summary>
        private sealed class EnqueueState
        {
            public Queue<WorkItem> Pending { get; init; } = new();

            /// <summary>本层没有入队的内层归档（停因是"多分支不展开"时才有意义）。</summary>
            public List<string> UnexpandedNames { get; } = new();

            /// <summary>本层没有入队的内层归档数量（= <see cref="UnexpandedNames"/> 的长度，⛔ 不另数一遍）。</summary>
            public int UnexpandedCount => UnexpandedNames.Count;

            /// <summary>这一层哪几个没入队（唯一写入点：四条提前返回各自调一次）。</summary>
            public void MarkUnexpanded(IEnumerable<string>? archivePaths)
            {
                UnexpandedNames.Clear();

                if (archivePaths == null)
                {
                    return;
                }

                foreach (string path in archivePaths)
                {
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        UnexpandedNames.Add(path);
                    }
                }
            }
        }

        /// <summary>队列里的一项：一个待解的归档 + 它在第几层 + 它的工作区目录。</summary>
        private sealed class WorkItem
        {
            public int Depth { get; init; }

            public string ArchivePath { get; init; } = string.Empty;


            public WorkspaceLayer Layer { get; init; } = new WorkspaceLayer();

            /// <summary>是否是任务自己的那个包（第 0 层）。</summary>
            public bool IsRoot { get; init; }

            /// <summary>
            /// **本层列出的清单成功、且一个加密条目都没有**（候选循环里列完清单就写这里，
            /// 再由本层报告 <see cref="RecursionLayerReport.ListingSaidNoEncryption"/> 带走）。
            /// </summary>
            public bool ListingSaidNoEncryption { get; set; }

            /// <summary>本层真正试过几个候选密码（候选循环里写，报告带走）。</summary>
            public int TriedCandidateCount { get; set; }

            /// <summary>
            /// 这一层停手是因为**连续两个候选的结果完全相同**（换密码不会改变结果；唯一出口见
            /// <see cref="RecursionLayerReport.CandidatesProducedIdenticalResults"/>）。
            /// ⛔ 只由「引擎说成功、但产物没通过校验」那一支写 —— 错密码那一支**不许**用它提前停
            /// （错密码留下的半截产物也会一模一样，而正确密码排在后面时产物会完全不同）。
            /// </summary>
            public bool CandidatesProducedIdenticalResults { get; set; }

            /// <summary>
            /// 上一候选**校验判否**时的产物读数（文件数 / 字节数）——「引擎说成功但产物不对」那一支据此判"结果相同"。
            /// </summary>
            public int LastVerifiedCandidateFileCount { get; set; } = -1;

            public long LastVerifiedCandidateBytes { get; set; } = -1;

            /// <summary>
            /// **本层开工时该当场回收的过程物**（递归路逐层回收，见 <see cref="LayerReclaim"/>）。
            ///
            /// <para>由**父层**入队这一项时一并算好、写死在项上：那些文件全部来自父层的产物目录，
            /// 而入队那一刻父层已经跑完、盘上事实就在眼前 —— 等到本层开工时再去父层目录里"重新认一遍"
            /// 只会读到已经被搬走的现状（`CrossLayerVolumeGather` 就可能动过那些片）。
            /// ⛔ 只在真正入队的那几个归档上建项：没入队的（多分支不展开 / 到层数上限 /
            /// 超过每层数量上限）一个都不建 —— 它们要原样留在成品目录里当内容物。</para>
            ///
            /// <para>第 0 层**永远为空**：它的输入是用户给的源包，口径是"最外层源包只留到链尾"。</para>
            /// </summary>
            public IReadOnlyList<RecursiveLayerReclaimRequest> Reclaims { get; init; } =
                Array.Empty<RecursiveLayerReclaimRequest>();

            /// <summary>
            /// 非 null 表示"只处理用户点名的这几个归档"。
            ///
            /// 存的是**文件名**而不是完整路径：决策里带的是上一次运行的工作区路径，本次续跑新建工作区后
            /// 前缀必然不同，按完整路径比对会把用户的选择全部判成"没了"。层内部的文件名是唯一稳定的定位键；
            /// 同一层里出现同名归档时两个都会被处理 —— 这是刻意的取舍：宁可多解一个同名分支，
            /// 也不能因为工作区换了就把用户点过的分支静默丢掉。
            ///
            /// 仅在 previousDecision 非 null 时落在第 0 层，后续层不继承（用户回答的是源包层面的问题）。
            /// </summary>
            public IReadOnlyList<string>? DecisionNames { get; init; }
        }

        /// <summary>一层的执行结果：要么成功（带报告，可以继续），要么停下（带报告 + 停因）。</summary>
        private sealed class LayerOutcome
        {
            public RecursionLayerReport? Report { get; private init; }

            public RecursionStopReason StopReason { get; private init; }

            public static LayerOutcome Ok(RecursionLayerReport report)
            {
                return new LayerOutcome { Report = report, StopReason = RecursionStopReason.None };
            }

            public static LayerOutcome Stop(RecursionLayerReport report, RecursionStopReason reason)
            {
                return new LayerOutcome { Report = report, StopReason = reason };
            }
        }
    }
}