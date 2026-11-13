using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Extraction
{    /// <summary>真实文件系统实现。</summary>
    public sealed class FileSystemSourceMoveFileSystem : ISourceMoveFileSystem
    {
        public static FileSystemSourceMoveFileSystem Instance { get; } = new();

        public bool FileExists(string path) => SafePathHelper.FileExists(path);

        public bool DirectoryExists(string path) => SafePathHelper.DirectoryExists(path);

        public bool CreateDirectory(string path) => SafePathHelper.EnsureDirectoryExists(path);

        public long GetFileSize(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                // 量不出大小只影响日志里的数字，不影响搬不搬。
                return 0;
            }
        }

        public bool IsSameVolume(string sourcePath, string targetPath)
        {
            try
            {
                string? sourceRoot = Path.GetPathRoot(SafePathHelper.GetFullPathSafe(sourcePath));
                string? targetRoot = Path.GetPathRoot(SafePathHelper.GetFullPathSafe(targetPath));

                if (string.IsNullOrWhiteSpace(sourceRoot) || string.IsNullOrWhiteSpace(targetRoot))
                {
                    // 判不出来时**保守地当成跨盘**：那条件更严（先复制成功再删原件），
                    // 代价只是多拷一次，而判错成同盘会走 rename —— 跨盘 rename 必然失败。
                    return false;
                }

                return string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // 两参数版本：目标已存在直接抛 IOException，**不会覆盖**（AGENTS.md §6 第 3 条）。
        public void MoveFile(string source, string target) => FileOperations.Move(source, target);

        public void CopyFile(string source, string target) => File.Copy(source, target, overwrite: false);

        public void DeleteFile(string path) => FileOperations.Delete(path);
    }



}
