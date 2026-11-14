using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 一键处理的**继续解作业区**：本轮要识别谁、产物里哪些算新内层包、新内层包怎么变成下一轮的任务、
/// 解压前要记下哪些老文件。
///
/// <para>从 <c>OneClickCoordinator</c> 按职责搬出来（续解 ≠ 轮循环编排）：这些方法都是"对任务表 /
/// 产物目录做事"，不动轮数与结论；协调器只保留同名的一行转发，⛔ 判据只有这一份。</para>
///
/// <para><b>内层包判据（2026-09-25 第 33 条改）：只认内容，⛔ 后缀不参与判决。</b>
/// 真机现场：第一层解出来的下一层叫 <c>222.ra删除r</c>，后缀不认识 → 在魔数体检之前就被扔掉 →
/// 日志只写"第 1 层没有发现可继续解压的内层包"。现在新产物逐个按内容判定（文件头 + 双面文件尾部），
/// 仍然按名字排除的只有"分卷的后续卷"（那是分组信息，不是格式判断，见
/// <see cref="InnerLayerCandidateClassifier.IsVolumeContinuationPart"/>）。</para>
/// </summary>
internal sealed class OneClickContinuationWorkbench
{
    private readonly MainViewModel _vm;
    private readonly ScanCoordinator _scanCoordinator;

    internal OneClickContinuationWorkbench(
        MainViewModel vm,
        ScanCoordinator scanCoordinator)
    {
        _vm = vm;
        _scanCoordinator = scanCoordinator;
    }

    private ObservableCollection<ArchiveTask> Tasks => _vm.Tasks;

    private AppSettings Settings => _vm.Settings;

    private void AppendLog(string level, string message) => _vm.AppendLog(level, message);

    /// <summary>
    /// 这个产物文件要不要进这一轮「内层包扫描」的**统计与日志**（唯一出口）。
    ///
    /// <para>2026-10-05 真机：那一轮扫出 857 个文件，其中 2 个是 <c>其余物\HK.7z.001/.002</c>
    /// （**本批的源包**）—— 它们永远只会落一句"跳过"，却把"新文件"这个数字和日志行都撑大了
    /// （用户点名的"857 个文件、20 行日志"）。所以这里把"已经是我们自己的东西"挡在统计之外：
    /// 本批的源包、以及已经登记过路径的文件（在任务列表里 / 本轮已加进来的）。</para>
    ///
    /// <para>⛔ <b>只过滤"已知的"，绝不整棵排除「其余物」</b>：默认档（只解当前这一层 + 一键处理续解）下，
    /// 上一层产出的内层包正是**收进其余物之后**才被这一轮扫描认出来、当下一个任务接着解的
    /// （`InnerLayerContinuationTests` 那条 <c>outer.7z → inner.7z.001</c> 的链就是它）——
    /// 把整棵树排掉等于把续解链砍掉（2026-10-05 实测：18 条用例当场红）。</para>
    /// </summary>
    internal static bool ShouldScanProducedFile(
        string file,
        IReadOnlyCollection<string> sourcePaths,
        IReadOnlyCollection<string> knownTaskPaths)
    {
        /*
         * ⛔ **真正的 APK 不进"下一轮的内层包"名单**（用户 2026-10-10 拍板：有些 APK 是用户真的要
         * 转移到手机上安装的）：它按魔数就是 ZIP，所以"按内容认得出是归档"这条路一定会把它收进去，
         * 而我们要的是**一个字节都不碰它**。
         *
         * 判据转调唯一出口 `AndroidPackageDetector`（内容证据，⛔ 不看后缀；判不出 ⇒ 不拦）。
         * 三条路的第三处（另两处：单层入口 `ExtractSingleTaskAsync` 开头、
         * 递归层的内层包候选 `RecursiveExtractor`）。
         */
        if (Detection.AndroidPackageDetector.IsAndroidPackage(file))
        {
            return false;
        }

        return !sourcePaths.Contains(file) && !knownTaskPaths.Contains(file);
    }

    /// <summary>
    /// 一轮的"识别"步骤。
    /// 第 1 轮沿用原来的整体扫描；第 2 轮起**只扫本轮新加进来的包**。
    /// </summary>
    internal async Task ScanRoundAsync(int round, IReadOnlyList<ArchiveTask> roundTargets)
    {
        List<ArchiveTask> pending = roundTargets.Where(NeedsScan).ToList();

        if (pending.Count == 0)
        {
            return;
        }

        if (round == 1)
        {
            AppendLog("INFO", "一键处理：先识别格式。");
            await _scanCoordinator.ScanTasksAsync();
            return;
        }

        /*
         * 为什么不直接再调一次 ScanTasksAsync：
         * 它会遍历**整个任务列表**，把已经解压完的任务状态从"解压成功"冲回"已识别" ——
         * 用户会看到第 1 层的结果在列表里突然没了，汇总里的成功数也跟着归零。
         * 新加进来的包只需要识别它自己，按任务扫没有这个副作用。
         */
        foreach (ArchiveTask task in pending)
        {
            await _scanCoordinator.RescanTaskAsync(task);
        }
    }

