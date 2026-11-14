using ArchiveFixer.Models;
using System;
using System.IO;
using System.Text;

namespace ArchiveFixer.Services;

/// <summary>
/// **写入核心**：四份日志文件（详细档 `.log` / 普通档 `.user.log` / 机器读 `.jsonl` /
/// 诊断事件 `.trace.jsonl`）的唯一物理追加口。
///
/// <para>为什么单独一个类：一行日志要落到几份文件上，但**哪一份写什么**是格式契约
/// （人读行与机器读行一比一、普通档不含探针、诊断事件只进 trace、详细档只写文件不进屏幕）。
/// 这条契约以前夹在屏幕日志、诊断事件、导出三件事中间；抽出来以后，改写入形态只需要看这一处。</para>
///
/// <para>⛔ 它不造字段、不加屏幕日志、不判任何事：行文本与 JSON 字段由
/// <see cref="LogDiagnosticWriter"/> 备好，这里只按既有形态落盘。
/// ⛔ 锁不在这里另造一把：全仓仍用 <see cref="LogOperationContext.SyncRoot"/> 那**一个**监视器，
/// 由调用方持锁（与拆分前的临界区边界完全一致）。</para>
/// </summary>
internal sealed class LogFileWriter
{
    private readonly LogOperationContext _context;

    internal LogFileWriter(LogOperationContext context)
    {
        _context = context;
    }

    /// <summary>
    /// 日志文件里一行的完整文本：<c>[时间] [级别] 消息</c>。
    ///
    /// <para>形态与屏幕日志（<c>OperationLogItem.DisplayText</c>）**一模一样**，差别只在时间戳走
    /// <see cref="LogService.FormatTimestamp"/>（固定文化）。为什么不直接用 <c>item.DisplayText</c>：
    /// 它内部是 <c>Time.ToString("yyyy-MM-dd HH:mm:ss")</c>，用的是**当前区域的日历** ——
    /// 在泰历这类区域里写进文件的就是 2569 年，而日志文件是要被解析、被比对的那一份。
    /// 屏幕那一行由视图直接绑定，改不到（在 Models 里），所以文件这一份先钉死。</para>
    /// </summary>
    internal static string FormatLogLine(DateTime time, string? level, string? message)
    {
        string normalizedLevel = string.IsNullOrWhiteSpace(level) ? "INFO" : level;

        return $"[{LogService.FormatTimestamp(time)}] [{normalizedLevel}] {message}";
    }

    /// <summary>
    /// 详细日志：**只写内部 `.log` / `.jsonl`，不进屏幕、也不进普通 `.user.log`**。
    /// 供任务级细节在用户选择普通档时仍然留下排查证据。
    /// </summary>
    internal void WriteFileOnly(string level, string message)
    {
        level = string.IsNullOrWhiteSpace(level) ? "INFO" : level.ToUpperInvariant();
        message = LogSanitizer.Sanitize(message);

        var item = new OperationLogItem(level, message);

        lock (_context.SyncRoot)
        {
            AppendEntry(item, criterion: null, evidence: null, includeUserLog: false);
        }
    }

    /// <summary>
    /// 人读行（详细档 + 普通档）与机器读行（`.jsonl`）**同一处写入** —— 一比一就是在这里保证的。
    ///
    /// <para>⛔ 调用方必须已经持有 <see cref="LogOperationContext.SyncRoot"/>；
    /// ⛔ 关掉文件日志或没有当前路径时**静默返回**（与拆分前同一判据）。</para>
    /// </summary>
    internal void AppendEntry(
        OperationLogItem item,
        CriterionFields? criterion,
        LogEventFields? evidence,
        bool includeUserLog = true)
    {
        if (!_context.EnableFileLog)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_context.MainLogPath))
        {
            return;
        }

        try
        {
            File.AppendAllText(
                _context.MainLogPath,
                FormatLogLine(item.Time, item.Level, item.Message) + Environment.NewLine,
                Encoding.UTF8);

            if (includeUserLog && _context.UserLogPath.Length > 0)
            {
                File.AppendAllText(
                    _context.UserLogPath,
                    FormatLogLine(item.Time, item.Level, item.Message) + Environment.NewLine,
                    Encoding.UTF8);
            }

            /*
             * **人读行与机器读行一比一**：同一处写入 ⇒ 行数永远相等（守门用例 `LogSchemaTests` 钉着）。
             * 机器读那一份是 `.jsonl`（一行一个 JSON 对象），字段固定、⛔ 不可省。
             */
            if (_context.JsonlPath.Length > 0)
            {
                File.AppendAllText(
                    _context.JsonlPath,
                    LogJsonRecord.Build(
                        item,
                        _context.RunContext,
                        criterion,
                        evidence,
                        _context.NextSequence(),
                        _context.RunId,
                        Environment.ProcessId,
                        Environment.CurrentManagedThreadId,
                        _context.OperationId)
                    + Environment.NewLine,
                    Encoding.UTF8);
            }
        }
        catch
        {
            _context.EnableFileLog = false;
        }
    }

    /// <summary>
    /// 只追加一条诊断事件到 `.trace.jsonl`（不占人读行）。
    ///
    /// <para>⛔ 调用方必须已经持有 <see cref="LogOperationContext.SyncRoot"/>，并且**已经判过**
    /// "文件日志开着 + trace 路径非空"，也已经包了 try/catch —— 与拆分前
    /// <c>LogService.Event</c> 里那段 <c>lock { if (…) return; try { … } catch { } }</c> 的分工一致：
    /// 失败模型留在知道上下文的调用方，本类只负责落盘。</para>
    /// </summary>
    internal void AppendTrace(OperationLogItem item, LogEventFields evidence)
    {
        File.AppendAllText(
            _context.TracePath,
            LogJsonRecord.Build(
                item,
                _context.RunContext,
                null,
                evidence,
                _context.NextTraceSequence(),
                _context.RunId,
                Environment.ProcessId,
                Environment.CurrentManagedThreadId,
                _context.OperationId)
            + Environment.NewLine,
            Encoding.UTF8);
    }

    /// <summary>
    /// **两份一起追加一行**（人读 + 机器读）—— 只给"不走屏幕日志"的那几处内部写入用
    /// （日志保留策略那一行）。⛔ 别的地方一律走 <see cref="LogDiagnosticWriter.WriteEntry"/>，那才是唯一收口。
    ///
    /// <para>⛔ 与拆分前一致：这里**不加锁**（只在启动期清理里被调用），失败就把文件日志关掉。</para>
    /// </summary>
    internal void AppendBothFiles(DateTime time, string level, string message)
    {
        if (!_context.EnableFileLog || string.IsNullOrWhiteSpace(_context.MainLogPath))
        {
            return;
        }

        try
        {
            string safe = LogSanitizer.Sanitize(message);

            File.AppendAllText(
                _context.MainLogPath,
                FormatLogLine(time, level, safe) + Environment.NewLine,
                Encoding.UTF8);

            if (_context.JsonlPath.Length > 0)
            {
                File.AppendAllText(
                    _context.JsonlPath,
                    LogJsonRecord.Build(new OperationLogItem(level, safe, string.Empty), _context.RunContext, null)
                    + Environment.NewLine,
                    Encoding.UTF8);
            }
        }
        catch
        {
            _context.EnableFileLog = false;
        }
    }
}
