using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using Microsoft.Win32;

namespace ArchiveFixer.Storage
{
    /// <summary>
    /// 删除档位（AGENTS.md §9.5、docs/输出与整理模型.md §3.2 / §5）。
    /// </summary>
    public enum DeleteMode
    {
        /// <summary>移入回收站（**默认档**）：可恢复，回收站不可用时一律不删。</summary>
        RecycleBin = 0,

        /// <summary>彻底删除（激进档）：不可恢复。界面必须红色标识 + 二次确认（§3.2 清理表）。</summary>
        Permanent = 1
    }

    /// <summary>
    /// "这次删除没有发生"的原因。枚举给机器判定，中文说明一律放各结果的 <c>Message</c>。
    ///
    /// 排在前面的都是**安全前置**（见 <see cref="DeleteSafetyGuard"/>），
    /// 出现任何一个都表示"一个字节都没动"，调用方可以放心地告诉用户"没有删除任何东西"。
    /// </summary>
    public enum DeleteBlockReason
    {
        /// <summary>删除成功，没有任何阻拦。</summary>
        None = 0,

        /// <summary>调用方没有传入"已获用户确认"标志（默认拒绝）。</summary>
        NoConfirmation,

        /// <summary>没有指定允许的根目录 —— 无法校验落点，拒绝。</summary>
        RootNotSpecified,

        /// <summary>目标路径为空。</summary>
        EmptyTarget,

        /// <summary>路径无法规范化（非法字符 / 超长 / 带 \\?\ 前缀等），"确认不了"就是拒绝。</summary>
        UnresolvablePath,

        /// <summary>目标就是允许根本身（那等于把整棵树端掉）。</summary>
        IsAllowedRootItself,

        /// <summary>目标是驱动器根（<c>C:\</c>）。</summary>
        IsDriveRoot,

        /// <summary>目标不在允许根之内（或无法确认在之内）。</summary>
        OutsideAllowedRoot,

        /// <summary>目标不存在（可能已被移动或删除）；本次没有执行删除。</summary>
        TargetNotFound,

        /// <summary>目标本身是符号链接 / 联接点。</summary>
        TargetIsReparsePoint,

        /// <summary>目标到允许根之间的某一级目录是符号链接 / 联接点（落点可能被解析到根之外）。</summary>
        ReparsePointInPath,

        /// <summary>目标目录的子树里有符号链接 / 联接点（递归删除可能跟着跑出去）。</summary>
        ReparsePointInSubtree,

        /// <summary>安全检查读不到文件属性，无法确认安全 —— 保守拒绝。</summary>
        SafetyProbeUnavailable,

        /// <summary>子树条目数超过安全检查上限，没扫完就不能保证安全 —— 保守拒绝。</summary>
        SubtreeTooLarge,

        /// <summary>回收站不可用（网络盘 / 策略禁用 / 卷没有回收站）：**不降级为永久删除**。</summary>
        RecycleBinUnavailable,

        /// <summary>删除动作本身失败（占用、权限、系统错误、用户取消）。</summary>
        DeleteFailed,

        /// <summary>系统报告成功，但目标仍然存在 —— 按失败处理，不假装删掉了。</summary>
        TargetStillExists,

        /// <summary>调用参数不成立（没有任何有效的删除选项）。</summary>
        InvalidRequest
    }

    /// <summary>一次删除请求：删哪个路径、为什么删。</summary>
    public sealed class DeleteRequest
    {
        public string Path { get; init; } = string.Empty;

        /// <summary>为什么要删。会原样写进删除日志（§3.2 要求日志含"路径 + 理由"）。</summary>
        public string Reason { get; init; } = string.Empty;

        public DeleteRequest()
        {
        }

        public DeleteRequest(string path, string reason = "")
        {
            Path = path ?? string.Empty;
            Reason = reason ?? string.Empty;
        }
    }

    /// <summary>
    /// 一次删除调用的选项。
    ///
    /// <see cref="UserConfirmed"/> 是**必填**的语义（默认 false）：没有它的调用一律被拒。
    /// 这不是"少一层校验"，而是"默认拒绝"这条规矩的落点 —— 删除不可逆，
    /// 只有调用方（界面层）明确说了"用户已确认"，本服务才动手。
    /// </summary>
    public sealed class DeleteOptions
    {
        /// <summary>允许的根目录。目标必须落在它之内，且不得等于它。</summary>
        public string AllowedRoot { get; init; } = string.Empty;

        /// <summary>调用方是否已获得用户明确确认。<b>默认 false = 一律拒绝</b>。</summary>
        public bool UserConfirmed { get; init; }

        /// <summary>删除档位，默认回收站。</summary>
        public DeleteMode Mode { get; init; } = DeleteMode.RecycleBin;

        /// <summary>本批次的删除理由（单个请求没写理由时用它），写进日志。</summary>
        public string Reason { get; init; } = string.Empty;
    }

    /// <summary>目标的度量结果：条目数 + 总大小，用于删除日志（§3.2 要求日志报总大小）。</summary>
    public sealed class DeleteMeasurement
    {
        /// <summary>是否完整量到了（目录读不到时为 false，此时数字是"至少这么多"）。</summary>
        public bool Determined { get; init; }

        public bool IsDirectory { get; init; }

        /// <summary>条目数（文件 + 目录，不含目标自己）。单个文件为 1。</summary>
        public int EntryCount { get; init; }

        public int FileCount { get; init; }

        public long TotalBytes { get; init; }
    }

    /// <summary>安全检查的结论。</summary>
    public sealed class DeleteSafetyDecision
    {
        public bool IsAllowed { get; init; }

        public DeleteBlockReason Reason { get; init; }

        /// <summary>中文说明，可直接给用户看 / 进日志。</summary>
        public string Message { get; init; } = string.Empty;

        public static DeleteSafetyDecision Allow(string message = "安全检查通过")
        {
            return new DeleteSafetyDecision
            {
                IsAllowed = true,
                Reason = DeleteBlockReason.None,
                Message = message
            };
        }

        public static DeleteSafetyDecision Block(DeleteBlockReason reason, string message)
        {
            return new DeleteSafetyDecision
            {
                IsAllowed = false,
                Reason = reason,
                Message = message
            };
        }
    }

    /// <summary>
    /// 一条删除日志（路径 + 理由 + 条目数 + 总大小 + 结果）。
    ///
    /// 这是本服务对外"写日志"的唯一形态：<see cref="IDeleteLogSink"/> 把它交给调用方，
    /// <see cref="DeleteResult.LogEntries"/> 也原样带回。**不允许**在这里拼 UI，
    /// 更不允许记密码类内容（AGENTS.md §6 第 5 条）。
    /// </summary>
    public sealed class DeleteLogEntry
    {
        public DateTime Time { get; init; } = DateTime.Now;

        public string Path { get; init; } = string.Empty;

        /// <summary>为什么要删（调用方给的理由）。</summary>
        public string Reason { get; init; } = string.Empty;

