using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Services;

/// <summary>
/// 改名结果的任务回填与「这一步变换」的记录（日志侧唯一写入出口）。
///
/// <para>从 <see cref="RenameService"/> 抽出的协作类（「日志 / 回填」）：把改名成功 / 失败 / 跳过
/// 如实写回 <see cref="ArchiveTask"/>（含 <c>VolumePaths</c> 清单里的旧名字与
/// <c>AppendTransformationStep</c>），⛔ 不判能不能改、不碰磁盘、不改判据。</para>
/// </summary>
internal static class RenameTaskJournal
{
    /// <summary>
    /// 按路径在任务表里找任务（改名执行与"只重扫被改过名的那些"共用这一份匹配口径）。
    ///
    /// <para>三个字段都要比：执行期会把 <c>CurrentPath</c> 换到新名字上，而
    /// <c>OriginalPath</c> / <c>RenamePreviewPath</c> 还留着旧路径 —— 少比一个就会漏掉任务，
    /// 于是"改名成功但列表里那一行还指着旧文件"。</para>
    /// </summary>
    internal static ArchiveTask? FindTaskByPath(IEnumerable<ArchiveTask> tasks, string path)
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

    internal static void UpdateTaskRenameSuccess(ArchiveTask? task, string oldPath, string newPath)
    {
        if (task == null)
        {
            return;
        }

        /*
         * ⚠ **分卷清单里那份旧名字也要一起改掉**（用户 2026-10-01 真机第五轮：400 MB 的 `222.z01`
         * 又被留在源目录里，而他报的是"这东西还在，你浪费了我两次操作"）。
         *
         * 现场：`222.zscip`（跨盘 zip 的**本体**，名字是被改坏的那一份）被「按真实格式修正」
         * 改成 `222.zip` —— 这条改名以前只写 <c>CurrentPath</c>；同一个文件在
         * <see cref="ArchiveTask.VolumePaths"/> 里**还叫 `222.zscip`**。后面批首那次整组改名
         * 只按自己那份计划的"旧名 → 新名"映射回填（`222.z删除01 → 222.z01`），
         * 那个盘上已经不存在的 `222.zscip` 没人管 ⇒ 账上成了
         * <c>[222.zscip（盘上没有）, 222.z01]</c>。
         *
         * 后果（`SourcePackageMover.ResolveSourceGroup` 的判据）：清单非空、却不含任务自己
         * ⇒ 这一单按**单文件**办 ⇒ 只搬 `222.zip`，`222.z01` 原地不动；
         * 而"清单能变多才补"那道闸门按**条数**比（2 条 vs 2 条 = 不变）也补不上。
         *
         * ⛔ 口径与本项目"整组改名必须同步全表任务路径 + 快照"（`SyncTasksAfterVolumeRename`）
         * 完全一致：改名之后，任务上**每一处**指向这个文件的旧路径都必须改过来。
         */
        for (int i = 0; i < task.VolumePaths.Count; i++)
        {
            if (string.Equals(task.VolumePaths[i], oldPath, StringComparison.OrdinalIgnoreCase))
            {
                task.VolumePaths[i] = newPath;
            }
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
        task.LastUpdatedTime = ArchiveFixer.Helpers.Clock.Now;

        /*
         * **记下这一步变换**（唯一写入出口 `ArchiveTask.AppendTransformationStep`）：用的就是
         * 这条改名记录自己的两个名字（旧名 → 新名）—— ⛔ 不是拿账上字段比对推出来的
         * （用户 2026-10-07：「你不要给我瞎猜，看程序怎么弄」；①页那一行靠这条链说明
         * 「最简单的改名操作也要有一个变换的过程」）。
         */
        task.AppendTransformationStep($"{Path.GetFileName(oldPath)} → {Path.GetFileName(newPath)}");
    }

    internal static void UpdateTaskRenameFailed(ArchiveTask? task, string errorMessage)
    {
        if (task == null)
        {
            return;
        }

        task.Operation = StatusText.OpRename;
        task.Status = StatusText.RenameFailed;
        task.ErrorMessage = errorMessage ?? string.Empty;
        task.LastUpdatedTime = ArchiveFixer.Helpers.Clock.Now;
    }

    internal static void UpdateTaskSkipped(ArchiveTask? task, string message)
    {
        if (task == null)
        {
            return;
        }

        task.Operation = StatusText.OpSkip;
        task.Status = StatusText.Skipped;
        task.ErrorMessage = message ?? string.Empty;
        task.LastUpdatedTime = ArchiveFixer.Helpers.Clock.Now;
    }
}
