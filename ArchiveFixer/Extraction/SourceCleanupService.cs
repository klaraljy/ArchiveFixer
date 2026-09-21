using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 清理源包的结果。
    /// 删除是**不可逆**操作，所以这里把"有没有真的动手"和"动了什么"分开记：
    /// <see cref="Attempted"/> = false 时调用方可以明确告诉用户"源文件一个都没碰"。
    /// </summary>
    public sealed class SourceCleanupResult
    {
        /// <summary>是否真的尝试删除（未开启 / 校验未通过 / 没有可删的路径时为 false）。</summary>
        public bool Attempted { get; init; }

        public IReadOnlyList<string> DeletedFiles { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> FailedFiles { get; init; } = Array.Empty<string>();

        public long FreedBytes { get; init; }

        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// 可选清理源包（AGENTS.md §9.5，用户 2026-09-21 指示：默认关闭，只有解压成功 + 校验通过才删）。
    ///
    /// 这个类是整个项目里**唯一**会删用户源文件的地方，因此所有安全边界都写死在这里：
    /// 1. 开关没开 → 一个文件都不碰；
    /// 2. 校验没过 → 一个文件都不碰；
    /// 3. 要删的清单只能来自任务自身（分卷组 = 这一组的分卷，单文件任务 = 它自己），
    ///    **不扫描目录**、**不删目录**、**不递归**；
    /// 4. 单个文件删不掉（占用 / 权限）只记录并继续，不让整批任务因为一个文件失败。
    /// </summary>
    public sealed class SourceCleanupService
    {
        public SourceCleanupResult Cleanup(ArchiveTask? task, OutputVerificationResult? verification, bool enabled)
        {
            /*
             * 规则 1：开关没开就什么都不做。
             * 这一条放在最前面，是为了让"默认关闭"成为代码层面的保证，
             * 而不是靠调用方记得别传 true。
             */
            if (!enabled)
            {
                return new SourceCleanupResult
                {
                    Attempted = false,
                    Message = "未开启清理源包"
                };
            }

            if (task == null)
            {
                return new SourceCleanupResult
                {
                    Attempted = false,
                    Message = "任务为空，已保留源文件"
                };
            }

            /*
             * 规则 2：校验没过（或压根没校验）就不删。
             * 宁可留下一个已经没用的源包，也不能把没解压全的包删掉 —— 后者无法恢复。
             */
            if (verification == null || !verification.Verified)
            {
                string reason = verification == null
                    ? "没有校验结果"
                    : verification.Message;

                return new SourceCleanupResult
                {
                    Attempted = false,
                    Message = $"校验未通过，已保留源文件（{reason}）"
                };
            }

            List<string> targets = BuildTargetList(task);

            if (targets.Count == 0)
            {
                return new SourceCleanupResult
                {
                    Attempted = false,
                    Message = "任务没有可删除的源文件路径，已保留源文件"
                };
            }

            var deletedFiles = new List<string>();
            var failedFiles = new List<string>();
            long freedBytes = 0;

            foreach (string targetPath in targets)
            {
                if (!SafeFileExists(targetPath))
                {
                    // 已经不在了（用户手动删了 / 上一轮已经清过）：跳过，**不算失败**，
                    // 否则每次重跑都会报一堆假的"删除失败"。
                    continue;
                }

                if (!TryReadAttributes(targetPath, out FileAttributes attributes))
                {
                    failedFiles.Add(targetPath);
                    continue;
                }

                /*
                 * 只处理**文件**：
                 * ① 目录一律不删（清单里理论上不会出现目录，但万一出现，这里也要拦住）；
                 * ② 符号链接 / 联接点不删 —— 它可能指到别处，删它属于"删了不在任务范围内的东西"，
                 *    与 AGENTS.md §6 第 13 条（不得误删工作区之外的文件）冲突。
                 */
                if ((attributes & FileAttributes.Directory) != 0 ||
                    (attributes & FileAttributes.ReparsePoint) != 0)
                {
                    failedFiles.Add(targetPath);
                    continue;
                }

                long size = 0;

                try
                {
                    // 必须在删除前取大小：删完再问就永远是 0 了。
                    size = new FileInfo(targetPath).Length;
                }
                catch
                {
                    // 取不到大小不影响删除，只是这次统计少算一点。
                    size = 0;
                }

                try
                {
                    // File.Delete 不递归、不跟随 reparse point，删的就是清单里这一个文件。
                    File.Delete(targetPath);

                    deletedFiles.Add(targetPath);
                    freedBytes += size;
                }
                catch
                {
                    // 被占用 / 权限不足：记下来继续处理其余文件，
                    // 不让一个删不掉的分卷拖垮整批清理（AGENTS.md §6 第 9 条）。
                    failedFiles.Add(targetPath);
                }
            }

            return new SourceCleanupResult
            {
                Attempted = true,
                DeletedFiles = deletedFiles,
                FailedFiles = failedFiles,
                FreedBytes = freedBytes,
                Message = BuildMessage(task, deletedFiles.Count, failedFiles.Count, freedBytes)
            };
        }

        /// <summary>
        /// 生成"要删哪些文件"的清单。
        ///
        /// 这是本类最关键的安全边界：清单**只能**来自任务自身记录，
        /// 分卷组 = <see cref="ArchiveTask.VolumePaths"/> 整组，单文件任务 = <see cref="ArchiveTask.CurrentPath"/>。
        /// 绝不扫描目录去"顺便"删掉旁边的同名文件、说明文件或目录。
        /// </summary>
        private static List<string> BuildTargetList(ArchiveTask task)
        {
            var targets = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (task.IsVolumeGroup)
            {
                /*
                 * 分卷组必须整组删：只删第一卷会留下一堆再也拼不起来的分卷碎片，
                 * 对用户来说比不删更糟。
                 *
                 * 注意：VolumePaths 为空时**不回退**到 CurrentPath —— 那说明任务分组信息不完整，
                 * 此时什么都不删才是对的（严格按清单执行，不替用户推断）。
                 */
                foreach (string volumePath in task.VolumePaths)
                {
                    AddTarget(targets, seen, volumePath);
                }

                return targets;
            }

            AddTarget(targets, seen, task.CurrentPath);

            return targets;
        }

        private static void AddTarget(List<string> targets, HashSet<string> seen, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            // 去重：同一路径在 VolumePaths 里出现两次时不能删两遍、也不能把释放字节算成两份。
            if (seen.Add(path))
            {
                targets.Add(path);
            }
        }

        private static string BuildMessage(ArchiveTask task, int deletedCount, int failedCount, long freedBytes)
        {
            // 单位跟着任务类型走：用户看到"删了 3 个源分卷"才知道整组都清干净了。
            string unit = task.IsVolumeGroup ? "个源分卷" : "个源文件";

            if (deletedCount == 0 && failedCount == 0)
            {
                return "清单里的源文件都已经不存在（可能已被移动或删除），无需清理";
            }

            if (failedCount == 0)
            {
                return $"已删除 {deletedCount} {unit}，释放 {freedBytes} 字节";
            }

            if (deletedCount == 0)
            {
                return $"删除失败：{failedCount} 个文件被占用或没有权限，一个都没删掉，源文件仍完整保留";
            }

            return $"部分失败：{failedCount} 个文件被占用未删除；已删除 {deletedCount} {unit}，释放 {freedBytes} 字节";
        }

        private static bool SafeFileExists(string? path)
        {
            try
            {
                // 传目录进来时 File.Exists 返回 false，于是被当作"不存在"跳过 —— 正好是我们要的"不删目录"。
                return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadAttributes(string path, out FileAttributes attributes)
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
    }
}
