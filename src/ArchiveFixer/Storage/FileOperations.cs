using System;
using System.IO;
using System.Threading;

namespace ArchiveFixer.Storage
{
    /// <summary>
    /// 文件删除/移动的统一入口。ViewModels 等业务代码不许直接调用 <see cref="File"/>；
    /// 删除闸门、日志与回滚策略以后都挂在这里。
    /// </summary>
    public static class FileOperations
    {
        public static void Delete(string path) => File.Delete(path);

        public static void Move(string source, string destination) => File.Move(source, destination);

        public static void Move(string source, string destination, bool overwrite) =>
            File.Move(source, destination, overwrite);

        /// <summary>
        /// 删除工作区目录：长路径前缀 + 清只读属性 + 重试；不把“删不掉”伪装成成功。
        /// 只用于调用方已经验证过“这是自己造的工作区目录”的路径。
        /// </summary>
        public static bool TryDeleteDirectory(string? directory, out string error)
        {
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return true;
            }

            string extended = ToExtendedPath(directory);
            Exception? last = null;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    ClearReadOnlyAttributes(extended);
                    Directory.Delete(extended, recursive: true);
                    return true;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Thread.Sleep(100);
                }
            }

            error = last?.Message ?? "未知删除错误";
            return false;
        }

        private static string ToExtendedPath(string path)
        {
            string full = Path.GetFullPath(path);

            if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                return full;
            }

            if (full.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return @"\\?\UNC\" + full.Substring(2);
            }

            return @"\\?\" + full;
        }

        private static void ClearReadOnlyAttributes(string extendedPath)
        {
            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(extendedPath, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(entry);

                        if ((attributes & FileAttributes.ReadOnly) != 0)
                        {
                            File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                        }
                    }
                    catch
                    {
                        // 单个条目清不掉不阻断整体尝试。
                    }
                }
            }
            catch
            {
                // 枚举本身失败时仍然尝试直接删除。
            }
        }
    }
}
