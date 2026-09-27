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
    /// 删除动作的唯一出口（用户 2026-09-24 要求：删除裁决要能被测试钉死）。
    ///
    /// <para>
    /// 为什么要留这个接缝：那条红线是"该不删的时候一次都不能调用删除"，
    /// 而"有没有调用"只有把执行体换成记账的假实现才能断言（源码里看 <c>File.Delete</c> 看不出运行时走没走到）。
    /// 与 <c>ISourceMoveFileSystem</c>（源包搬运）同一套做法，产品代码一行都不需要知道它存在。
    /// </para>
    /// </summary>
    public interface ISourceDeleteFileSystem
    {
        bool FileExists(string path);

        /// <summary>删除**一个**文件（不递归、不跟随联接点）。失败时抛异常，由调用方记成"删不掉"。</summary>
        void DeleteFile(string path);
    }

    /// <summary>真实文件系统（产品代码用这一个）。</summary>
    public sealed class FileSystemSourceDeleteFileSystem : ISourceDeleteFileSystem
    {
        public static FileSystemSourceDeleteFileSystem Instance { get; } = new();

        public bool FileExists(string path) => SourceCleanupService.SafeFileExists(path);

        public void DeleteFile(string path) => File.Delete(path);
    }

    /// <summary>
    /// 可选清理源包（AGENTS.md §9.5，用户 2026-09-21 指示：默认关闭，只有解压成功 + 校验通过才删）。
    ///
    /// <para>
    /// ⚠ <b>2026-09-25 第 32 条之后它不再挂在"解压收尾"那一条路上</b>：用户把"删源包"表达成
    /// "源包操作 = 放入其余物 + 删除操作 = 回收站 / 彻底删除"，那条路的唯一执行体是
    /// <see cref="ArchiveFixer.Storage.RestItemPurger"/>（它删的是定稿时记下的那一份 `其余物`，
    /// 门槛也更多）。
    /// </para>
    ///
    /// <para>
    /// ✅ <b>2026-09-27 起它有一个、也只有一个在管线上的调用点</b>：①页「空间不足」模式
    /// （<c>ExtractionCoordinator.PurgeSourcePackageForSpaceTight</c>）—— 那个模式的**全部机制**
    /// 就是"定稿 + 校验通过之后立刻永久删源包"，把空间当场还给后面的包。
    /// ⛔ 除了那一个调用点，别再往管线里接它：两条删除路径并存正是"到底谁在删"这类问题的温床。
    /// </para>
    ///
    /// <para>
    /// 这个类是整个项目里**唯一**会删用户源文件的地方，因此所有安全边界都写死在这里：
    /// 1. 开关没开 → 一个文件都不碰；
    /// 2. 校验没过 → 一个文件都不碰；
    /// 3. 要删的清单只能来自任务自身（分卷组 = 这一组的分卷，单文件任务 = 它自己），
    ///    **不扫描目录**、**不删目录**、**不递归**；
    /// 4. 单个文件删不掉（占用 / 权限）只记录并继续，不让整批任务因为一个文件失败。
    ///
    /// <para>
    /// ⚠ <b>判据只准看事实（用户 2026-09-24 要求）</b>：删除条件就是「输出校验通过 + 未取消 + 属于本任务来源」，
    /// ⛔ <b>不许</b>写 <c>task.Status == "解压成功"</c> 之类的字符串判断 ——
    /// 真机日志里出现过"状态写着解压成功、校验却已判否"，拿状态字符串当判据迟早会删错东西。
    /// 本类因此**完全不读** <see cref="ArchiveTask.Status"/>。
    /// </para>
    /// </summary>
    public sealed class SourceCleanupService
    {
        private readonly ISourceDeleteFileSystem _fileSystem;

        /// <summary>
        /// <paramref name="fileSystem"/> 只给测试用（注入一个记账的假执行器，断言"该不删时一次都没被调用"）；
        /// 产品代码一律用默认的 <see cref="FileSystemSourceDeleteFileSystem"/>。
        /// </summary>
        public SourceCleanupService(ISourceDeleteFileSystem? fileSystem = null)
        {
            _fileSystem = fileSystem ?? FileSystemSourceDeleteFileSystem.Instance;
        }

        /// <summary>
        /// 解压收尾那条路用的入口（判据是**这一次真的跑过的那份校验结果**）。
        /// </summary>
        public SourceCleanupResult Cleanup(ArchiveTask? task, OutputVerificationResult? verification, bool enabled)
        {
            return CleanupCore(
                task,
                enabled,
                verified: verification != null && verification.Verified,
                verificationNote: verification == null ? "没有校验结果" : verification.Message);
        }

        /// <summary>
        /// 续解链跑完之后那条路用的入口（判据由调用方按**明确的既有事实**给出）。
        ///
        /// <para>
        /// 为什么要单独一个入口：链结束时的"内容物已好"不是一个 <see cref="OutputVerificationResult"/> 对象
        /// （那是**整条链**的结论：根任务校验通过、链里每个落在同一目录的任务都校验通过、目录里确实有内容物），
        /// 硬造一个假的校验结果对象反而更危险（数字是编的）。
        /// 所以这里要求调用方把<strong>事实</strong>作为参数传进来，而"不满足就不删"这条判断仍然在本类里，
        /// 一处定义 —— 与 <see cref="Cleanup"/> 共用同一段执行体。
        /// </para>
        /// </summary>
        /// <param name="verified">
        /// 输出校验是否通过。⛔ 只有调用方逐条查过"根任务校验通过 + 全链校验通过 + 那个目录里有内容物"之后才允许传 true。
        /// </param>
        /// <param name="verificationNote">这一条事实的说明（进日志与结论，便于事后对账）。</param>
        public SourceCleanupResult CleanupVerified(
            ArchiveTask? task,
            bool verified,
            string verificationNote,
            bool enabled)
        {
            return CleanupCore(task, enabled, verified, verificationNote);
        }

        private SourceCleanupResult CleanupCore(
            ArchiveTask? task,
            bool enabled,
            bool verified,
            string verificationNote)
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
            if (!verified)
            {
                string reason = string.IsNullOrWhiteSpace(verificationNote) ? "没有校验结果" : verificationNote;

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
                if (!_fileSystem.FileExists(targetPath))
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
                    _fileSystem.DeleteFile(targetPath);

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

        internal static bool SafeFileExists(string? path)
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
