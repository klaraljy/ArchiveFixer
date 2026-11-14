using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 日志导出/诊断包/失败清单的命令与落盘；UI 只经对话框服务/回调。
/// </summary>
internal sealed class LogExportCoordinator
{
    private readonly Func<IReadOnlyList<ArchiveTask>> _tasks;
    private readonly Func<IReadOnlyList<OperationLogItem>> _logs;
    private readonly Func<string> _outputLocationForLog;
    private readonly Func<string, bool> _writeClipboard;
    private readonly Action<string> _write_settings;
    private readonly Action<string, string> _log;
    private readonly ExtractionCoordinator _extraction;
    private readonly TaskSummaryService _taskSummary;
    private readonly LogService _logService;
    private readonly DialogService _dialog;
    private readonly ClipboardService _clipboard;
    private readonly AppSettings _settings;

    public LogExportCoordinator(
        Func<IReadOnlyList<ArchiveTask>> tasks,
        Func<IReadOnlyList<OperationLogItem>> logs,
        Func<string> outputLocationForLog,
        Func<string, bool> writeClipboard,
        Action<string> write_settings,
        Action<string, string> log,
        ExtractionCoordinator extraction,
        TaskSummaryService taskSummary,
        LogService logService,
        DialogService dialog,
        ClipboardService clipboard,
        AppSettings settings)
    {
        _tasks = tasks;
        _logs = logs;
        _outputLocationForLog = outputLocationForLog;
        _writeClipboard = writeClipboard;
        _write_settings = write_settings;
        _log = log;
        _extraction = extraction;
        _taskSummary = taskSummary;
        _logService = logService;
        _dialog = dialog;
        _clipboard = clipboard;
        _settings = settings;
    }

    private LogExportBuilder Builder => new(_tasks, _logs, _outputLocationForLog, _extraction, _taskSummary, _settings);

    public IReadOnlyList<string> BuildHeader() => Builder.BuildHeader();

    public IReadOnlyList<string> BuildSections() => Builder.BuildSections();

    /// <summary>
    /// 「导出日志」对话框的**起始目录**（用户 2026-10-01 真机 `giu910` 那一单问的"为什么这里有个 txt"）。
    ///
    /// <para>现场：导出对话框默认开在"上次用过的那个文件夹"，而那一批的源包正好在
    /// <c>…\giu910\giu910\其余物\</c> 里（他把上一轮被收进其余物的包再跑了一次）⇒ 日志就落在了
    /// **其余物**里。那个文件夹是程序自己会**整份删掉**的地方，日志落在那儿哪天就跟着没了。</para>
    ///
    /// <para>两条口径：① 记住**上一次成功导出到哪个目录**，下次就从那儿开（他习惯把日志放哪儿就一直在那儿，
    /// ⛔ 不在代码里写死任何个人路径 —— §8）；② 记着的目录若落在**其余物 / 工作区**里，**一律不采用**
    /// （退回系统默认），⛔ 绝不把用户往那两个会被清掉的地方引。</para>
    /// </summary>
    public string? ResolveInitialDirectory()
    {
        string remembered = _settings.LastLogExportDirectory ?? string.Empty;

        if (string.IsNullOrWhiteSpace(remembered) || !Directory.Exists(remembered))
        {
            return null;
        }

        string full;

        try
        {
            full = Path.GetFullPath(remembered);
        }
        catch
        {
            return null;
        }

        if (IsInsideDeletableProcessFolders(full))
        {
            return null;
        }

        return full;
    }

    /// <summary>
    /// 这个目录是不是落在"程序自己会整份删掉"的地方（**其余物** / **工作区**）——
    /// 日志导出**不记**这种目录、也不用它当起始目录（用户 2026-10-01 真机就是导出落进了其余物）。
    /// 判据只有一处：<see cref="ProcessArtifactLayout.IsInsideDeletableProcessFolders"/>。
    /// </summary>
    private static bool IsInsideDeletableProcessFolders(string fullPath) =>
        ProcessArtifactLayout.IsInsideDeletableProcessFolders(fullPath);

    /// <summary>导出成功后记住这个目录（下次对话框从这儿开）。⛔ 其余物 / 工作区那一档一个字节都不记。</summary>
    private void RememberDirectory(string? exportedFilePath)
    {
        try
        {
            string? directory = Path.GetDirectoryName(exportedFilePath ?? string.Empty);

            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return;
            }

            string full = Path.GetFullPath(directory);

            if (IsInsideDeletableProcessFolders(full))
            {
                return;
            }

            if (string.Equals(_settings.LastLogExportDirectory, full, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _settings.LastLogExportDirectory = full;
            _write_settings("记住日志导出目录");
        }
        catch
        {
            // 记不住只是下次少一点方便，⛔ 不许因为它把导出本身搞失败。
        }
    }

    public void ExportDiagnosticBundle()
    {
        string path = _dialog.ShowSaveFileDialog(
            "导出诊断包",
            "诊断包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            $"ArchiveFixer-诊断包_{ArchiveFixer.Helpers.Clock.Now:yyyyMMdd_HHmmss}.zip",
            ResolveInitialDirectory());

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            string exported = DiagnosticBundleService.Export(
                _logService,
                path,
                _tasks().ToList(),
                _settings);

            _log("INFO", $"诊断包已导出：{exported}");
            _dialog.ShowInfo(
                "诊断包已导出，可直接发给开发者：" + Environment.NewLine + exported);
        }
        catch (Exception ex)
        {
            _logService.Exception("导出诊断包失败", ex, errorCode: DiagnosticCodes.LogWrite);
            _dialog.ShowError("导出诊断包失败：" + ex.Message);
        }
    }

