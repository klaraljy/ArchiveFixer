using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Engines;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 解压产物校验结果（M3「一批包一键搞定」）。
    /// </summary>
    public sealed class OutputVerificationResult
    {
        public bool Verified { get; init; }

        public int ExpectedFileCount { get; init; }

        public int ActualFileCount { get; init; }

        public long ExpectedTotalSize { get; init; }

        public long ActualTotalSize { get; init; }

        /// <summary>给人看的一句话（直接进汇总报告与日志，不要在这里拼密码等敏感内容）。</summary>
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// 解压结果校验。
    ///
    /// 定位：它是**清理源包（不可逆操作）的唯一前置门槛**，所以刻意做得很保守 ——
    /// 只比"文件数 + 总字节数"，且一律用 &gt;= 比较；宁可判定不通过（源包留着），
    /// 也不要把没解压全的包当成成功、进而删掉用户的源文件。
    /// </summary>
    public static class OutputVerifier
    {
        /// <summary>递归统计目录下的文件数与总字节数。目录不存在返回 (0,0)，不抛。</summary>
        public static (int FileCount, long TotalSize) Measure(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return (0, 0);
            }

            if (!SafeDirectoryExists(directory))
            {
                return (0, 0);
            }

            /*
             * 输出目录自身就是符号链接 / 联接点时直接按"没有产物"处理。
             * 理由：跟着链接统计到的字节数完全不能代表本次解压的结果（可能落在别的盘、别的目录），
             * 而这种误判的后果是"校验通过 → 删源包"，属于不可逆错误，必须往保守一侧倒。
             */
            if (!TryGetAttributes(directory, out FileAttributes rootAttributes) ||
                (rootAttributes & FileAttributes.ReparsePoint) != 0)
            {
                return (0, 0);
            }

            int fileCount = 0;
            long totalSize = 0;

            // 用显式栈而不是递归：解压产物可能有很深的目录层级，递归写法有栈溢出风险。
            var pending = new Stack<string>();
            pending.Push(directory);

            while (pending.Count > 0)
            {
                string currentDirectory = pending.Pop();
                string[] entries;

                try
                {
                    entries = Directory.GetFileSystemEntries(currentDirectory);
                }
                catch
                {
                    // 权限不足、目录刚好被删掉等等：跳过这一层继续统计。
                    // 单个目录读不了不能把整次校验变成"一个文件都没有"。
                    continue;
                }

                foreach (string entry in entries)
                {
                    if (!TryGetAttributes(entry, out FileAttributes attributes))
                    {
                        continue;
                    }

                    /*
                     * 不跟随符号链接 / 目录联接点：
                     * 跟随可能绕圈（链接指回祖先目录 → 死循环），
                     * 也可能把链接目标位置的无关文件算进本次产物。
                     */
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }

                    fileCount++;

                    try
                    {
                        totalSize += new FileInfo(entry).Length;
                    }
                    catch
                    {
                        // 大小读不出来时仍然算作一个文件、字节按 0 记：
                        // 校验用 >= 比较，少算字节只会更保守，不会把"缺文件"误判成通过。
                    }
                }
            }

            return (fileCount, totalSize);
        }

        /// <summary>
        /// 解压后校验。expected 为 null 或 Success=false 时：只做“输出目录存在且非空”的底线校验。
        /// </summary>
        public static OutputVerificationResult Verify(string? outputDirectory, ArchiveListResult? expected)
        {
            int expectedFileCount = expected?.FileCount ?? 0;
            long expectedTotalSize = expected?.TotalUncompressedSize ?? 0;

            bool directoryExists = SafeDirectoryExists(outputDirectory);
            (int actualFileCount, long actualTotalSize) = Measure(outputDirectory);

            /*
             * 规则 1：输出目录不存在 / 是空目录 —— 直接不通过。
             * 必须放在最前面：后面两条规则都以"确实有产物"为前提，
             * 一个文件都没有时比较预期数量毫无意义。
             */
            if (actualFileCount <= 0)
            {
                return new OutputVerificationResult
                {
                    Verified = false,
                    ExpectedFileCount = expectedFileCount,
                    ActualFileCount = actualFileCount,
                    ExpectedTotalSize = expectedTotalSize,
                    ActualTotalSize = actualTotalSize,
                    Message = directoryExists
                        ? "输出目录是空目录，没有产物"
                        : "输出目录不存在，没有产物"
                };
            }

            /*
             * 规则 3：没有可信的预期条目（没列过目录，或列目录失败）时，
             * 只能做"输出目录非空"的底线校验。
             * Message 必须写明这一点，否则用户会以为做过完整校验、进而相信后面的删源包。
             */
            if (expected == null || !expected.Success)
            {
                return new OutputVerificationResult
                {
                    Verified = true,
                    ExpectedFileCount = 0,
                    ActualFileCount = actualFileCount,
                    ExpectedTotalSize = 0,
                    ActualTotalSize = actualTotalSize,
                    Message = $"未取得预期条目数，只做了非空校验：输出目录里有 {actualFileCount} 个文件 / {actualTotalSize} 字节"
                };
            }

            /*
             * 规则 4：用 >= 而不是 ==。
             * ① 用户可能把产物解压进一个本来就有东西的目录；
             * ② 归档里可能有重复条目（同名同内容的多份），展开后的数量与条目数未必一一对应。
             * 用 == 会把这两种正常情况判成"解压不完整"，白白拦下清理源包。
             */
            bool verified = actualFileCount >= expectedFileCount && actualTotalSize >= expectedTotalSize;

            return new OutputVerificationResult
            {
                Verified = verified,
                ExpectedFileCount = expectedFileCount,
                ActualFileCount = actualFileCount,
                ExpectedTotalSize = expectedTotalSize,
                ActualTotalSize = actualTotalSize,
                Message = verified
                    ? $"校验通过：预期 {expectedFileCount} 个文件 / {expectedTotalSize} 字节，实际 {actualFileCount} 个 / {actualTotalSize} 字节"
                    : $"校验未通过：预期 {expectedFileCount} 个文件 / {expectedTotalSize} 字节，实际 {actualFileCount} 个 / {actualTotalSize} 字节"
            };
        }

        private static bool SafeDirectoryExists(string? path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetAttributes(string path, out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(path);
                return true;
            }
            catch
            {
                attributes = default;
                return false;
            }
        }
    }
}