        public DeleteMode Mode { get; init; }

        public bool Success { get; init; }

        public DeleteBlockReason BlockReason { get; init; }

        /// <summary>结果说明（失败时是失败原因）。</summary>
        public string Message { get; init; } = string.Empty;

        public int EntryCount { get; init; }

        public long TotalBytes { get; init; }

        public string ToDisplayText()
        {
            string modeText = Mode == DeleteMode.Permanent ? "彻底删除" : "移入回收站";

            return $"[{Time:yyyy-MM-dd HH:mm:ss}] [{modeText}] {(Success ? "成功" : "未执行")} " +
                   $"路径={Path}；理由={(string.IsNullOrWhiteSpace(Reason) ? "未说明" : Reason)}；" +
                   $"条目数={EntryCount}；总大小={TotalBytes} 字节；说明={Message}";
        }
    }

    /// <summary>
    /// 删除日志的落点。由调用方注入（界面日志、文件日志、测试收集器都行）——
    /// 本层**不依赖** UI，也不需要知道日志最终写到哪。
    /// </summary>
    public interface IDeleteLogSink
    {
        void Write(DeleteLogEntry entry);
    }

    /// <summary>
    /// 删除前的安全检查所需要的文件系统信息。
    ///
    /// 抽成接口只为一件事：**让安全检查可以被单测**。造符号链接要管理员权限、造联接点要外部命令，
    /// 在测试里都不稳定（见 ExtractionMaintenanceTests 里同样的说明），
    /// 所以"路径上有联接点"这类判定必须能靠注入的假探针来验证。
    /// </summary>
    public interface IDeleteFileSystemProbe
    {
        /// <summary>读属性；不存在 / 读不到一律返回 null（调用方按"无法确认"处理）。</summary>
        FileAttributes? TryGetAttributes(string? path);

        /// <summary>列目录里的全部条目（文件 + 目录）；读不到返回 false。</summary>
        bool TryListChildren(string? directory, out IReadOnlyList<string> children);

        /// <summary>列目录里的直接子目录；读不到返回 false。</summary>
        bool TryListChildDirectories(string? directory, out IReadOnlyList<string> directories);
    }

    /// <summary>真实文件系统的探针。</summary>
    public sealed class WindowsDeleteFileSystemProbe : IDeleteFileSystemProbe
    {
        public static WindowsDeleteFileSystemProbe Instance { get; } = new();

        public FileAttributes? TryGetAttributes(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return null;
                }

