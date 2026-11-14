using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.Views;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ArchiveFixer.ViewModels;

public partial class MainViewModel
{
    private LogCoordinator? _logCoordinator;

    private LogCoordinator Log => _logCoordinator ??= new LogCoordinator(_logService, Logs);

    public void AppendLog(string message) => Log.AppendLog(message);

    public void MarkOperationStartForLog(string operationName) => Log.MarkOperationStartForLog(operationName);

    public void UpdateLogRunContext(
        string? batch = null,
        int? round = null,
        string? sample = null,
        string? stage = null,
        string? task = null,
        int? layer = null)
        => Log.UpdateLogRunContext(batch, round, sample, stage, task, layer);

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
        => Log.CriterionLog(
            criterion, inputs, verdict, action, file, identity, level, errorCode, component,
            sourceFile: sourceFile, sourceLine: sourceLine, caller: caller);

    public void AppendLog(
        string level,
        string message,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string caller = "")
        => Log.AppendLog(level, message, sourceFile, sourceLine, caller);

    public void AppendDiagnosticLog(
        string level,
        string message,
        string errorCode,
        string component,
        string taskPath = "",
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string caller = "")
        => Log.AppendDiagnosticLog(
            level, message, errorCode, component, taskPath,
            sourceFile, sourceLine, caller);

    internal void AppendLogFileOnly(string level, string message) => Log.AppendLogFileOnly(level, message);

    /// <summary>
    /// 导出日志文件的**头部**（用户 2026-09-25 第 44 条追加拍板）：
    /// 时间范围 / 任务数 / 成功失败 / 引擎版本 / 输出根 + "细节在哪看"。
    ///
    /// <para>为什么值得写：这份 txt 的用途就是"发给别人（或未来的自己）排查" ——
    /// 没有头，读的人得先自己找"这是哪一批、跑到几点、成不成、用的哪个引擎"。
    /// ⛔ 这里只写**机器事实**（任务表的终态枚举与引擎标识），不写任何密码相关内容。</para>
    ///
    /// <para>internal 是为了让测试能直接钉住"头部写了哪几件事、顺序对不对"
    /// （产品入口只有「导出日志（本次操作）」这一个，见 <see cref="ExportLog"/>）。</para>
    /// </summary>
    private LogExportBuilder? _logExportBuilder;

    private LogExportBuilder LogExport => _logExportBuilder ??= new LogExportBuilder(
        () => Tasks,
        () => Logs,
        () => OutputLocationForLog,
        _extractionCoordinator,
        _taskSummaryService,
        Settings);

    internal IReadOnlyList<string> BuildLogExportHeader() => LogExport.BuildHeader();

    internal IReadOnlyList<string> BuildLogExportSections() => LogExport.BuildSections();


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
    private string? ResolveLogExportInitialDirectory()
    {
        string remembered = Settings.LastLogExportDirectory ?? string.Empty;

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
    private void RememberLogExportDirectory(string? exportedFilePath)
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

            if (string.Equals(Settings.LastLogExportDirectory, full, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Settings.LastLogExportDirectory = full;
            WriteSettingsToDisk("记住日志导出目录");
        }
        catch
        {
            // 记不住只是下次少一点方便，⛔ 不许因为它把导出本身搞失败。
        }
    }

    private void ExportDiagnosticBundle()
    {
        string path = _dialogService.ShowSaveFileDialog(
            "导出诊断包",
            "诊断包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            $"ArchiveFixer-诊断包_{ArchiveFixer.Helpers.Clock.Now:yyyyMMdd_HHmmss}.zip",
            ResolveLogExportInitialDirectory());

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            string exported = DiagnosticBundleService.Export(
                _logService,
                path,
                Tasks.ToList(),
                Settings);

            AppendLog("INFO", $"诊断包已导出：{exported}");
            _dialogService.ShowInfo(
                "诊断包已导出，可直接发给开发者：" + Environment.NewLine + exported);
        }
        catch (Exception ex)
        {
            _logService.Exception("导出诊断包失败", ex, errorCode: DiagnosticCodes.LogWrite);
            _dialogService.ShowError("导出诊断包失败：" + ex.Message);
        }
    }

