using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ArchiveFixer.Services;

/// <summary>
/// 一次批次的改名执行与结果：**整批先撤到临时名，再一起落位**（不变量 3）+ 批次编排。
///
/// <para>从 <see cref="RenameService"/> 的嵌套类原样提升为顶层协作类（一类型一文件）：
/// 回滚原语在 <see cref="RenameRollback"/>，落点计划在 <see cref="RenamePlan"/>，
/// 任务回填在 <see cref="RenameTaskJournal"/>，本类只按固定顺序调它们。</para>
///
/// <para>⛔ 顺序不能颠倒、也不能逐项做：名称交换 <c>A.zip ↔ B.zip</c> 就靠"整批先撤名字"成立；
/// 任何一步失败都整体回滚，结果只可能是"全是旧名字"或"全是新名字"，绝不会少文件。</para>
/// </summary>
internal sealed class RenameExecution
{
    private readonly Dictionary<string, string> _failures = new(StringComparer.OrdinalIgnoreCase);

    internal string GetFailure(string sourcePath) =>
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
    internal static RenameExecution Run(RenamePlan plan)
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
                RenameRollback.RollBackStaged(staged);

                return Failed(
                    $"改名失败（已回滚，所有文件保持原名）：撤走 {Path.GetFileName(source)} 时出错 —— {ex.Message}",
                    sources);
            }

            staged.Add((source, temporaryPath, finalPath));
        }

        // ── 阶段 2：腾位 ────────────────────────────────────────────────
        var vacated = new List<(string OccupiedPath, string TemporaryPath, bool HasIdentity, ulong VolumeSerial, ulong FileIndex)>();

        foreach ((string _, string _, string finalPath) in staged)
        {
            if (!File.Exists(finalPath))
            {
                continue;
            }

            string temporaryPath;
            bool hasIdentity = FileIdentity.TryGetIdentity(finalPath, out ulong volumeSerial, out ulong fileIndex);

            try
            {
                temporaryPath = SafePathHelper.BuildVacatingPath(finalPath, isDirectory: false);

                // 占着这个名字的是外来的文件（要覆盖的目标）：挪成临时名，落位成功后才删。
                // 先记下它那一刻的 FileIdentity；清场前必须还是同一份，防止这中间被别人替换。
                File.Move(finalPath, temporaryPath);
            }
            catch (Exception ex)
            {
                RenameRollback.RollBackStaged(staged);
                RenameRollback.RollBackVacated(vacated);

                return Failed(
                    $"无法为 {Path.GetFileName(finalPath)} 腾出名字（已回滚，所有文件保持原名）：{ex.Message}",
                    sources);
            }

            vacated.Add((finalPath, temporaryPath, hasIdentity, volumeSerial, fileIndex));
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
                RenameRollback.RollBackLanded(landed);
                RenameRollback.RollBackStaged(staged);
                RenameRollback.RollBackVacated(vacated);

                return Failed(
                    $"改名失败（已回滚，所有文件保持原名）：落位 {Path.GetFileName(finalPath)} 时出错 —— {ex.Message}",
                    sources);
            }
        }

        // ── 阶段 4：清场 ────────────────────────────────────────────────
        foreach ((string _, string temporaryPath, bool hasIdentity, ulong volumeSerial, ulong fileIndex) in vacated)
        {
            if (!hasIdentity
                || !FileIdentity.TryGetIdentity(temporaryPath, out ulong currentVolume, out ulong currentIndex)
                || currentVolume != volumeSerial
                || currentIndex != fileIndex)
            {
                // 身份变了 / 取不到：绝不删；留给用户那个 .af-vacating 名字，比删错安全。
                continue;
            }

            RenameRollback.TryDelete(temporaryPath);
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

    /// <summary>
    /// 一次改名批次的编排：先定整批落点计划（<see cref="RenamePlan"/>），再整批执行
    /// （<see cref="RenameExecution.Run"/>），最后把每条结果回填到预览项与任务
    /// （<see cref="RenameTaskJournal"/>）。
    ///
    /// <para>⛔ 不变量 3：必须"整批先定计划再一起动手"，⛔ 不能一项一项来 ——
    /// 名称交换 <c>A.zip ↔ B.zip</c> 天生是两项之间的关系。</para>
    /// </summary>
    /// <param name="previewItems">预览项（本批次要处理的全部条目）。</param>
    /// <param name="tasks">任务表（按路径匹配后回填）。</param>
    internal static Task RunBatchAsync(
        IEnumerable<RenamePreviewItem> previewItems,
        IEnumerable<ArchiveTask> tasks)
    {
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

            ArchiveTask? matchedTask = RenameTaskJournal.FindTaskByPath(taskList, item.OriginalPath);

            string sourcePath = item.OriginalPath;
            string finalPath = plan.GetFinalPath(sourcePath);
            string failure = execution.GetFailure(sourcePath);

            if (!string.IsNullOrWhiteSpace(failure) || string.IsNullOrWhiteSpace(finalPath))
            {
                item.Status = StatusText.RenameCannot;
                item.ErrorMessage = string.IsNullOrWhiteSpace(failure)
                    ? "改名未执行（目标路径为空）"
                    : failure;
                RenameTaskJournal.UpdateTaskRenameFailed(matchedTask, item.ErrorMessage);
                continue;
            }

            item.NewPath = finalPath;
            item.NewFileName = Path.GetFileName(finalPath);

            if (plan.IsSkipped(sourcePath))
            {
                item.Status = StatusText.RenameWillSkip;
                item.ErrorMessage = plan.GetSkipReason(sourcePath);
                RenameTaskJournal.UpdateTaskSkipped(matchedTask, item.ErrorMessage);
                continue;
            }

            item.Status = StatusText.RenameSuccess;
            item.ErrorMessage = string.Empty;

            try
            {
                RenameTaskJournal.UpdateTaskRenameSuccess(matchedTask, sourcePath, finalPath);
            }
            catch
            {
                // 这里故意吞掉异常。
                // 原因：文件已经改名成功，不能因为 UI/任务对象更新异常再弹“改名失败”。
                // 后续重新扫描时会以文件系统真实路径为准。
            }
        }

        return Task.CompletedTask;
    }
}
