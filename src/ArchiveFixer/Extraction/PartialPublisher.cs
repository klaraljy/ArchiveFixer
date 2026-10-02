using ArchiveFixer.Helpers;
using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **部分完成发布**：把"逐条核对过、确实解出来了"的那些文件放进
    /// <c>&lt;目标&gt;\&lt;包名&gt;\部分完成\</c>（用户 2026-10-02 拍板的口径 A）。
    ///
    /// <para><b>为什么单独一个固定子目录</b>（而不是走正常落点）：</para>
    /// <list type="number">
    /// <item><description>正常落点要求"最里层 = 最后一个内层包那一层"，而部分完成时**那个内层包本身可能正是坏的**
    /// ⇒ 那条规则要么报错、要么把最里层吃掉（后者是明令禁止的）；</description></item>
    /// <item><description>重跑同一份包时**不会撞名**（上一份在 <c>部分完成\</c> 里，这一次还是进同一个目录，
    /// 同名只让位、绝不覆盖）；</description></item>
    /// <item><description>用户一眼看得出"这是没做完的那一份"。</description></item>
    /// </list>
    ///
    /// <para><b>⛔ 四条红线（本类自己守，不靠调用方）</b>：</para>
    /// <list type="number">
    /// <item><description>**绝不覆盖**任何已存在的文件：同名一律让位（<c>名字(1).bin</c>）；
    /// 让位后仍然存在 ⇒ 干脆不搬那一个（宁可少发布一个文件，也不覆盖）；</description></item>
    /// <item><description>**绝不越出** <c>部分完成\</c> 这一层：计划里带 <c>..</c> / 绝对路径 / 盘符的一律拒搬；</description></item>
    /// <item><description>**绝不碰源包**：本类只动暂存目录（我们自己造的）与目标目录里的 <c>部分完成\</c>；</description></item>
    /// <item><description>**搬不动就如实记一笔并留给调用方**：调用方据 <see cref="Result.AllMoved"/>
    /// 决定要不要清工作区（没全搬成就不清，免得把没搬走的内容一起删掉）。</description></item>
    /// </list>
    /// </summary>
    public static class PartialPublisher
    {
        /// <summary>固定子目录名（口径 A：不参与落点规则，字面量只在这里写一次）。</summary>
        public const string DirectoryName = "部分完成";

        /// <summary>一次部分发布的结论。</summary>
        public sealed class Result
        {
            /// <summary>真动过手（目标目录建出来了、计划非空）。false = 一个字节都没动。</summary>
            public bool Attempted { get; init; }

            /// <summary>发布落点（<c>&lt;包名&gt;\部分完成</c>）；没动手时为空串。</summary>
            public string DestinationDirectory { get; init; } = string.Empty;

            /// <summary>搬成功的文件数 / 字节数。</summary>
            public int MovedCount { get; init; }

            public long MovedBytes { get; init; }

            /// <summary>因为同名而让位（改成 <c>名字(1).bin</c>）的个数。</summary>
            public int RenamedCount { get; init; }

            /// <summary>计划里有、但没能发布的那些（相对路径 + 一句原因）；空 = 全部搬成。</summary>
            public IReadOnlyList<(string Path, string Reason)> Failures { get; init; } =
                Array.Empty<(string, string)>();

            /// <summary>计划里的文件**全部**搬成了（调用方据此决定要不要清工作区）。</summary>
            public bool AllMoved => Failures.Count == 0 && MovedCount > 0;
        }

        /// <summary>
        /// 执行发布。⛔ 只搬 <paramref name="publishable"/> 里点名的那些文件，目录里的别的东西一个都不碰。
        /// </summary>
        /// <param name="stagingRoot">已解出来的东西在哪（本任务的工作区 / 暂存目录）。</param>
        /// <param name="publishable">逐条核对过、确认可以发布的文件（相对路径 + 字节数）。</param>
        /// <param name="packageOutputRoot">这一份包的输出根（<c>&lt;目标&gt;\&lt;包名&gt;</c>）。</param>
        /// <param name="probe">占用探测（用例注入用；生产走真实文件系统）。</param>
        public static Result Publish(
            string? stagingRoot,
            IReadOnlyList<(string Path, long Size)>? publishable,
            string? packageOutputRoot,
            IArtifactTargetProbe? probe = null)
        {
            var failures = new List<(string, string)>();

            if (string.IsNullOrWhiteSpace(stagingRoot) ||
                string.IsNullOrWhiteSpace(packageOutputRoot) ||
                publishable == null ||
                publishable.Count == 0)
            {
                return new Result { Attempted = false };
            }

            string staging = SafePathHelper.GetFullPathSafe(stagingRoot);

            if (staging.Length == 0 || !SafePathHelper.DirectoryExists(staging))
            {
                return new Result { Attempted = false };
            }

            string destination = SafePathHelper.Combine(packageOutputRoot, DirectoryName);
            string destinationFull = SafePathHelper.GetFullPathSafe(destination);

            if (destinationFull.Length == 0)
            {
                return new Result { Attempted = false };
            }

            try
            {
                Directory.CreateDirectory(destinationFull);
            }
            catch (Exception ex)
            {
                // 建不出落点 ⇒ 一个字节都不搬（判不出 / 动不了 ⇒ 什么都不做）。
                return new Result
                {
                    Attempted = false,
                    Failures = new[] { (DirectoryName, $"发布落点建不出来：{ex.Message}") }
                };
            }

            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IArtifactTargetProbe targetProbe =
                probe ?? FileSystemArtifactTargetProbe.Instance;

            int moved = 0;
            int renamed = 0;
            long movedBytes = 0;

            foreach ((string relativePath, long size) in publishable)
            {
                string relative = (relativePath ?? string.Empty).Replace('/', '\\').TrimStart('\\');

                if (relative.Length == 0 || IsEscapeAttempt(relative))
                {
                    failures.Add((relativePath ?? string.Empty, "计划里的相对路径不合法（可能越出落点）—— 拒搬"));
                    continue;
                }

                string source = SafePathHelper.Combine(staging, relative);

                if (!SafePathHelper.FileExists(source))
                {
                    failures.Add((relative, "计划里有它，可暂存目录里已经没有了 —— 没搬"));
                    continue;
                }

                string target = SafePathHelper.Combine(destinationFull, relative);
                string targetFull = SafePathHelper.GetFullPathSafe(target);
                string targetDirectory = Path.GetDirectoryName(targetFull) ?? string.Empty;

                // 第二道越界闸门：拼出来的落点必须仍在 部分完成\ 这一层之下。
                if (targetFull.Length == 0 ||
                    !IsUnder(destinationFull, targetFull))
                {
                    failures.Add((relative, "拼出来的落点越出了「部分完成」目录 —— 拒搬"));
                    continue;
                }

                try
                {
                    if (targetDirectory.Length > 0)
                    {
                        Directory.CreateDirectory(targetDirectory);
                    }

                    /*
                     * ⚠ 落点**被占了**才去要一个"不撞名的名字"。
                     * ⛔ 不能无条件调 `MakeUniqueTarget` —— 它**从不返回原名**（循环从 `(1)` 起），
                     * 无条件调会把每一个文件都改名成 `xxx(1)`（写这一版时自测当场踩到，两条用例红）。
                     */
                    string unique = reserved.Contains(targetFull) || targetProbe.Exists(targetFull)
                        ? ProcessArtifactLayout.MakeUniqueTarget(
                            targetFull,
                            isDirectory: false,
                            reserved,
                            targetProbe)
                        : targetFull;

                    reserved.Add(unique);

                    if (!string.Equals(unique, targetFull, StringComparison.OrdinalIgnoreCase))
                    {
                        renamed++;
                    }

                    /*
                     * ⛔ 最后一道不覆盖：让位算完之后目标还在（并发 / 探测失真）⇒ 这一个不搬。
                     * 宁可少发布一个文件，也不覆盖用户上次那半份。
                     */
                    if (SafePathHelper.FileExists(unique) || SafePathHelper.DirectoryExists(unique))
                    {
                        failures.Add((relative, "落点已被占用（让位之后还在）—— 不覆盖，这一个没搬"));
                        continue;
                    }

                    File.Move(source, unique);

                    moved++;
                    movedBytes += size > 0 ? size : 0;
                }
                catch (Exception ex)
                {
                    failures.Add((relative, ex.Message));
                }
            }

            return new Result
            {
                Attempted = true,
                DestinationDirectory = destinationFull,
                MovedCount = moved,
                MovedBytes = movedBytes,
                RenamedCount = renamed,
                Failures = failures
            };
        }

        /// <summary>计划里的相对路径想往上跑（<c>..</c> / 绝对路径 / 盘符）—— 一律拒。</summary>
        private static bool IsEscapeAttempt(string relative)
        {
            if (relative.StartsWith(@"\\", StringComparison.Ordinal) ||
                relative.StartsWith("/", StringComparison.Ordinal))
            {
                return true;
            }

            if (relative.Length >= 2 && relative[1] == ':')
            {
                return true;
            }

            foreach (string segment in relative.Split('\\'))
            {
                if (string.Equals(segment, "..", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary><paramref name="candidate"/> 是不是在 <paramref name="root"/> 这一层之下（含更深层）。</summary>
        private static bool IsUnder(string root, string candidate)
        {
            string normalizedRoot = root.TrimEnd('\\');

            return candidate.StartsWith(normalizedRoot + "\\", StringComparison.OrdinalIgnoreCase);
        }
    }
}