    public void ExportLog()
    {
        /*
         * 用户 2026-09-25 第 38 条："我想要的是本次操作的日志，也就是我最近一次点开操作的日志，
         * 我看着全部的日志非常的累" —— 所以这里默认只导本次操作（从"本次操作开始"那行分隔线起），
         * 不再把历史日志全拼进来（历史日志另有下面那个显式入口）。
         */
        string path = _dialog.ShowSaveFileDialog(
            "导出日志（本次操作）",
            "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            $"ArchiveFixer-本次操作_{ArchiveFixer.Helpers.Clock.Now:yyyyMMdd_HHmmss}.txt",
            ResolveInitialDirectory());

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            (int lineCount, bool fromMarker) = _logService.ExportOperationLog(
                path,
                Builder.BuildHeader(),
                includeDetails: _settings.VerboseLog,
                extraSections: Builder.BuildSections());

            _log("INFO", $"日志已导出（本次操作）：{path}（{lineCount} 行）");

            RememberDirectory(path);

            _dialog.ShowInfo(
                fromMarker
                    ? $"本次操作的日志已导出：{lineCount} 行。{Environment.NewLine}{path}"
                    : $"这次还没有「操作开始」的标记，已导出本次运行的完整日志：{lineCount} 行。{Environment.NewLine}{path}");
        }
        catch (Exception ex)
        {
            _log("ERROR", "导出日志失败：" + ex.Message);
            _dialog.ShowError(
                $"导出日志失败：{ex.Message}{Environment.NewLine}"
                + "换一个位置（例如桌面或 D 盘）再试；目标文件若正被别的程序打开，先把它关掉。");
        }
    }

    /// <summary>
    /// 导出**全部历史日志**（日志目录里每一份都拼进来；用户 2026-09-25 第 37 条要过一版，
    /// 第 38 条明确了"平时只要本次操作" —— 所以它降级成一个**显式**入口，不再占主按钮）。
    /// </summary>
    public void ExportAllLogs()
    {
        string path = _dialog.ShowSaveFileDialog(
            "导出全部历史日志",
            "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            $"ArchiveFixer-全部历史日志_{ArchiveFixer.Helpers.Clock.Now:yyyyMMdd_HHmmss}.txt");

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            (int fileCount, int lineCount) = _logService.ExportAllLogs(
                path,
                includeDetails: _settings.VerboseLog);

            _log("INFO", $"全部历史日志已导出：{path}（{fileCount} 份 / {lineCount} 行）");

            _dialog.ShowInfo(
                fileCount > 0
                    ? $"全部历史日志已导出：{fileCount} 份 / {lineCount} 行。{Environment.NewLine}{path}"
                    : "日志目录里还没有任何日志文件。");
        }
        catch (Exception ex)
        {
            _log("ERROR", "导出全部历史日志失败：" + ex.Message);
            _dialog.ShowError($"导出全部历史日志失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 导出失败清单。
    /// 不弹两个窗口：没选路径就什么都不做（用户取消不该被当成错误）。
    /// </summary>
    public void ExportFailedList()
    {
        try
        {
            string path = _dialog.ShowSaveFileDialog(
                "导出失败清单",
                "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                "ArchiveFixer-失败清单.txt");

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string text = _taskSummary.BuildFailedListText(_tasks());

            // 清单里不能有明文密码：服务层已经做过脱敏，这里再兜一道，避免以后有人改坏。
            File.WriteAllText(path, PasswordMasker.Sanitize(text), new UTF8Encoding(false));

            _log("INFO", $"失败清单已导出：{path}");
            _dialog.ShowInfo("失败清单已导出："

                + Environment.NewLine + path);
        }
        catch (Exception ex)
        {
            _log("ERROR", "导出失败清单失败：" + ex.Message);
            _dialog.ShowException(ex, "导出失败清单失败（换一个位置再试，或先关掉正在打开这个文件的程序）");
        }
    }

    public void CopyFailedList()
    {
        bool ok = CopySanitizedToClipboard(_taskSummary.BuildFailedListText(_tasks()));
        _dialog.ShowInfo(ok ? "失败列表已复制。" : "复制失败。");
    }

    /// <summary>
    /// 剪贴板**唯一出口**：任何文本上剪贴板之前都必须先过脱敏
    /// （AGENTS.md §6 第 5 条：密码默认只存内存，日志、报告、剪贴板、详情窗口一律脱敏；
    /// 密码列表那条加密落盘的路也绝不放明文进来）。
    /// 四个入口（失败列表 / 任务信息 / 任务路径 / 错误信息）全部走这里，
    /// 口径与「导出日志」「导出失败清单」一致。
    /// </summary>
    internal bool CopySanitizedToClipboard(string? text)
    {
        return _writeClipboard(PasswordMasker.Sanitize(text));
    }



}


