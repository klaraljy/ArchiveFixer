using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 发布时的**内容搬运**：把某一层产物目录下的条目搬进发布目标，返回真正移动成功的文件数。
///
/// <para>三条边界都写在这里：⛔ 同名绝不覆盖（撞名自动改名，<c>File.Move</c> 两参数版是最后一道保险）；
/// ⛔ 不跟随符号链接 / 联接点；⛔ 同一个物理文件已经在目标位上时只去掉源名字、不再落第二份
/// （判据走唯一出口 <see cref="FileIdentity.IsSamePhysicalFile"/>）。</para>
/// </summary>
internal static class WorkspaceContentMover
{
    /// <summary>把内容根目录下的条目搬进目标目录，返回真正移动成功的文件数。</summary>
    /// <param name="movedFromTo">
    /// **真的搬成了的那些文件的 `来源全路径 → 落点全路径`**（落点是撞名改名之后的那个名字）——
    /// 调用方要靠它回答"这一份到底搬到哪去了"（见 <see cref="WorkspaceConsumedCleanup.TryDeleteConsumedPackage"/>）。
    /// </param>
    internal static int MoveContent(
        string sourceDirectory,
        string destinationDirectory,
        List<string> renamed,
        List<string> errors,
        Dictionary<string, string> movedFromTo)
    {
        string[] entries;

        try
        {
            entries = Directory.GetFileSystemEntries(sourceDirectory);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Problem("工作区", "降级处理", ex);
            errors.Add($"无法读取产物目录（{ex.Message}）");
            return 0;
        }

        int movedCount = 0;

        foreach (string entry in entries)
        {
            if (!WorkspacePathRules.TryGetAttributes(entry, out FileAttributes attributes))
            {
                errors.Add($"{Path.GetFileName(entry)}（读不到文件属性）");
                continue;
            }

            // 不跟随符号链接 / 联接点：跟着走会把别处的文件挪走，还可能绕圈。
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                errors.Add($"{Path.GetFileName(entry)}（是符号链接/联接点，未移动）");
                continue;
            }

            string entryName = Path.GetFileName(entry);

            if ((attributes & FileAttributes.Directory) != 0)
            {
                movedCount += MoveContent(
                    entry,
                    Path.Combine(destinationDirectory, entryName),
                    renamed,
                    errors,
                    movedFromTo);

                continue;
            }

            if (TryMoveFile(entry, destinationDirectory, renamed, errors, movedFromTo))
            {
                movedCount++;
            }
        }

        // 这一层已经搬空了就顺手删掉（只用非递归重载：目录里还有东西就必然抛，删不掉非空目录）。
        TryDeleteDirectoryIfEmpty(sourceDirectory);

        return movedCount;
    }

    private static bool TryMoveFile(
        string sourceFile,
        string destinationDirectory,
        List<string> renamed,
        List<string> errors,
        Dictionary<string, string> movedFromTo)
    {
        string fileName = Path.GetFileName(sourceFile);
        string targetPath = Path.Combine(destinationDirectory, fileName);

        try
        {
            if (!SafePathHelper.EnsureDirectoryExists(destinationDirectory))
            {
                errors.Add($"{fileName}（无法创建目标目录）");
                return false;
            }

            if (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                /*
                 * ⛔ **同一个物理文件已经在目标位上了**（硬链接双胞胎 / 同一个 inode）：这不是"同名冲突"，
                 * 而是"这一份已经到位了" ⇒ 只把**源这个名字**去掉，⛔ 绝不再改名落出第二份。
                 *
                 * <para>真机 EEEE 2026-10-10：入口包 `111.zip` 一边被"接片"那一档按规范卷名接进落点层、
                 * 一边又被定稿从暂存目录往同一层搬；两份本来就是同一个 inode（接片建的是硬链接），
                 * 老写法按"目标同名"自动改名 ⇒ 盘上多出 `…\111\111\111(1).zip`（用户点名的
                 * 「第二轮续解又解一遍 ⇒ 多出 `111(1).zip`」）。</para>
                 *
                 * <para>判据走**唯一出口** <see cref="FileIdentity.IsSamePhysicalFile"/>（⛔ 不自己比路径、
                 * ⛔ 不比时间戳、⛔ 不比体积）；判不出 ⇒ 照旧走下面的自动改名（**绝不覆盖**，一个字不改）。
                 * 删掉源那一条名字**不丢数据**：内容就在目标那一条名字上（同一个 inode）。</para>
                 */
                if (File.Exists(targetPath)
                    && FileIdentity.IsSamePhysicalFile(sourceFile, targetPath))
                {
                    FileOperations.Delete(sourceFile);
                    movedFromTo[SafePathHelper.GetFullPathSafe(sourceFile)] = SafePathHelper.GetFullPathSafe(targetPath);

                    return true;
                }

                // 目标同名：换成 名字(1).ext 并记下来，**绝不覆盖**（AGENTS.md §6 第 3 条）。
                string renamedPath = SafePathHelper.AutoRenameFilePath(targetPath);

                if (!string.Equals(renamedPath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    renamed.Add($"{fileName} → {Path.GetFileName(renamedPath)}");
                }

                targetPath = renamedPath;
            }

            /*
             * 两参数 File.Move 在目标已存在时直接抛 IOException，**不会覆盖**。
             * 这是最后一道保险：即使 AutoRename 与 Move 之间有人往目标目录里塞了同名文件，
             * 也只会少移动一个文件并记进 Message，不会把别人的文件冲掉。
             */
            FileOperations.Move(sourceFile, targetPath);

            movedFromTo[SafePathHelper.GetFullPathSafe(sourceFile)] = SafePathHelper.GetFullPathSafe(targetPath);

            return true;
        }
        catch (Exception ex)
        {
            AppDiagnostics.Problem("工作区", "降级处理", ex);
            errors.Add($"{fileName}（{ex.Message}）");
            return false;
        }
    }

    /// <summary>尽力删除已经空掉的目录：删不掉（非空 / 被占用 / 没权限）一律当没发生。</summary>
    private static void TryDeleteDirectoryIfEmpty(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            if (Directory.GetFileSystemEntries(directory).Length > 0)
            {
                return;
            }

            Directory.Delete(directory, recursive: false);
        }
        catch
        {
            // 空目录删不掉不影响发布结果，留给用户自己收拾。
        }
    }
}
