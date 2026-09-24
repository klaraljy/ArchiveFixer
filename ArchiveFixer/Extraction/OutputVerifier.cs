using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Engines;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 产物校验的**机器可判**结论（AGENTS.md §7：统计与判定不得依赖中文文案比较）。
    ///
    /// <para>
    /// 为什么一定要有它：清理源包 / 危险模式删其余物 / 续解下一层 / 汇总分桶全都以"这一单到底算出东西了吗"
    /// 为前提，而这个前提以前只能靠 <c>task.Status</c> 的中文字符串去猜。用户 2026-09-24 的真机日志里
    /// 恰好出现了最坏那种情形：**校验已经判否，状态却仍是「解压成功」** ——
    /// 于是"删源 / 搬源 / 续解"三条路会一起误判（那一次源包侥幸没被删，是因为另有一条碰巧的守卫）。
    /// 现在这些裁决一律读这个枚举，读的是**校验那一刻的事实**，与后来谁改了状态字符串无关。
    /// </para>
    /// </summary>
    public enum OutputVerificationOutcome
    {
        /// <summary>还没做过校验（没解压 / 越界这类在定稿之前就结束的情形）。</summary>
        NotAttempted = 0,

        /// <summary>校验通过：产物非空，且与引擎声明的条目数 / 总字节数对得上。</summary>
        Passed = 1,

        /// <summary>校验**判否**：产物为空、或少于预期。这一档永远不许被当成成功（不变量 6）。</summary>
        Failed = 2
    }

    /// <summary>
    /// 解压产物校验结果（M3「一批包一键搞定」）。
    /// </summary>
    public sealed class OutputVerificationResult
    {
        public bool Verified { get; init; }

        /// <summary>机器可判的结论（<see cref="Verified"/> 的枚举形态；二者恒一致）。</summary>
        public OutputVerificationOutcome Outcome =>
            Verified ? OutputVerificationOutcome.Passed : OutputVerificationOutcome.Failed;

        /// <summary>
        /// 用户可读的失败结论（**只在判否时用**）：一句话说清"产物校验未通过 + 预期与实际"。
        /// 任务状态与失败清单都写它 —— 用户 2026-09-24 要求错误信息里必须带这三个数字。
        /// </summary>
        public string FailureMessage =>
            Message.StartsWith("校验未通过", StringComparison.Ordinal)
                ? "产物" + Message
                : "产物校验未通过：" + Message;

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
    ///
    /// <para>
    /// <b>产物为空（0 个文件 / 全是 0 字节 / 总字节为 0）一律判否</b>（用户 2026-09-24 铁证）：
    /// "预期 0 个文件 / 0 字节、实际也 0 字节"不是"校验通过"，而是"什么都没解出来"。
    /// </para>
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
                        ? "校验未通过：输出目录是空目录，没有产物"
                        : "校验未通过：输出目录不存在，没有产物"
                };
            }

            /*
             * 规则 2：**产物总字节为 0** —— 一律判否（用户 2026-09-24 真机铁证，本条是新增的）。
             *
             * 现场：外层 ZIP 里有加密条目，7z 半成功，写出两个 **0 字节**的 `*.7z.001/.002`；
             * 老口径下"文件数够、字节数也够（拿 0 比 0）"居然判了**通过**，于是
             * ① 任务落「解压成功」② 0 字节的 `*.7z.001` 被当成内层包继续解 ③ 那 2 项还进了 `其余物`。
             * 一条 0 字节的记录根本不是产物：它既不能看、也不能再解，把它算成"解出来了"就是骗人。
             *
             * 位置刻意放在"没有可信预期"那条**之前**：预期拿不到（列目录失败 / `-mhe` 读不出清单）时
             * 老口径只做"目录非空"的底线校验 —— 一堆 0 字节垃圾照样能把那个底线混过去。
             */
            if (actualTotalSize <= 0)
            {
                return new OutputVerificationResult
                {
                    Verified = false,
                    ExpectedFileCount = expectedFileCount,
                    ActualFileCount = actualFileCount,
                    ExpectedTotalSize = expectedTotalSize,
                    ActualTotalSize = actualTotalSize,
                    Message = expected == null || !expected.Success
                        ? $"校验未通过：产物为空 —— 输出目录里有 {actualFileCount} 个文件，但全是 0 字节（没有解出任何内容）"
                        : $"校验未通过：产物为空 —— 预期 {expectedFileCount} 个文件 / {expectedTotalSize} 字节，" +
                          $"实际 {actualFileCount} 个 / {actualTotalSize} 字节（解出来的全是 0 字节文件）"
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