    private void ExportLog()
    {
        /*
         * 用户 2026-09-25 第 38 条："我想要的是本次操作的日志，也就是我最近一次点开操作的日志，
         * 我看着全部的日志非常的累" —— 所以这里默认只导本次操作（从"本次操作开始"那行分隔线起），
         * 不再把历史日志全拼进来（历史日志另有下面那个显式入口）。
         */
        string path = _dialogService.ShowSaveFileDialog(
            "导出日志（本次操作）",
            "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            $"ArchiveFixer-本次操作_{ArchiveFixer.Helpers.Clock.Now:yyyyMMdd_HHmmss}.txt",
            ResolveLogExportInitialDirectory());

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            (int lineCount, bool fromMarker) = _logService.ExportOperationLog(
                path,
                BuildLogExportHeader(),
                includeDetails: Settings.VerboseLog,
                extraSections: BuildLogExportSections());

            AppendLog("INFO", $"日志已导出（本次操作）：{path}（{lineCount} 行）");

            RememberLogExportDirectory(path);

            _dialogService.ShowInfo(
                fromMarker
                    ? $"本次操作的日志已导出：{lineCount} 行。{Environment.NewLine}{path}"
                    : $"这次还没有「操作开始」的标记，已导出本次运行的完整日志：{lineCount} 行。{Environment.NewLine}{path}");
        }
        catch (Exception ex)
        {
            AppendLog("ERROR", "导出日志失败：" + ex.Message);
            _dialogService.ShowError(
                $"导出日志失败：{ex.Message}{Environment.NewLine}"
                + "换一个位置（例如桌面或 D 盘）再试；目标文件若正被别的程序打开，先把它关掉。");
        }
    }

    /// <summary>
    /// 导出**全部历史日志**（日志目录里每一份都拼进来；用户 2026-09-25 第 37 条要过一版，
    /// 第 38 条明确了"平时只要本次操作" —— 所以它降级成一个**显式**入口，不再占主按钮）。
    /// </summary>
    private void ExportAllLogs()
    {
        string path = _dialogService.ShowSaveFileDialog(
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
                includeDetails: Settings.VerboseLog);

            AppendLog("INFO", $"全部历史日志已导出：{path}（{fileCount} 份 / {lineCount} 行）");

            _dialogService.ShowInfo(
                fileCount > 0
                    ? $"全部历史日志已导出：{fileCount} 份 / {lineCount} 行。{Environment.NewLine}{path}"
                    : "日志目录里还没有任何日志文件。");
        }
        catch (Exception ex)
        {
            AppendLog("ERROR", "导出全部历史日志失败：" + ex.Message);
            _dialogService.ShowError($"导出全部历史日志失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 导出失败清单。
    /// 不弹两个窗口：没选路径就什么都不做（用户取消不该被当成错误）。
    /// </summary>
    private void ExportFailedList()
    {
        try
        {
            string path = _dialogService.ShowSaveFileDialog(
                "导出失败清单",
                "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                "ArchiveFixer-失败清单.txt");

            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string text = _taskSummaryService.BuildFailedListText(Tasks);

            // 清单里不能有明文密码：服务层已经做过脱敏，这里再兜一道，避免以后有人改坏。
            File.WriteAllText(path, PasswordMasker.Sanitize(text), new UTF8Encoding(false));

            AppendLog("INFO", $"失败清单已导出：{path}");
            _dialogService.ShowInfo("失败清单已导出："

                + Environment.NewLine + path);
        }
        catch (Exception ex)
        {
            AppendLog("ERROR", "导出失败清单失败：" + ex.Message);
            _dialogService.ShowException(ex, "导出失败清单失败（换一个位置再试，或先关掉正在打开这个文件的程序）");
        }
    }

    private void CopyFailedList()
    {
        bool ok = CopySanitizedToClipboard(_taskSummaryService.BuildFailedListText(Tasks));
        _dialogService.ShowInfo(ok ? "失败列表已复制。" : "复制失败。");
    }

    /// <summary>
    /// 剪贴板**唯一出口**：任何文本上剪贴板之前都必须先过脱敏
    /// （AGENTS.md §6 第 5 条：密码默认只存内存，日志、报告、剪贴板、详情窗口一律脱敏；
    /// 密码列表那条加密落盘的路也绝不放明文进来）。
    /// 四个入口（失败列表 / 任务信息 / 任务路径 / 错误信息）全部走这里，
    /// 口径与「导出日志」「导出失败清单」一致。
    /// </summary>
    private bool CopySanitizedToClipboard(string? text)
    {
        return WriteClipboardText(PasswordMasker.Sanitize(text));
    }

    /// <summary>
    /// 把**已经脱敏**的文本交给剪贴板服务。
    ///
    /// 做成 virtual 只是为了能在测试里截住实际写出的文本：无头进程里 <c>Clipboard.SetText</c>
    /// 必然不可用（没有 STA / OLE 消息泵），测试无法从系统剪贴板读回内容，只能从这个出口观察。
    /// ⚠ 不要在别处直接调它 —— 那会绕过 <see cref="CopySanitizedToClipboard"/> 里的脱敏。
    /// </summary>
    internal virtual bool WriteClipboardText(string sanitizedText)
    {
        return _clipboardService.CopyText(sanitizedText);
    }
}
