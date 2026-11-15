using ArchiveFixer.BindingModels;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 任务行操作：删除/复制/打开目录/改名/试密码/检查内容；通过 MainViewModel 的公开/内部门面操作列表。
/// </summary>
internal sealed class TaskActionCoordinator
{
    private readonly MainViewModel _vm;

    public TaskActionCoordinator(MainViewModel vm)
    {
        _vm = vm;
    }

    internal void RemoveTask(object? parameter)
    {
        if (parameter is not ArchiveTask task)
        {
            // ⛔ 不许静默（用户 2026-10-10：「什么东西都是没有用的」）：命令那层已有 IsRowInList 判据，这里说一句。
            ExplainNoRow();
            return;
        }

        _vm.Tasks.Remove(task);
        RebuildTaskIndex();
        _vm.Workspace.LogLeftover();
        _vm.PasswordBook.AutoLoad();

        _vm.UpdateSummary();
    }

    /// <summary>
    /// 移除**勾选的**任务（用户 2026-09-24 第 15 条："列表要能删无用物"）。
    ///
    /// <para>只动任务列表：源文件、输出目录、日志一个字节都不碰 —— 移除错了再"添加"一次就回来了，
    /// 所以这里**不做二次确认**（确认框留给真正不可逆的事：删源、清工作区、清其余物）。</para>
    /// </summary>
    internal void RemoveCheckedTasks()
    {
        List<ArchiveTask> checkedTasks = _vm.Tasks.Where(task => task.IsSelected).ToList();

        /*
         * 一个都没勾、但用户**点中了一行**（高亮）→ 就按那一行办。
         *
         * ⚠ 2026-09-29 补上"没勾着就不亮"那一半之后（命令判据 = 列表里至少有一条勾着的，⛔ 不看忙闲），
         * 界面**已经点不到**这种状态了（按钮是灰的 + 不显示 hover），所以这一段降级成"兜底网"：
         * 判据在 WPF 重问与用户实际点下之间可能差一拍（例如刚点完「全不选」），
         * 那一下也**不能**变成"点了没反应"——还是照旧按高亮那一行办，并在日志里写明是按哪一行办的。
         * ⛔ 绝不会退化成"删掉用户没打算删的那一行"：只有 _vm.SelectedTask 确实还在列表里才走这条。
         */
        if (checkedTasks.Count == 0)
        {
            ArchiveTask? highlighted = _vm.SelectedTask;

            if (highlighted != null && _vm.Tasks.Contains(highlighted))
            {
                _vm.AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.RemoveCheckedTasksHighlightFallbackLogFormat,
                        highlighted.FileName));

                RemoveTasksCore(new[] { highlighted });
                return;
            }

