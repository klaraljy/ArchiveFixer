using ArchiveFixer.Models;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Services;

/// <summary>
/// 本批「密码类失败」登记与「手动密码」状态的唯一出口（从 ExtractionCoordinator 的 PasswordCandidates /
/// PasswordPromptStep 两个 partial 搬出）。
///
/// <para>⛔ 判据/文案不在这里：这里只存事实（哪几个任务、什么状态、有没有手动密码），
/// 批次结束的合并提示仍由 PasswordPromptStep 读这里再组织。</para>
/// </summary>
internal sealed class PasswordFailureRegistry
{
    private readonly List<(string FileName, string Status)> _failures = new();
    private readonly object _lock = new();

    /// <summary>本批用户手动输入的密码（只在内存，⛔ 不落盘）。</summary>
    internal List<string> ManualBatchPasswords { get; } = new();

    /// <summary>本批是否已经问过手动密码（一次性：问过就不再问，无论填没填）。</summary>
    internal bool ManualPasswordPrompted { get; set; }

    /// <summary>本批是否出现过密码类失败（批次结束提示据此换一句话）。</summary>
    internal bool BatchHadFailures { get; set; }

    internal void Record(ArchiveTask task)
    {
        if (task == null)
        {
            return;
        }

        /*
         * 三档（2026-10-01）：`PasswordOrCorrupted`（RAR 那句"文件已损坏或密码错误"）也算**密码类失败**。
         *
         * 为什么必须算：那一档恰恰是"最可能缺密码"的一档 —— 用户真机那批 RAR `-p` 包走的全是它，
         * 而原来它既不被登记、批末那条提示（"下一步去密码页导入 / 在确认框里填"）也就**一次都不出现**，
         * 用户只看到一行红字，不知道下一步该干什么。
         *
         * ⛔ 但**不许**因此把它说成确认的密码问题：引擎在那条路上本来就没法区分"密码错"与"数据坏"
         *（§11.4 的 RAR 两义句），批末文案里必须带着"可能"。
         */
        if (task.Status != StatusText.WrongPassword &&
            task.Status != StatusText.PasswordAttemptLimitReached &&
            task.Status != StatusText.PasswordOrCorrupted)
        {
            return;
        }

        // 本批出现过"密码类失败"这个事实：批次结束后据此在合并提示里带上"手动输入密码"的出口。
        BatchHadFailures = true;

        string fileName = string.IsNullOrWhiteSpace(task.FileName)
            ? Path.GetFileName(task.CurrentPath)
            : task.FileName;

        lock (_lock)
        {
            _failures.Add((fileName, task.Status));
        }
    }

    internal void ClearFailures()
    {
        lock (_lock)
        {
            _failures.Clear();
        }
    }

    /// <summary>取走本批登记并清空（合并提示的唯一读取口）。</summary>
    internal List<(string FileName, string Status)> TakeFailures()
    {
        lock (_lock)
        {
            if (_failures.Count == 0)
            {
                return new List<(string FileName, string Status)>();
            }

            List<(string FileName, string Status)> copy = _failures.ToList();
            _failures.Clear();
            return copy;
        }
    }

    /// <summary>批开始：清手动密码与失败事实（只对本次运行有效）。</summary>
    internal void ResetManualBatchPassword()
    {
        ManualBatchPasswords.Clear();
        ManualPasswordPrompted = false;
        BatchHadFailures = false;
    }
}

