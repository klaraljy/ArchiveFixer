using ArchiveFixer.Helpers;
using ArchiveFixer.Password;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ArchiveFixer.Services;

/// <summary>
/// 对话框服务的降级出口（从 <see cref="DialogService"/> 按职责拆出的协作类）。
///
/// <para><b>它服务的是"测试替身"这一档</b>：无界面宿主（单元测试、控制台宿主、被别的程序调起的管线）里，
/// 每一次本该弹窗的调用都在这里落一条记录并返回默认值 —— 用例靠
/// <see cref="DialogService.FallbackLog"/> 断言"打算弹什么、弹了几次、弹的是哪一档"，
/// 而不是真去点一个窗口（模态窗口还会把用例卡死）。</para>
///
/// <para><b>硬约束</b>：记录即返回，绝不抛异常、绝不死等。记录前过
/// <see cref="PasswordMasker.Sanitize"/> —— 提示文案里可能夹带"密码：xxx"，
/// 日志里不得出现明文（AGENTS.md §6 第 5 条）。写文件本身吞掉所有异常：
/// 降级路径不允许再制造新的失败。</para>
/// </summary>
internal static class DialogFallbackLog
{
    /// <summary>内存记录的上限：排障只需要最近这一段，超过就从最旧的丢。</summary>
    private const int MaxEntries = 200;

    private static readonly object Lock = new();

    /// <summary>本进程内发生过的降级记录（按发生顺序，最早的在前）。</summary>
    private static readonly List<string> Entries = new();

    /// <summary>
    /// 降级日志的数据根（2026-11-01：以前写死 <c>&lt;程序目录&gt;\data\logs</c>，
    /// 与固定数据根 <c>%LOCALAPPDATA%\ArchiveFixer</c> 分成了两处；现在跟 PathService 走）。
    /// 静态是因为写降级日志的是静态出口；PathService 默认根会读测试覆盖变量。
    /// </summary>
    private static readonly PathService FallbackPathService = new();

    /// <summary>无 UI 宿主时是否把降级记录写进 <c>data\logs\dialog-fallback.log</c>。</summary>
    internal static bool FileLoggingEnabled { get; set; } = true;

    /// <summary>本进程内发生过的降级记录（按发生顺序，最近的在后，最多 <see cref="MaxEntries"/> 条）。</summary>
    internal static IReadOnlyList<string> Snapshot
    {
        get
        {
            lock (Lock)
            {
                return Entries.ToArray();
            }
        }
    }

    /// <summary>清空降级记录（测试用）。</summary>
    internal static void Clear()
    {
        lock (Lock)
        {
            Entries.Clear();
        }
    }

    /// <summary>
    /// 记一条降级记录：<c>[时间] [context] 原因｜提示正文</c>。
    ///
    /// <paramref name="context"/> 是用例定位"哪一档对话框"的唯一键（如 <c>ShowReminderConfirm</c>），
    /// 它被原样写进方括号里 —— 测试按 <c>[context]</c> 计数与取最近一条，改名字等于改用例判据。
    /// </summary>
    internal static void Record(string context, string message, string reason)
    {
        string line =
            $"[{Clock.Now:yyyy-MM-dd HH:mm:ss}] [{context}] {reason}｜" +
            PasswordMasker.Sanitize(message);

        lock (Lock)
        {
            Entries.Add(line);

            if (Entries.Count > MaxEntries)
            {
                Entries.RemoveAt(0);
            }
        }

        if (!FileLoggingEnabled)
        {
            return;
        }

        try
        {
            string directory = Path.Combine(FallbackPathService.DataRootDirectory, "logs");

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(
                Path.Combine(directory, "dialog-fallback.log"),
                line + Environment.NewLine,
                Encoding.UTF8);
        }
        catch
        {
            // 降级日志写不进去也不允许影响调用方。
        }
    }
}
