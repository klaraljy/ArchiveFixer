using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Services
{
    /// <summary>工作区这一步要问宿主的事（只用非 WPF 类型 —— Extraction 层不许引用 ViewModels）。</summary>
    internal interface IWorkspaceStepHost
    {
        PathService PathService { get; }

        AppSettings? Settings { get; }

        OneClickRunOptions? RunOptions { get; }

        ExtractOptions BuildExtractOptions(bool tryExtractUnknownFormat);

        bool IsUnresolvedVolumeGroupMember(ArchiveTask task);

        /// <summary>"这个名字是不是我们自己造的工作区子目录"（判据唯一出口在协调器那一个静态函数）。</summary>
        Func<string, bool> IsOurWorkspaceSubdirectory { get; }

        void AppendLog(string level, string message);

        /// <summary>把这一单的递归器从账本上摘下来（摘不到 ⇒ null）。</summary>
        RecursiveExtractor? TakeTaskRecursiveExtractor(ArchiveTask task);
    }

    /// <summary>
    /// 工作区根 / 工作区清理（把原来 <c>ExtractionCoordinator.WorkspaceStep.cs</c> 的实现搬成独立类）。
    /// 协调器只留一行调用，宿主能力走 <see cref="IWorkspaceStepHost"/>。
    /// </summary>
    internal sealed class WorkspaceStep
    {
        private readonly IWorkspaceStepHost _host;

        internal WorkspaceStep(IWorkspaceStepHost host)
        {
            _host = host;
        }
        public WorkspaceRootResolution ApplyBatchWorkspaceRoot(IReadOnlyList<ArchiveTask> tasks)
        {
            ExtractOptions options = _host.BuildExtractOptions(tryExtractUnknownFormat: false);

            // 本次选项面板选出来的落点也要算进去 —— 不然"这批落在 D 盘"会被算成设置里那个盘。
            _host.RunOptions?.ApplyTo(options);

            var destinations = new List<string>();
            var unresolvedGroupDestinations = new List<string>();

            foreach (ArchiveTask task in tasks ?? Array.Empty<ArchiveTask>())
            {
                if (task == null)
                {
                    continue;
                }

                OutputPlacementResult placement = _host.PathService.ResolveOutputPlacement(task, options);

                if (!placement.Success || string.IsNullOrWhiteSpace(placement.DestinationDirectory))
                {
                    continue;
                }

                /*
                 * ===== 工作区根**不许**由"这一组的入口包还不在盘上"那一单来定 =====
                 *
                 * 真机 `CCCC`（2026-10-06，用户当场问「批的工作区根/落点还是按任务表里这一组第一单选的，
                 * 你到底改没改」）：表里第一单是散在 `111(4)` 的 `111.z0删除3`，它那一组的**入口包
                 * `111.zip` 正压在 `111.rar` 里、还没被解出来** ⇒ 落点退回旧口径（源包自己那一层 +
                 * 包名）⇒ `111(4)\111`。工作区根取"第一个算得出落点的目标目录"，于是整批的中间产物
                 * 全落在 `CCCC\111(4)\111\.ArchiveFixer.work` —— 用户目录里凭空多出这一层，
                 * 而真正该落的地方（`111.rar` 自己那条链的落点 `CCCC\111\111`）一个字节都没用上。
                 *
                 * ⇒ 判据只有一条：**这一单的入口包现在在不在盘上**（唯一出口
                 * `Detection.GroupVolumeDirectory.Resolve`，与落点实现读的是同一个事实位）。
                 * 不在 ⇒ 这一单的"目标目录"只是个占位值，排到最后当兜底；等入口包被解出来之后，
                 * 它的落点会由 `ResolveBatchGroupDirectory` 收敛到产出方那一条链上（与工作区根同一个答案）。
                 *
                 * ⛔ 这一档**只影响"挑哪个目录当工作区根"**：落点实现、发布、收卷判据一个字都不改。
                 * ⛔ 一句都不删：实在只有这一种任务时，兜底照旧用它的目录（绝不因为挑不出根就整批停手）。
                 */
                if (_host.IsUnresolvedVolumeGroupMember(task))
                {
                    unresolvedGroupDestinations.Add(placement.DestinationDirectory);
                    continue;
                }

                destinations.Add(placement.DestinationDirectory);
            }

            if (unresolvedGroupDestinations.Count > 0)
            {
                _host.AppendLog(
                    "INFO",
                    $"本批有 {unresolvedGroupDestinations.Count} 单是分卷组的成员、而这一组的入口包还没被解出来"
                    + "（现在挑不出它的落点）—— 工作区根改用别的单来定；等入口包解出来之后，"
                    + "这一组的产物照样落在入口包所在的那一层。");

                destinations.AddRange(unresolvedGroupDestinations);
            }

            /*
             * ===== 上一次跑到一半被中断留下的空壳：**这一次跑的时候顺手收掉**（用户 2026-09-30 亲口点了"推荐"）=====
             *
             * 现场：正常跑完的收尾会连壳一起删（批尾那两步），只有**中断**这一档会残留 ——
             * 真机上 `H:\成果\1111\` 里就只剩一个空的 `.ArchiveFixer.work`，一个成品都没有，
             * 而用户翻自己目录时会看见这个凭空多出来的文件夹。
             *
             * 位置是**硬要求**：必须在 `WorkspaceRootResolver.Resolve` **之前**。
             * 那一句会按"解析之前它在不在"如实记下 TargetDirectoryCreated，并把目标目录与工作区都建出来 ——
             * 先建后收会把**刚建出来的工作区**当成残留删掉（顺序反了就是自伤）。
             *
             * ⛔ 只查**本批这些目标目录这一层**（destinations 就是唯一实现 ResolveOutputPlacement 算出来的落点），
             * 不递归、不看兄弟目录、更不做全盘扫描；判据一条都不在这里写（唯一出口
             * `Storage/WorkspaceLeftoverShellCleaner.ReclaimAll`：除了那个空工作区什么都没有才删）。
             */
            foreach (WorkspaceLeftoverShellOutcome leftover in WorkspaceLeftoverShellCleaner.ReclaimAll(destinations))
            {
                _host.AppendLog(leftover.LogLevel, leftover.Message);
            }

            WorkspaceRootResolution resolution = WorkspaceRootResolver.Resolve(
                destinations,
                WorkspaceDriveOverride);

            _host.AppendLog(resolution.LogLevel, resolution.Reason);

            if (!resolution.Resolved)
            {
                /*
                 * ⛔ 定不下来就**什么都不做**：不写 WorkDirectory（留空只会让后面拼出相对路径，
                 * 那是"写到一个没人知道的地方"的更坏版本）、不碰递归静态、不记账本。
                 * 停手由调用方做（它才知道怎么把这一批收干净）。
                 */
                return resolution;
            }

            _host.PathService.WorkDirectory = resolution.RootDirectory;

            /*
             * 记下"目标目录是不是我们建的"：工作区必须先有目标目录才能落在它里面，
             * 所以这一批必然会建出那一个目录壳；批尾它要是空的，就该由我们自己收掉
             * （见 RemoveEmptyTargetShellIfWeCreatedIt，用户 2026-09-30）。
             */
            _batchCreatedTargetDirectory = resolution.TargetDirectoryCreated
                ? resolution.TargetDirectory
                : string.Empty;

            // 递归核心的工作区根是进程级静态，且它自己会再挂一层 "recursive"（见 RecursiveExtractor）。
            RecursiveExtractor.ConfiguredWorkspaceRoot = resolution.RootDirectory;

            /*
             * 工作区与目标盘的关系**永远**是"同一棵树"（工作区就建在目标目录里面），
             * 所以这里不再有"工作区不在批目标盘上"那一档 —— 那个警告的前提（用户设过缓存根目录）
             * 已随设置项一起删除。跨盘剩下的是**批内部**的：各任务落点在不同盘上，那个由
             * WorkspaceRootResolver 的跨盘说明如实写出来。
             */

            WorkspaceRootIndex.Remember(resolution.RootDirectory);

            return resolution;
        }

        /// <summary>
        /// 收掉**空的工作区壳**（用户 2026-09-27：真机跑完 <c>…\.ArchiveFixer.work\recursive</c> 会留一个空壳）。
        /// </summary>
        public void RemoveEmptyWorkspaceShells()
        {
            try
            {
                string root = _host.PathService.WorkDirectory;

                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    return;
                }

                // ① 只要根下面还有一个文件，就什么都不动（宁可留壳，也不误删）。
                if (Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any())
                {
                    return;
                }

                foreach (string directory in Directory.EnumerateDirectories(root))
                {
                    try
                    {
                        // ② 只删直接子目录（`recursive` 这种父壳就在这一层）。
                        Directory.Delete(directory, recursive: true);
                        _host.AppendLog("INFO", $"顺手清掉了空的工作区壳：{directory}");
                    }
                    catch (Exception ex)
                    {
                        // ③ 收尾的顺手活：失败只写一句，绝不影响批结论。
                        _host.AppendLog("INFO", $"空的工作区壳没清掉（不影响结果）：{directory} —— {ex.Message}");
                    }
                }

                /*
                 * ④ 连 `<目标目录>\.ArchiveFixer.work` 这一层空壳一起收掉（用户 2026-09-30 明确要求：
                 * "整批结束后这一层空壳也要删掉，别留在用户目录里"）。
                 *
                 * 走到这里**已经确认整棵树里一个文件都没有**（① 那道闸门），所以这一删不可能碰到内容物、
                 * 也不可能碰到源包 —— 它删掉的只是一个空的隐藏目录。
                 * 删完若用户目录那一层也是空的，那说明这一批什么都没解出来，那个目录本来就该自己消失，
                 * 但**不越界去删目标目录本身**（那是用户的目录，可能有别的用途，也不是我们造的）。
                 */
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(root).Any())
                    {
                        Directory.Delete(root, recursive: false);
                        _host.AppendLog("INFO", $"顺手收掉了空的工作区目录：{root}");
                    }
                }
                catch (Exception ex)
                {
                    _host.AppendLog("INFO", $"空的工作区目录没清掉（不影响结果）：{root} —— {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                _host.AppendLog("INFO", $"清理空工作区壳时出错（不影响结果）：{ex.Message}");
            }
        }

        /// <summary>
        /// 把工作区根下面**空掉的壳目录**收掉（见调用点的三条安全边界）。
        /// </summary>
        /// <summary>
        /// 收掉**我们自己建出来的那个空目标目录**（用户 2026-09-30：工作区改到目标目录里面之后的新收尾）。
        ///
        /// <para><b>为什么必须有这一步</b>：工作区要落在 <c>&lt;目标目录&gt;\.ArchiveFixer.work</c>，
        /// 所以批首必须先建出目标目录（用户原话"原地就在原地创"）。老口径那句
        /// "最终目录连建都不该建"在实现上因此不再成立；但**用户看得见的残留一条都不许多** ——
        /// 一批全失败 / 被取消 / 越界时，那个目录里只剩一个空壳，留着就是"凭空多出来一个文件夹"，
        /// 正是用户反复抱怨过的那种东西。</para>
        ///
        /// <para><b>三条安全边界（⛔ 一条都不许松）</b>：</para>
        /// <list type="number">
        /// <item><description>只处理**这一次建出来的**那一个（批首记的事实，用户本来就有的目录永远不碰）；</description></item>
        /// <item><description>只删**空的**（现枚举；里面还有任何条目 —— 包括我们删不掉的工作区残留、
        /// 目录联接点 —— 就一个字节都不动）；</description></item>
        /// <item><description>**非递归**删（<c>recursive: false</c>），且整段包在 try/catch 里：
        /// 收尾的顺手活，失败只写日志，绝不影响批结论。</description></item>
        /// </list>
        /// </summary>
        public void RemoveEmptyTargetShellIfWeCreatedIt()
        {
            string directory = _batchCreatedTargetDirectory;

            try
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    return;
                }

                if (Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    // 里面有东西（内容物 / 清不掉的工作区残留 / 用户后来放进去的）→ 一律不碰。
                    return;
                }

                Directory.Delete(directory, recursive: false);
                _host.AppendLog("INFO", $"顺手收掉了这一批建出来的空目标目录：{directory}");
            }
            catch (Exception ex)
            {
                _host.AppendLog("INFO", $"空目标目录没清掉（不影响结果）：{directory} —— {ex.Message}");
            }
        }

        /// <summary>
        /// 清理**本任务自己**的中间工作区目录：<c>&lt;work&gt;\&lt;taskId&gt;\</c>。
        ///
        /// 为什么必须有（端到端验收实测）：双面文件要先按偏移把它尾部那段真正的 ZIP 抠进工作区再解压，
        /// 一次**成功**的一键处理就在那个目录里留下近 1 GB 过程物（实测 733 MB + 167 MB 两个包）。
        /// 这些是从源文件按偏移可再生的派生数据，成功后留着纯属垃圾：跑几批就把盘吃掉，
        /// 而且 MainViewModel 启动时会把工作区根下的每个子目录都报成"未完成的工作区"，留着还会造成假警报。
        ///
        /// 入仓（stage）之后这一条覆盖的范围变大了：暂存目录也在这个工作区里，
        /// 所以**每个任务**（不只是抠过内嵌归档的）成功后都要清 —— 条件反而更简单：
        /// 定稿把内容物搬走之后，这里剩下的全是可再生的过程物。
        ///
        /// 只在这三件事同时成立时才删（与 AGENTS.md §9.5 的清理语义对齐）：
        /// ① 解压成功；② 输出校验通过；③ 没有被取消、没有越界结论、没有超预算、没有定稿失败。
        /// 取消 / 部分完成 / 校验失败 / 越界一律不删 —— 产物可能没落全，工作区里的过程物是用户唯一的线索。
        ///
        /// 安全边界（这是"删目录"，每一条都要有）：
        /// · 目录按**暂存目录的父目录**算出（不再按 CurrentPath 重算，见下），
        ///   再规范化确认它确实在工作区根**之下**（容器内校验，越界就什么都不删）；
        /// · 目录里只允许出现**我们自己造的那些**子目录（<c>stage</c>、第 42 条拼装用的 <c>volumes</c>，
        ///   见 <see cref="IWorkspaceStepHost.IsOurWorkspaceSubdirectory"/>）与本任务的过程物文件；
        ///   出现别的子目录说明这不是我们造的那个目录（最典型：任务名撞上了递归工作区的 <c>recursive</c>），
        ///   为安全起见一个字节都不碰；
        /// · 删失败（被占用 / 权限不足）只写日志，绝不让已经成功的任务变成失败。
        ///
        /// ⚠ 目录**必须**由 <paramref name="stageDirectory"/> 反推，不能再调
        /// <see cref="PathService.BuildTaskWorkDirectory"/> 重算：它的 taskId 含源路径哈希，
        /// 而一键处理成功后会按设置把源包搬进其余物并回写 <c>task.CurrentPath</c> ——
        /// 重算出来的就是另一个目录，清理会静默地什么都不做（实测口径：近 1 GB 过程物留在工作区）。
        /// </summary>
        public void CleanupTaskWorkspaceDirectory(ArchiveTask task, string stageDirectory)
        {
            UiActivity.Mark("工作区清理", task?.LogName ?? string.Empty, "递归删除工作区");

            if (task == null)
            {
                return;
            }

            string workRoot = _host.PathService.WorkDirectory;
            string taskDirectory = string.IsNullOrWhiteSpace(stageDirectory)
                ? _host.PathService.BuildTaskWorkDirectory(task)
                : Path.GetDirectoryName(stageDirectory.TrimEnd('\\', '/')) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(taskDirectory))
            {
                return;
            }

            if (!ArchivePathGuard.IsInsideRoot(workRoot, taskDirectory, out string reason))
            {
                _host.AppendLog("WARN", $"{task.LogName}：中间工作区目录不在工作区根目录之下，已跳过清理 —— {reason}");
                return;
            }

            if (!Directory.Exists(taskDirectory))
            {
                return;
            }

            try
            {
                string[] subdirectories = Directory.GetDirectories(taskDirectory);

                // 判据本体同上（一处纯函数，两条路径转调）。
                string? foreign = WorkspaceCleanupGuard.FindForeignSubdirectory(
                    subdirectories,
                    _host.IsOurWorkspaceSubdirectory);

                if (foreign != null)
                {
                    _host.AppendLog("WARN", $"{task.LogName}：中间工作区目录里有非本任务造的子目录（{foreign}），为安全起见不清理：{taskDirectory}");
                    return;
                }
            }
            catch (Exception ex)
            {
                _host.AppendLog("WARN", $"{task.LogName}：读不了中间工作区目录（{ex.Message}），已跳过清理：{taskDirectory}");
                return;
            }

            (int fileCount, long totalSize) = OutputVerifier.Measure(taskDirectory);

            try
            {
                // 删除是**不可逆**的：动手之前先把"删什么、为什么"写进日志（AGENTS.md §9.5 的同一要求）。
                _host.AppendLog(
                    "INFO",
                    $"{task.LogName}：解压成功且输出校验通过，清理本任务的中间工作区（{fileCount} 个文件 / {totalSize} 字节）：{taskDirectory}");

                Directory.Delete(taskDirectory, recursive: true);

                _host.AppendLog("INFO", $"{task.LogName}：中间工作区已清理：{taskDirectory}");
            }
            catch (Exception ex)
            {
                // 删不掉只是"垃圾多留一会儿"，不影响任务结论（同 ExtractionWorkspace.Cleanup 的口径）。
                _host.AppendLog("WARN", $"{task.LogName}：清理中间工作区失败（{ex.Message}），目录保留：{taskDirectory}");
            }
        }


        /// <summary>
        /// 失败 / 取消 / 部分完成收尾时清理**这个任务自己的工作区**（用户 2026-09-25 第 25 条）。
        ///
        /// <para><b>默认档（<see cref="AppSettings.KeepFailedWorkspace"/> = false）：整份删掉。</b>
        /// 用户原话："我不希望有这么多的失败残留……如果解压 40G，两层，解压失败有 80G 的卸载残留，
        /// 用户不得气死……失败了就失败了，成功了就成功了"。删掉的是 <c>&lt;work&gt;\&lt;taskId&gt;</c>
        /// 整棵（含 <c>stage</c>、抠出来的内嵌归档副本、已解出的过程物）。
        /// <b>零文件空壳无论如何都删</b>（没有现场可留，这条与设置无关；
        /// 它就是原来的"空壳当场清"，现在并进这一条）。</para>
        ///
        /// <para><b>打开 <see cref="AppSettings.KeepFailedWorkspace"/> 时保持老行为</b>：
        /// 有文件就保留 + 一条 INFO 说清在哪、几个、多大（③ 页扫得到、能清）。</para>
        ///
        /// <para><b>四条边界</b>（这是删目录，每条都要有）：</para>
        /// <list type="number">
        /// <item><description>成功的任务不走这里（成功路径的清理是 <see cref="CleanupTaskWorkspaceDirectory"/>，
        /// 它按"校验通过"删，口径不同，不许两处都插一脚）；</description></item>
        /// <item><description>目录必须在**当前生效的工作区根**之下（容器内校验，越界只写 WARN、什么都不删）；
        /// 而且**只认记下来的那一个目录**，绝不按名字 / 按"最新"去扫工作区根 —— 并发跑两个任务时，
        /// 扫描式删除会把对方正在写的工作区端掉；</description></item>
        /// <item><description>目录里除 <c>stage</c> 之外不许有别的子目录（与 <see cref="CleanupTaskWorkspaceDirectory"/>
        /// 同一道边界：出现别的子目录说明它不是我们造的那个目录，一个字节都不碰）；</description></item>
        /// <item><description>删不掉（被占用 / 权限）只写 WARN —— 一次清理失败绝不该改写任务结论；
        /// 源包、已经定稿搬出去的内容物、<c>&lt;数据根&gt;</c>（日志 / 密码列表 / 设置）一个字节都不动。</description></item>
        /// </list>
        /// </summary>
        /// <param name="keepWorkspace">
        /// true = 这一单**不许清**（部分完成发布时"有文件没能发布"的那一档：没搬走的内容只在这里，
        /// 删掉等于丢用户的产物）。判据由 <see cref="TryPublishPartialProductsAsync"/> 给
        /// （<c>PartialPublisher.Result.AllMoved</c>），⛔ 不在这里自己猜。
        /// </param>
        public void CleanupFailedTaskWorkspace(ArchiveTask task, bool keepWorkspace = false)
        {
            if (task == null)
            {
                return;
            }

            _taskWorkspaceDirectories.TryRemove(task, out string? recorded);

            // 递归核心的逐层工作区不在这个目录里，单独按同一份判据处理（见下面那个方法）。
            RecursiveExtractor? recursiveExtractor = _host.TakeTaskRecursiveExtractor(task);

            bool concludedSuccess =
                string.Equals(task.Status, StatusText.ExtractSuccess, StringComparison.Ordinal) &&
                task.Outcome != TaskOutcome.PartiallyCompleted;

            if (concludedSuccess)
            {
                /*
                 * 成功：这一支的清理在 PostProcessSuccessAsync 里（按"校验通过"删），
                 * 递归核心的工作区也由它自己在 FinalizeRun 里清掉了。这里什么都不做。
                 */
                return;
            }

            if (keepWorkspace)
            {
                /*
                 * 部分完成发布时"有文件没能发布"的那一档：工作区原样留着。
                 * 两层理由：① 没搬走的内容只在这里（删掉就是丢用户的产物）；
                 * ② ③页的「失败时保留中间产物」本来就是"要留现场"，这一档比它更强 ——
                 *    现场里躺着的是**已经解出来、核对过**的真内容，不是中间垃圾。
                 * 递归逐层工作区同样不动（那些层的内容可能正是下一份要发布的）。
                 */
                _host.AppendLog(
                    "WARN",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PartialPublishRestKeptFormat,
                        task.FileName,
                        "有内容没能按「部分完成」发布，工作区与递归逐层工作区都原样留着（那是它们唯一的落点）"));

                return;
            }

            CleanupFailedRecursionWorkspace(task, recursiveExtractor);

            string taskDirectory = string.IsNullOrWhiteSpace(recorded)
                ? _host.PathService.BuildTaskWorkDirectory(task)
                : recorded;

            if (string.IsNullOrWhiteSpace(taskDirectory) || !Directory.Exists(taskDirectory))
            {
                return;
            }

            string workRoot = _host.PathService.WorkDirectory;

            if (!ArchivePathGuard.IsInsideRoot(workRoot, taskDirectory, out string guardReason))
            {
                _host.AppendLog(
                    "WARN",
                    $"{task.LogName}：要清的工作区目录不在工作区根之下，已跳过 —— {guardReason}：{taskDirectory}");

                return;
            }

            try
            {
                string[] subdirectories = Directory.GetDirectories(taskDirectory);

                /*
                 * 第二道容器内校验：目录里只许有我们自己造的子目录（判据本体在 Extraction 层的纯函数里，
                 * 递归那条路转调同一个 —— 2026-10-05 只读审计）。
                 */
                string? foreign = WorkspaceCleanupGuard.FindForeignSubdirectory(
                    subdirectories,
                    _host.IsOurWorkspaceSubdirectory);

                if (foreign != null)
                {
                    _host.AppendLog(
                        "WARN",
                        $"{task.LogName}：工作区目录里有非本任务造的子目录（{foreign}），为安全起见不清：{taskDirectory}");

                    return;
                }
            }
            catch (Exception ex)
            {
                _host.AppendLog("WARN", $"{task.LogName}：读不了工作区目录（{ex.Message}），已跳过清理：{taskDirectory}");

                return;
            }

            (int fileCount, long totalSize) = OutputVerifier.Measure(taskDirectory);

            /*
             * 有东西 + 用户要求留现场 → 保留，并且说清它在哪、几个、多大：
             * 这一行既是"东西还在不在"的答案，也是 ③ 页那条"工作区残留"的来处。
             * 零文件空壳不走这一支（空目录不占空间、也没有内容可看，留着只会在 ③ 页里多一行噪声）。
             */
            if (fileCount > 0 && _host.Settings?.KeepFailedWorkspace == true)
            {
                _host.AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.WorkspaceKeptOnFailureFormat,
                        task.FileName,
                        fileCount,
                        TaskSpaceEstimate.FormatSize(totalSize),
                        taskDirectory));

                return;
            }

            try
            {
                Directory.Delete(taskDirectory, recursive: true);

                _host.AppendLog(
                    "INFO",
                    fileCount == 0
                        ? string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.WorkspaceEmptyShellRemovedFormat,
                            task.FileName,
                            taskDirectory)
                        : string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.WorkspaceClearedOnFailureFormat,
                            task.FileName,
                            fileCount,
                            TaskSpaceEstimate.FormatSize(totalSize),
                            taskDirectory));
            }
            catch (Exception ex)
            {
                _host.AppendLog(
                    "WARN",
                    fileCount == 0
                        ? string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.WorkspaceEmptyShellRemoveFailedFormat,
                            task.FileName,
                            ex.Message,
                            taskDirectory)
                        : string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.WorkspaceClearOnFailureFailedFormat,
                            task.FileName,
                            ex.Message,
                            taskDirectory));
            }
        }

        /// <summary>
        /// 没成功（失败 / 取消 / 部分完成）时，按同一份设置清掉**递归核心的逐层工作区**
        /// （<c>&lt;work&gt;\recursive\&lt;taskId&gt;</c>，双层包里最占地方的那一份）。
        ///
        /// <para><b>为什么必须单独处理这一处</b>：它与任务工作区是两个目录 —— 递归核心的根是
        /// <see cref="RecursiveExtractor.ConfiguredWorkspaceRoot"/> 之下再挂一层 <c>recursive</c>。
        /// 只清任务工作区的话，双层包失败之后那几百 MB / 几十 GB 的逐层产物还会留在盘上，
        /// ③ 页那句"默认不会留下东西"就成了假话。</para>
        ///
        /// <para><b>什么时候不动</b>：① 这一单成功了（递归核心自己在 <c>FinalizeRun</c> 里清）；
        /// ② 用户打开了「失败时保留中间产物」；③ 这个任务根本没走递归（字典里没有它）。
        /// 删除本身的安全校验（容器内校验 + 只认本实例持有的那一个目录 + 删不掉只写 WARN）
        /// 全在 <see cref="RecursiveExtractor.TryDiscardCurrentWorkspaceOnFailure"/> 里，
        /// 这里只负责"该不该清"这一个判断。</para>
        /// </summary>
        private void CleanupFailedRecursionWorkspace(ArchiveTask task, RecursiveExtractor? recursiveExtractor)
        {
            if (recursiveExtractor == null || _host.Settings?.KeepFailedWorkspace == true)
            {
                return;
            }

            recursiveExtractor.TryDiscardCurrentWorkspaceOnFailure(task.FileName);
        }

        /// <summary>
        /// 这个落点目录里**除了工作区自己那棵树之外**还有没有东西。
        ///
        /// <para>⛔ 判据必须排除工作区（用户 2026-09-30）：工作区默认就建在目标目录里面
        /// （<c>&lt;目标目录&gt;\.ArchiveFixer.work</c>），于是目标目录**永远是"非空"的** ——
        /// 不排除的后果是每一个任务都被判成"输出目录已存在且非空"：按「询问」档会停下来问用户，
        /// 按自动改名档则把成品落进 <c>包名(1)</c>（用户看到的是一堆莫名其妙的 (1) 目录）。
        /// 判据唯一出口 <see cref="WorkspaceTree"/>。</para>
        /// </summary>
        public bool HasProductEntriesOutsideWorkspace(string directory)
        {
            /*
             * 扫描期在批中，工作区根已经由批首定好；不过这里刻意**不只在有根时才排**：
             * WorkspaceTree 的名字那一条（点开头的 .ArchiveFixer.work）与根无关，
             * 所以哪怕读不到当前根，默认档那一层照样被排除。
             */
            string workRoot = _host.PathService.WorkDirectory;

            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (!WorkspaceTree.ShouldSkipEntry(entry, workRoot))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                /*
                 * 读不了目录：按"有东西"处理（与既有各处同一口径）—— 那一档只会走冲突询问 / 自动改名，
                 * 绝不覆盖、绝不删任何东西，落在安全的一侧。
                 */
                return true;
            }
        }


        /// <summary>单测注入：把"一个目标目录"映射到**假盘根**（默认 = <see cref="Path.GetPathRoot(string)"/>）。
        ///
        /// <para>⚠ 现在它**只影响日志里"跨了几个盘"那句描述**：工作区位置不再取盘符，
        /// 而是落在这一单的目标目录里面（<c>&lt;目标目录&gt;\.ArchiveFixer.work</c>，用户 2026-09-30）。
        /// 于是单测可以直接用真实临时目录验证落点，不必再注入假盘。</para>
        /// </summary>
        internal Func<string, string>? WorkspaceDriveOverride { get; set; }


        /// <summary>
        /// 本次运行里每个任务**真正用的**工作区目录（<c>&lt;work&gt;\&lt;taskId&gt;</c>，由暂存目录反推）。
        ///
        /// <para>为什么必须记、不能事后重算：<see cref="PathService.BuildTaskWorkDirectory"/> 的 taskId 含
        /// **源路径哈希**，而任务收尾会把源包搬进 `其余物` 并回写 <c>task.CurrentPath</c> ——
        /// 重算出来的是**另一个**目录（<see cref="CleanupTaskWorkspaceDirectory"/> 的注释里记着这个坑：
        /// 实测近 1 GB 过程物因为重算而"清理"了个空）。空壳清理同样只能在**当时那个**目录上做。</para>
        ///
        /// <para>键用任务对象本身（理由与 <see cref="_spaceRuntime"/> 一样），条目在任务收尾时移除。</para>
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<ArchiveTask, string> _taskWorkspaceDirectories = new();

        /// <summary>
        /// 本批**我们自己建出来的那个目标目录**（工作区要落在它里面，所以批首必须先建它）。
        ///
        /// <para>为什么要记：工作区改到目标目录里面之后（用户 2026-09-30），"最终目录连建都不该建"
        /// 这条老口径在实现上不可能再成立 —— 不先建它就没地方放工作区。但**用户看得见的残留**
        /// 一条都不许多：批尾如果这个目录里什么都没有（这一批什么都没解出来 / 全失败 / 被取消），
        /// 它就是**我们自己造的一个空壳**，必须自己收掉。</para>
        ///
        /// <para>⛔ 只在"确实是这一次建出来的"并且"现在一个条目都没有"这两条同时成立时才删
        /// （前者靠 <see cref="WorkspaceRootResolution.TargetDirectoryCreated"/>，
        /// 后者靠现枚举）—— 用户本来就有的目录、或者里面有我们删不掉的东西（目录联接点），
        /// 一律一个字节都不动。空串 = 这一批没建过任何目标目录。</para>
        /// </summary>
        private string _batchCreatedTargetDirectory = string.Empty;

        /// <summary>本次运行每个任务真正用的工作区目录（清理时必须用"当时那个"，不能事后重算）。</summary>
        internal ConcurrentDictionary<ArchiveTask, string> TaskWorkspaceDirectories => _taskWorkspaceDirectories;

        /// <summary>本批我们自己建出来的那个目标目录（空串 = 这一批没建过）。</summary>
        internal string BatchCreatedTargetDirectory
        {
            get => _batchCreatedTargetDirectory;
            set => _batchCreatedTargetDirectory = value;
        }
    }
}