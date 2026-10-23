using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;


namespace ArchiveFixer.ViewModels
{
    /// <summary>输出目录认领（同一目录只许一个任务写；第 12 块，同一 partial 类）。</summary>
    internal sealed partial class ExtractionCoordinator
    {
        private readonly HashSet<string> _claimedOutputDirectories = new(StringComparer.OrdinalIgnoreCase);

        private readonly object _outputClaimLock = new();

        /// <summary>
        /// 把一个"还不存在的最终目录"在进程内占下来；被别的正在跑的任务占了就换 <c>名字(1)</c>。
        ///
        /// <para><c>(i)</c> 的命名规则与 <see cref="SafePathHelper.AutoRenameDirectoryPath"/> 一致
        /// （同一个用户可见约定），区别只有一个：那里判"磁盘上有没有"，这里还要判"有没有被并发任务占下"——
        /// 被占下的目录此刻在磁盘上还不存在，所以那个方法看不见它。</para>
        /// </summary>
        private string ClaimOutputDirectory(string requestedPath, ArchiveTask task, ref string outputRedirectNote)
        {
            lock (_outputClaimLock)
            {
                string candidate = requestedPath;

                for (int attempt = 1; attempt <= MaxOutputClaimAttempts; attempt++)
                {
                    string full = SafePathHelper.GetFullPathSafe(candidate);

                    if (string.IsNullOrWhiteSpace(full))
                    {
                        return candidate;
                    }

                    bool claimedByAnotherTask = _claimedOutputDirectories.Contains(full);

                    // 磁盘上真出现了也算"占不到"（TOCTOU 的温和版本：两次系统调用之间别人建了）。
                    bool existsOnDisk = Directory.Exists(candidate);

                    if (!claimedByAnotherTask && !existsOnDisk)
                    {
                        _claimedOutputDirectories.Add(full);

                        if (!string.Equals(candidate, requestedPath, StringComparison.OrdinalIgnoreCase))
                        {
                            /*
                             * 与"目录已存在"那条路同一口径：实际落点与打算的落点不一致**不许静默**。
                             * 日志 + outputRedirectNote（会进任务详情的校验结论）+ task.OutputPath 回写，三处都要有。
                             */
                            string note =
                                $"原定输出目录 {requestedPath} 正被另一个正在解压的任务使用，本次实际输出到 {candidate}";

                            outputRedirectNote = string.IsNullOrWhiteSpace(outputRedirectNote)
                                ? note
                                : outputRedirectNote + "；" + note;

                            AppendLog(
                                "WARN",
                                $"{task.LogName}：同一个名字的包正在并行解压，为避免两个任务写进同一个目录，" +
                                $"本次落成 {candidate}（绝不合并、绝不覆盖）。");
                        }

                        return candidate;
                    }

                    try
                    {
                        string? parent = Path.GetDirectoryName(requestedPath);
                        string name = Path.GetFileName(requestedPath);

                        if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(name))
                        {
                            return candidate;
                        }

                        candidate = Path.Combine(parent, $"{name}({attempt})");
                    }
                    catch
                    {
                        return candidate;
                    }
                }

                return candidate;
            }
        }

        /// <summary>释放最终目录占位（任务收尾时调；没占过就是一次空操作）。</summary>
        private void ReleaseOutputDirectoryClaim(string? outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return;
            }

            string full = SafePathHelper.GetFullPathSafe(outputPath);

            if (string.IsNullOrWhiteSpace(full))
            {
                return;
            }

            lock (_outputClaimLock)
            {
                _claimedOutputDirectories.Remove(full);
            }
        }


        /// <summary>占位失败时的兜底尝试上限（与 <c>SafePathHelper.AutoRenameDirectoryPath</c> 同一个量级）。</summary>
        private const int MaxOutputClaimAttempts = 10000;

    }
}