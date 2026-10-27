using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **临时组入口目录**（共用骨架）：把一组的标准名做成硬链接，供 GroupAnalysis/引擎读取。
    ///
    /// <para>用户 2026-10-27 拍板 A：临时目录放在**源卷**上（硬链接不能跨卷），
    /// 只放标准名硬链接，跑完立即清理；⛔ 不复制、不移动、不改名用户源文件。</para>
    ///
    /// <para>形状：<c>&lt;源卷&gt;\.ArchiveFixer.work\group-entry\&lt;基名&gt;-&lt;guid&gt;\</c>。
    /// 生产环境不传 <c>entryRoot</c> 时走这条；测试可传同卷临时根。</para>
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
            out string error)
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

            string root = string.IsNullOrWhiteSpace(entryRoot)
                ? BuildDefaultEntryRoot(sourcePath)
                : entryRoot!;

            if (string.IsNullOrWhiteSpace(root))
            {
                error = "拿不到源卷上的临时入口根";
                return null;
            }

            string directory = Path.Combine(
                root,
                SafeDirectoryName(group.BaseName) + "-" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(directory);
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

                    if (!HardLinkHelper.CanHardLink(piece.OriginalPath, directory))
                    {
                        error = $"{piece.CanonicalName}：源片与临时入口根不在同一卷，硬链接做不了";
                        CleanupDirectory(directory, root);
                        return null;
                    }

                    if (!HardLinkHelper.TryCreateHardLink(target, piece.OriginalPath))
                    {
                        error = $"{piece.CanonicalName}：硬链接建不出来";
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
            return !Directory.Exists(directory);
        }

        private static string BuildDefaultEntryRoot(string sourcePath)
        {
            try
            {
                string? volumeRoot = Path.GetPathRoot(Path.GetFullPath(sourcePath));

                if (string.IsNullOrWhiteSpace(volumeRoot))
                {
                    return string.Empty;
                }

                return Path.Combine(volumeRoot, WorkFolderName, GroupEntryFolderName);
            }
            catch
            {
                return string.Empty;
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
            }
            catch
            {
                // 清理失败不影响"这一组没有建好"的结论；下次批首/工作区清理会收。
            }
        }
    }
}