    /// <summary>
    /// 把**已经处理过**的任务全部取消勾选（续解的每一步之前都要做，见调用处的说明）。
    ///
    /// <para><b>为什么收成一个出口</b>（2026-10-05 只读审计）：到顶停下那一支过去漏了这一步，
    /// 于是"继续解"会把上一批最后一轮已经解过的包再解一遍（产物同名 ⇒ <c>(1)</c> 垃圾副本）。
    /// 两处各写一遍的话，下次再有人加一支"停下"的分支照样会漏 —— 这里只留一份做法。</para>
    ///
    /// <para>必须走批量守卫（用户 2026-09-24 第 12 条"卡死"的修法之一）：逐项改勾选
    /// 会让**每一项**都触发一次全表汇总重算 + 38 条命令重查，
    /// 几百项的任务列表在这一步就是几百次 O(N) 扫描（O(N²)）。</para>
    /// </summary>
    internal void DeselectProcessedTasks(List<ArchiveTask> processed)
    {
        _vm.RunBulkSelectionUpdate(() =>
        {
            foreach (ArchiveTask task in processed)
            {
                task.IsSelected = false;
            }
        });
    }

    /// <summary>
    /// 把内层包加进任务列表，返回新加进来的任务（已显式勾选），并给每个内层任务挂上
    /// **父任务的最终目录**（<see cref="ArchiveTask.ParentOutputDirectory"/>）。
    ///
    /// 挂父目录这一步是"一个源包 = 一个最终目录"的接线点：
    /// <see cref="PathService.BuildOutputPath"/> 见到这个字段就直接返回它，
    /// 于是内层包不会再算出 <c>&lt;id&gt;.7z\内容物</c> 这种自己的目录。
    ///
    /// 为什么这一次导入要关掉"顺手整表重新识别"（<c>suppressAutoScan</c>）：
    /// AddPathsAsync 在「导入后自动识别」开启时会顺手对整个列表做一次重新识别，同样会把已经解压完的
    /// 任务状态冲回"已识别"（第 1 层的结果在界面上就没了）。这里只要"把新文件变成任务"，
    /// 识别由本轮自己按任务做（见 <see cref="ScanRoundAsync"/>）。
    ///
    /// ⚠ 2026-09-26 改：以前是"临时把 <c>Settings.AutoScanAfterDrop</c> 改成 false、用完还原" ——
    /// 那是**用户看得见、还会被自动保存写进盘**的一个设置项：万一在那段窗口里进程被杀（或断电报错），
    /// 用户的"导入后自动扫描"就永久变成关的，而他从没动过那个开关。
    /// 现在走 <c>AddPathsAsync</c> 的参数，一个设置项都不碰。
    /// </summary>
    internal async Task<List<ArchiveTask>> AddInnerTasksAsync(IReadOnlyList<InnerArchiveCandidate> candidates)
    {
        int before = Tasks.Count;

        /*
         * 同一基名 + 同一卷号如果用户已经导入过源片，链产出副本不再加成任务。
         * P53 真机：输入 P53-8 与成品 P53-8 各有一份 .001/.002，输出副本被加成一单
         * ⇒ 这一单先跑失败，第二大步入口反而选了它，而不是用户源片。
         * 判据只读“用户导入”与卷号，不按路径猜。
         */
        var userVolumeSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ArchiveTask existing in Tasks)
        {
            if (existing == null
                || existing.IsContinuationTask
                || string.IsNullOrWhiteSpace(existing.CurrentPath))
            {
                continue;
            }

            /*
             * ⛔ 2026-11-01 真机：`TryGetVolumeIndex("2.rar")` 会把裸 `.rar` 当成第 1 卷（slot=1），
             * 于是父包 `2.rar` 被记成"用户源片 slot 1"，把真正的 `2.part1.rar` 当成重复副本跳过
             * ⇒ 入口任务补建 0 个、链停住。只有**名字是分卷片**的才参与这个去重。
             */
            int? index = ArchiveFixer.Detection.LegacyVolumeNames.IsVolumePart(
                    FileNameHelper.GetFileName(existing.CurrentPath))
                ? LegacyVolumeNames.TryGetIndex(
                    FileNameHelper.GetFileName(existing.CurrentPath))
                : null;

            if (index.HasValue)
            {
                userVolumeSlots.Add(
                    ArchiveFixer.Helpers.FileNameHelper.SecondStepBaseName(existing.CurrentPath)
                    + "|" + index.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        List<InnerArchiveCandidate> tasksToAdd = candidates
            .Where(candidate =>
            {
                if (string.IsNullOrWhiteSpace(candidate.Path))
                {
                    return false;
                }

                int? index = ArchiveFixer.Detection.LegacyVolumeNames.IsVolumePart(
                        FileNameHelper.GetFileName(candidate.Path))
                    ? LegacyVolumeNames.TryGetIndex(
                        FileNameHelper.GetFileName(candidate.Path))
                    : null;

                return !index.HasValue
                    || !userVolumeSlots.Contains(
                        ArchiveFixer.Helpers.FileNameHelper.SecondStepBaseName(candidate.Path)
                        + "|" + index.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            })
            .ToList();

        if (tasksToAdd.Count < candidates.Count)
        {
            AppendLog(
                "INFO",
                $"一键处理：跳过 {candidates.Count - tasksToAdd.Count} 个内层包 —— "
                + "同基名同卷号的用户源片已在任务表里，链产出副本不重复当原包。");
        }

        /*
         * ⚠ 这里必须是**追加**（ImportMode.Append）。
         *
         * 「添加文件 / 添加文件夹」的默认语义是"清空整张表再加"（用户 2026-09-24 第 12 条），
         * 而这一步是解压途中往列表里补内层包 —— 用替换语义就会把用户的任务表
         * （连同刚刚解完的那些结果行）整张清掉。AddPathsAsync 刻意**不给默认值**，
         * 就是为了让这种调用点必须把意图写出来。
         */
        await _scanCoordinator.AddPathsAsync(
            tasksToAdd.Select(c => c.Path).ToList(),
            ImportMode.Append,
            suppressAutoScan: true);

        // 按完整路径找回"这个任务对应哪个内层候选"：AddPathsAsync 可能改写路径大小写，
        // 也可能因为重复而少加几个，所以用查表而不是按下标一一对应。
        var byPath = new Dictionary<string, InnerArchiveCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (InnerArchiveCandidate candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate.Path))
            {
                byPath[NormalizePath(candidate.Path)] = candidate;
            }
        }

        var added = new List<ArchiveTask>();

        /*
         * 新内层包一律勾上，整批只刷一次汇总（批量守卫）——
         * 内层包可能有几十上百个，逐项勾选会逐项触发全表重算（O(N²)）。
         */
        _vm.RunBulkSelectionUpdate(() =>
        {
            for (int i = before; i < Tasks.Count; i++)
            {
                /*
                 * 新任务必须处于勾选状态才会被下一轮处理。
                 * FileScanService 建任务时就是 IsSelected = true（也确认过 ArchiveTask 的字段默认值），
                 * 但这条是"续解能不能继续"的命门，显式写一遍，免得以后有人改默认值时静默失效。
                 */
                Tasks[i].IsSelected = true;

                /*
                 * 按目录补跑一次分卷归组：已停用（用户 2026-10-24）。
                 * 实现 `ResolveVolumeGroupFromDirectory` 仍在原处（第二大步重建时可能要用），
                 * 但第一大步不再把同组卷并成一个任务。
                 */

                if (byPath.TryGetValue(NormalizePath(Tasks[i].CurrentPath), out InnerArchiveCandidate? candidate))
                {
                    Tasks[i].ParentOutputDirectory = candidate.ParentOutputDirectory;
                    Tasks[i].ParentTaskName = candidate.ParentTaskName;
                    Tasks[i].RootSourcePath = candidate.RootSourcePath;

                    /*
                     * 落点当场写一遍，别等界面刷新：续解下一轮要读 task.OutputPath 找内层包，
                     * 中间任何一次时序错位都会让第二层静默不跑（历史事故）。
                     */
                    if (!string.IsNullOrWhiteSpace(candidate.ParentOutputDirectory))
                    {
                        Tasks[i].OutputPath = candidate.ParentOutputDirectory;
                    }
                }

                added.Add(Tasks[i]);
            }
        });

        return added;
    }

    /// <summary>路径比较用的规范化形式（全路径 + 统一分隔符；大小写在 Windows 上不敏感）。</summary>
    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    /// <summary>源文件路径集合（当前路径 + 最初路径 + 该分卷组的每一卷）。</summary>
    internal static HashSet<string> BuildSourcePathSet(IEnumerable<ArchiveTask> tasks)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ArchiveTask task in tasks)
        {
            if (task == null)
            {
                continue;
            }

            foreach (string path in new[] { task.CurrentPath, task.OriginalPath }.Concat(task.VolumePaths))
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(path);
                }
            }
        }

        return paths;
    }

    /// <summary>
    /// 记录候选产物目录在**解压前**已有的文件，返回（已有文件集合, 解压前读不到内容的目录集合）。
    ///
    /// 返回"读不到内容的目录"的用途：这种目录里的老包和新产物混在一起、分不出新旧，
    /// 续解时整个不看它 —— 宁可漏掉一层，也不能把用户本来就放在那里的包当成新产物去解压。
    ///
    /// ⚠ 这里**刻意不再**返回"快照成功的目录集合"。
    /// 旧实现要求"当前目录必须在快照集合里"，于是漏掉了最要命的一种情况：
    /// 输出目录已存在且非空时，解压实际落到自动改名的 <c>xxx(1)</c>，而快照是解压**之前**做的，
    /// 里面只有原定的 <c>xxx</c> —— 内层包所在的 <c>xxx(1)</c> 被整段跳过，
    /// 第二层静默不跑，汇总仍然打印"一键处理完成：成功 N"。
    /// 换成"黑名单"之后，<c>xxx(1)</c> 这种本轮新建的目录天然被放行，
    /// 新旧之分仍然由 <c>existingFiles</c> 把关。
    ///
    /// 线程规则：目录集合与设置读取在调用线程（UI）上做，**枚举（可能几万个文件）放后台**。
    /// 一键处理整条流程跑在 UI 线程上，任何一次全目录遍历都会让窗口卡住。
    /// </summary>
    internal async Task<(HashSet<string> ExistingFiles, HashSet<string> UnreadableDirectories)> SnapshotCandidateDirectoriesAsync(
        IReadOnlyList<ArchiveTask> roundTargets)
    {
        // 没有输出目录就没法做"解压前后对比"。补算一次（与界面刷新输出目录用的是同一套规则）。
        if (roundTargets.Any(t => string.IsNullOrWhiteSpace(t.OutputPath)))
        {
            _vm.RefreshOutputPaths();
        }

        var directories = new List<string>();

        foreach (ArchiveTask task in roundTargets)
        {
            directories.AddRange(InnerLayerCandidateClassifier.CandidateDirectories(task));
        }

        // 归集目标目录在解压前就能确定：不先记下来，它里面的老文件会被当成这一轮的新产物。
        if (Settings.CollectResultsToDirectory && !string.IsNullOrWhiteSpace(Settings.CollectTargetDirectory))
        {
            directories.Add(Settings.CollectTargetDirectory);
        }

        List<string> targets = directories
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        (HashSet<string> existingFiles, List<(string Directory, string Message)> unreadable) = await Task.Run(() =>
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failures = new List<(string, string)>();

            foreach (string directory in targets)
            {
                if (!Directory.Exists(directory))
                {
                    // 目录还不存在：解压之后出现在里面的都算新产物，不需要记任何东西。
                    continue;
                }

                try
                {
                    /*
                     * ⛔ 必须**排除工作区自己那棵树**（用户 2026-09-30）：工作区默认建在目标目录里面
                     * （<目标目录>\.ArchiveFixer.work），解压过程中它会被填满中间产物。
                     * 不排除的话，这些中间产物会被当成本轮"新出现的产物"，进而被当成内层包继续解 /
                     * 被当成内容物搬出去 —— 不可逆的事故。
                     * 判据唯一出口见 WorkspaceTree（名字 + 当前生效的根两条）。
                     */
                    foreach (string file in WorkspaceTree.EnumerateFiles(directory, _vm.CurrentWorkspaceRoot))
                    {
                        files.Add(file);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add((directory, ex.Message));
                }
            }

            return (files, failures);
        });

        var unreadableDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 日志回到 UI 线程再写（后台线程不许碰界面集合）。
        foreach ((string directory, string message) in unreadable)
        {
            unreadableDirectories.Add(directory);
            AppendLog("WARN", $"一键处理：读不了产物目录 {directory}（{message}），本轮不看这个目录。");
        }

        return (existingFiles, unreadableDirectories);
    }

    private static bool NeedsScan(ArchiveTask task)
    {
        return task.ExtensionStatus == StatusText.NotChecked ||
            string.IsNullOrWhiteSpace(task.ExtensionStatus) ||
            string.Equals(task.DetectedFormat, "Unknown", StringComparison.OrdinalIgnoreCase) ||
            task.Status == StatusText.WaitingScan;
    }
}
