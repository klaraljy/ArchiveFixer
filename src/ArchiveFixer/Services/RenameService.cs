using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 改名服务。
    /// 
    /// 职责：
    /// 1. 根据 RenameOptions 生成改名预览。
    /// 2. 执行用户确认后的改名。
    /// 3. 处理目标文件冲突。
    /// 4. 更新 ArchiveTask 的 CurrentPath / FileName / CurrentExtension / 状态。
    /// 
    /// 注意：
    /// 这里不直接操作 WPF 控件。
    /// 这里不弹窗。
    /// 所有后缀修改都必须先 BuildPreview，再由用户确认 ExecuteRenameAsync。
    /// </summary>
    public class RenameService
    {
        public List<RenamePreviewItem> BuildPreview(
            IEnumerable<ArchiveTask> tasks,
            RenameOptions options)
        {
            List<RenamePreviewItem> result = new();

            if (tasks == null)
            {
                return result;
            }

            options ??= new RenameOptions();
            options.Normalize();

            foreach (ArchiveTask task in tasks)
            {
                try
                {
                    if (task == null)
                    {
                        continue;
                    }

                    if (!task.IsSelected)
                    {
                        continue;
                    }

                    string oldPath = task.CurrentPath;

                    if (string.IsNullOrWhiteSpace(oldPath))
                    {
                        oldPath = task.OriginalPath;
                    }

                    if (string.IsNullOrWhiteSpace(oldPath))
                    {
                        continue;
                    }

                    string newPath = BuildNewPath(task, options);
                    string operationName = BuildOperationLabel(options, task.DetectedFormat);

                    RenamePreviewItem item = new RenamePreviewItem(
                        oldPath,
                        task.DetectedFormat,
                        operationName,
                        newPath,
                        options.ConflictAction);

                    if (!File.Exists(oldPath))
                    {
                        item.MarkInvalid("源文件不存在");
                        result.Add(item);
                        continue;
                    }

                    /*
                     * 分卷文件**不许改名** —— 只有一种例外：名字被改坏的第 1 卷能被修回标准名
                     * （那一条在上面 BuildNewPath 的 FixByDetectedFormat 分支里算，判据是
                     * VolumeNameRepair.Plan）。这里判"算出来的落点与原名相同"= 没有可修的东西，
                     * 于是如实标「将跳过」并说清原因（2026-09-26 审计：以前这条闸门只长在智能修正里，
                     * 「替换后缀」能把 set.7z.001 改成 set.7z.7z，整组就废了）。
                     */
                    if (FileNameHelper.IsVolumePartFileName(item.OriginalFileName) &&
                        SafePathHelper.PathEquals(oldPath, newPath))
                    {
                        item.NewPath = oldPath;
                        item.NewFileName = item.OriginalFileName;
                        item.MarkSkip(StatusText.RenameVolumeSkippedReason);
                        result.Add(item);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(newPath))
                    {
                        item.MarkInvalid("无法生成新路径");
                        result.Add(item);
                        continue;
                    }

                    // 必须用规范化路径比较，不能比字符串：
                    // 只要路径写法有差异（正斜杠 / 冗余分隔符 / 大小写），
                    // 下面那句 File.Exists(newPath) 就会把"自己"当成"已存在的目标"，
                    // 于是把 data.tar.gz 这种本来不用改的文件改成 data.tar(1).gz。
                    if (SafePathHelper.PathEquals(oldPath, newPath))
                    {
                        item.MarkSkip("新路径与原路径相同，无需改名");
                        result.Add(item);
                        continue;
                    }

                    string? directory = Path.GetDirectoryName(newPath);

                    if (string.IsNullOrWhiteSpace(directory))
                    {
                        item.MarkInvalid("无法确定目标目录");
                        result.Add(item);
                        continue;
                    }

                    if (!Directory.Exists(directory))
                    {
                        item.MarkInvalid("目标目录不存在");
                        result.Add(item);
                        continue;
                    }

                    if (File.Exists(newPath))
                    {
                        /*
                         * 预览期的冲突结论必须和真正执行时的结论一致，否则用户看到的 NewPath
                         * 与落盘结果会对不上。所以两边共用同一套判定：
                         * - Skip      → 跳过；
                         * - Overwrite → 目标保持 NewPath，状态 TargetExists（执行阶段走"先移开再落位"）；
                         * - Ask       → **在预览里逐条让用户选**（本方法只标"待选择"，落点由
                         *               RenameCoordinator 在窗口关掉之后按用户的选择写回）；
                         * - 其余       → 换成自动改名后的路径。
                         *
                         * Ask 以前被并进"其余"分支，于是预览表里出现「询问」+「将自动重命名」并列
                         * —— 界面在自相矛盾（真实缺陷，见 fix-ask.md §6 第 1 条）。现在它真的会问，
                         * 而且问在预览里（改名本来就必须先预览，不变量 3），不额外弹第二个对话框。
                         */
                        if (string.Equals(options.ConflictAction, "Skip", StringComparison.OrdinalIgnoreCase))
                        {
                            item.MarkSkip("目标文件已存在，已跳过");
                        }
                        else if (string.Equals(options.ConflictAction, "Overwrite", StringComparison.OrdinalIgnoreCase))
                        {
                            item.Status = StatusText.TargetExists;
                            item.ErrorMessage = "确认后将覆盖目标文件（先移开旧文件再落位，中途失败不丢文件）";
                        }
                        else if (ConflictActions.IsAsk(options.ConflictAction))
                        {
                            item.MarkNeedsConflictChoice();
                        }
                        else
                        {
                            // 默认档（AutoRename）：**绝不覆盖**，只改目标侧的名字。
                            item.NewPath = AutoRenamePath(newPath);
                            item.MarkAutoRename("目标文件已存在，将自动重命名");
                        }
                    }

                    result.Add(item);
                }
                catch (Exception ex)
                {
                    string path = task?.CurrentPath ?? task?.OriginalPath ?? string.Empty;

                    RenamePreviewItem item = new RenamePreviewItem
                    {
                        OriginalPath = path,
                        OriginalFileName = Path.GetFileName(path),
                        DetectedFormat = task?.DetectedFormat ?? "Unknown",
                        Operation = GetOperationDisplayName(options),
                        NewPath = string.Empty,
                        NewFileName = string.Empty,
                        ConflictAction = options.ConflictAction
                    };

                    item.MarkInvalid("生成预览失败：" + ex.Message);
                    result.Add(item);
                }
            }

            ResolveBatchDuplicateTargets(result);

            return result;
        }

        /// <summary>
        /// **本批次内**两行改成同一个落点时，把后来者错开（2026-09-26 审计修的真缺陷）。
        ///
        /// <para><b>为什么必须在预览期做</b>：上面那个循环只看得见**磁盘上**已有的文件，
        /// 看不见"这一批里另一行马上就要占这个名字"。<c>A.jpg</c> 与 <c>A.png</c> 同目录、
        /// 一起用「替换后缀」改成 <c>.rar</c> 时两行的落点都是 <c>A.rar</c> ——
        /// 预览里两行都是「可改名」、统计还写着"确认改名 2 个文件"，
        /// 可执行阶段第 3 步 <c>File.Move</c> 撞名 → **整批回滚** + 用户拿到一句
        /// "落位 A.rar 时出错：文件已存在"（数据不丢，但结论与预览不符、报错也看不懂）。</para>
        ///
        /// <para><b>为什么只处理"批内重名"</b>：磁盘上已存在的冲突在生成预览时就已经按
        /// 用户的冲突档位处理过了（跳过 / 覆盖 / 询问 / 自动重命名）。这里**不重新判磁盘**，
        /// 否则会把"名称交换"（A↔B，两个落点都真实存在）误判成冲突 —— 那是执行期刻意支持的一档。
        /// 错开用的名字仍然绕开磁盘上已有的文件（<see cref="ResolveBatchUniqueTargetPath"/>）。</para>
        /// </summary>
        private static void ResolveBatchDuplicateTargets(List<RenamePreviewItem> items)
        {
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (RenamePreviewItem item in items)
            {
                if (!WillExecute(item))
                {
                    continue;
                }

                if (claimed.Add(item.NewPath))
                {
                    continue;
                }

                string resolved = ResolveBatchUniqueTargetPath(item.NewPath, claimed);

                item.NewPath = resolved;
                item.MarkAutoRename(string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.RenameBatchDuplicateAutoRenameFormat,
                    Path.GetFileName(resolved)));
            }
        }

        /// <summary>这一条会不会真的执行（与执行期 <c>RenamePlan.ShouldExecute</c> 同一口径）。</summary>
        private static bool WillExecute(RenamePreviewItem? item)
        {
            return item != null
                && item.IsSelected
                && !string.IsNullOrWhiteSpace(item.OriginalPath)
                && !string.IsNullOrWhiteSpace(item.NewPath)
                && !string.Equals(item.Status, StatusText.RenameCannot, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(item.Status, StatusText.RenameWillSkip, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 取一个"本批次还没被占、磁盘上也没有"的名字：<c>名字(1).ext</c>、<c>名字(2).ext</c>…
        /// 编号风格与 <see cref="SafePathHelper.AutoRenameFilePath"/> 一致（用户看到的还是同一套）。
        /// </summary>
        private static string ResolveBatchUniqueTargetPath(string desiredPath, ISet<string> claimed)
        {
            if (string.IsNullOrWhiteSpace(desiredPath))
            {
                return desiredPath;
            }

            string? directory = Path.GetDirectoryName(desiredPath);
            string fileName = Path.GetFileNameWithoutExtension(desiredPath);
            string extension = Path.GetExtension(desiredPath);

            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
            {
                return SafePathHelper.AutoRenameFilePath(desiredPath);
            }

            for (int i = 1; i < 10000; i++)
            {
                string candidate = Path.Combine(directory, $"{fileName}({i}){extension}");

                if (claimed.Contains(candidate))
                {
                    continue;
                }

                if (File.Exists(candidate) || Directory.Exists(candidate))
                {
                    continue;
                }

                return candidate;
            }

            return SafePathHelper.AutoRenameFilePath(desiredPath);
        }

        public string BuildNewPath(ArchiveTask task, RenameOptions options)
        {
            if (task == null)
            {
                return string.Empty;
            }

            options ??= new RenameOptions();
            options.Normalize();

            string oldPath = task.CurrentPath;

            if (string.IsNullOrWhiteSpace(oldPath))
            {
                oldPath = task.OriginalPath;
            }

            if (string.IsNullOrWhiteSpace(oldPath))
            {
                return string.Empty;
            }

            string directory = Path.GetDirectoryName(oldPath) ?? string.Empty;
            string fileName = Path.GetFileName(oldPath);

            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
            {
                return string.Empty;
            }

            string newFileName;

            /*
             * 分卷文件只允许**一种**改名：「名字被改坏的第一卷」被修回标准名
             * （那一条只走下面的 FixByDetectedFormat 分支，判据是 VolumeNameRepair.Plan）。
             * 「添加 / 替换最后一个 / 删除最后一个 / 删除多个后缀」这四种对分卷一律**原样返回** ——
             * 7z / WinRAR 只认 .001 这一套命名，改一下整组就再也解不开
             * （以前这道闸门只长在 FixByDetectedFormat 里，2026-09-26 审计补全）。
             */
            if (FileNameHelper.IsVolumePartFileName(fileName) &&
                !string.Equals(options.OperationType, "FixByDetectedFormat", StringComparison.OrdinalIgnoreCase))
            {
                return oldPath;
            }

            switch (options.OperationType)
            {
                case "AddExtension":
                    newFileName = BuildAddExtensionFileName(fileName, options.TargetExtension);
                    break;

                case "ReplaceLastExtension":
                    newFileName = BuildReplaceLastExtensionFileName(fileName, options.TargetExtension);
                    break;

                case "DeleteLastExtension":
                    newFileName = BuildDeleteExtensionFileName(fileName, 1);
                    break;

                case "FixByDetectedFormat":
                    newFileName = BuildFixByDetectedFormatFileName(task, options);
                    break;

                default:
                    newFileName = BuildFixByDetectedFormatFileName(task, options);
                    break;
            }

            if (string.IsNullOrWhiteSpace(newFileName))
            {
                return oldPath;
            }

            return Path.Combine(directory, newFileName);
        }

        /// <summary>
        /// 把用户在预览里为某一条选定的冲突处理方式落到 <see cref="RenamePreviewItem.NewPath"/> 上。
        ///
        /// <para>
        /// 「询问」档（<see cref="AppSettings.ConflictAction"/> = <c>Ask</c>）在改名路径上的落地就是它：
        /// 预览把撞名的条目标成 <see cref="RenamePreviewItem.NeedsConflictChoice"/>，
        /// 用户在预览表的「冲突处理」列里逐条选；预览窗口关掉之后由
        /// <c>RenameCoordinator</c> 对每一条调用本方法，把结论写死到 NewPath 上 ——
        /// 于是预览里显示的那个路径**就是**执行时会落的那个路径（两边不会各判一次）。
        /// </para>
        /// <para>
        /// ⛔ 不变量 3：**没选 / 选不出来一律落到"自动重命名"，绝不覆盖**。
        /// 用户直接关掉预览窗口时走的也是这条 —— 内容不丢、已有文件一个字节不动。
        /// </para>
        /// </summary>
        /// <returns>真的改写了落点返回 true（用于日志里说清"这一条按你选的办了"）。</returns>
        public bool ApplyConflictChoice(RenamePreviewItem? item)
        {
            if (item == null || !item.NeedsConflictChoice)
            {
                return false;
            }

            string choice = string.IsNullOrWhiteSpace(item.ConflictChoice)
                ? "AutoRename"
                : item.ConflictChoice.Trim();

            // 判定表只有一处实现（PathService.ResolveConflict），这里不另写一套 switch。
            var pathService = new PathService();
            ConflictResolution resolution = pathService.ResolveConflict(
                item.NewPath,
                ConflictTargetKind.File,
                ConflictActions.Ask,
                ConflictDecision.Once(choice switch
                {
                    "Overwrite" => ConflictChoice.Overwrite,
                    "Skip" => ConflictChoice.Skip,
                    _ => ConflictChoice.AutoRename
                }));

            if (resolution.Choice == ConflictChoice.Overwrite)
            {
                // 覆盖：落点不变，执行阶段走"先移到临时名 → 落位 → 再删"两阶段（不变量 3）。
                item.Status = StatusText.TargetExists;
                item.ErrorMessage = "按你的选择：覆盖目标文件（先移开旧文件再落位，中途失败不丢文件）";
                item.NeedsConflictChoice = false;
                return true;
            }

            if (resolution.Choice == ConflictChoice.Skip)
            {
                item.MarkSkip("按你的选择：跳过（目标文件已存在，一个字节都不动）");
                item.NeedsConflictChoice = false;
                return true;
            }

            // 兜底（含"没选"）：自动重命名 —— 绝不覆盖。
            item.NewPath = string.IsNullOrWhiteSpace(resolution.TargetPath)
                ? AutoRenamePath(item.NewPath)
                : resolution.TargetPath;

            item.MarkAutoRename(
                string.IsNullOrWhiteSpace(item.ConflictChoice)
                    ? "你没选，按保守档自动重命名（绝不覆盖）"
                    : "按你的选择：自动重命名");

            item.NeedsConflictChoice = false;
            return true;
        }

        public async Task ExecuteRenameAsync(
            IEnumerable<RenamePreviewItem> previewItems,
            IEnumerable<ArchiveTask> tasks)
        {
            await Task.Yield();

            if (previewItems == null)
            {
                return;
            }

            List<RenamePreviewItem> itemList = previewItems.ToList();
            List<ArchiveTask> taskList = tasks?.ToList() ?? new List<ArchiveTask>();

            /*
             * 一次改名请求 = 一个批次，必须先定计划再一起动手。
             *
             * 为什么不能"一项一项来"（不变量 3）：名称交换 A.zip ↔ B.zip 天生是两项之间的关系，
             * 逐项处理时无论谁先动，都会先把对方当成"目标已存在"而删掉它 —— 那正是丢文件的写法。
             * 只有把整批的落点先算清、一起撤到临时名、再一起落位，交换才可能成立。
             *
             * 计划里存的是**源路径 → 最终落点**：执行与结果回填都查它，不会两处各判一次。
             */
            RenamePlan plan = RenamePlan.Build(itemList);
            RenameExecution execution = RenameExecution.Run(plan);

            foreach (RenamePreviewItem item in itemList)
            {
                if (item == null || !plan.ShouldExecute(item))
                {
                    continue;
                }

                ArchiveTask? matchedTask = FindTaskByPath(taskList, item.OriginalPath);

                string sourcePath = item.OriginalPath;
                string finalPath = plan.GetFinalPath(sourcePath);
                string failure = execution.GetFailure(sourcePath);

                if (!string.IsNullOrWhiteSpace(failure) || string.IsNullOrWhiteSpace(finalPath))
                {
                    item.Status = StatusText.RenameCannot;
                    item.ErrorMessage = string.IsNullOrWhiteSpace(failure)
                        ? "改名未执行（目标路径为空）"
                        : failure;
                    UpdateTaskRenameFailed(matchedTask, item.ErrorMessage);
                    continue;
                }

                item.NewPath = finalPath;
                item.NewFileName = Path.GetFileName(finalPath);

                if (plan.IsSkipped(sourcePath))
                {
                    item.Status = StatusText.RenameWillSkip;
                    item.ErrorMessage = plan.GetSkipReason(sourcePath);
                    UpdateTaskSkipped(matchedTask, item.ErrorMessage);
                    continue;
                }

                item.Status = StatusText.RenameSuccess;
                item.ErrorMessage = string.Empty;

                try
                {
                    UpdateTaskRenameSuccess(matchedTask, finalPath);
                }
                catch
                {
                    // 这里故意吞掉异常。
                    // 原因：文件已经改名成功，不能因为 UI/任务对象更新异常再弹“改名失败”。
                    // 后续重新扫描时会以文件系统真实路径为准。
                }
            }
        }

        /// <summary>
        /// 一次改名批次的落点计划：谁要动、各自动到哪个路径、谁不用动。
        /// 只回答"落到哪"，除读一次"目标是否存在"外不碰文件系统。
        /// </summary>
        private sealed class RenamePlan
        {
            private readonly List<RenamePreviewItem> _items;
            private readonly Dictionary<string, string> _finalPaths = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, string> _skipReasons = new(StringComparer.OrdinalIgnoreCase);

            private RenamePlan(List<RenamePreviewItem> items)
            {
                _items = items;
            }

            /// <summary>源路径 → 最终落点。执行阶段与结果回填都查它。</summary>
            public IReadOnlyDictionary<string, string> FinalPaths => _finalPaths;

            public static RenamePlan Build(List<RenamePreviewItem> items)
            {
                var plan = new RenamePlan(items);

                plan.PlanCore();

                return plan;
            }

            public bool ShouldExecute(RenamePreviewItem? item)
            {
                return item != null
                    && item.IsSelected
                    && !string.IsNullOrWhiteSpace(item.OriginalPath)
                    && !string.IsNullOrWhiteSpace(item.NewPath)
                    && !string.Equals(item.Status, StatusText.RenameCannot, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(item.Status, StatusText.RenameWillSkip, StringComparison.OrdinalIgnoreCase);
            }

            public string GetFinalPath(string sourcePath) =>
                _finalPaths.TryGetValue(sourcePath, out string? value) ? value : string.Empty;

            public bool IsSkipped(string sourcePath) => _skipReasons.ContainsKey(sourcePath);

            public string GetSkipReason(string sourcePath) =>
                _skipReasons.TryGetValue(sourcePath, out string? value) ? value : string.Empty;

            /// <summary>
            /// 定落点。顺序有意为之：先把"谁要占哪个名字"整批登记完，再逐个看冲突 ——
            /// 因为 <see cref="File.Exists(string)"/> 问的是磁盘现状，而本批次会先把名字腾出来，
            /// 只有计划表才知道"这个名字其实马上就是空的"（名称交换就靠它）。
            /// </summary>
            private void PlanCore()
            {
                foreach (RenamePreviewItem item in _items)
                {
                    if (!ShouldExecute(item))
                    {
                        continue;
                    }

                    _finalPaths[item.OriginalPath] = item.NewPath;
                }

                foreach (KeyValuePair<string, string> pair in _finalPaths.ToList())
                {
                    string source = pair.Key;
                    string destination = pair.Value;

                    // 这个名字上是本批次另一个"要腾出去"的源 → 不是冲突，交给两阶段。
                    if (_finalPaths.TryGetValue(destination, out string? occupant) &&
                        !string.Equals(occupant, source, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!File.Exists(destination))
                    {
                        continue;
                    }

                    /*
                     * 预览期已经判定"目标是别人的文件、且要覆盖"的项：最终落点就是预览给的那个路径，
                     * 执行时不再问一次文件系统。
                     *
                     * 为什么：本批次前面的项可能已经把这个名字腾出来了（名称交换），
                     * 这时 File.Exists 会说"不存在"，这一项就会退化成普通移动 ——
                     * 而它本该顶掉的那个文件正躺在临时名下，落点判断与计划对不上。
                     */
                    RenamePreviewItem? owner = Find(source);

                    if (string.Equals(owner?.Status, StatusText.TargetExists, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string conflictAction = string.IsNullOrWhiteSpace(owner?.ConflictAction)
                        ? "AutoRename"
                        : owner!.ConflictAction;

                    if (string.Equals(conflictAction, "Skip", StringComparison.OrdinalIgnoreCase))
                    {
                        _finalPaths.Remove(source);
                        _skipReasons[source] = "目标文件已存在";
                        continue;
                    }

                    if (string.Equals(conflictAction, "Overwrite", StringComparison.OrdinalIgnoreCase))
                    {
                        // 明确要覆盖：旧文件会在"腾名字"阶段被挪走，落位成功后才删。
                        continue;
                    }

                    // AutoRename / Ask：执行前按当前文件系统重算一次落地名。
                    _finalPaths[source] = SafePathHelper.AutoRenameFilePath(destination);
                }
            }

            private RenamePreviewItem? Find(string source)
            {
                return _items.FirstOrDefault(x =>
                    x != null && string.Equals(x.OriginalPath, source, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>一次批次执行的结果：总体成败 + 每条源路径各自的失败原因。</summary>
        private sealed class RenameExecution
        {
            private readonly Dictionary<string, string> _failures = new(StringComparer.OrdinalIgnoreCase);

            public string GetFailure(string sourcePath) =>
                _failures.TryGetValue(sourcePath, out string? value) ? value : string.Empty;

            private static RenameExecution Failed(string message, IEnumerable<string> sources)
            {
                var execution = new RenameExecution();

                foreach (string source in sources)
                {
                    execution._failures[source] = message;
                }

                return execution;
            }

            /// <summary>
            /// 执行改名：**整批先撤到临时名，再一起落位**（不变量 3）。
            ///
            /// 顺序不能颠倒，也不能逐项做：
            /// 1. 撤名字：把本批次每一个源文件都改成临时名（同一目录内的改名，原子且便宜）；
            /// 2. 腾位（少见）：落点上还留着别人的文件（就是要被覆盖的那个）时，把它也挪成临时名；
            /// 3. 落位：临时名 → 最终名。到这一步所有名字都是空的，<c>File.Move</c> 不会撞名。
            ///
            /// 为什么"逐项做"是错的：名称交换 <c>A.zip ↔ B.zip</c> 里，处理到第二项时第一项已经
            /// 落到了它的目标名上，于是第二项找不到自己的源文件（它已经被挪走了）——
            /// 结果是交换做了一半、还留下一个临时文件。
            /// 为什么不能"先 <c>File.Delete(目标)</c> 再 <c>File.Move(源, 目标)</c>"（旧写法）：
            /// 中间任何失败（跨卷、权限、被占用）都会让**被覆盖的那个文件永久消失**，
            /// 而源文件还在原处 —— 用户丢的正是他本来想保留的那一份。
            ///
            /// 任何一步失败都整体回滚：结果只可能是"全是旧名字"或"全是新名字"，绝不会少文件。
            /// </summary>
            public static RenameExecution Run(RenamePlan plan)
            {
                List<string> sources = plan.FinalPaths
                    .Where(pair => !plan.IsSkipped(pair.Key))
                    .Select(pair => pair.Key)
                    .ToList();

                var staged = new List<(string OriginalPath, string TemporaryPath, string FinalPath)>();

                // ── 阶段 1：撤名字 ──────────────────────────────────────────────
                foreach (string source in sources)
                {
                    string finalPath = plan.GetFinalPath(source);

                    string? directory = Path.GetDirectoryName(finalPath);

                    if (string.IsNullOrWhiteSpace(directory))
                    {
                        return Failed($"无法确定目标目录（{finalPath}），本次没有文件被改动", sources);
                    }

                    if (!Directory.Exists(directory))
                    {
                        return Failed($"目标目录不存在（{directory}），本次没有文件被改动", sources);
                    }

                    if (!File.Exists(source))
                    {
                        return Failed("源文件不存在", new[] { source });
                    }

                    if (SafePathHelper.PathEquals(source, finalPath))
                    {
                        continue;
                    }

                    string temporaryPath;

                    try
                    {
                        temporaryPath = BuildStagingPath(finalPath);

                        File.Move(source, temporaryPath);
                    }
                    catch (Exception ex)
                    {
                        RollBackStaged(staged);

                        return Failed(
                            $"改名失败（已回滚，所有文件保持原名）：撤走 {Path.GetFileName(source)} 时出错 —— {ex.Message}",
                            sources);
                    }

                    staged.Add((source, temporaryPath, finalPath));
                }

                // ── 阶段 2：腾位 ────────────────────────────────────────────────
                var vacated = new List<(string OccupiedPath, string TemporaryPath)>();

                foreach ((string _, string _, string finalPath) in staged)
                {
                    if (!File.Exists(finalPath))
                    {
                        continue;
                    }

                    string temporaryPath;

                    try
                    {
                        temporaryPath = BuildVacatingPath(finalPath);

                        // 占着这个名字的是外来的文件（要覆盖的目标）：挪成临时名，落位成功后才删。
                        File.Move(finalPath, temporaryPath);
                    }
                    catch (Exception ex)
                    {
                        RollBackStaged(staged);
                        RollBackVacated(vacated);

                        return Failed(
                            $"无法为 {Path.GetFileName(finalPath)} 腾出名字（已回滚，所有文件保持原名）：{ex.Message}",
                            sources);
                    }

                    vacated.Add((finalPath, temporaryPath));
                }

                // ── 阶段 3：一起落位 ────────────────────────────────────────────
                var landed = new List<(string TemporaryPath, string FinalPath)>();

                foreach ((string _, string temporaryPath, string finalPath) in staged)
                {
                    try
                    {
                        // 两参数 File.Move：目标已存在会直接抛异常，绝不静默覆盖。
                        File.Move(temporaryPath, finalPath);
                        landed.Add((temporaryPath, finalPath));
                    }
                    catch (Exception ex)
                    {
                        RollBackLanded(landed);
                        RollBackStaged(staged);
                        RollBackVacated(vacated);

                        return Failed(
                            $"改名失败（已回滚，所有文件保持原名）：落位 {Path.GetFileName(finalPath)} 时出错 —— {ex.Message}",
                            sources);
                    }
                }

                // ── 阶段 4：清场 ────────────────────────────────────────────────
                foreach ((string _, string temporaryPath) in vacated)
                {
                    TryDelete(temporaryPath);
                }

                return new RenameExecution();
            }

            /// <summary>
            /// 临时（staging）路径：跟**最终落点**同一个目录。
            ///
            /// 为什么必须在同一个目录：换了目录就可能是换卷，跨卷的 <c>File.Move</c> 不再是原子操作，
            /// 半途失败会留下半个文件 —— 那就正好违反我们要守的那条不变量。
            /// </summary>
            private static string BuildStagingPath(string finalPath)
            {
                string directory = Path.GetDirectoryName(finalPath) ?? string.Empty;
                string fileName = Path.GetFileNameWithoutExtension(finalPath);
                string extension = Path.GetExtension(finalPath);

                for (int i = 0; i < 10000; i++)
                {
                    string candidate = i == 0
                        ? Path.Combine(directory, fileName + ".af-staging" + extension)
                        : Path.Combine(directory, $"{fileName}.af-staging{i}{extension}");

                    if (!File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                return Path.Combine(directory, $"{fileName}.af-staging-{Guid.NewGuid():N}{extension}");
            }

            /// <summary>腾名字用的临时路径：一定还没被占用（占用就退到带序号的候选）。</summary>
            private static string BuildVacatingPath(string path)
            {
                string directory = Path.GetDirectoryName(path) ?? string.Empty;
                string fileName = Path.GetFileNameWithoutExtension(path);
                string extension = Path.GetExtension(path);

                for (int i = 0; i < 10000; i++)
                {
                    string candidate = i == 0
                        ? Path.Combine(directory, fileName + ".af-vacating" + extension)
                        : Path.Combine(directory, $"{fileName}.af-vacating{i}{extension}");

                    if (!File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                return Path.Combine(directory, $"{fileName}.af-vacating-{Guid.NewGuid():N}{extension}");
            }

            /// <summary>回滚"撤名字"：临时名挪回源路径。挪不动时保留临时文件（名字带 .af-staging，数据不丢）。</summary>
            private static void RollBackStaged(
                List<(string OriginalPath, string TemporaryPath, string FinalPath)> staged)
            {
                for (int i = staged.Count - 1; i >= 0; i--)
                {
                    TryMove(staged[i].TemporaryPath, staged[i].OriginalPath);
                }
            }

            /// <summary>回滚"腾位"：临时名挪回它占着的那个名字。</summary>
            private static void RollBackVacated(List<(string OccupiedPath, string TemporaryPath)> vacated)
            {
                for (int i = vacated.Count - 1; i >= 0; i--)
                {
                    TryMove(vacated[i].TemporaryPath, vacated[i].OccupiedPath);
                }
            }

            /// <summary>回滚"落位"：已经落到最终名的文件挪回临时名（随后由 RollBackStaged 送回源路径）。</summary>
            private static void RollBackLanded(List<(string TemporaryPath, string FinalPath)> landed)
            {
                for (int i = landed.Count - 1; i >= 0; i--)
                {
                    TryMove(landed[i].FinalPath, landed[i].TemporaryPath);
                }
            }

            private static void TryMove(string from, string to)
            {
                try
                {
                    if (File.Exists(from) && !File.Exists(to))
                    {
                        File.Move(from, to);
                    }
                }
                catch
                {
                    // 回滚本身失败只能到此为止：调用方会把失败原因如实报给用户，
                    // 数据不会丢 —— 文件就在原目录里，名字里带 .af-vacating。
                }
            }

            private static void TryDelete(string path)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                    // 删不掉被覆盖的旧文件不影响"改名成功"这个结论，留给用户自己处理。
                }
            }
        }

        public string AutoRenamePath(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return string.Empty;
            }

            if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
            {
                return targetPath;
            }

            string? directory = Path.GetDirectoryName(targetPath);
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(targetPath);
            string extension = Path.GetExtension(targetPath);

            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = Environment.CurrentDirectory;
            }

            for (int i = 1; i <= 9999; i++)
            {
                string candidate = Path.Combine(
                    directory,
                    $"{fileNameWithoutExtension}({i}){extension}");

                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            string fallback = Path.Combine(
                directory,
                $"{fileNameWithoutExtension}_{DateTime.Now:yyyyMMddHHmmssfff}{extension}");

            return fallback;
        }

        private static string BuildAddExtensionFileName(string fileName, string extension)
        {
            extension = NormalizeExtension(extension, ".7z");

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return string.Empty;
            }

            return fileName + extension;
        }

        private static string BuildReplaceLastExtensionFileName(string fileName, string extension)
        {
            extension = NormalizeExtension(extension, ".7z");

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return string.Empty;
            }

            string currentExtension = Path.GetExtension(fileName);

            if (string.IsNullOrWhiteSpace(currentExtension))
            {
                return fileName + extension;
            }

            string baseName = fileName[..^currentExtension.Length];

            if (string.IsNullOrWhiteSpace(baseName))
            {
                return fileName;
            }

            return baseName + extension;
        }

        private static string BuildDeleteExtensionFileName(string fileName, int deleteCount)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return string.Empty;
            }

            if (deleteCount < 1)
            {
                deleteCount = 1;
            }

            string result = fileName;

            for (int i = 0; i < deleteCount; i++)
            {
                string extension = Path.GetExtension(result);

                if (string.IsNullOrWhiteSpace(extension))
                {
                    break;
                }

                result = result[..^extension.Length];

                if (string.IsNullOrWhiteSpace(result))
                {
                    return fileName;
                }
            }

            return result;
        }

        private static string BuildFixByDetectedFormatFileName(ArchiveTask task, RenameOptions options)
        {
            string oldPath = task.CurrentPath;

            if (string.IsNullOrWhiteSpace(oldPath))
            {
                oldPath = task.OriginalPath;
            }

            string fileName = Path.GetFileName(oldPath);

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return string.Empty;
            }

            /*
             * 分卷文件：先看"名字被改坏的第一卷"能不能靠这一颗按钮修好（2026-09-26 审计：入口收敛）。
             *
             * 判据仍然是 VolumeNameRepair.Plan 那六条（与失败清单里写的建议、与①页「修复分卷名并重试」
             * 真正会改成的名字**同一份计算**）—— 能修就给标准名；修不了（是后续卷 / 没有同组卷 /
             * 目标名被占 / 名字本来就标准…）才落到下面那句"分卷文件不改名"。
             *
             * 以前这条路是断的：智能修正对分卷一律原样返回，于是"名字被改坏的第一卷"只能靠
             * 另一颗按钮（而且那颗按钮要先跑一次失败的解压才亮）—— 用户看到的就是"两颗按钮意义一样，
             * 可第一颗从来没亮过"。
             */
            if (FileNameHelper.IsVolumePartFileName(fileName))
            {
                VolumeNameRepairPlan volumeRepair = VolumeNameRepair.Plan(
                    oldPath,
                    VolumeNameRepair.EnumerateFileNamesInDirectory(oldPath));

                return volumeRepair.CanRepair ? volumeRepair.SuggestedFileName : fileName;
            }

            /*
             * 后缀本来就正常 → 没有可修正的东西，原样返回。
             *
             * 光比"当前后缀 == 建议后缀"是不够的：`xxx.apk` 的内容也是 ZIP，建议后缀是 .zip，
             * 按那个比法它就会被改成 `xxx.zip` —— 把 Android 安装包变成一个打不开的压缩包。
             * 同一类还有 .jar / .docx / .xlsx / .pptx / .epub / .vsix / .nupkg …（ExtensionHelper.ZipContainerExtensions）。
             * 判定统一由 ArchiveDetectService 给（后缀正常 / 分卷后缀 / 内嵌归档 / 不匹配 / …），这里只认结论，不自己再推一遍。
             */
            if (task.ExtensionStatus == StatusText.ExtensionNormal ||
                task.ExtensionStatus == StatusText.ExtensionVolume ||
                task.ExtensionStatus == StatusText.ExtensionEmbedded)
            {
                return fileName;
            }

            string suggestedExtension = task.SuggestedExtension;

            if (string.IsNullOrWhiteSpace(suggestedExtension))
            {
                suggestedExtension = GetSuggestedExtensionByFormat(task.DetectedFormat);
            }

            suggestedExtension = NormalizeExtension(suggestedExtension, options.TargetExtension);

            if (string.IsNullOrWhiteSpace(task.DetectedFormat) ||
                string.Equals(task.DetectedFormat, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(options.UnknownFormatAction, "Skip", StringComparison.OrdinalIgnoreCase))
                {
                    return fileName;
                }

                suggestedExtension = NormalizeExtension(options.TargetExtension, ".7z");
            }

            string currentExtension = Path.GetExtension(fileName);

            if (string.IsNullOrWhiteSpace(currentExtension))
            {
                return fileName + suggestedExtension;
            }

            if (string.Equals(currentExtension, suggestedExtension, StringComparison.OrdinalIgnoreCase))
            {
                return fileName;
            }

            string fixedMultiExtensionName = TryFixMultiExtensionFileName(fileName, suggestedExtension);

            if (!string.IsNullOrWhiteSpace(fixedMultiExtensionName))
            {
                return fixedMultiExtensionName;
            }

            string baseName = fileName[..^currentExtension.Length];

            if (string.IsNullOrWhiteSpace(baseName))
            {
                return fileName;
            }

            return baseName + suggestedExtension;
        }

        private static string TryFixMultiExtensionFileName(string fileName, string suggestedExtension)
        {
            if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(suggestedExtension))
            {
                return string.Empty;
            }

            suggestedExtension = NormalizeExtension(suggestedExtension, ".7z");

            List<string> extensions = GetAllExtensions(fileName);

            if (extensions.Count < 2)
            {
                return string.Empty;
            }

            int matchedIndex = -1;

            for (int i = 0; i < extensions.Count; i++)
            {
                if (string.Equals(extensions[i], suggestedExtension, StringComparison.OrdinalIgnoreCase))
                {
                    matchedIndex = i;
                }
            }

            if (matchedIndex < 0)
            {
                return string.Empty;
            }

            string namePart = fileName;

            int totalRemoveLength = 0;

            for (int i = extensions.Count - 1; i > matchedIndex; i--)
            {
                totalRemoveLength += extensions[i].Length;
            }

            if (totalRemoveLength <= 0 || totalRemoveLength >= fileName.Length)
            {
                return string.Empty;
            }

            namePart = fileName[..^totalRemoveLength];

            if (string.IsNullOrWhiteSpace(namePart))
            {
                return string.Empty;
            }

            return namePart;
        }

        private static List<string> GetAllExtensions(string fileName)
        {
            List<string> result = new();

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return result;
            }

            string[] parts = fileName.Split('.', StringSplitOptions.None);

            if (parts.Length <= 1)
            {
                return result;
            }

            for (int i = 1; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i]))
                {
                    continue;
                }

                result.Add("." + parts[i]);
            }

            return result;
        }

        private static string GetSuggestedExtensionByFormat(string format)
        {
            return ExtensionHelper.GetSuggestedExtensionByFormat(format);
        }

        private static string NormalizeExtension(string? extension, string fallback)
        {
            string value = string.IsNullOrWhiteSpace(extension)
                ? fallback
                : extension.Trim();

            if (string.IsNullOrWhiteSpace(value))
            {
                value = ".7z";
            }

            if (!value.StartsWith(".", StringComparison.Ordinal))
            {
                value = "." + value;
            }

            return value;
        }

        /// <summary>
        /// 预览表「操作」列的文字。
        ///
        /// <para>「智能修正」这一类把**检测格式**并进来（<c>按真实格式修正（ZIP）</c>）——
        /// 它以前是单独一列「检测格式」，可 5 种操作里只有这一种用得上它，
        /// 白占 78px 而名字列被挤到看不见后缀（2026-09-26 审计）。</para>
        /// </summary>
        private static string BuildOperationLabel(RenameOptions options, string? detectedFormat)
        {
            string name = GetOperationDisplayName(options);

            if (!string.Equals(options?.OperationType, "FixByDetectedFormat", StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }

            return string.IsNullOrWhiteSpace(detectedFormat) ||
                   string.Equals(detectedFormat, "Unknown", StringComparison.OrdinalIgnoreCase)
                ? name
                : $"{name}（{detectedFormat}）";
        }

        private static string GetOperationDisplayName(RenameOptions options)
        {
            if (options == null)
            {
                return "按真实格式修正";
            }

            return options.OperationType switch
            {
                "AddExtension" => "添加后缀",
                "ReplaceLastExtension" => "替换最后后缀",
                "DeleteLastExtension" => "删除最后后缀",
                "FixByDetectedFormat" => "按真实格式修正",
                _ => options.OperationType
            };
        }

        /// <summary>
        /// 按路径在任务表里找任务（改名执行与"只重扫被改过名的那些"共用这一份匹配口径）。
        ///
        /// <para>三个字段都要比：执行期会把 <c>CurrentPath</c> 换到新名字上，而
        /// <c>OriginalPath</c> / <c>RenamePreviewPath</c> 还留着旧路径 —— 少比一个就会漏掉任务，
        /// 于是"改名成功但列表里那一行还指着旧文件"。</para>
        /// </summary>
        public static ArchiveTask? FindTaskByPath(IEnumerable<ArchiveTask> tasks, string path)
        {
            if (tasks == null || string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return tasks.FirstOrDefault(x =>
                string.Equals(x.CurrentPath, path, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.OriginalPath, path, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.RenamePreviewPath, path, StringComparison.OrdinalIgnoreCase));
        }

        private static void UpdateTaskRenameSuccess(ArchiveTask? task, string newPath)
        {
            if (task == null)
            {
                return;
            }

            task.CurrentPath = newPath;
            task.RenamePreviewPath = string.Empty;
            task.FileName = Path.GetFileName(newPath);
            task.DirectoryPath = Path.GetDirectoryName(newPath) ?? string.Empty;
            task.CurrentExtension = string.IsNullOrWhiteSpace(Path.GetExtension(newPath))
                ? "无"
                : Path.GetExtension(newPath);

            task.Operation = StatusText.OpRename;
            task.Status = StatusText.RenameSuccess;
            task.ErrorMessage = string.Empty;
            task.LastUpdatedTime = DateTime.Now;
        }

        private static void UpdateTaskRenameFailed(ArchiveTask? task, string errorMessage)
        {
            if (task == null)
            {
                return;
            }

            task.Operation = StatusText.OpRename;
            task.Status = StatusText.RenameFailed;
            task.ErrorMessage = errorMessage ?? string.Empty;
            task.LastUpdatedTime = DateTime.Now;
        }

        private static void UpdateTaskSkipped(ArchiveTask? task, string message)
        {
            if (task == null)
            {
                return;
            }

            task.Operation = StatusText.OpSkip;
            task.Status = StatusText.Skipped;
            task.ErrorMessage = message ?? string.Empty;
            task.LastUpdatedTime = DateTime.Now;
        }
    }
}
