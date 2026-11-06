using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **临时组入口目录**（共用骨架）：把一组的标准名做成硬链接，供 VolumeCandidateDiscovery/引擎读取。
    ///
    /// <para>入口根必须由调用方给出**合法工作区**（<c>&lt;目标目录&gt;\.ArchiveFixer.work\group-entry</c>）。
    /// 目标盘与源片不同卷、硬链接做不了时，⛔ 不再回落到源卷根；拿不到合法根就失败，
    /// 由调用方把组落成 Unknown。⛔ 不复制、不移动、不改名用户源文件。</para>
    ///
    /// <para>形状：<c>&lt;目标目录&gt;\.ArchiveFixer.work\group-entry\&lt;基名&gt;-&lt;guid&gt;\</c>。
    /// 目录必须带 Hidden 属性。</para>
    /// </summary>
    public static class VolumeGroupEntryDirectory
    {
        public const string WorkFolderName = ".ArchiveFixer.work";

        public const string GroupEntryFolderName = "group-entry";

        /// <summary>
        /// 给一个已判成 Complete 的组建标准名硬链接；返回带 <see cref="PreparedVolumeGroup.EntryLayer"/> 的副本。
        /// 任何一片失败 ⇒ 整组回滚并返回 null。
        /// </summary>
        public static PreparedVolumeGroup? TryMaterialize(
            PreparedVolumeGroup? group,
            string? entryRoot,
            Action<string, string>? log,
            out string error,
            bool allowCrossVolumeCopy = true)
        {
            error = string.Empty;

            if (group == null)
            {
                error = "组为空";
                return null;
            }

            if (group.State != PreparedGroupState.Complete)
            {
                error = $"组结论是 {group.State}，不建标准名";
                return null;
            }

            if (group.Pieces.Count == 0)
            {
                error = "组里没有片";
                return null;
            }

            if (string.IsNullOrWhiteSpace(group.EntryCanonicalName))
            {
                error = "入口标准名为空";
                return null;
            }

            string sourcePath = group.Pieces[0].OriginalPath;

            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                error = "第一片的原始路径不在盘上";
                return null;
            }

            /*
             * ⛔ 不再回落盘根：合法入口根只能来自这一组的目标工作区/明确允许的源卷工作区。
             * 拿不到就不建目录、不动作，由调用方把组落成 Unknown。
             */
            string root = entryRoot ?? string.Empty;

            if (string.IsNullOrWhiteSpace(root))
            {
                error = "拿不到合法的临时入口根（不回落到盘根）";
                return null;
            }

            string directory = Path.Combine(
                root,
                SafeDirectoryName(group.BaseName) + "-" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(directory);
                MarkHidden(directory);
                MarkHidden(Path.GetDirectoryName(directory));
                MarkHidden(Path.GetDirectoryName(Path.GetDirectoryName(directory)));
            }
            catch (Exception ex)
            {
                error = $"临时入口目录建不出来：{ex.GetType().Name}：{ex.Message}";
                return null;
            }

            try
            {
                foreach (PreparedVolumePiece piece in group.Pieces)
                {
                    if (!IsSafeCanonicalName(piece.CanonicalName))
                    {
                        error = $"标准名不合法：{piece.CanonicalName}";
                        CleanupDirectory(directory, root);
                        return null;
                    }

                    if (string.IsNullOrWhiteSpace(piece.OriginalPath) || !File.Exists(piece.OriginalPath))
                    {
                        error = $"{piece.CanonicalName}：原始片不在盘上";
                        CleanupDirectory(directory, root);
                        return null;
                    }

                    string target = Path.Combine(directory, piece.CanonicalName);

                    if (File.Exists(target))
                    {
                        error = $"{piece.CanonicalName}：目标名已被占用";
                        CleanupDirectory(directory, root);
                        return null;
                    }

                    /*
                     * 同卷优先硬链接；跨卷（目标位置在另一个盘）复制一份到**目标工作区**。
                     * ⛔ 不移动、不改名用户源片；复制件只活在 group-entry 临时目录里。
                     */
                    if (HardLinkHelper.TryCreateHardLink(target, piece.OriginalPath))
                    {
                        continue;
                    }

                    /*
                     * 空间不足模式不允许跨盘复制：复制一份原包会把“新写空间 = 内容物”
                     * 变成“内容物 + 原包副本”，正好破坏这个模式的省空间前提。
                     * 这里不回落到 copy；调用方把这一组落成 Unknown，不写盘、不动源。
                     */
                    if (!allowCrossVolumeCopy)
                    {
                        error = $"{piece.CanonicalName}：跨盘无法建硬链接，空间不足模式禁止复制源包";
                        CleanupDirectory(directory, root);
                        return null;
                    }

                    try
                    {
                        File.Copy(piece.OriginalPath, target, overwrite: false);
                    }
                    catch (Exception ex)
                    {
                        error = $"{piece.CanonicalName}：入口链接/复制都失败：{ex.GetType().Name}：{ex.Message}";
                        CleanupDirectory(directory, root);
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                error = $"建标准名硬链接时出错：{ex.GetType().Name}：{ex.Message}";
                CleanupDirectory(directory, root);
                return null;
            }

            log?.Invoke(
                "INFO",
                $"临时组入口目录已建好：{directory}（{group.Pieces.Count} 条硬链接；源文件未动）");

            return group.WithEntryLayer(directory);
        }

        /// <summary>清理一个临时组入口目录；只删 <c>.ArchiveFixer.work\group-entry</c> 下的目录。</summary>
        /// <summary>
        /// 优先把标准名入口建在**任务自己的目标工作区**（&lt;目标目录&gt;\.ArchiveFixer.work\group-entry）；
        /// 只有目标盘与源片不同卷、硬链接做不了时才返回 null，让调用方回落源卷根。
        /// </summary>
        public static string? TryResolveTargetEntryRoot(
            PreparedVolumeGroup? group,
            string? targetWorkRoot,
            bool allowSourceVolumeFallback = false)
        {
            if (group == null
                || string.IsNullOrWhiteSpace(targetWorkRoot)
                || group.Pieces.Count == 0
                || string.IsNullOrWhiteSpace(group.Pieces[0].OriginalPath))
            {
                return null;
            }

            try
            {
                string sourcePath = group.Pieces[0].OriginalPath;
                string targetRoot = Path.GetFullPath(targetWorkRoot);
                string sourceRoot = Path.GetFullPath(Path.GetPathRoot(sourcePath) ?? sourcePath);
                string targetDrive = Path.GetPathRoot(targetRoot) ?? string.Empty;

                /*
                 * 空间不足模式：目标跨盘时不在目标盘复制源包。
                 * 标准名入口改放**源盘**自己的 .ArchiveFixer.work\group-entry，
                 * 用同盘硬链接（零数据复制），引擎读源盘、内容仍写目标盘。
                 * 普通模式保留目标工作区 + 跨盘复制兜底。
                 */
                if (allowSourceVolumeFallback
                    && !string.Equals(
                        sourceRoot.TrimEnd(Path.DirectorySeparatorChar),
                        targetDrive.TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                {
                    string sourceDirectory = Path.GetDirectoryName(sourcePath) ?? string.Empty;

                    if (sourceDirectory.Length > 0)
                    {
                        return Path.Combine(sourceDirectory, WorkFolderName, GroupEntryFolderName);
                    }
                }

                /*
                 * 合法目标工作区优先；普通模式跨卷时 TryMaterialize 允许复制，
                 * 这里不再用“能不能硬链接”把合法目标否掉。
                 */
                return Path.Combine(targetRoot, GroupEntryFolderName);
            }
            catch
            {
                return null;
            }
        }

        public static bool TryDelete(string? directory, string? entryRoot = null)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            string? root = string.IsNullOrWhiteSpace(entryRoot)
                ? FindEntryRoot(directory)
                : entryRoot;

            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            try
            {
                string fullDirectory = Path.GetFullPath(directory);
                string fullRoot = Path.GetFullPath(root);

                if (!fullDirectory.StartsWith(
                        fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            CleanupDirectory(directory, root);
            RemoveEmptyParents(directory);
            return !Directory.Exists(directory);
        }

        /// <summary>删掉组目录后，把这次自己建出来的空 <c>group-entry</c> / <c>.ArchiveFixer.work</c> 也收掉。</summary>
        /// <summary>
        /// 临时入口目录在源卷根上：必须带隐藏属性，普通资源管理器不显示。
        /// 只改属性，不动内容；失败不影响硬链接可用性。
        /// </summary>
        private static void MarkHidden(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (Directory.Exists(path))
                {
                    FileAttributes attributes = File.GetAttributes(path);

                    if ((attributes & FileAttributes.Hidden) == 0)
                    {
                        File.SetAttributes(path, attributes | FileAttributes.Hidden);
                    }
                }
            }
            catch
            {
                // 设隐藏失败不阻断临时入口；至少内容不会被误删。
            }
        }

        private static void RemoveEmptyParents(string directory)
        {
            try
            {
                string? groupEntry = Path.GetDirectoryName(directory);
                string? work = string.IsNullOrWhiteSpace(groupEntry)
                    ? null
                    : Path.GetDirectoryName(groupEntry);

                if (!string.IsNullOrWhiteSpace(groupEntry)
                    && Directory.Exists(groupEntry)
                    && !Directory.EnumerateFileSystemEntries(groupEntry).Any())
                {
                    Directory.Delete(groupEntry);
                }

                if (!string.IsNullOrWhiteSpace(work)
                    && Directory.Exists(work)
                    && !Directory.EnumerateFileSystemEntries(work).Any())
                {
                    Directory.Delete(work);
                }
            }
            catch
            {
                // 收不掉空壳只影响整洁，不影响这一组已经处理完的事实。
            }
        }

        private static string? FindEntryRoot(string directory)
        {
            try
            {
                string full = Path.GetFullPath(directory);
                string marker = Path.DirectorySeparatorChar + WorkFolderName + Path.DirectorySeparatorChar;

                int index = full.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);

                if (index < 0)
                {
                    return null;
                }

                return full[..(index + marker.Length + GroupEntryFolderName.Length)];
            }
            catch
            {
                return null;
            }
        }

        private static string SafeDirectoryName(string? baseName)
        {
            string name = string.IsNullOrWhiteSpace(baseName) ? "volume" : baseName!;
            var invalid = Path.GetInvalidFileNameChars();

            var builder = new System.Text.StringBuilder(name.Length);

            foreach (char ch in name)
            {
                builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            }

            string safe = builder.ToString().Trim();

            return safe.Length == 0 ? "volume" : safe;
        }

        private static bool IsSafeCanonicalName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            if (name!.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return false;
            }

            return string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal);
        }

        private static void CleanupDirectory(string directory, string root)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                RemoveEmptyParents(directory);
            }
            catch
            {
                // 清理失败不影响"这一组没有建好"的结论；下次批首/工作区清理会收。
            }
        }
    }
}
