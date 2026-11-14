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

namespace ArchiveFixer.Extraction;

public sealed partial class RecursiveExtractor
{        /// <summary>
         /// 由调用方在**接管了本次运行的结论**之后调用：把最近一次运行留下的工作区按"任务成功"清掉。
         ///
         /// 目前只有一个地方用得上（<c>ExtractionCoordinator</c> 的"用户选择只保留当前这一层"）：
         /// 递归核心是以 NeedsDecision 收的尾，按规则**没有**清工作区；而调用方随后把第 0 层产物
         /// 取回暂存目录、把结论改写成 Completed —— 那份工作区（含 report.json）从此是纯垃圾，
         /// 留着还会在下次启动时被算进"未完成的工作区"报告。
         ///
         /// ⚠ 调用方必须先确认**产物已经不在工作区里**（拿走之后）再调；否则这就是在丢用户的产物。
         /// 只认本实例持有的那一个目录，不做任何目录扫描。
         /// </summary>
         /// <returns>工作区确实不在了（本次删掉 / 本来就不在）返回 true。</returns>
    public bool TryCleanupCurrentWorkspace(string taskLabel, string reason)
    {
        ExtractionWorkspace? workspace = CurrentWorkspace;

        if (workspace == null)
        {
            return false;
        }

        return CleanupWorkspace(workspace, taskLabel, reason);
    }

    /// <summary>
    /// 失败 / 取消 / 部分完成收尾时，按用户设置（<c>AppSettings.KeepFailedWorkspace</c>，默认关）
    /// 清掉**本次运行自己建的**那个工作区（用户 2026-09-25 第 25 条：失败不留残留）。
    ///
    /// <para>它是逐层产物的落点，双层包里最占地方的一份（"解压 40G，两层，解压失败有 80G"）。
    /// 与 <see cref="TryCleanupCurrentWorkspace"/> 的区别只有"为什么清"与日志措辞：
    /// 那一条是**结论被调用方改写**时才调（"用户选择只保留当前一层"），
    /// 这一条是**整单没成功**时调 —— 调用方要判两件事才轮到它：这一单确实没成功、
    /// 而且用户没有打开「失败时保留中间产物」（判断在
    /// <c>ExtractionCoordinator.CleanupFailedTaskWorkspace</c> 里，一处收口）。</para>
    ///
    /// <para>安全口径与 <see cref="CleanupWorkspace"/> 完全一致：容器内校验先做一遍
    /// （必须在工作区根目录之下 + 目录里只许有我们自己造的子目录，见
    /// <see cref="WorkspaceCleanupGuard"/>）、<see cref="ExtractionWorkspace.Cleanup"/> 内部再独立校验一遍、
    /// 只认本实例持有的那一个目录（绝不按目录名 / "最新目录"去扫 <c>work\recursive</c> ——
    /// 并发跑两个递归任务时，扫描式删除会把对方正在写的工作区端掉）、
    /// 删不掉只写 WARN（绝不改任务结论）。</para>
    ///
    /// <para>工作区本来就不在（成功那一支已经清过 / 这次没建到工作区）时**一个字都不写**：
    /// 那种情况下没有任何"删了什么"需要交代，多写一行只会让日志变噪声。</para>
    /// </summary>
    /// <returns>工作区确实不在了（本次删掉 / 本来就不在）返回 true。</returns>
    public bool TryDiscardCurrentWorkspaceOnFailure(string taskLabel)
    {
        ExtractionWorkspace? workspace = CurrentWorkspace;

        if (workspace == null)
        {
            return false;
        }

        if (!Directory.Exists(workspace.TaskDirectory))
        {
            return true;
        }

        if (!ArchivePathGuard.IsInsideRoot(workspace.RootDirectory, workspace.TaskDirectory, out string guardReason))
        {
            Log(
                "WARN",
                $"{taskLabel}：工作区不在工作区根目录之下，已跳过清理 —— {guardReason}：{workspace.TaskDirectory}");

            return false;
        }

        /*
         * 第二道容器内校验（与 CleanupWorkspace 同一处判据）：目录里只许有我们自己造的子目录。
         * 见 WorkspaceCleanupGuard 的说明 —— 这道以前只有单层路径有，递归这两次删工作区一直缺它。
         */
        if (!HasOnlyOwnedWorkspaceSubdirectories(workspace.TaskDirectory, taskLabel))
        {
            return false;
        }

        // 删除是**不可逆**的：动手之前先把"删什么、为什么、多大"写进日志（同 CleanupWorkspace）。
        (int fileCount, long totalSize) = OutputVerifier.Measure(workspace.TaskDirectory);

        Log(
            "INFO",
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.WorkspaceClearedOnFailureFormat,
                taskLabel,
                fileCount,
                TaskSpaceEstimate.FormatSize(totalSize),
                workspace.TaskDirectory));

