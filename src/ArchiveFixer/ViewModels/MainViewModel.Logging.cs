using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;

namespace ArchiveFixer.ViewModels;

public partial class MainViewModel
{
    public void AppendLog(string message)
    {
        AppendLog("INFO", message);
    }

    /// <summary>
    /// 在操作日志里打一条"======== 本次操作开始：<paramref name="operationName"/> ========"。
    ///
    /// <para>「导出日志（本次操作）」就是从**最后一条**这样的标记开始导的（第 38 条那套口径）。
    /// ⛔ 每个"开始干活"的入口都要打一条，否则用户刚干完就导出，导出来的头一行会是**上一次**操作的名字
    /// （打包与扫描都漏过这一条，见第 46 条 ⑬）。</para>
    /// </summary>
    public void MarkOperationStartForLog(string operationName) => _logService.MarkOperationStart(operationName);

    /// <summary>
    /// **给日志换一次来源上下文**（批次 / 轮次 / 样本 / 阶段 / 任务 / 层号）—— 大测试"只看日志排查"靠它
    /// （人读行不变，机器读的 `.jsonl` 那一份每一行都带上这几格）。
    /// ⛔ 只影响日志记录：⛔ 不参与任何判据、⛔ 不改任何行为。
    /// </summary>
    public void UpdateLogRunContext(
        string? batch = null,
        int? round = null,
        string? sample = null,
        string? stage = null,
        string? task = null,
        int? layer = null)
        => _logService.UpdateRunContext(batch, round, sample, stage, task, layer);

    /// <summary>
    /// **写一处判据**（人读一行 + 机器读一条，字段固定）—— 大测试"只看日志排查"靠它。
    /// ⛔ 只是记录：⛔ 不改任何判据、⛔ 不动盘上任何东西（唯一收口 `LogService.Criterion`）。
    /// </summary>
    public void CriterionLog(
        string criterion,
        string inputs,
        string verdict,
        string action = "",
        string file = "",
        string identity = "",
        string level = "INFO",
        string errorCode = "",
        string component = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string caller = "")
        => _logService.Criterion(
            criterion, inputs, verdict, action, file, identity, level, errorCode, component,
            sourceFile: sourceFile, sourceLine: sourceLine, caller: caller);

    public void AppendLog(
        string level,
        string message,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string caller = "")
    {
        if (string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            AppendDiagnosticLog(
                "ERROR",
                message,
                DiagnosticCodes.AppUnknown,
                "Application",
                sourceFile: sourceFile,
                sourceLine: sourceLine,
                caller: caller);
            return;
        }

        WriteLogCore(level, message, fileOnly: false);
    }

    public void AppendDiagnosticLog(
        string level,
        string message,
        string errorCode,
        string component,
        string taskPath = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string caller = "")
    {
        _logService.WriteWithCode(
            level,
            message,
            taskPath,
            errorCode,
            component,
            sourceFile: sourceFile,
            sourceLine: sourceLine,
            caller: caller);

        string display = string.IsNullOrWhiteSpace(errorCode)
            ? message
            : message + "（错误码 " + errorCode + "）";

        // 屏幕日志与文件日志共用同一个“入队”出口；⛔ 不再从后台线程直接 Logs.Add。
        EnqueueScreenLog(level, display);
    }

    /// <summary>
    /// 只写内部详细日志（`.log` / `.jsonl`），不进屏幕、不进普通档 `.user.log`。
    /// 用于用户选择普通档时仍然保留排查细节；导出普通档不会看到这些行。
    /// </summary>
    internal void AppendLogFileOnly(string level, string message)
    {
        WriteLogCore(level, message, fileOnly: true);
    }

    private void WriteLogCore(string level, string message, bool fileOnly)
    {
        string safeMessage = message ?? string.Empty;

        try
        {
            if (fileOnly)
            {
                _logService.WriteFileOnly(level, safeMessage);
                return;
            }

            switch (level)
            {
                case "ERROR":
                    _logService.WriteError(safeMessage);
                    break;

                case "WARN":
                case "WARNING":
                    _logService.WriteWarning(safeMessage);
                    break;

                default:
                    _logService.WriteInfo(safeMessage);
                    break;
            }
        }
        catch
        {
            // 文件日志失败不影响屏幕日志。
        }

        EnqueueScreenLog(level, safeMessage);
    }

    /// <summary>
    /// 屏幕日志的唯一入队出口：UI 线程直接加，后台线程走 Dispatcher.BeginInvoke。
    /// 文件日志已经由 LogService 落好；这里只维护界面集合，顺序保持 FIFO。
    /// </summary>
    private void EnqueueScreenLog(string level, string message)
    {
        void Add()
        {
            Logs.Add(new OperationLogItem
            {
                Time = ArchiveFixer.Helpers.Clock.Now,
                Level = level,
                Message = message
            });

            while (Logs.Count > 1000)
            {
                Logs.RemoveAt(0);
            }
        }

        Application? application = Application.Current;

        if (application == null || application.Dispatcher == null)
        {
            return;
        }

        if (application.Dispatcher.CheckAccess())
        {
            Add();
            return;
        }

        application.Dispatcher.BeginInvoke(new Action(Add));
    }
}