                return File.GetAttributes(path);
            }
            catch
            {
                // 不存在、路径非法、权限不足…… 全部收敛成"读不到"。
                return null;
            }
        }

        public bool TryListChildren(string? directory, out IReadOnlyList<string> children)
        {
            children = Array.Empty<string>();

            try
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return false;
                }

                children = Directory.GetFileSystemEntries(directory);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool TryListChildDirectories(string? directory, out IReadOnlyList<string> directories)
        {
            directories = Array.Empty<string>();

            try
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return false;
                }

                directories = Directory.GetDirectories(directory);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>单个目标的删除结果。</summary>
    public sealed class DeleteOutcome
    {
        public string Path { get; init; } = string.Empty;

        /// <summary>为什么要删（调用方给的理由）。</summary>
        public string Reason { get; init; } = string.Empty;

        public bool Success { get; init; }

        public DeleteMode Mode { get; init; }

        public DeleteBlockReason BlockReason { get; init; }

        /// <summary>中文说明（失败时即失败原因）。</summary>
        public string Message { get; init; } = string.Empty;

        public int EntryCount { get; init; }

        public long TotalBytes { get; init; }

        /// <summary>**彻底删除**真正释放的字节数。移入回收站时为 0（文件还在回收站里占着空间）。</summary>
        public long FreedBytes { get; init; }

        /// <summary>移入回收站的字节数（没有真正释放空间，单独记，免得把"搬走"说成"腾出"）。</summary>
        public long RecycledBytes { get; init; }
    }

    /// <summary>一次批量删除的结果。</summary>
    public sealed class DeleteResult
    {
        public DeleteMode Mode { get; init; }

        public string AllowedRoot { get; init; } = string.Empty;

        public IReadOnlyList<DeleteOutcome> Outcomes { get; init; } = Array.Empty<DeleteOutcome>();

        /// <summary>本次调用产生的删除日志（与注入的 <see cref="IDeleteLogSink"/> 收到的是同一批）。</summary>
        public IReadOnlyList<DeleteLogEntry> LogEntries { get; init; } = Array.Empty<DeleteLogEntry>();

        public int SuccessCount => Outcomes.Count(outcome => outcome.Success);

        public int FailureCount => Outcomes.Count(outcome => !outcome.Success);

        /// <summary>彻底删除真正释放的字节数合计。</summary>
        public long FreedBytes => Outcomes.Sum(outcome => outcome.FreedBytes);

        /// <summary>移入回收站的字节数合计（未真正释放）。</summary>
        public long RecycledBytes => Outcomes.Sum(outcome => outcome.RecycledBytes);

        /// <summary>失败原因清单（"路径：说明"），失败的每一个都在这里。</summary>
        public IReadOnlyList<string> FailureReasons { get; init; } = Array.Empty<string>();

        /// <summary>一句话结论，可直接进日志 / 提示框。</summary>
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// 删除的安全前置（AGENTS.md §6 第 13 条、docs/输出与整理模型.md §3.2 / §5）。
    ///
    /// 五条缺一不可，全部在这里，且**默认拒绝**：
    /// ① 目标必须在允许的根之内（复用 <see cref="ArchivePathGuard.IsInsideRoot"/>，不另写一套）；
    /// ② 目标不得是驱动器根、不得是允许根本身；
    /// ③ 目标到根的路径上、以及目标的子树里，都不得有符号链接 / 联接点；
    /// ④ 调用方必须显式传"已获用户确认"；
    /// ⑤ 每个动作由 <see cref="RecycleBinService"/> 写日志（路径 + 理由 + 条目数 + 总大小）。
    ///
    /// 拆成两层是为了可测：<see cref="EvaluatePath"/> 是**纯函数**（只碰字符串），
    /// <see cref="Evaluate"/> 在它之上再做需要查文件系统的第 ③ 条（探针可注入）。
    ///
    /// ⚠ 这里是**尽力而为**的检查，不是原子保证：检查通过到真正删除之间，路径仍可能被换掉
    /// （TOCTOU）。能做到的是"检查到可疑就拒绝"，做不到"检查过就一定安全"。
    /// </summary>
    public static class DeleteSafetyGuard
    {
        /// <summary>
        /// 子树安全检查的条目上限。超过就**拒绝**而不是"扫一半算了"：
        /// 没扫完就没法保证里面没有联接点，而递归删除跟着联接点跑出去的后果是不可逆的。
        /// </summary>
        public const int MaxSubtreeScanEntryCount = 200_000;

        /// <summary>上级目录链的最大长度（正常路径不可能有这么多级，纯属防死循环）。</summary>
        private const int MaxAncestorDepth = 512;

        /// <summary>
        /// 纯判定（不碰文件系统）：确认标志、根目录、越界、根本身、驱动器根。
        /// </summary>
        public static DeleteSafetyDecision EvaluatePath(string? allowedRoot, string? targetPath, bool userConfirmed)
        {
            // ① 用户确认放在最前面：没有确认时，"路径合不合法"根本不该被讨论（默认拒绝最安全）。
            if (!userConfirmed)
            {
                return DeleteSafetyDecision.Block(
                    DeleteBlockReason.NoConfirmation,
                    "调用方没有传入\"已获用户确认\"标志，一律拒绝删除（删除不可逆，必须由界面层显式确认）");
            }

            if (string.IsNullOrWhiteSpace(allowedRoot))
            {
                return DeleteSafetyDecision.Block(
                    DeleteBlockReason.RootNotSpecified,
                    "没有指定允许的根目录，无法校验目标是否在其中，拒绝删除");
            }

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return DeleteSafetyDecision.Block(DeleteBlockReason.EmptyTarget, "目标路径为空，拒绝删除");
            }

            if (!TryNormalizeForCompare(allowedRoot, out string rootFull) ||
                !TryNormalizeForCompare(targetPath, out string targetFull))
            {
                return DeleteSafetyDecision.Block(
                    DeleteBlockReason.UnresolvablePath,
                    $"路径无法规范化（可能含非法字符或超长），无法确认落点，拒绝删除：{targetPath}");
            }

            // ② 允许根本身：IsInsideRoot 会把"等于根"判成在根之内，这里必须单独拦一次 ——
            // 否则一次"看起来在根内"的删除会把整个根目录端掉。
            if (string.Equals(rootFull, targetFull, StringComparison.OrdinalIgnoreCase))
            {
                return DeleteSafetyDecision.Block(
                    DeleteBlockReason.IsAllowedRootItself,
                    $"目标是允许的根目录本身（{targetFull}），拒绝删除");
            }

            // ② 驱动器根：C:\ 这种目标是"整盘删除"的量级，任何情况下都不允许。
            // 放在包含判定之前，是为了让 C:\ 这种目标报出"驱动器根"而不是笼统的"不在根之内"。
            if (IsDriveRoot(targetFull))
            {
                return DeleteSafetyDecision.Block(
                    DeleteBlockReason.IsDriveRoot,
                    $"目标是驱动器根目录（{targetFull}），拒绝删除");
            }

            // ① 容器内校验：复用既有的 IsInsideRoot，不自己写一套前缀比较。
            if (!ArchivePathGuard.IsInsideRoot(rootFull, targetFull, out string insideReason))
            {
                return DeleteSafetyDecision.Block(
                    DeleteBlockReason.OutsideAllowedRoot,
                    $"目标不在允许的根目录之内，拒绝删除（{insideReason}）");
            }

            return DeleteSafetyDecision.Allow();
        }

        /// <summary>
        /// 完整判定：在 <see cref="EvaluatePath"/> 之上，再做"路径上 / 子树里有没有符号链接、联接点"的检查。
        /// </summary>
        public static DeleteSafetyDecision Evaluate(
            string? allowedRoot,
            string? targetPath,
            bool userConfirmed,
            IDeleteFileSystemProbe? probe = null)
        {
            DeleteSafetyDecision pathDecision = EvaluatePath(allowedRoot, targetPath, userConfirmed);

            if (!pathDecision.IsAllowed)
            {
                return pathDecision;
            }

            probe ??= WindowsDeleteFileSystemProbe.Instance;

            if (!TryNormalizeForCompare(allowedRoot, out string rootFull) ||
                !TryNormalizeForCompare(targetPath, out string targetFull))
            {
                return DeleteSafetyDecision.Block(
                    DeleteBlockReason.UnresolvablePath,
                    $"路径无法规范化，无法确认落点，拒绝删除：{targetPath}");
            }

            FileAttributes? targetAttributes = probe.TryGetAttributes(targetFull);

            if (targetAttributes == null)
            {
                return DeleteSafetyDecision.Block(
                    DeleteBlockReason.TargetNotFound,
                    $"目标不存在或读不到属性（{targetFull}），本次未执行删除");
            }

            // ③-a 目标自己是链接 / 联接点：删它等于"删了不在授权范围内的东西"（链接指向的内容在别处）。
            if ((targetAttributes.Value & FileAttributes.ReparsePoint) != 0)
            {
                return DeleteSafetyDecision.Block(
                    DeleteBlockReason.TargetIsReparsePoint,
                    $"目标是符号链接 / 联接点（{targetFull}），删除它会影响链接指向的其它位置，拒绝删除");
            }

            // ③-b 目标到根之间的每一级：中间夹着链接时，IsInsideRoot 的字符串判定会被绕过去
            //（root\link\file 文本上在根内，物理上可能在另一块盘）。
            DeleteSafetyDecision ancestorDecision = CheckAncestors(targetFull, rootFull, probe);

            if (!ancestorDecision.IsAllowed)
            {
                return ancestorDecision;
            }

            // ③-c 目标目录的子树：递归删除跟着联接点跑出去过，就不能让它开始。
            if ((targetAttributes.Value & FileAttributes.Directory) != 0)
            {
                DeleteSafetyDecision subtreeDecision = CheckSubtree(targetFull, probe);

                if (!subtreeDecision.IsAllowed)
                {
                    return subtreeDecision;
                }
            }

            return DeleteSafetyDecision.Allow();
        }

        /// <summary>目标（含）以上、直到允许根（不含）的每一级目录都不能是符号链接 / 联接点。</summary>
        private static DeleteSafetyDecision CheckAncestors(
            string targetFull,
            string rootFull,
            IDeleteFileSystemProbe probe)
        {
            string? current = Path.GetDirectoryName(targetFull);

            for (int depth = 0; depth < MaxAncestorDepth && !string.IsNullOrEmpty(current); depth++)
            {
                if (string.Equals(current, rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    // 走到根了，链上全是普通目录。
                    return DeleteSafetyDecision.Allow();
                }

                FileAttributes? attributes = probe.TryGetAttributes(current);

                if (attributes == null)
                {
                    return DeleteSafetyDecision.Block(
                        DeleteBlockReason.SafetyProbeUnavailable,
                        $"读不到上级目录的属性（{current}），无法确认路径上没有联接点，拒绝删除");
                }

                if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
                {
                    return DeleteSafetyDecision.Block(
                        DeleteBlockReason.ReparsePointInPath,
                        $"上级目录 {current} 是符号链接 / 联接点，目标的真实位置可能不在允许的根之内，拒绝删除");
                }

                string? parent = Path.GetDirectoryName(current);

                // 到盘根了还没碰到允许根 —— 说明这条路径与根的包含关系不可信，保守拒绝。
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = parent;
            }

            return DeleteSafetyDecision.Block(
                DeleteBlockReason.SafetyProbeUnavailable,
                $"无法确认目标位于允许的根（{rootFull}）之下，拒绝删除");
        }

        /// <summary>
        /// 子树里不得出现符号链接 / 联接点。扫不完（读不到 / 条目过多）也拒绝 ——
        /// 这一条是"宁可少删，不可误删"的直接体现。
        /// </summary>
        private static DeleteSafetyDecision CheckSubtree(string directory, IDeleteFileSystemProbe probe)
        {
            var pending = new Stack<string>();
            pending.Push(directory);

            int scanned = 0;

            while (pending.Count > 0)
            {
                string current = pending.Pop();

                if (!probe.TryListChildren(current, out IReadOnlyList<string> children))
                {
                    return DeleteSafetyDecision.Block(
                        DeleteBlockReason.SafetyProbeUnavailable,
                        $"无法读取目录内容（{current}），不能确认里面没有联接点，拒绝删除");
                }

                foreach (string child in children)
                {
                    scanned++;

                    if (scanned > MaxSubtreeScanEntryCount)
                    {
                        return DeleteSafetyDecision.Block(
                            DeleteBlockReason.SubtreeTooLarge,
                            $"待删除目录的条目数超过安全检查上限（{MaxSubtreeScanEntryCount}），" +
                            "没有扫完就不能保证里面没有联接点，拒绝删除");
                    }

                    FileAttributes? attributes = probe.TryGetAttributes(child);

                    if (attributes == null)
                    {
                        return DeleteSafetyDecision.Block(
                            DeleteBlockReason.SafetyProbeUnavailable,
                            $"读不到条目属性（{child}），不能确认它不是联接点，拒绝删除");
                    }

                    if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
                    {
                        return DeleteSafetyDecision.Block(
                            DeleteBlockReason.ReparsePointInSubtree,
                            $"待删除目录里含符号链接 / 联接点（{child}），递归删除可能跟着它跑出允许的根，拒绝删除");
                    }

                    if ((attributes.Value & FileAttributes.Directory) != 0)
                    {
                        pending.Push(child);
                    }
                }
            }

            return DeleteSafetyDecision.Allow();
        }

        /// <summary>是否是驱动器根（<c>C:\</c>）。</summary>
        private static bool IsDriveRoot(string fullPath)
        {
            try
            {
                string normalized = Path.TrimEndingDirectorySeparator(fullPath);
                string? root = Path.GetPathRoot(normalized);

                if (string.IsNullOrWhiteSpace(root))
                {
                    return false;
                }

                return string.Equals(
                    normalized,
                    Path.TrimEndingDirectorySeparator(root),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // 取不到盘根时按"不是驱动器根"处理：越界与自身判定已经在前面拦过一遍了。
                return false;
            }
        }

        /// <summary>规范化成可比较的完整路径（展开相对路径、消掉 ..、去掉结尾分隔符）。</summary>
        private static bool TryNormalizeForCompare(string? path, out string fullPath)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                fullPath = string.Empty;
                return false;
            }

            try
            {
                fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                return !string.IsNullOrWhiteSpace(fullPath);
            }
            catch
            {
                fullPath = string.Empty;
                return false;
            }
        }
    }

    /// <summary>回收站删除的尝试结果。</summary>
    public enum RecycleAttemptResult
    {
        /// <summary>已移入回收站（目标已经不在原位置）。</summary>
        Recycled = 0,

        /// <summary>回收站不可用（网络盘 / 策略禁用 / 卷没有回收站 / 非 64 位进程）：<b>没有执行任何删除</b>。</summary>
        Unavailable = 1,

        /// <summary>回收站可用但这次没成功（占用 / 权限 / 被取消 / 系统错误）。</summary>
        Failed = 2
    }

    /// <summary>
    /// 真正动手删的那一层。抽出来是为了让 <see cref="RecycleBinService"/> 的
    /// 安全规则与日志**可以脱离真实回收站被测试**（测试绝不能往用户系统回收站里塞东西）。
    /// </summary>
    public interface IDeleteExecutor
    {
        /// <summary>
        /// 移入回收站。
        ///
        /// 返回 <see cref="RecycleAttemptResult.Unavailable"/> 表示回收站不可用 ——
        /// 实现方**不得**在此时改用永久删除，调用方也不得据此自行降级（§3.2 清理表）。
        /// </summary>
        RecycleAttemptResult TryMoveToRecycleBin(string path, bool isDirectory, out string message);

        /// <summary>永久删除（文件，或目录递归）。失败时抛异常，由调用方记录并不影响其余目标。</summary>
        void DeletePermanently(string path, bool isDirectory);
    }

    /// <summary>判断"这个路径能不能进回收站"。同样可注入，便于验证"不可用时不降级"。</summary>
    public interface IRecycleBinAvailabilityProbe
    {
        /// <summary>可用返回 true；不可用时 <paramref name="reason"/> 说明原因。</summary>
        bool IsRecycleBinUsable(string fullPath, out string reason);
    }

    /// <summary>
    /// 用系统策略 / 卷类型 / 回收站查询判断可用性。
    ///
    /// 为什么要**先判断**再调 Shell：<c>SHFileOperation</c> 的 <c>FOF_ALLOWUNDO</c> 语义是
    /// "**能回收就回收**" —— 回收不了的时候它会直接永久删除。那正是我们绝不允许的降级，
    /// 所以判定必须发生在调用之前，而不是事后看结果。
    ///
    /// 判定为不可用时**一律拒绝**（fail closed）：读不到策略也算不可用，
    /// 宁可让用户显式选择"彻底删除"，也不替他猜"应该能回收吧"。
    /// </summary>
    public sealed class ShellRecycleBinAvailabilityProbe : IRecycleBinAvailabilityProbe
    {
        private const string ExplorerPolicySubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

        /// <summary>"不将文件移入回收站，而是直接删除"策略值（资源管理器 → 回收站属性）。</summary>
        private const string NoRecycleFilesValueName = "NoRecycleFiles";

        /// <summary>x64 下 SHQUERYRBINFO 的大小（DWORD + 4 字节填充 + 两个 __int64）。</summary>
        private const int ShQueryRbInfoSizeX64 = 24;

        public bool IsRecycleBinUsable(string fullPath, out string reason)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                reason = "路径为空，无法判断回收站是否可用";
                return false;
            }

            /*
             * Shell 的 SHQUERYRBINFO 在 32 位下是 pack(1)（shellapi.h：
             * `#if !defined(_WIN64) #include <pshpack1.h>`），与 C# 的默认布局不同。
             * 与其在 32 位进程里赌一个结构布局，不如直接判定"回收站档不可用"（fail closed）：
             * 本产品按 64 位分发，32 位进程下用户仍可显式选择彻底删除。
             */
            if (IntPtr.Size != 8)
            {
                reason = "当前不是 64 位进程，回收站接口不可用（保守拒绝，可改用彻底删除）";
                return false;
            }

            // UNC 路径（\\server\share）一定没有回收站；网络盘即使映射成盘符也一样。
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                reason = "目标是网络共享路径（UNC），系统不会为它保留回收站，已拒绝（不会改为永久删除）";
                return false;
            }

            string? volumeRoot;

            try
            {
                volumeRoot = Path.GetPathRoot(Path.GetFullPath(fullPath));
            }
            catch (Exception ex)
            {
                reason = $"路径无法解析，无法判断回收站是否可用：{ex.Message}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(volumeRoot))
            {
                reason = "取不到目标所在卷，无法判断回收站是否可用";
                return false;
            }

            try
            {
                var drive = new DriveInfo(volumeRoot);

                if (!drive.IsReady)
                {
                    reason = $"卷未就绪（{volumeRoot}），回收站不可用";
                    return false;
                }

                if (drive.DriveType == DriveType.Network)
                {
                    reason = $"目标是网络盘（{volumeRoot}），系统不会为它保留回收站，已拒绝（不会改为永久删除）";
                    return false;
                }

                if (drive.DriveType == DriveType.CDRom || drive.DriveType == DriveType.NoRootDirectory)
                {
                    reason = $"卷类型（{drive.DriveType}）不支持回收站";
                    return false;
                }
            }
            catch (Exception ex)
            {
                reason = $"无法确定目标所在卷（{ex.Message}），不能确认回收站可用";
                return false;
            }

            if (!IsPolicyAllowingRecycleBin(out string policyReason))
            {
                reason = policyReason;
                return false;
            }

            if (!TryQueryRecycleBin(volumeRoot, out string queryReason))
            {
                reason = queryReason;
                return false;
            }

            reason = "回收站可用";
            return true;
        }

        /// <summary>
        /// "不经过回收站"策略是否被打开。读不到注册表时返回 false（不可用）并说明原因 ——
        /// 属于 fail closed：无法确认"文件会被回收"时，就不做这次回收站删除。
        /// </summary>
        private static bool IsPolicyAllowingRecycleBin(out string reason)
        {
            // 两个位置都查（用户策略 + 机器策略），任意一个说"不经过回收站"就按不可用处理。
            // 连 Registry.CurrentUser / LocalMachine 这两个属性访问都放在 try 里：
            // 受限环境下取注册表根本身就可能抛，而那也必须收敛成"不可用"，不能漏出去变成一次删除。
            foreach (bool machineScope in new[] { false, true })
            {
                try
                {
                    RegistryKey root = machineScope ? Registry.LocalMachine : Registry.CurrentUser;

                    using RegistryKey? key = root.OpenSubKey(ExplorerPolicySubKey);
                    object? value = key?.GetValue(NoRecycleFilesValueName);

                    if (value is int flag && flag != 0)
                    {
                        reason = $"系统策略已设置\"删除时不将文件移入回收站\"（{NoRecycleFilesValueName}=1），" +
                                 "回收站删除不可用，已拒绝（不会改为永久删除）";
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    reason = $"无法读取系统策略（{ex.Message}），不能确认回收站可用，已拒绝（不会改为永久删除）";
                    return false;
                }
            }

            reason = "没有禁止使用回收站的策略";
            return true;
        }

        /// <summary>问系统"这个卷有没有回收站"。失败 = 没有（不猜成有）。</summary>
        private static bool TryQueryRecycleBin(string volumeRoot, out string reason)
        {
            int size = Marshal.SizeOf<SHQUERYRBINFO>();

            if (size != ShQueryRbInfoSizeX64)
            {
                // 结构布局与预期不符（例如有人把它改成 Pack=1）：不拿一个可疑的结构去调系统。
                reason = "回收站信息结构与预期不符，无法查询回收站状态，已拒绝";
                return false;
            }

            try
            {
                var info = new SHQUERYRBINFO { cbSize = size };
                int hr = SHQueryRecycleBinW(volumeRoot, ref info);

                if (hr != 0)
                {
                    reason = $"系统报告该卷没有可用的回收站（结果 0x{hr:X8}），已拒绝（不会改为永久删除）";
                    return false;
                }
            }
            catch (Exception ex)
            {
                reason = $"查询回收站状态失败（{ex.Message}），不能确认回收站可用";
                return false;
            }

            reason = "该卷有可用的回收站";
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBinW(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);
    }

    /// <summary>
    /// 用 Shell API（<c>SHFileOperationW</c>）把目标移入回收站；彻底删除走 .NET 的
    /// <see cref="File.Delete(string)"/> / <see cref="Directory.Delete(string, bool)"/>。
    ///
    /// 三个实现要点，都是踩过的坑，别改回去：
    /// ① **先判可用性再调用**：<c>FOF_ALLOWUNDO</c> 是"能回收就回收"，回收不了会**直接永久删除**；
    /// ② 默认带 <c>FOF_WANTNUKEWARNING</c>：万一系统仍要永久删除，它会弹系统警告让用户确认 ——
    ///    宁可多一次系统提示，也不允许静默永久删除（代价：无人值守时会等用户点一下）；
    /// ③ 长路径：<c>SHFileOperation</c> 不接受 <c>\\?\</c> 前缀，超过 MAX_PATH 时退到 8.3 短名，
    ///    短名也拿不到就**失败**（不改成永久删除）。
    ///
    /// ⚠ 已知限制（不假装解决）：若系统无法回收而用户在 <c>FOF_WANTNUKEWARNING</c> 的警告里
    /// 选择了永久删除，这次删除同样是"成功"，本方法无法把它与真正进回收站区分开 ——
    /// 但那已经是用户在系统提示下的显式选择，不属于"静默降级"。
    /// </summary>
    public sealed class ShellDeleteExecutor : IDeleteExecutor
    {
        /// <summary>MAX_PATH。超过它的路径 SHFileOperation 处理不了。</summary>
        private const int MaxShellPathLength = 260;

        private const uint FoDelete = 0x0003;

        private const ushort FofSilent = 0x0004;
        private const ushort FofNoConfirmation = 0x0010;
        private const ushort FofAllowUndo = 0x0040;
        private const ushort FofNoErrorUi = 0x0400;
        private const ushort FofWantNukeWarning = 0x4000;

        /// <summary>ERROR_CANCELLED：用户在系统提示里点了取消。</summary>
        private const int ErrorCancelled = 1223;

        private const uint CoInitApartmentThreaded = 0x0002;

        private readonly IRecycleBinAvailabilityProbe _availability;
        private readonly bool _warnBeforePermanentDelete;

        public ShellDeleteExecutor(
            IRecycleBinAvailabilityProbe? availability = null,
            bool warnBeforePermanentDelete = true)
        {
            _availability = availability ?? new ShellRecycleBinAvailabilityProbe();
            _warnBeforePermanentDelete = warnBeforePermanentDelete;
        }

        public RecycleAttemptResult TryMoveToRecycleBin(string path, bool isDirectory, out string message)
        {
            string fullPath = SafePathHelper.GetFullPathSafe(path);

            if (string.IsNullOrWhiteSpace(fullPath))
            {
                message = "路径为空，无法移入回收站";
                return RecycleAttemptResult.Failed;
            }

            if (!_availability.IsRecycleBinUsable(fullPath, out string unavailableReason))
            {
                // 这里**只**返回"不可用"，绝不回落到 DeletePermanently（§3.2 清理表）。
                message = unavailableReason;
                return RecycleAttemptResult.Unavailable;
            }

            if (!TryGetShellCompatiblePath(fullPath, out string shellPath, out string pathReason))
            {
                message = pathReason;
                return RecycleAttemptResult.Failed;
            }

            int result = InvokeShellDelete(shellPath, out bool aborted);

            if (result != 0)
            {
                message = $"移入回收站失败：{DescribeShellError(result)}";
                return RecycleAttemptResult.Failed;
            }

            if (aborted)
            {
                message = "删除被取消（可能是系统弹出的\"永久删除\"警告被拒绝），目标未删除";
                return RecycleAttemptResult.Failed;
            }

            if (SafePathHelper.FileExists(fullPath) || SafePathHelper.DirectoryExists(fullPath))
            {
                message = "系统报告成功，但目标仍然存在，按失败处理（不假装删掉了）";
                return RecycleAttemptResult.Failed;
            }

            message = "已移入回收站";
            return RecycleAttemptResult.Recycled;
        }

        public void DeletePermanently(string path, bool isDirectory)
        {
            string fullPath = SafePathHelper.GetFullPathSafe(path);

            if (string.IsNullOrWhiteSpace(fullPath))
            {
                throw new ArgumentException("路径为空，无法彻底删除", nameof(path));
            }

            if (isDirectory)
            {
                // 递归删除。目标子树里的联接点已在 DeleteSafetyGuard 里被拦掉，所以这里不会跟着链接跑出去。
                Directory.Delete(fullPath, recursive: true);
                return;
            }

            File.Delete(fullPath);
        }

        /// <summary>
        /// SHFileOperation 只认 MAX_PATH 以内的常规路径（<c>\\?\</c> 前缀它反而处理不了），
        /// 超长时退到 8.3 短名；拿不到短名就失败，**不**改成永久删除。
        /// </summary>
        private static bool TryGetShellCompatiblePath(string fullPath, out string shellPath, out string reason)
        {
            shellPath = fullPath;
            reason = string.Empty;

            if (fullPath.Length < MaxShellPathLength)
            {
                return true;
            }

            string shortPath = TryGetShortPathName(fullPath);

            if (!string.IsNullOrWhiteSpace(shortPath) && shortPath.Length < MaxShellPathLength)
            {
                shellPath = shortPath;
                return true;
            }

            reason = $"路径过长（{fullPath.Length} 个字符），回收站接口不支持该路径；本次未执行删除，" +
                     "未改用彻底删除（如需删除请显式选择彻底删除，或先把路径改短）";
            return false;
        }

        private static string TryGetShortPathName(string fullPath)
        {
            try
            {
                var buffer = new StringBuilder(MaxShellPathLength + 1);
                uint length = GetShortPathNameW(fullPath, buffer, (uint)buffer.Capacity);

                if (length == 0 || length > buffer.Capacity)
                {
                    return string.Empty;
                }

                return buffer.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private int InvokeShellDelete(string shellPath, out bool aborted)
        {
            aborted = false;

            /*
             * SHFileOperation 属于 Shell API，需要 COM 已经初始化。
             * S_OK / S_FALSE（本线程已初始化过）都表示这次调用"成功占用了引用计数"，
             * 必须配对 CoUninitialize；RPC_E_CHANGED_MODE（负值）表示本线程已按别的模式初始化过，
             * 此时不配对释放，调用照常进行（Shell 会自行处理）。
             */
            int comResult = CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded);
            bool releaseCom = comResult >= 0;

            try
            {
                ushort flags = (ushort)(FofSilent | FofNoConfirmation | FofAllowUndo | FofNoErrorUi);

                if (_warnBeforePermanentDelete)
                {
                    flags |= FofWantNukeWarning;
                }

                var operation = new SHFILEOPSTRUCTW
                {
                    hwnd = IntPtr.Zero,
                    wFunc = FoDelete,

                    // pFrom 是"双重 null 结尾"的路径列表：字符串尾部再加一个 \0，
                    // 加上封送器自己补的结尾 null，正好两个。少一个会让 Shell 读到越界内存。
                    pFrom = shellPath + "\0",
                    pTo = null,
                    fFlags = flags,
                    hNameMappings = IntPtr.Zero,
                    lpszProgressTitle = null
                };

                int result = SHFileOperationW(ref operation);
                aborted = operation.fAnyOperationsAborted;
                return result;
            }
            finally
            {
                if (releaseCom)
                {
                    CoUninitialize();
                }
            }
        }

        /// <summary>把 Shell 的返回码说成人话。返回值可能是 DE_* 码、Win32 错误码或 HRESULT。</summary>
        private static string DescribeShellError(int code)
        {
            if (code == ErrorCancelled)
            {
                return "操作被取消";
            }

            string? named = code switch
            {
                0x71 => "源与目标相同",
                0x72 => "多个源对应一个目标",
                0x73 => "不能把目录移动到其子目录",
                0x74 => "源是根目录",
                0x75 => "操作被取消",
                0x76 => "目标位于源的子树中",
                0x78 => "源被拒绝访问（权限不足或被占用）",
                0x79 => "路径过深",
                0x7A => "目标过多",
                0x7C => "源文件无效或不存在",
                0x7D => "目标与源在同一目录树",
                0x7E => "目标目录中已有同名文件",
                0x80 => "目标是目录，源是文件",
                0x81 => "文件名过长",
                0x82 => "目标是只读介质",
                0x83 => "目标是只读介质",
                0x84 => "目标目录已存在",
                0x85 => "源是只读介质",
                0x86 => "源是只读介质",
                0xB7 => "目标中存在同名文件",
                _ => null
            };

            return named != null
                ? $"{named}（0x{code:X8}）"
                : $"系统错误码 0x{code:X8}";
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCTW
        {
            public IntPtr hwnd;
            public uint wFunc;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string? pFrom;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string? pTo;

            public ushort fFlags;

            [MarshalAs(UnmanagedType.Bool)]
            public bool fAnyOperationsAborted;

            public IntPtr hNameMappings;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetShortPathNameW(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();
    }

    /// <summary>
    /// 两档删除的统一入口（AGENTS.md §9.5、docs/输出与整理模型.md §3.2 / §5）。
    ///
    /// 这个类是全项目**唯一**真正执行"删除用户文件"的地方（源包清理另有 SourceCleanupService），
    /// 所以安全边界全部写死在这里，而且都走 <see cref="DeleteSafetyGuard"/>：
    /// 根内 → 不是根本身 / 驱动器根 → 路径与子树里没有符号链接 / 联接点 → 调用方已确认 → 每一步写日志。
    ///
    /// 两条"绝不做"：
    /// ① 回收站不可用**绝不**降级成永久删除（返回失败 + 原因，由上层决定怎么办）；
    /// ② 目标不存在 / 系统报成功但目标还在，都不算成功。
    ///
    /// 单个目标失败不影响其余目标（AGENTS.md §6 第 9 条），删除跑在批处理收尾阶段，
    /// 一次抛异常把整批带崩的代价比多删几次大得多。
    /// </summary>
    public sealed class RecycleBinService
    {
        private readonly IDeleteExecutor _executor;
        private readonly IDeleteLogSink? _logSink;
        private readonly IDeleteFileSystemProbe _probe;

        /// <param name="executor">真正动手的实现；默认走 Shell 回收站 + .NET 彻底删除。</param>
        /// <param name="logSink">删除日志落点；不传时日志只在 <see cref="DeleteResult.LogEntries"/> 里返回。</param>
        /// <param name="probe">安全检查用的文件系统探针；单测可注入假探针。</param>
        public RecycleBinService(
            IDeleteExecutor? executor = null,
            IDeleteLogSink? logSink = null,
            IDeleteFileSystemProbe? probe = null)
        {
            _executor = executor ?? new ShellDeleteExecutor();
            _logSink = logSink;
            _probe = probe ?? WindowsDeleteFileSystemProbe.Instance;
        }

        /// <summary>删除单个目标。</summary>
        public DeleteResult Delete(DeleteRequest? request, DeleteOptions? options)
        {
            if (request == null)
            {
                return Delete(Array.Empty<DeleteRequest>(), options);
            }

            return Delete(new[] { request }, options);
        }

        /// <summary>
        /// 批量删除。<paramref name="requests"/> 里每一个都会走完整的安全前置并留下一条日志，
        /// 被拒绝的也留日志（"为什么没删"和"删了什么"一样重要）。
        /// </summary>
        public DeleteResult Delete(IEnumerable<DeleteRequest>? requests, DeleteOptions? options)
        {
            var outcomes = new List<DeleteOutcome>();
            var logEntries = new List<DeleteLogEntry>();

            if (requests == null)
            {
                return BuildResult(options, outcomes, logEntries, "没有要删除的目标");
            }

            // options 为空 = 没有允许根、没有确认标志：按"请求参数不成立"逐个拒绝，不是静默跳过。
            if (options == null)
            {
                foreach (DeleteRequest request in requests)
                {
                    if (request == null)
                    {
                        continue;
                    }

                    var outcome = new DeleteOutcome
                    {
                        Path = request.Path,
                        Reason = request.Reason,
                        Success = false,
                        Mode = DeleteMode.RecycleBin,
                        BlockReason = DeleteBlockReason.InvalidRequest,
                        Message = "没有有效的删除选项（缺少允许根 / 用户确认标志），已拒绝删除"
                    };

                    outcomes.Add(outcome);
                    WriteLog(logEntries, outcome, null);
                }

                return BuildResult(null, outcomes, logEntries, "没有有效的删除选项，已拒绝全部删除");
            }

            foreach (DeleteRequest request in requests)
            {
                if (request == null)
                {
                    continue;
                }

                DeleteOutcome outcome = DeleteOne(request, options);

                outcomes.Add(outcome);
                WriteLog(logEntries, outcome, options);
            }

            return BuildResult(options, outcomes, logEntries, null);
        }

        /// <summary>只做安全检查 + 度量，不删除。给"删之前先让用户看清单"用。</summary>
        public DeleteSafetyDecision Evaluate(string? targetPath, DeleteOptions? options)
        {
            if (options == null)
            {
                return DeleteSafetyDecision.Block(
                    DeleteBlockReason.InvalidRequest,
                    "没有有效的删除选项（缺少允许根 / 用户确认标志），已拒绝删除");
            }

            try
            {
                return DeleteSafetyGuard.Evaluate(
                    options.AllowedRoot,
                    targetPath,
                    options.UserConfirmed,
                    _probe);
            }
            catch (Exception ex)
            {
                return DeleteSafetyDecision.Block(
                    DeleteBlockReason.DeleteFailed,
                    $"安全检查时出现意外错误：{ex.Message}");
            }
        }

        /// <summary>
        /// 只度量、不删除：条目数与总大小。
        ///
        /// 给 §3.2 的确认框用 —— 那里必须在动手前"列出路径数量与总大小"，
        /// 而真正删除时算出来的数字（<see cref="DeleteOutcome.TotalBytes"/>）事后才拿得到。
        /// 度量不出来时 <see cref="DeleteMeasurement.Determined"/> 为 false（数字宁可没有，也不能是编的）。
        /// </summary>
        public DeleteMeasurement MeasureTarget(string? path)
        {
            try
            {
                return Measure(path ?? string.Empty);
            }
            catch
            {
                return new DeleteMeasurement { Determined = false };
            }
        }

        /// <summary>
        /// 删除单个目标。外面这层 try 是**最后一道保险**：本方法跑在批处理收尾阶段，
        /// 任何意外（注入的探针实现抛异常、文件系统调用抛异常）都只能变成"这一项失败"，
        /// 不能让整批清理跟着崩（AGENTS.md §6 第 9 条）。
        /// </summary>
        private DeleteOutcome DeleteOne(DeleteRequest request, DeleteOptions options)
        {
            try
            {
                return DeleteOneCore(request, options);
            }
            catch (Exception ex)
            {
                return Failure(
                    request.Path,
                    string.IsNullOrWhiteSpace(request.Reason) ? options.Reason : request.Reason,
                    options.Mode,
                    DeleteBlockReason.DeleteFailed,
                    $"删除过程中出现意外错误：{ex.Message}");
            }
        }

        private DeleteOutcome DeleteOneCore(DeleteRequest request, DeleteOptions options)
        {
            string reason = string.IsNullOrWhiteSpace(request.Reason) ? options.Reason : request.Reason;

            // ① 安全前置：一条不过就到此为止（连"文件还在不在"都不去碰，保证零副作用）。
            DeleteSafetyDecision decision = Evaluate(request.Path, options);

            if (!decision.IsAllowed)
            {
                return Failure(request.Path, reason, options.Mode, decision.Reason, decision.Message);
            }

            // ② 度量：日志要求报"条目数 + 总大小"，必须在删除前量（删完再问就永远是 0）。
            DeleteMeasurement measurement = Measure(request.Path);

            if (!measurement.Determined)
            {
                return Failure(
                    request.Path,
                    reason,
                    options.Mode,
                    DeleteBlockReason.SafetyProbeUnavailable,
                    "无法完整度量目标（读不到部分目录内容），删除日志会缺少条目数 / 总大小，已拒绝删除");
            }

            // ③ 动手。
            try
            {
                if (options.Mode == DeleteMode.Permanent)
                {
                    _executor.DeletePermanently(request.Path, measurement.IsDirectory);
                }
                else
                {
                    RecycleAttemptResult attempt = _executor.TryMoveToRecycleBin(
                        request.Path,
                        measurement.IsDirectory,
                        out string recycleMessage);

                    if (attempt != RecycleAttemptResult.Recycled)
                    {
                        DeleteBlockReason blockReason = attempt == RecycleAttemptResult.Unavailable
                            ? DeleteBlockReason.RecycleBinUnavailable
                            : DeleteBlockReason.DeleteFailed;

                        return Failure(request.Path, reason, options.Mode, blockReason, recycleMessage);
                    }
                }
            }
            catch (Exception ex)
            {
                return Failure(
                    request.Path,
                    reason,
                    options.Mode,
                    DeleteBlockReason.DeleteFailed,
                    $"删除失败：{ex.Message}");
            }

            // ④ 后校验：系统说成功但目标还在，不算成功（部分成功不得显示为成功，AGENTS.md §6 第 6 条）。
            if (_probe.TryGetAttributes(SafePathHelper.GetFullPathSafe(request.Path)) != null)
            {
                return Failure(
                    request.Path,
                    reason,
                    options.Mode,
                    DeleteBlockReason.TargetStillExists,
                    "删除后目标仍然存在，按失败处理（不假装删掉了）");
            }

            bool permanent = options.Mode == DeleteMode.Permanent;

            return new DeleteOutcome
            {
                Path = request.Path,
                Reason = reason,
                Success = true,
                Mode = options.Mode,
                BlockReason = DeleteBlockReason.None,
                Message = permanent ? "已彻底删除（不可恢复）" : "已移入回收站（可从回收站还原）",
                EntryCount = measurement.EntryCount,
                TotalBytes = measurement.TotalBytes,

                // 移入回收站 = 把文件搬进回收站，空间并没有真正释放，所以分开记，不混进"释放字节数"。
                FreedBytes = permanent ? measurement.TotalBytes : 0L,
                RecycledBytes = permanent ? 0L : measurement.TotalBytes
            };
        }

        /// <summary>
        /// 度量目标的条目数与总大小。读不到一律 <see cref="DeleteMeasurement.Determined"/> = false ——
        /// 日志里的数字宁可没有，也不能是编的。
        /// </summary>
        private DeleteMeasurement Measure(string path)
        {
            string fullPath = SafePathHelper.GetFullPathSafe(path);
            FileAttributes? attributes = _probe.TryGetAttributes(fullPath);

            if (attributes == null)
            {
                return new DeleteMeasurement { Determined = false };
            }

            if ((attributes.Value & FileAttributes.Directory) == 0)
            {
                long length = 0;

                try
                {
                    length = new FileInfo(fullPath).Length;
                }
                catch
                {
                    return new DeleteMeasurement { Determined = false };
                }

                return new DeleteMeasurement
                {
                    Determined = true,
                    IsDirectory = false,
                    EntryCount = 1,
                    FileCount = 1,
                    TotalBytes = length
                };
            }

            int entryCount = 0;
            int fileCount = 0;
            long totalBytes = 0;
            bool determined = true;

            var pending = new Stack<string>();
            pending.Push(fullPath);

            while (pending.Count > 0)
            {
                string current = pending.Pop();

                if (!_probe.TryListChildren(current, out IReadOnlyList<string> children))
                {
                    determined = false;
                    continue;
                }

                foreach (string child in children)
                {
                    entryCount++;

                    FileAttributes? childAttributes = _probe.TryGetAttributes(child);

                    if (childAttributes == null)
                    {
                        determined = false;
                        continue;
                    }

                    if ((childAttributes.Value & FileAttributes.Directory) != 0)
                    {
                        pending.Push(child);
                        continue;
                    }

                    fileCount++;

                    try
                    {
                        totalBytes += new FileInfo(child).Length;
                    }
                    catch
                    {
                        determined = false;
                    }
                }
            }

            return new DeleteMeasurement
            {
                Determined = determined,
                IsDirectory = true,
                EntryCount = entryCount,
                FileCount = fileCount,
                TotalBytes = totalBytes
            };
        }

        private static DeleteOutcome Failure(
            string path,
            string reason,
            DeleteMode mode,
            DeleteBlockReason blockReason,
            string message)
        {
            return new DeleteOutcome
            {
                Path = path,
                Reason = reason,
                Success = false,
                Mode = mode,
                BlockReason = blockReason,
                Message = message
            };
        }

        private void WriteLog(List<DeleteLogEntry> logEntries, DeleteOutcome outcome, DeleteOptions? options)
        {
            var entry = new DeleteLogEntry
            {
                Path = outcome.Path,
                Reason = outcome.Reason,
                Mode = outcome.Mode,
                Success = outcome.Success,
                BlockReason = outcome.BlockReason,
                Message = outcome.Message,
                EntryCount = outcome.EntryCount,
                TotalBytes = outcome.TotalBytes
            };

            logEntries.Add(entry);

            try
            {
                _logSink?.Write(entry);
            }
            catch
            {
                // 日志写不进去（界面集合跨线程、磁盘满）不该让删除结果变形：
                // 结果本身已经带回了同一批日志条目，调用方照样能落盘。
            }
        }

        private static DeleteResult BuildResult(
            DeleteOptions? options,
            List<DeleteOutcome> outcomes,
            List<DeleteLogEntry> logEntries,
            string? overrideMessage)
        {
            var failureReasons = outcomes
                .Where(outcome => !outcome.Success)
                .Select(outcome => string.IsNullOrWhiteSpace(outcome.Path)
                    ? outcome.Message
                    : $"{outcome.Path}：{outcome.Message}")
                .ToList();

            int successCount = outcomes.Count(outcome => outcome.Success);
            int failureCount = outcomes.Count - successCount;
            long freedBytes = outcomes.Sum(outcome => outcome.FreedBytes);
            long recycledBytes = outcomes.Sum(outcome => outcome.RecycledBytes);

            return new DeleteResult
            {
                Mode = options?.Mode ?? DeleteMode.RecycleBin,
                AllowedRoot = options?.AllowedRoot ?? string.Empty,
                Outcomes = outcomes,
                LogEntries = logEntries,
                FailureReasons = failureReasons,
                Message = overrideMessage ?? BuildMessage(successCount, failureCount, freedBytes, recycledBytes)
            };
        }

        private static string BuildMessage(int successCount, int failureCount, long freedBytes, long recycledBytes)
        {
            if (successCount == 0 && failureCount == 0)
            {
                return "没有要删除的目标";
            }

            var parts = new List<string>
            {
                successCount > 0
                    ? $"成功 {successCount} 项（彻底删除释放 {freedBytes} 字节，移入回收站 {recycledBytes} 字节；" +
                      "回收站里的文件并未真正释放空间）"
                    : "没有成功删除任何目标"
            };

            if (failureCount > 0)
            {
                parts.Add($"失败 {failureCount} 项（原因见失败清单，被拒绝的目标一个字节都没有动）");
            }

            return string.Join("；", parts);
        }
    }
}
