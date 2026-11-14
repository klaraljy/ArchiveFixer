using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 工作区残留扫描/清理；MainViewModel 只保留可绑定属性。
/// </summary>
internal sealed class WorkspaceCoordinator
{
    private readonly Func<IReadOnlyList<string>> _roots;
    private readonly Action<long, bool, string> _updateLeftover;
    private readonly Action _raiseCanExecute;
    private readonly Action<string, string> _log;
    private readonly DialogService _dialog;

    public WorkspaceCoordinator(
        Func<IReadOnlyList<string>> roots,
        Action<long, bool, string> updateLeftover,
        Action raiseCanExecute,
        Action<string, string> log,
        DialogService dialog)
    {
        _roots = roots;
        _updateLeftover = updateLeftover;
        _raiseCanExecute = raiseCanExecute;
        _log = log;
        _dialog = dialog;
    }

    internal void LogLeftover()
    {
        try
        {
            /*
             * 扫描根 = **当前生效的工作区根 + 老位置 + 账本里用过的根**（见 ResolveWorkspaceScanRoots）。
             *
             * 为什么不是一个根（用户 2026-09-24 拍板"工作区跟输出盘"之后的必然后果）：
             * 根会随输出盘变，而老位置里可能还压着升级前那批的残留 —— 只扫一个就会出现
             * "东西明明在盘上，界面却报没有"。每个残留条目自己带着它在哪个根下，清理时按它回到各自的根校验。
             */
            IReadOnlyList<string> scanRoots = _roots();

            IReadOnlyList<WorkspaceLeftover> leftovers = WorkspaceCleanupService.ScanMany(scanRoots);

            /*
             * 用户 2026-09-24 第 22 条：他实测攒了 10 个目录 / 5.7 GB，问"为什么失败后你会留下这个残留"。
             * 保留本身是**故意的**（失败 / 取消时工作区里是他唯一的一份产物线索，不变量 12/13），
             * 但以前只写一句"发现 N 个" —— 没体积、界面上也没有清理入口，等于没告诉他。
             * 现在：① 日志带上体积**与实际位置**；② 界面上留一行常驻提示 + 一条清理命令（删前二次确认）。
             */
            long totalBytes = WorkspaceCleanupService.TotalBytesOf(leftovers);
            string banner = leftovers.Count > 0
                ? string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.WorkspaceLeftoverBannerFormat,
                    leftovers.Count,
                    WorkspaceCleanupService.FormatSize(totalBytes))
                : string.Empty;

            _updateLeftover(totalBytes, leftovers.Count > 0, banner);

            // 「清理工作区」的可用性跟着真实扫描结果走（没有残留就点不动）。
            _raiseCanExecute();

            if (leftovers.Count == 0)
            {
                return;
            }

            _log(
                "WARN",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.WorkspaceLeftoverLogFormat,
                    leftovers.Count,
                    WorkspaceCleanupService.FormatSize(totalBytes),
                    DescribeLeftoverLocations(leftovers)));

            // 把"这次一共看了哪几个根"也写清楚：用户照着这一行就能自己去盘上核对。
            _log(
                "INFO",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.WorkspaceScannedRootsLogFormat,
                    string.Join("；", scanRoots)));

            _log("INFO", StatusText.WorkspaceLeftoverHintLog);
        }
        catch (Exception ex)
        {
            _log("WARN", "检查工作区失败：" + ex.Message);
        }
    }

    private static string DescribeLeftoverLocations(IReadOnlyList<WorkspaceLeftover> leftovers)
    {
        var locations = new List<string>();

        foreach (WorkspaceLeftover leftover in leftovers)
        {
            bool seen = locations.Any(existing => string.Equals(
                existing,
                leftover.RootDirectory,
                StringComparison.OrdinalIgnoreCase));

            if (!seen && !string.IsNullOrWhiteSpace(leftover.RootDirectory))
            {
                locations.Add(leftover.RootDirectory);
            }
        }

        return locations.Count == 0 ? "（未知）" : string.Join("；", locations);
    }

    internal void Clear()
    {
        IReadOnlyList<WorkspaceLeftover> leftovers =
            WorkspaceCleanupService.ScanMany(_roots());

        if (leftovers.Count == 0)
        {
            _updateLeftover(0, false, string.Empty);

            _dialog.ShowInfo(StatusText.WorkspaceCleanupNothingText);

            return;
        }

        long totalBytes = WorkspaceCleanupService.TotalBytesOf(leftovers);

        string detail = StatusText.WorkspaceCleanupConfirmDetailHeader + Environment.NewLine
                        + string.Join(
                            Environment.NewLine,
                            leftovers.Select(item =>
                                "  " + item.Name
                                + "（" + WorkspaceCleanupService.FormatSize(item.TotalBytes)
                                + "，" + item.FileCount + " 个文件）"
                                + "　" + item.RootDirectory))
                        + Environment.NewLine + Environment.NewLine
                        + StatusText.WorkspaceCleanupConfirmDetailFooter;

        bool confirmed = _dialog.ShowConfirm(
            StatusText.WorkspaceCleanupConfirmTitle
            + "："
            + string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.WorkspaceCleanupConfirmFormat,
                leftovers.Count,
                WorkspaceCleanupService.FormatSize(totalBytes)),
            optionText: string.Empty,
            optionCheckedByDefault: false,
            detail: detail,
            out _);

        if (!confirmed)
        {
            _log("INFO", "工作区清理已取消：一个目录都没删。");

            return;
        }

        /*
         * 清理**按根分组**做：每个根各自做一次容器内校验（不变量 13）。
         *
         * 为什么不能把一堆路径丢给某一个根去做：默认跟输出盘之后残留可能散在多个根下
         * （当前生效的根、老位置、账本里用过的根），拿 A 根去校验 B 根下面的目录
         * 会被正确判成"越界"从而一个都删不掉 —— 那不是保守，是坏了。
         */
        var outcomes = new List<WorkspaceCleanupOutcome>();

        foreach (IGrouping<string, WorkspaceLeftover> group in leftovers.GroupBy(
                     item => item.RootDirectory,
                     StringComparer.OrdinalIgnoreCase))
        {
            outcomes.AddRange(WorkspaceCleanupService.Cleanup(
                group.Key,
                group.Select(item => item.DirectoryPath)));
        }

        int deleted = outcomes.Count(item => item.Deleted);
        int failed = outcomes.Count - deleted;

        foreach (WorkspaceCleanupOutcome outcome in outcomes)
        {
            if (!outcome.Deleted)
            {
                _log("WARN", "工作区：" + outcome.Message);
            }
        }

        _log(
            "INFO",
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.WorkspaceCleanupResultLogFormat,
                deleted,
                failed,
                WorkspaceCleanupService.FormatSize(totalBytes)));

        // 删完重新扫一遍（失败的那几个会留在提示里，不假装清空了）。
        LogLeftover();

        if (failed == 0)
        {
            _dialog.ShowInfo(
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.WorkspaceCleanupResultLogFormat,
                    deleted,
                    failed,
                    WorkspaceCleanupService.FormatSize(totalBytes)));
        }
    }
}


