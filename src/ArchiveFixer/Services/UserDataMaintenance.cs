using System;
using System.IO;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 旧版本留在数据根下的日志/临时空壳清理。
    ///
    /// <para>日志正式目录已经跟程序目录走（PathService.LogsDirectory）；旧版本曾写在
    /// <c>%LOCALAPPDATA%\ArchiveFixer\logs</c>。这里只清旧路径下**我们自己命名**的日志文件，
    /// 不碰当前正式日志目录、不碰设置/密码、不递归扫用户目录。</para>
    /// </summary>
    internal static class UserDataMaintenance
    {
        internal static void CleanupLegacyLocalLogs(string dataRoot, string currentLogsDirectory)
        {
            if (string.IsNullOrWhiteSpace(dataRoot)
                || string.IsNullOrWhiteSpace(currentLogsDirectory))
            {
                return;
            }

            string legacyLogs = Path.Combine(dataRoot, "logs");

            if (PathsEqual(legacyLogs, currentLogsDirectory)
                || !Directory.Exists(legacyLogs))
            {
                return;
            }

            try
            {
                foreach (string file in Directory.EnumerateFiles(legacyLogs, "ArchiveFixer_*", SearchOption.TopDirectoryOnly))
                {
                    File.Delete(file);
                }

                foreach (string file in Directory.EnumerateFiles(legacyLogs, "fatal_*", SearchOption.TopDirectoryOnly))
                {
                    File.Delete(file);
                }

                if (Directory.GetFileSystemEntries(legacyLogs).Length == 0)
                {
                    Directory.Delete(legacyLogs);
                }
            }
            catch
            {
                // 旧日志清不掉不影响启动；下次启动再试。
            }
        }

        internal static void CleanupEmptyLegacyLocalTemp(string dataRoot, string currentTempDirectory)
        {
            if (string.IsNullOrWhiteSpace(dataRoot)
                || string.IsNullOrWhiteSpace(currentTempDirectory))
            {
                return;
            }

            string legacyTemp = Path.Combine(dataRoot, "temp");

            if (PathsEqual(legacyTemp, currentTempDirectory)
                || !Directory.Exists(legacyTemp))
            {
                return;
            }

            try
            {
                if (Directory.GetFileSystemEntries(legacyTemp).Length == 0)
                {
                    Directory.Delete(legacyTemp);
                }
            }
            catch
            {
                // 空壳清不掉不影响启动。
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