        WorkspaceCleanupResult cleanup = workspace.Cleanup();

        // 删成了是 INFO，没删成（占用 / 权限 / 越界）是 WARN，而 Cleanup 的消息里已经带上了路径。
        if (!cleanup.Cleaned)
        {
            Log("WARN", $"{taskLabel}：{cleanup.Message}");
        }

        return cleanup.Cleaned;
    }

    /// <summary>
    /// 清理**本实例持有的那个**工作区目录。
    ///
    /// 安全性（这是删目录，每一条都写死在这里）：
    /// · 删的是本实例手上的那个工作区对象（本次运行开头建出来的那一个），**不按目录名、
    ///   也不按"最新目录"去扫** <c>data\work\recursive</c> —— 并发跑两个递归任务时，
    ///   扫描式删除会把对方正在写的工作区端掉；
    /// · 容器内校验先在这里做一遍（必须在工作区根目录之下 **+ 目录里只许有我们自己造的子目录**，
    ///   见 <see cref="HasOnlyOwnedWorkspaceSubdirectories"/>），
    ///   <see cref="ExtractionWorkspace.Cleanup"/> 内部还会再独立校验一遍，越界时一个字节都不动；
    /// · 删不掉只写 WARN：清工作区失败绝不该让**已经成功**的任务变成失败
    ///   （与 AGENTS.md §6 第 9 条同一精神）。
    ///
    /// 生产环境里工作区根目录是 <c>&lt;程序目录&gt;\data\work\recursive</c>
    /// （<see cref="ConfiguredWorkspaceRoot"/> 由 MainViewModel 设成 PathService.WorkDirectory），
    /// 所以"在工作区根之下"同时就是"在 data\work 之下"。
    /// </summary>
    private bool CleanupWorkspace(ExtractionWorkspace workspace, string taskLabel, string reason)
    {
        if (!ArchivePathGuard.IsInsideRoot(workspace.RootDirectory, workspace.TaskDirectory, out string guardReason))
        {
            // 越界＝工作区的身份本身可疑：一个字节都不动，但必须留下证据（这是失败，不是静默跳过）。
            Log(
                "WARN",
                $"{taskLabel}：工作区不在工作区根目录之下，已跳过清理 —— {guardReason}：{workspace.TaskDirectory}");

            return false;
        }

        /*
         * 第二道容器内校验（2026-10-05 只读审计）：目录里只许有我们自己造的子目录。
         * 单层路径一直有两道，递归这条路（两次删工作区）以前只有上一道 ——
         * 判据本体是同一个纯函数（<see cref="WorkspaceCleanupGuard"/>），⛔ 不在这里再写一遍。
         */
        if (!HasOnlyOwnedWorkspaceSubdirectories(workspace.TaskDirectory, taskLabel))
        {
            return false;
        }

        // 删除是**不可逆**的：动手之前先把"删什么、为什么、多大"写进日志（AGENTS.md §9.5 同一要求）。
        (int fileCount, long totalSize) = OutputVerifier.Measure(workspace.TaskDirectory);

        Log(
            "INFO",
            $"{taskLabel}：{reason}，清理本次任务的工作区（{fileCount} 个文件 / {totalSize} 字节）：{workspace.TaskDirectory}");

        WorkspaceCleanupResult cleanup = workspace.Cleanup();

        // 删成了是 INFO，没删成（占用 / 权限 / 越界）是 WARN —— 两种都要留证据。
        Log(cleanup.Cleaned ? "INFO" : "WARN", $"{taskLabel}：{cleanup.Message}");

        return cleanup.Cleaned;
    }

    /// <summary>
    /// 第二道容器内校验：这个工作区目录里**只许有我们自己造的子目录**（<c>layer-NNN</c> /
    /// <c>carved</c> / <c>_密码预检</c>）。返回 false = 发现外来的，调用方一律**一个字节都不删**。
    ///
    /// <para>判据本体是 <see cref="WorkspaceCleanupGuard"/> 里那个纯函数（单层路径也用它），
    /// 这里只负责"读目录 + 写 WARN"这两件与日志出口绑定的事。
    /// 白名单刻意**不是**单层路径那一套（<c>stage</c>/<c>volumes</c>）：递归工作区的形状是层的目录，
    /// 套用别人的白名单会把每一次清理都拦下 —— 那等于把清理整块关掉。</para>
    ///
    /// <para>读不动目录（占用 / 权限）时也拦下：这是"要删东西"的动作，判不出来就什么都不做
    /// （与 <see cref="CleanupWorkspace"/> 的兜底同一条口径）。</para>
    /// </summary>
    private bool HasOnlyOwnedWorkspaceSubdirectories(string taskDirectory, string taskLabel)
    {
        string[] subdirectories;

        try
        {
            if (!Directory.Exists(taskDirectory))
            {
                // 本来就不在：没有"删了什么"需要交代，交给 Cleanup() 那一边如实回答。
                return true;
            }

            subdirectories = Directory.GetDirectories(taskDirectory);
        }
        catch (Exception ex)
        {
            Log("WARN", $"{taskLabel}：读不了工作区目录（{ex.Message}），已跳过清理：{taskDirectory}");

            return false;
        }

        string? foreign = WorkspaceCleanupGuard.FindForeignSubdirectory(
            subdirectories,
            WorkspaceCleanupGuard.IsOwnedRecursiveWorkspaceDirectory);

        if (foreign == null)
        {
            return true;
        }

        Log(
            "WARN",
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.WorkspaceForeignSubdirectoryLogFormat,
                taskLabel,
                foreign,
                taskDirectory));

        return false;
    }

    /// <summary>
    /// 清理"上一次运行留下、已被本次续跑取代"的工作区（多分支询问 → 用户确认继续这一条路）。
    ///
    /// 为什么它必须跟着一起清：用户点"继续"之后，续跑会把第 0 层在**新工作区**里重解一遍，
    /// 上一次那份产物因此变成纯垃圾 —— 而它往往就是几百 MB。它当时没被清是对的
    /// （"等用户决定"属于部分完成，规则 7 要求保留）；现在用户已经决定、整条递归也真的走完了，
    /// 那个理由不再成立，两份都留着才是泄漏。
    ///
    /// 只在本次**成功**时清（失败 / 取消 / 部分完成时两份都留着），而且只认"同一个归档的续跑"
    /// （判据在 <see cref="ExtractAsync"/> 里）—— 免得调用方拿同一个实例去跑另一个任务时误删。
    /// </summary>
    private void CleanupSupersededWorkspaces(string taskLabel)
    {
        if (_supersededWorkspaces.Count == 0)
        {
            return;
        }

        // 先清账再动手：万一清理过程里抛了（它自己不抛），也不会在下次成功时重复删同一个目录。
        ExtractionWorkspace[] superseded = _supersededWorkspaces.ToArray();
        _supersededWorkspaces.Clear();

        foreach (ExtractionWorkspace workspace in superseded)
        {
            CleanupWorkspace(workspace, taskLabel, "上一次运行（等待用户决定多分支）的工作区已被本次续跑取代");
        }
    }

}
