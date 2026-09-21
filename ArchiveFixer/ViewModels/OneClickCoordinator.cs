using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 「一个包一次搞定」：把 识别 → 修正伪装后缀 → 按密码本试密码解压 → 一行汇总 串成一次操作
    /// （AGENTS.md §10 的 M2 验收）。
    ///
    /// 关于"一键"的准确含义：
    /// 不变量 3 要求**任何改名都必须先预览**，所以这里不会跳过确认。
    /// 一键 = "一次确认之后全自动"；如果这批任务里没有任何需要改名的（后缀本来就对、或者是分卷），
    /// 改名这一步会整个跳过，那就真的零确认。
    ///
    /// 刻意不做的事：
    /// - 不自动删源包（那是 M3 的独立开关，默认关闭，且要校验通过才删）；
    /// - 不改用户没勾选的任务（沿用既有"只处理选中项"的约定）。
    /// </summary>
    internal sealed class OneClickCoordinator
    {
        private readonly MainViewModel _vm;
        private readonly ScanCoordinator _scanCoordinator;
        private readonly RenameCoordinator _renameCoordinator;
        private readonly ExtractionCoordinator _extractionCoordinator;
        private readonly DialogService _dialogService;

        public OneClickCoordinator(
            MainViewModel vm,
            ScanCoordinator scanCoordinator,
            RenameCoordinator renameCoordinator,
            ExtractionCoordinator extractionCoordinator,
            DialogService dialogService)
        {
            _vm = vm;
            _scanCoordinator = scanCoordinator;
            _renameCoordinator = renameCoordinator;
            _extractionCoordinator = extractionCoordinator;
            _dialogService = dialogService;
        }

        private ObservableCollection<ArchiveTask> Tasks => _vm.Tasks;
        private bool IsBusy { get => _vm.IsBusy; set => _vm.IsBusy = value; }
        private void AppendLog(string level, string message) => _vm.AppendLog(level, message);
        private void UpdateSummary() => _vm.UpdateSummary();

        public async Task RunAsync()
        {
            if (IsBusy)
            {
                return;
            }

            /*
             * 只处理**勾选**的任务。
             * 以前是"选了就处理选中的、没选就处理全部"，看起来贴心，实际是灾难：
             * 用户以为只动自己挑的那几个，结果整列表都被解压；日志里的任务数还会前后对不上。
             * 明确一点更好：没勾就提示去勾。
             */
            List<ArchiveTask> targets = Tasks.Where(t => t.IsSelected).ToList();

            if (targets.Count == 0)
            {
                _dialogService.ShowInfo(Tasks.Count == 0
                    ? "任务列表是空的。先把文件或文件夹拖进来，或点“添加文件夹”。"
                    : $"请先勾选要处理的任务。{Environment.NewLine}{Environment.NewLine}" +
                      $"列表里有 {Tasks.Count} 个任务，当前一个都没勾。在列表上按 Ctrl+A 可以全选。");
                return;
            }

            IsBusy = true;

            try
            {
                AppendLog("INFO", $"一键处理开始，共 {targets.Count} 个任务。");

                // 第一步：识别。只对"还没识别过"的重新扫，避免每次一键都把所有文件重读一遍。
                if (targets.Any(NeedsScan))
                {
                    AppendLog("INFO", "一键处理：先识别格式。");
                    await _scanCoordinator.ScanTasksAsync();
                }

                // 第二步：修正伪装后缀。只处理确实需要改的，且必须经过预览确认（不变量 3）。
                int needRename = Tasks.Count(t => t.IsSelected && NeedsRename(t));

                if (needRename > 0)
                {
                    AppendLog("INFO", $"一键处理：{needRename} 个任务需要修正后缀，先出预览。");
                    await _renameCoordinator.SmartRenameAsync();
                }
                else
                {
                    AppendLog("INFO", "一键处理：没有需要修正的后缀，跳过改名。");
                }

                // 第三步：解压（密码本、旁路说明文件、分卷守卫都在解压流程里生效）。
                AppendLog("INFO", "一键处理：开始解压。");
                await _extractionCoordinator.StartExtractAsync();

                // 第四步：一行汇总。
                UpdateSummary();
                string summary = BuildSummaryLine(targets);
                AppendLog("INFO", summary);
                _dialogService.ShowInfo(summary);
            }
            catch (OperationCanceledException)
            {
                AppendLog("WARN", "一键处理被取消。");
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "一键处理失败：" + ex.Message);
                _dialogService.ShowException(ex, "一键处理失败");
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// 只对"还没识别"的任务重扫：
        /// 一律重扫会把已经识别好的、甚至已经解压完的任务重新洗一遍，白费时间还冲掉结果。
        /// </summary>
        private static bool NeedsScan(ArchiveTask task)
        {
            return task.ExtensionStatus == StatusText.NotChecked ||
                string.IsNullOrWhiteSpace(task.ExtensionStatus) ||
                string.Equals(task.DetectedFormat, "Unknown", StringComparison.OrdinalIgnoreCase) ||
                task.Status == StatusText.WaitingScan;
        }

        /// <summary>
        /// 需要改名的三种后缀状态：缺失、不匹配、多重伪装。
        /// "后缀正常"不用动，"分卷后缀"**绝对不能动**（改了会切断分卷链），"格式未知"交给设置里的策略。
        /// </summary>
        private static bool NeedsRename(ArchiveTask task)
        {
            /*
             * 内嵌归档（文件尾部藏着 ZIP 的双面文件）**不改名**。
             *
             * 它看起来最像"该改名"的那一类（`xxx.mp4` 里明明有 ZIP），但改名是纯粹的误导：
             * ZIP 的内部偏移相对它自己，而前置数据远超 7-Zip 的容忍上限（实测 8 MiB），
             * 所以 `xxx.zip` 交到 7z 手里仍然打不开。
             * 该做的事是解压管线里按偏移把尾部那段取出来，不是动后缀。
             */
            if (task.ExtensionStatus == StatusText.ExtensionEmbedded)
            {
                return false;
            }

            return task.ExtensionStatus == StatusText.ExtensionMissing ||
                task.ExtensionStatus == StatusText.ExtensionMismatch ||
                task.ExtensionStatus == StatusText.ExtensionMultiFake;
        }

        /// <summary>
        /// 一行汇总。
        ///
        /// 硬要求：各分项加起来**必须等于本次处理的任务数**。
        /// 以前这里统计整个列表，还把"扫描过但从没被处理"的任务算成失败 ——
        /// 于是出现过 `成功 0 / 失败 1 / 跳过 2（共 4 个任务）`：0+1+2=3≠4，
        /// 用户根本没法判断到底发生了什么。汇总如果自己都对不上，还不如不显示。
        /// </summary>
        private string BuildSummaryLine(IReadOnlyList<ArchiveTask> targets)
        {
            int success = targets.Count(t =>
                t.Status == StatusText.ExtractSuccess ||
                t.Status == StatusText.Overwritten);

            int partial = targets.Count(t => t.Status == StatusText.PartiallyCompleted);
            int cancelled = targets.Count(t => t.Status == StatusText.Cancelled);
            int skipped = targets.Count(t => t.Status == StatusText.Skipped);
            int failed = targets.Count(IsFailureStatus);

            // 剩下的就是"既没成功也没失败、也没跳过"的：没轮到它（例如格式未知却没被处理）。
            int untouched = targets.Count - success - partial - cancelled - skipped - failed;

            var parts = new List<string> { $"成功 {success}", $"失败 {failed}", $"跳过 {skipped}" };

            if (partial > 0)
            {
                parts.Add($"部分完成 {partial}");
            }

            if (cancelled > 0)
            {
                parts.Add($"取消 {cancelled}");
            }

            if (untouched > 0)
            {
                parts.Add($"未处理 {untouched}");
            }

            string scope = targets.Count == Tasks.Count
                ? $"本次 {targets.Count} 个任务"
                : $"本次 {targets.Count} 个 / 列表共 {Tasks.Count} 个";

            string line = $"一键处理完成：{string.Join(" / ", parts)}（{scope}）。";

            int renameSuccess = targets.Count(t => t.Status == StatusText.RenameSuccess);

            if (renameSuccess > 0)
            {
                line += $" 已修正后缀 {renameSuccess} 个。";
            }

            // 跳过必须说清为什么，否则"跳过 2"等于没说
            int notArchive = targets.Count(t => t.Status == StatusText.Skipped && !t.IsArchive);

            if (notArchive > 0)
            {
                line += $" 跳过的 {notArchive} 个已由 7-Zip 确认不是压缩包。";
            }

            int passwordError = targets.Count(t => t.Status == StatusText.WrongPassword);

            if (passwordError > 0)
            {
                line += $" 密码错误 {passwordError} 个：检查密码本里是否包含这些包的密码。";
            }

            int corrupted = targets.Count(t => t.Status == StatusText.Corrupted);

            if (corrupted > 0)
            {
                line += $" 文件损坏 {corrupted} 个：这类只能重新下载。";
            }

            return line;
        }

        /// <summary>真正"处理过并且失败了"的状态。</summary>
        private static bool IsFailureStatus(ArchiveTask task)
        {
            return task.Status is
                StatusText.ExtractFailed or
                StatusText.WrongPassword or
                StatusText.Corrupted or
                StatusText.AccessDenied or
                StatusText.OutputConflict or
                StatusText.VolumeMissing or
                StatusText.PathTooLong or
                StatusText.SevenZipMissing or
                StatusText.UnknownError or
                StatusText.RenameFailed or
                StatusText.TestFailed;
        }
    }
}
