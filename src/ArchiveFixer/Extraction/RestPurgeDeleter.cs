using ArchiveFixer.Helpers;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 其余物删除的**删除执行**阶段：只负责"动手"，不判断该不该删（那是
/// <see cref="RestPurgeTargetGate"/> 与 <see cref="RestItemPurger"/> 公开入口的事），
/// 也不组装给用户看的话（那是 <see cref="RestPurgeReporter"/> 的事）。
///
/// <para><b>两个动作</b>：① 空壳目录**就地永久删**（不进回收站，判据见
/// <see cref="IsFileFreeDirectoryTree"/>）；② 把一组目标按「彻底删除」档交给
/// <see cref="RecycleBinService"/>（项目里唯一真正执行删除的地方，安全前置全在它那边）。</para>
///
/// <para><b>失败模型</b>：删东西绝不许把已经成立的结论拖成异常 —— 这里的每个动作都把异常
/// 收敛成"没删成 + 一句原因"（同时写诊断日志），调用方据此组装结论。
/// 单个空壳目录或单个目标失败不影响其余目标（与 <see cref="RecycleBinService"/> 同一口径）。</para>
///
/// <para><b>线程</b>：全是磁盘活，调用方必须放后台线程。</para>
/// </summary>
internal sealed class RestPurgeDeleter
{
    private readonly IDeleteExecutor _executor;
    private readonly IDeleteFileSystemProbe _probe;

    internal RestPurgeDeleter(IDeleteExecutor executor, IDeleteFileSystemProbe probe)
    {
        _executor = executor;
        _probe = probe;
    }

    /// <summary>
    /// **空壳判据（唯一出口）**：这个目录**递归地一个文件都没有**（只剩空目录结构，含嵌套空目录）。
    ///
    /// <para><b>为什么要它</b>（用户 2026-10-03 真机）：删除操作跑完一批之后，回收站里堆了
    /// 非常多 1 KB 的同名「其余物」文件夹 —— 整份删除把整个 `其余物\` 目录当一个条目交出去、
    /// 每个任务（递归时每层）各送一次，而里面往往只剩空壳。空目录没有"可还原"的价值
    /// ⇒ 这一档改为**就地永久删掉、不进回收站**。</para>
    ///
    /// <para>两个执行体（<see cref="RestItemPurger.Purge"/> 与 <see cref="RestItemPurger.PurgeExcept"/>）
    /// 都转调这一份，⛔ 不许各写一遍（AGENTS.md §9.5：同一件事的真值只允许有一个出口）。</para>
    ///
    /// <para><b>判据只读文件系统事实</b>：数"这一棵下面有没有文件"
    /// （⛔ 不比中文名、⛔ 不看时间戳、⛔ 不看大小）。见到第一个文件就返回 false，不必数到底。
    /// 目录不在 / 读不动（枚举中途抛异常）⇒ 一律 false = **判不出就当它不空** ——
    /// 于是 <c>Directory.Delete(recursive: true)</c> 只可能作用在**已被本判据证明递归无文件**的那个目录上。</para>
    ///
    /// <para>⚠ 目录联接点（junction）会被枚举跟着进去（实测 .NET 如此）：所以"链到别处一棵有文件的树"
    /// 会被正确判成不空；而链到一棵空树时本判据会说"是空壳"，随后的
    /// <c>Directory.Delete(recursive: true)</c> 会因为重解析点报"拒绝访问"抛出来
    /// —— 由 <see cref="TryDeleteEmptyShellDirectory"/> 的 catch 兜住
    /// （实测：.NET 的递归删除**不跟着重解析点删**，链接目标不会被删）。</para>
    /// </summary>
    internal static bool IsFileFreeDirectoryTree(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(
                         directory,
                         "*",
                         SearchOption.AllDirectories))
            {
                // 只认文件：`File.Exists` 对目录恒为 false（枚举里的目录一律放过）。
                if (File.Exists(entry))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 就地删掉一个**已被 <see cref="IsFileFreeDirectoryTree"/> 证明递归无文件**的目录
    /// （<c>Directory.Delete(recursive: true)</c>，不进回收站）。
    ///
    /// <para>⛔ 调用方必须先过判据：这里不再复核，越界 / 有文件 / 被判据拒绝的目录不许走到这里。</para>
    /// </summary>
    /// <returns>null = 删成功；否则是异常消息（同时已写诊断日志）。</returns>
    internal static string? TryDeleteEmptyShellDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
            return null;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Problem("其余物", "删除或处理", ex);
            return ex.Message;
        }
    }

    /// <summary>
    /// 按「彻底删除」档把一组目标交出去（唯一真正动手的地方）。
    ///
    /// <para>⛔ 档位在这里写死 <see cref="DeleteMode.Permanent"/>：解压侧的"回收站"档已整条下线，
    /// 其余物只剩"不动 / 彻底删除"两档，"不动"根本到不了这里。</para>
    /// </summary>
    /// <param name="requests">要删的目标（已由调用方按档位选好）。</param>
    /// <param name="allowedRoot">允许根（<see cref="RecycleBinService"/> 的安全前置用它判越界）。</param>
    /// <param name="reason">这一档的删除理由（写进删除日志）。</param>
    /// <param name="error">异常消息（返回 null 时才有值）。</param>
    /// <returns>删除结果；出现意外异常时返回 null（已写诊断日志）。</returns>
    internal DeleteResult? TryDeletePermanently(
        IReadOnlyList<DeleteRequest> requests,
        string allowedRoot,
        string reason,
        out string? error)
    {
        error = null;

        // logSink 传 null：删除日志由调用方（协调器）原样写进界面与文件日志，
        // 不在这里另开一个落点（两处各写一份会让日志出现两个时间戳）。
        var service = new RecycleBinService(_executor, null, _probe);

        try
        {
            return service.Delete(
                requests,
                new DeleteOptions
                {
                    AllowedRoot = allowedRoot,
                    UserConfirmed = true,
                    Mode = DeleteMode.Permanent,
                    Reason = reason
                });
        }
        catch (Exception ex)
        {
            AppDiagnostics.Problem("其余物", "删除或处理", ex);
            error = ex.Message;
            return null;
        }
    }
}
