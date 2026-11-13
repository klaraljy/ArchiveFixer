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

    }
}
