using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace ArchiveFixer.ViewModels;

public partial class MainViewModel
{
    private void LogLeftoverWorkspaces()
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
            IReadOnlyList<string> scanRoots = ResolveWorkspaceScanRoots();

            IReadOnlyList<WorkspaceLeftover> leftovers = WorkspaceCleanupService.ScanMany(scanRoots);

            /*
             * 用户 2026-09-24 第 22 条：他实测攒了 10 个目录 / 5.7 GB，问"为什么失败后你会留下这个残留"。
             * 保留本身是**故意的**（失败 / 取消时工作区里是他唯一的一份产物线索，不变量 12/13），
             * 但以前只写一句"发现 N 个" —— 没体积、界面上也没有清理入口，等于没告诉他。
             * 现在：① 日志带上体积**与实际位置**；② 界面上留一行常驻提示 + 一条清理命令（删前二次确认）。
             */
            WorkspaceLeftoverTotalBytes = WorkspaceCleanupService.TotalBytesOf(leftovers);
            HasWorkspaceLeftovers = leftovers.Count > 0;
            WorkspaceLeftoverBanner = leftovers.Count > 0
                ? string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.WorkspaceLeftoverBannerFormat,
                    leftovers.Count,
                    WorkspaceCleanupService.FormatSize(WorkspaceLeftoverTotalBytes))
                : string.Empty;

            // 「清理工作区」的可用性跟着真实扫描结果走（没有残留就点不动）。
            RaiseAllCommandCanExecuteChanged();

            if (leftovers.Count == 0)
            {
                return;
            }

            AppendLog(
                "WARN",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.WorkspaceLeftoverLogFormat,
                    leftovers.Count,
                    WorkspaceCleanupService.FormatSize(WorkspaceLeftoverTotalBytes),
                    DescribeLeftoverLocations(leftovers)));

            // 把"这次一共看了哪几个根"也写清楚：用户照着这一行就能自己去盘上核对。
            AppendLog(
                "INFO",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.WorkspaceScannedRootsLogFormat,
                    string.Join("；", scanRoots)));

            AppendLog("INFO", StatusText.WorkspaceLeftoverHintLog);
        }
        catch (Exception ex)
        {
            AppendLog("WARN", "检查工作区失败：" + ex.Message);
        }
    }

    /// <summary>残留**实际**在哪些根下（去重，按首次出现顺序）—— 日志里那一格就是它。</summary>
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

    /// <summary>
    /// 界面上那一行"工作区残留：N 个目录，共 X"（空 = 没有残留，整行不显示）。
    /// </summary>
    public string WorkspaceLeftoverBanner
    {
        get => _workspaceLeftoverBanner;
        private set => SetProperty(ref _workspaceLeftoverBanner, value);
    }

    /// <summary>有没有工作区残留（界面据此显示/隐藏那一行与「清理工作区」）。</summary>
    public bool HasWorkspaceLeftovers
    {
        get => _hasWorkspaceLeftovers;
        private set => SetProperty(ref _hasWorkspaceLeftovers, value);
    }

    /// <summary>残留一共占多少字节（界面提示用）。</summary>
    public long WorkspaceLeftoverTotalBytes
    {
        get => _workspaceLeftoverTotalBytes;
        private set => SetProperty(ref _workspaceLeftoverTotalBytes, value);
    }

    /// <summary>
    /// 上一批一键处理撞到层数上限、还剩多少内层包没解（用户 2026-09-24 第 16 条追加）。
    ///
    /// <para>值来自 <c>OneClickCoordinator</c> 的结论（<c>OneClickOutcome.PendingContinuationCount</c>）：
    /// 到顶时那些内层包已经加进任务列表并勾好，界面上给一行提示 + 一个「继续解」按钮。
    /// 到顶**必须提示**（他要的就是这个）——静默停下正是他抱怨的那件事。</para>
    /// </summary>
    public int PendingContinuationCount => _pendingContinuationCount;

    /// <summary>要不要显示「继续解」那一行 / 那个按钮。</summary>
    public bool HasPendingContinuation => _pendingContinuationCount > 0;



    /// <summary>「继续解」那一行的文案（按钮与提示共用一份措辞：<c>StatusText.ContinueOneClick*</c>）。</summary>
    public string PendingContinuationText => !HasPendingContinuation
        ? string.Empty
        : string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            StatusText.ContinueOneClickHintFormat,
            _oneClickCoordinator?.RoundLimit ?? 5,
            _pendingContinuationCount);

    /// <summary>「继续解」按钮上的文案。</summary>
    public string ContinueOneClickButtonText => !HasPendingContinuation
        ? string.Empty
        : string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            StatusText.ContinueOneClickTextFormat,
            _pendingContinuationCount);

    /// <summary>一键处理收尾时把"还剩几个内层包"报进来（0 = 没有待续解的）。</summary>
    internal void ReportPendingContinuation(int count)
    {
        int normalized = Math.Max(0, count);

        if (_pendingContinuationCount == normalized)
        {
            return;
        }

        _pendingContinuationCount = normalized;

        OnPropertyChanged(nameof(PendingContinuationCount));
        OnPropertyChanged(nameof(HasPendingContinuation));
        OnPropertyChanged(nameof(PendingContinuationText));
        OnPropertyChanged(nameof(ContinueOneClickButtonText));

        RaiseAllCommandCanExecuteChanged();
    }

    /// <summary>「继续解」能不能点：有剩下的内层包，而且现在不忙。</summary>
    private bool CanContinueOneClick() => !IsBusy && HasPendingContinuation;

    /// <summary>
    /// 清理工作区残留（用户 2026-09-24 第 22 条）：**先确认、再删**，而且只删工作区根目录的直属子目录。
    ///
    /// <para>为什么必须有这一步确认（不变量 13）：工作区里可能是那一批唯一解出来的一份产物，
    /// 删掉不可逆。所以这里把要删的目录**逐条列进确认框的明细区**，用户点确定才动手。</para>
    ///
    /// <para>没有任何残留时只提示一句，不弹确认框（空确认框只会训练用户闭眼点确定）。</para>
    /// </summary>
    private void ClearWorkspaceLeftovers()
    {
        IReadOnlyList<WorkspaceLeftover> leftovers =
            WorkspaceCleanupService.ScanMany(ResolveWorkspaceScanRoots());

        if (leftovers.Count == 0)
        {
            WorkspaceLeftoverTotalBytes = 0;
            HasWorkspaceLeftovers = false;
            WorkspaceLeftoverBanner = string.Empty;

            _dialogService.ShowInfo(StatusText.WorkspaceCleanupNothingText);

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

        bool confirmed = _dialogService.ShowConfirm(
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
            AppendLog("INFO", "工作区清理已取消：一个目录都没删。");

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
                AppendLog("WARN", "工作区：" + outcome.Message);
            }
        }

        AppendLog(
            "INFO",
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.WorkspaceCleanupResultLogFormat,
                deleted,
                failed,
                WorkspaceCleanupService.FormatSize(totalBytes)));

        // 删完重新扫一遍（失败的那几个会留在提示里，不假装清空了）。
        LogLeftoverWorkspaces();

        if (failed == 0)
        {
            _dialogService.ShowInfo(
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.WorkspaceCleanupResultLogFormat,
                    deleted,
                    failed,
                    WorkspaceCleanupService.FormatSize(totalBytes)));
        }
    }
}