            _vm.DialogService.ShowInfo(StatusText.RemoveCheckedTasksNoneText);
            return;
        }

        RemoveTasksCore(checkedTasks);
    }

    /*
     * ==================== 「按建议改名并重试」（用户 2026-09-25 第 41 条） ====================
     *
     * 场景：一组分卷的**第一卷**名字被改坏（`Code Complete-BZ.7z(删掉.001` 那种），
     * 程序只能报「分卷缺失」+ 给一个标准改名建议 —— 源文件它自己一个字节都不能动（不变量 1）。
     * 但这一卷是真的需要改名，让用户对着日志手打一个不规则的名字，既慢又容易打错。
     *
     * 所以：**用户显式点这个按钮**（勾选 + 确认框两道），程序替他改**这一个名字**，然后立刻重试。
     * ⛔ 三条红线：①只在用户点了之后才动；②只 `File.Move` 改名，**绝不覆盖**（目标名被占就拒绝）、
     * 绝不删除、绝不改内容；③改名建议只有一处推法（`Extraction/VolumeNameRepair`），
     * 与失败清单里写给他的是同一个名字。
     */

    /// <summary>「按建议改名并重试」当前能改的那几个任务（勾选 + 这一档的毛病 + 计划成立）。</summary>
    internal List<(ArchiveTask Task, VolumeNameRepairPlan Plan)> BuildVolumeRepairCandidates(
        IReadOnlyList<ArchiveTask>? targets = null)
    {
        var candidates = new List<(ArchiveTask, VolumeNameRepairPlan)>();
        IEnumerable<ArchiveTask> source = targets != null
            ? targets.Where(_vm.Tasks.Contains)
            : _vm.Tasks.Where(item => item != null && item.IsSelected);

        foreach (ArchiveTask task in source)
        {
            /*
             * 判据是**机器事实**：任务上记着改名建议（= 上一轮判决认定"名字被改坏的第一卷"），
             * 而且此刻从文件系统真能算出一个成立的计划。
             * ⛔ 不比对中文状态文案（AGENTS.md §7）；建议为空时这个按钮根本不该亮。
             */
            if (string.IsNullOrWhiteSpace(task.VolumeRenameSuggestion))
            {
                continue;
            }

            VolumeNameRepairPlan plan = VolumeNameRepair.Plan(task.CurrentPath, EnumerateDirectoryFileNames(task.CurrentPath));

            if (plan.CanRepair)
            {
                candidates.Add((task, plan));
            }
        }

        return candidates;
    }

    /// <summary>
    /// 同目录里的文件名（读不了就当作空 —— 计划会因此判"不能改"，绝不抛）。
    ///
    /// <para>实现只有一份：<see cref="VolumeNameRepair.EnumerateFileNamesInDirectory"/>
    /// （识别阶段、改名预览、①页那颗按钮三处共用；⛔ 各写一份必然漂移）。</para>
    /// </summary>
    private static IEnumerable<string?> EnumerateDirectoryFileNames(string? filePath)
    {
        return VolumeNameRepair.EnumerateFileNamesInDirectory(filePath);
    }

    internal bool CanRenameBySuggestionAndRetry()
    {
        return !_vm.IsBusy && BuildVolumeRepairCandidates().Count > 0;
    }

    /// <summary>
    /// 按建议改名（只改名字）并立刻重试解压。
    /// </summary>
    internal async Task RenameBySuggestionAndRetryAsync(IReadOnlyList<ArchiveTask>? targets = null)
    {
        List<(ArchiveTask Task, VolumeNameRepairPlan Plan)> candidates = BuildVolumeRepairCandidates(targets);

        if (candidates.Count == 0)
        {
            _vm.DialogService.ShowInfo(StatusText.VolumeRepairNoneText);
            return;
        }

        string list = string.Join(
            Environment.NewLine,
            candidates.Select(item => "· " + item.Plan.Describe()));

        bool confirmed = _vm.DialogService.ShowConfirm(
            StatusText.VolumeRepairConfirmTitle
            + Environment.NewLine + Environment.NewLine
            + string.Format(StatusText.VolumeRepairConfirmBodyFormat, list));

        if (!confirmed)
        {
            _vm.AppendLog("INFO", $"用户取消了「{StatusText.VolumeRepairConfirmTitle}」，磁盘上的文件没有动。");
            return;
        }

        _vm.LogService.MarkOperationStart(StatusText.VolumeRepairConfirmTitle);

        int renamed = 0;
        int failed = 0;
        var repaired = new List<ArchiveTask>();

        // 改名同步的唯一实现只在本协作类里；它无实例状态，按调用点就地建一次即可。
        var volumeNameNormalize = new VolumeNameNormalizeHandler(_vm.ExtractionCoordinator);

        foreach ((ArchiveTask task, VolumeNameRepairPlan plan) in candidates)
        {
            VolumeNameRepairResult result = VolumeNameRepair.TryApply(plan);

            if (!result.Success)
            {
                failed++;
                task.ErrorMessage = result.Message;
                _vm.AppendLog("ERROR", $"{plan.CurrentFileName}：{result.Message}");
                continue;
            }

            renamed++;
            repaired.Add(task);

            _vm.AppendLog("INFO", result.Message);

            /*
             * 任务要跟着搬到新路径上：文件在磁盘上已经叫新名字了，任务还指着旧名字的话，
             * 紧接着的"重新识别"第一步就会撞上"源文件不在了"。
             *
             * ⚠ 同步走的是**唯一出口** <see cref="VolumeNameNormalizeHandler.SyncTasksAfterVolumeRename"/>：
             * 这次改名动的是**整组**（组里别的卷很可能是同一批里另一个任务的文件，用户 2026-09-30 真机），
             * 所以它把所有指着被改过名的文件的任务一起搬到新名字上、并重拍它们的源文件快照
             * （不变量 11 的基准跟着名字走）。以前这里只改自己这一行 → 别的任务被不变量 11
             * 报成"源文件已变化（文件不见了）"。
             */
            volumeNameNormalize.SyncTasksAfterVolumeRename(plan.Items, task);

            /*
             * 重新识别 + **重拍源文件快照**（不变量 11）：路径换了，旧快照对新名字毫无意义，
             * 不重拍的话紧接着的解压会被"源文件已变化"拦下 —— 那条提示是防"文件被改过"的，
             * 不该拿来挡用户主动改的名字。
             */
            await _vm.ScanCoordinator.RescanTaskAsync(task).ConfigureAwait(true);
        }

        _vm.AppendLog(
            "INFO",
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.VolumeRepairBatchDoneFormat,
                renamed,
                failed));

        if (renamed == 0)
        {
            _vm.DialogService.ShowWarning(StatusText.VolumeRepairNoneText);
            return;
        }

        /*
         * **同一组的其它任务也要重扫**（2026-09-26 真机代跑当场撞到）：
         * 它们的"分卷组"是**改名之前**拍下来的快照 —— 例：首卷名字坏掉时，`set.7z.002/.003/.004`
         * 会各自成组（缺首卷），修好首卷之后这些任务手上还是"缺 set.7z.001"的旧结论，
         * 紧接着的重试就会弹一句"分卷不完整，现在缺：set.7z.001"（可那个文件明明已经在了）。
         * 判据用既有的 BelongsToSameGroup（族 + 基名），⛔ 不自己再写一套名字比较。
         */
        foreach (ArchiveTask sibling in FindSameGroupTasks(repaired))
        {
            await _vm.ScanCoordinator.RescanTaskAsync(sibling).ConfigureAwait(true);
        }

        /*
         * 重试 = 与「只解压」**同一条路**（不复制一份解压逻辑出来），但**只解刚刚改过名的那些任务**
         * （2026-09-26 审计补的口径）：
         *
         * 这颗按钮以前只在"解压跑过一轮并报了分卷缺失"之后才亮，那时勾选的就是刚跑的那一批，
         * 所以"按勾选重试"没问题；现在它**扫完就亮**，用户完全可能在还没跑过任何解压的时候点它 ——
         * 那时整张表都勾着，旧写法会把**所有**勾选任务都解一遍（他只想修这一卷）。
         * 用完把勾选原样还回去：勾选是用户的作用域，不该被这一下改掉。
         */
        List<ArchiveTask> previousSelection = _vm.Tasks.Where(item => item.IsSelected).ToList();

        _vm.RunBulkSelectionUpdate(() =>
        {
            foreach (ArchiveTask item in _vm.Tasks)
            {
                item.IsSelected = repaired.Contains(item);
            }
        });

        try
        {
            await _vm.ExtractionCoordinator.StartExtractAsync().ConfigureAwait(true);
        }
        finally
        {
            _vm.RunBulkSelectionUpdate(() =>
            {
                foreach (ArchiveTask item in previousSelection)
                {
                    item.IsSelected = true;
                }
            });
        }
    }

    /// <summary>
    /// 与刚修好的那几卷**同目录、同组**的其它任务（用来在改名之后把它们一起重扫）。
    ///
    /// <para>判据全用既有的 <c>LegacyVolumeNames.BelongsToSameGroup</c>（族 + 基名）+ 目录相等 ——
    /// ⛔ 不自己拿字符串前缀去猜"像不像一组"（那正是第 33 条踩过的坑）。</para>
    /// </summary>
    private IEnumerable<ArchiveTask> FindSameGroupTasks(IReadOnlyList<ArchiveTask> repaired)
    {
        var repairedSet = new HashSet<ArchiveTask>(repaired);
        var result = new List<ArchiveTask>();

        foreach (ArchiveTask task in _vm.Tasks)
        {
            if (task == null || repairedSet.Contains(task) || string.IsNullOrWhiteSpace(task.CurrentPath))
            {
                continue;
            }

            string otherDirectory = System.IO.Path.GetDirectoryName(task.CurrentPath) ?? string.Empty;

            foreach (ArchiveTask fixedTask in repaired)
            {
                string fixedDirectory = System.IO.Path.GetDirectoryName(fixedTask.CurrentPath) ?? string.Empty;

                if (!string.Equals(otherDirectory, fixedDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (LegacyVolumeNames.BelongsToSameGroup(
                        System.IO.Path.GetFileName(task.CurrentPath),
                        System.IO.Path.GetFileName(fixedTask.CurrentPath)))
                {
                    result.Add(task);
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 右键「智能修正此文件后缀」的入口：**只把这一行纳入本次操作，用完把勾选原样还回去**。
    ///
    /// <para>以前是选项卡的 code-behind 自己"把其余行全取消勾选"就完事 —— 用户批量勾了 20 个、
    /// 右键修其中一个，回来发现勾选全没了，而且没有任何提示（2026-09-26 审计）。
    /// 勾选是"命令的作用域"，不该被一个针对单行的动作悄悄清空。</para>
    /// </summary>
    internal async Task RunSmartRenameForSingleTaskAsync(ArchiveTask? task)
    {
        if (task == null)
        {
            return;
        }

        List<ArchiveTask> previouslySelected = _vm.Tasks.Where(item => item.IsSelected).ToList();

        _vm.RunBulkSelectionUpdate(() =>
        {
            foreach (ArchiveTask item in _vm.Tasks)
            {
                item.IsSelected = ReferenceEquals(item, task);
            }
        });

        try
        {
            if (_vm.SmartRenameRunnerOverride != null)
            {
                await _vm.SmartRenameRunnerOverride().ConfigureAwait(true);
                return;
            }

            await _vm.RenameCoordinator.SmartRenameAsync().ConfigureAwait(true);
        }
        finally
        {
            _vm.RunBulkSelectionUpdate(() =>
            {
                foreach (ArchiveTask item in previouslySelected)
                {
                    item.IsSelected = true;
                }
            });
        }
    }

    /// <summary>
    /// 右键单文件改名的**执行替身**（只给测试用；正式路径永远是 null = 走真预览窗口）。
    ///
    /// <para>为什么需要它：真窗口在无界面宿主里不会弹，而"勾选会不会被还回来"这件事
    /// 只有把"执行"这一步换掉才测得到（与 <c>ScanCoordinator.JunkReminderOverride</c> 同一套做法）。</para>
    /// </summary>

    /// <summary>
    /// 按源路径移除任务（文件夹导入时自动剔除无用物走这条；⛔ 只动列表，不动磁盘）。
    /// </summary>
    /// <returns>真的移掉了几个（路径对不上的不算）。</returns>
    /// <summary>
    /// **批末重算"改过名那些行"的检测格式 / 后缀状态**（用户 2026-10-07 点名的三列过期）。
    /// 转调扫描那边唯一的出口，⛔ 这里不另写判据。
    /// </summary>
    internal Task RefreshRenamedRowFactsAsync() => _vm.ScanCoordinator.RefreshRenamedRowFactsAsync();
    internal int RemoveTasksBySourcePaths(IEnumerable<string>? paths)
    {
        if (paths == null)
        {
            return 0;
        }

        var wanted = new HashSet<string>(
            paths.Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);

        if (wanted.Count == 0)
        {
            return 0;
        }

        /*
         * ⛔ **按规范化路径比，⛔ 不比裸字符串**（真机 2026-10-07：无用物扫描认得出 15 个，
         * 可"移出列表"一个都没匹配上 ⇒ 那些 `.txt` 一直挂在列表里、还顶出"未处理 N"）。
         * 裸 `HashSet<string>` 比 `CurrentPath` 对大小写/分隔符/`..` 一律敏感 ——
         * 用既有出口 `SafePathHelper.PathEquals`（与全项目其它地方同一把尺子）。
         */
        List<ArchiveTask> matched = _vm.Tasks
            .Where(task => wanted.Any(path => SafePathHelper.PathEquals(path, task.CurrentPath)))
            .ToList();

        if (matched.Count == 0)
        {
            return 0;
        }

        RemoveTasksCore(matched);

        return matched.Count;
    }

    /// <summary>移除一批任务并收尾（顺序、索引、汇总、日志一处收口）。</summary>
    private void RemoveTasksCore(IReadOnlyList<ArchiveTask> toRemove)
    {
        foreach (ArchiveTask task in toRemove)
        {
            _vm.Tasks.Remove(task);
        }

        RebuildTaskIndex();
        _vm.UpdateSummary();

        _vm.AppendLog(
            "INFO",
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.RemoveTasksLogFormat,
                toRemove.Count));
    }

    /// <summary>
    /// 三个"复制…"入口：⛔ **点了不许没反应**（用户 2026-10-10：「什么东西都是没有用的」）。
    ///
    /// <para>参数不是列表里那一行时（右键落在空白处 / 列头、或调用方没给参数）**说一句**；
    /// 命令那一层已有 `IsRowInList` 判据（该灰就灰），这里是第二道 —— 两条腿都要有：
    /// 判据让菜单项变灰，说明让"万一还是点到了"这一档不静默。</para>
    /// </summary>
    internal void CopyTaskInfo(object? parameter)
    {
        if (parameter is ArchiveTask task)
        {
            _vm.LogExport.CopySanitizedToClipboard(BuildTaskInfoText(task));
            return;
        }

        ExplainNoRow();
    }

    internal void CopyTaskPath(object? parameter)
    {
        if (parameter is ArchiveTask task)
        {
            _vm.LogExport.CopySanitizedToClipboard(task.CurrentPath);
            return;
        }

        ExplainNoRow();
    }

    internal void CopyTaskError(object? parameter)
    {
        if (parameter is ArchiveTask task)
        {
            _vm.LogExport.CopySanitizedToClipboard(task.ErrorMessage);
            return;
        }

        ExplainNoRow();
    }

    /// <summary>行级命令"没有可作用的那一行"时的统一说明（唯一措辞，⛔ 别各写一句）。</summary>
    internal void ExplainNoRow()
        => _vm.DialogService.ShowInfo("请先在任务列表里点中一行，再用这一项。");

    /// <summary>
    /// 任务信息文本。
    ///
    /// ⚠ 字段清单与 <see cref="ClipboardService.CopyTaskInfo"/> 刻意保持一致 ——
    /// 为什么不用那个方法：它直接把文本塞进系统剪贴板，中间没有脱敏的位置，
    /// 而 ClipboardService 本轮不在授权文件清单里（不能给它加脱敏重载）。
    /// 这里自己拼文本再交给 <see cref="CopySanitizedToClipboard"/>，四个剪贴板入口就统一了口径
    /// （与「导出日志 / 导出失败清单」一样都过 PasswordMasker）。
    /// 那边加/减字段时，这里必须同步改，否则复制出来的内容会和菜单里的"复制任务信息"不一致。
    /// </summary>
    internal static string BuildTaskInfoText(ArchiveTask task) => ArchiveFixer.ViewModels.Export.TaskInfoFormatter.Format(task);

    // ==================== 「看内容 / 试密码」（用户 2026-09-26 批准加的两个高频功能） ====================

    /// <summary>
    /// 「看内容」：对勾选的任务只 `list` 一遍 —— 回答"这个包里有什么、多大、要不要密码"。
    ///
    /// <para>⛔ 不写盘、不解压（<see cref="ArchiveInspectService"/> 只调 list）；结论走一个可复制的弹窗 + 日志。</para>
    /// </summary>
    internal async Task InspectCheckedArchivesAsync()
    {
        await RunInspectAsync(tryPasswords: false).ConfigureAwait(true);
    }

    /// <summary>
    /// 「试密码」：与「看内容」同一条实现，只是**把候选逐个都问一遍**，报出能开的那个。
    /// </summary>
    internal async Task TryPasswordsForCheckedArchivesAsync()
    {
        await RunInspectAsync(tryPasswords: true).ConfigureAwait(true);
    }

    /// <summary>
    /// 两个按钮共用的本体（⛔ 一份实现：不然"看内容"说不能开、"试密码"说能开，用户只会更糊涂）。
    /// </summary>
    internal async Task RunInspectAsync(bool tryPasswords, ArchiveTask? singleTask = null)
    {
        List<ArchiveTask> targets = singleTask != null && _vm.Tasks.Contains(singleTask)
            ? new List<ArchiveTask> { singleTask }
            : _vm.Tasks.Where(item => item.IsSelected).ToList();

        if (targets.Count == 0)
        {
            _vm.DialogService.ShowInfo(
                singleTask != null
                    ? "当前没有选中的任务。"
                    : "请先勾选要看的任务（最左侧一列）。");
            return;
        }

        _vm.LogService.MarkOperationStart(tryPasswords ? "试密码" : "看内容");
        _vm.AppendLog("INFO", $"{(tryPasswords ? "试密码" : "看内容")}：{targets.Count} 个任务（只列目录，不解压、不写盘）。");

        _vm.IsBusy = true;

        try
        {
            var inspect = new ArchiveInspectService(_vm.ArchiveEngine);
            var report = new System.Text.StringBuilder();

            foreach (ArchiveTask task in targets)
            {
                /*
                 * 候选按**既有那一套顺序**算（与解压完全同一份：本批已成功 → 空密码 → 映射式 → 统一密码 → 列表）——
                 * ⛔ 这里不许自己拼一套，否则界面上给的顺序与真正解压时的顺序会对不上。
                 * 「看内容」不试密码（只回答"要不要密码"）；「试密码」才把候选交下去。
                 */
                IReadOnlyList<PasswordItem>? candidates = tryPasswords
                    ? _vm.PasswordService
                        .GetPasswordCandidates(
                            task,
                            _vm.Settings.UseGlobalPasswordForAllTasks ? _vm.GlobalPassword : string.Empty,
                            _vm.PasswordService.Passwords,
                            _vm.Settings.TryEmptyPasswordFirst,
                            _vm.Settings.EnableSidecarPassword)
                        .Take(Math.Clamp(_vm.Settings.MaxPasswordAttemptsPerLayer, 1, 1000))
                        .ToList()
                    : null;

                ArchiveInspectResult result = await inspect.InspectAsync(task, candidates).ConfigureAwait(true);

                report.AppendLine(DescribeInspectResult(result, tryPasswords));
                report.AppendLine();

                _vm.AppendLog(
                    result.Success ? "INFO" : "WARN",
                    $"{task.FileName}：{(tryPasswords ? "试密码" : "看内容")} —— {SummarizeInspectResult(result, tryPasswords)}");
            }

            _vm.DialogService.ShowInfo(
                $"{(tryPasswords ? "试密码" : "看内容")}（{targets.Count} 个任务，只列目录、没写盘）："
                + Environment.NewLine + Environment.NewLine
                + report.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            _vm.AppendLog("ERROR", $"{(tryPasswords ? "试密码" : "看内容")}失败：{ex.Message}");
            _vm.DialogService.ShowError($"{(tryPasswords ? "试密码" : "看内容")}失败：{ex.Message}");
        }
        finally
        {
            _vm.IsBusy = false;
        }
    }

    /// <summary>弹窗里那一段（人读的：多行、可复制）。</summary>
    internal static string DescribeInspectResult(ArchiveInspectResult result, bool tryPasswords) => ArchiveFixer.ViewModels.Export.InspectResultFormatter.Describe(result, tryPasswords);

    internal static string SummarizeInspectResult(ArchiveInspectResult result, bool tryPasswords) => ArchiveFixer.ViewModels.Export.InspectResultFormatter.Summarize(result, tryPasswords);
    /// <summary>
    /// 「打开源目录」（手动那一栏）：第一个勾选任务所在的文件夹；一个都没勾就退回"当前行"。
    ///
    /// <para>⛔ 它只**打开目录**，不动任何文件（不选中、不改名、不删除）。</para>
    /// </summary>
    internal void CopyCurrentVolumeGroupPaths()
    {
        if (_vm.SelectedTask == null)
        {
            return;
        }

        IEnumerable<string> paths = _vm.SelectedTask.VolumePaths.Count > 0
            ? _vm.SelectedTask.VolumePaths
            : new[] { _vm.SelectedTask.CurrentPath };

        string text = string.Join(Environment.NewLine, paths);

        if (_vm.LogExport.CopySanitizedToClipboard(text))
        {
            _vm.AppendLog("INFO", $"已复制当前任务同组路径（{paths.Count()} 条）。");
        }
    }

    internal async Task AddCurrentOutputFolderToTasksAsync()
    {
        if (_vm.SelectedTask == null)
        {
            return;
        }

        string output = _vm.SelectedTask.OutputPath;

        if (string.IsNullOrWhiteSpace(output) || !Directory.Exists(output))
        {
            _vm.DialogService.ShowInfo("当前任务还没有可用的输出目录；先解压这一层，再添加下一层。");
            return;
        }

        await _vm.ScanCoordinator.AddPathsAsync(new[] { output }, ImportMode.Append).ConfigureAwait(true);
        _vm.AppendLog("INFO", $"已把当前任务输出目录追加到任务列表，供手动逐层继续：{output}");
    }

    internal void OpenCheckedSourceDirectory()
    {
        ArchiveTask? task = _vm.Tasks.FirstOrDefault(item => item.IsSelected) ?? _vm.SelectedTask;

        if (task == null)
        {
            _vm.DialogService.ShowInfo("列表里还没有任务：先点「添加文件 / 添加文件夹」（也可以直接把文件拖进窗口）。");
            return;
        }

        // ⛔ 不再在这里补一句"已打开"：成功/失败都由 OpenTaskDirectory 自己如实记账
        // （原来无条件写"已在资源管理器里打开它所在的目录"，而它可能压根没打开 —— 那就是撒谎）。
        OpenTaskDirectory(task);
    }

    /// <summary>
    /// 「打开源目录」：在资源管理器里打开这一行**那个包所在的目录**。
    ///
    /// <para>⛔ **点了必须有反应**（用户 2026-10-10：「右键点打开输出目录没有，打开文件夹目录没用，
    /// 什么东西都是没有用的」）：原来把 <see cref="PathService.OpenDirectory"/> 的返回值**丢掉** ——
    /// 目录已经不在了（源包被搬进其余物、被按档删掉、或被改名）时**什么都不发生、不弹窗、不写日志**，
    /// 就是那类"界面在撒谎"的死点。现在：成功写一行日志，失败**如实说清是哪条路径、为什么打不开、
    /// 下一步能做什么**。</para>
    /// </summary>
    /// <returns>真的打开了 ⇒ true；否则 false（调用方不必再补一句"已打开"）。</returns>
    internal bool OpenTaskDirectory(object? parameter)
    {
        if (parameter is not ArchiveTask task)
        {
            // 右键落在空白处 / 列头时会是这一档：⛔ 不许静默。
            _vm.DialogService.ShowInfo("请先在任务列表里点中一行，再右键选这一项。");
            return false;
        }

        string directory = Path.GetDirectoryName(task.CurrentPath) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(directory))
        {
            // 点了没反应是最难排查的一类"界面在撒谎"：说清为什么 + 下一步做什么。
            _vm.DialogService.ShowInfo(
                $"「{task.LogName}」还没有可打开的目录（路径信息不完整）。"
                + "可以先右键「重新扫描此文件」，或把文件重新添加一次。");
            return false;
        }

        if (!_vm.PathService.OpenDirectory(directory))
        {
            string message =
                $"打不开「{directory}」—— 这个目录现在不在了（源包可能被搬进「其余物」、按删除档删掉，或者改过名）。"
                + $"这一行现在是「{task.LogName}」。";

            _vm.DialogService.ShowInfo(message + "可以先右键「重新扫描此文件」，或在「查看任务详情」里看它当初的完整路径。");
            _vm.AppendLog("WARN", $"{task.LogName}：打开源目录失败 —— {directory} 不存在（可能已被搬走或删除）。");
            return false;
        }

        _vm.AppendLog("INFO", $"{task.LogName}：已在资源管理器里打开它所在的目录：{directory}");
        return true;
    }

    /// <summary>
    /// 「打开输出目录」：在资源管理器里打开这一行的**产物目录**。
    ///
    /// <para>⛔ 同上：打不开就必须**如实说一句**（用户实测过"右键点打开输出目录没有"）——
    /// 输出目录被收掉（定稿后整份删掉 / 归集后搬走）是常态，静默就是死点。</para>
    /// </summary>
    /// <returns>真的打开了 ⇒ true；否则 false。</returns>
    internal bool OpenTaskOutputDirectory(object? parameter)
    {
        if (parameter is not ArchiveTask task)
        {
            _vm.DialogService.ShowInfo("请先在任务列表里点中一行，再右键选这一项。");
            return false;
        }

        if (string.IsNullOrWhiteSpace(task.OutputPath))
        {
            _vm.DialogService.ShowInfo(
                $"「{task.LogName}」还没有输出目录（先解压一次才会有）。"
                + "可以先勾选它并点「只解压」或「一键处理」。");
            return false;
        }

        if (!_vm.PathService.OpenDirectory(task.OutputPath))
        {
            _vm.DialogService.ShowInfo(
                $"打不开「{task.OutputPath}」—— 这个输出目录现在不在了（可能已经按档清理掉，或产物被搬去了别的落点）。"
                + $"这一行现在是「{task.LogName}」。");
            _vm.AppendLog("WARN", $"{task.LogName}：打开输出目录失败 —— {task.OutputPath} 不存在（可能已被清理或搬走）。");
            return false;
        }

        _vm.AppendLog("INFO", $"{task.LogName}：已在资源管理器里打开它的输出目录：{task.OutputPath}");
        return true;
    }

    /// <summary>
    /// 打开其余物目录（右键菜单入口）。
    ///
    /// 路径用与「删除其余物」**同一份解析**（<see cref="MaintenanceCleanupService.ResolveArtifactScope"/>）：
    /// 用户在这里看到的目录，就是那条删除入口会动的那一个 —— 两个入口给出不同答案是最容易出事的形态。
    /// </summary>
    internal void OpenTaskArtifactDirectory(object? parameter)
    {
        if (parameter is not ArchiveTask task)
        {
            return;
        }

        ArtifactCleanupScope scope = MaintenanceCleanupService.ResolveArtifactScope(task);

        if (!scope.IsResolved && string.IsNullOrWhiteSpace(scope.ArtifactDirectory))
        {
            _vm.DialogService.ShowInfo("这个任务还没有其余物目录（还没解压，或已经被删掉了）。");
            return;
        }

        if (SafePathHelper.DirectoryExists(scope.ArtifactDirectory))
        {
            // ⛔ 返回值不许丢（同「打开源/输出目录」那一档）：打不开也要如实说一句，不许静默。
            if (_vm.PathService.OpenDirectory(scope.ArtifactDirectory))
            {
                _vm.AppendLog("INFO", $"{task.LogName}：已在资源管理器里打开其余物目录：{scope.ArtifactDirectory}");
                return;
            }

            _vm.DialogService.ShowInfo("其余物目录打不开（可能刚被别的程序删掉或权限不够）：" + scope.ArtifactDirectory);
            _vm.AppendLog("WARN", $"{task.LogName}：打开其余物目录失败 —— {scope.ArtifactDirectory}");
            return;
        }

        _vm.DialogService.ShowInfo("其余物目录不存在（可能已经被删掉了）：" + scope.ArtifactDirectory);
    }

    /// <summary>
    /// **行级命令的可用性判据（唯一出口）**：参数必须是**列表里真的存在的那一行**。
    ///
    /// <para>给谁用：右键菜单里那些"针对某一行"的命令（移除这一行 / 重新扫描 / 三个复制 / 打开目录…）。
    /// 它们原来一律没有判据 ⇒ 列表空着、或者右键落在空白处时**菜单项是亮的、点下去什么都不发生**
    /// （用户 2026-10-10：「什么东西都是没有用的」）。判据只有这一处，⛔ 不许各写一套。</para>
    ///
    /// <para>为什么是"在不在列表里"而不是"选中项非空"：右键菜单给的是
    /// `PlacementTarget.SelectedItem`（那一行的对象），而选中项可能已经过期
    /// （`ReplaceAll` 之后 `_vm.SelectedTask` 被清成 null，见 <c>RebuildTaskIndex</c> 那段说明）——
    /// 按"对象还在表里"判，两个来源就都稳。</para>
    /// </summary>
    internal bool IsRowInList(object? parameter)
        => parameter is ArchiveTask task && _vm.Tasks.Contains(task);

    internal void RebuildTaskIndex()
    {
        for (int i = 0; i < _vm.Tasks.Count; i++)
        {
            _vm.Tasks[i].Index = i + 1;
        }
    }
}

