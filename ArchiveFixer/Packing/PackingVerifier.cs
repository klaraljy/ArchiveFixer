using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.Packing
{
    /// <summary>产物校验的结论。</summary>
    public sealed class PackingVerification
    {
        public bool Ok { get; init; }

        /// <summary>给人看的一句话（对上了写"数出几个、分别是哪一层"，不对写缺什么 / 多什么）。</summary>
        public string Detail { get; init; } = string.Empty;

        /// <summary>逐条问题（为空 = 通过）。</summary>
        public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// 打完包**核对产物**（不变量 6：对不上就不许显示成功）。
    ///
    /// <para>纯函数：喂进"rar 里实际有哪些条目" + "B 里实际有哪些分卷"，回答"对不对得上"。
    /// 为什么要拆出来：这条判据必须能被**确定性地**测（造一份"少了一卷"的条目表就行），
    /// 而不是只能靠真跑一次 WinRAR 才知道它有没有生效。</para>
    ///
    /// <para>两个判据（docs/打包功能.md §6 最后两行）：</para>
    /// <list type="number">
    /// <item><description>B 里的每一卷都在 rar 里（按文件名比，不区分大小写）；
    /// rar 里也没有计划外的条目（除 B 这一层目录本身之外）；</description></item>
    /// <item><description>rar 里有 <c>B</c> 这一层（条目名以 <c>B\</c> 开头，或就是 <c>B</c> 自己）
    /// —— 用户解出来必须看到一个文件夹，而不是一堆散着的分卷。</description></item>
    /// </list>
    /// </summary>
    public static class PackingVerifier
    {
        /// <summary>
        /// 把 <c>Rar.exe lb</c>（bare listing，一行一个名字）的输出切成条目名。
        ///
        /// <para>选 <c>lb</c> 而不是 <c>l</c>：后者带表头 / 分隔线 / 汇总行，而且列宽随内容变，
        /// 解析它等于把"某一版 RAR 的排版"钉进代码里。</para>
        /// </summary>
        public static IReadOnlyList<string> ParseBareList(string? output)
        {
            var entries = new List<string>();

            if (string.IsNullOrWhiteSpace(output))
            {
                return entries;
            }

            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim('\r', ' ', '\t');

                if (line.Length == 0)
                {
                    continue;
                }

                entries.Add(line);
            }

            return entries;
        }

        /// <summary>
        /// 把 <c>7z l -slt</c>（技术信息）的输出切成条目名。
        ///
        /// <para><b>为什么只取分隔线之后的那一段</b>：<c>-slt</c> 的输出分两块 ——
        /// 第一块描述**归档自己**（以 <c>Path = &lt;归档路径&gt;</c> 开头，本机 7-Zip 26.03 实测），
        /// 第二块才是条目。分隔线（十个 <c>-</c>）之后才是条目块；不切掉第一块的话，
        /// 归档路径本身会被当成一条"计划外条目"，产物校验会**永远不通过**。</para>
        ///
        /// <para>只认 <c>Path = </c> 这一种键：条目名可能含空格、<c>=</c>、中文，
        /// 按"键 = 值"切成两段正好（值与名字都是原样的）。</para>
        /// </summary>
        public static IReadOnlyList<string> ParseSevenZipSltList(string? output)
        {
            var entries = new List<string>();

            if (string.IsNullOrWhiteSpace(output))
            {
                return entries;
            }

            bool insideEntries = false;

            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim('\r', ' ', '\t');

                if (line.Length == 0)
                {
                    continue;
                }

                if (!insideEntries)
                {
                    // 分隔线：一串 '-'（归档块的 "--" 只有两段，条目分隔线是十个，所以要求至少 5 个）。
                    if (line.Length >= 5 && line.All(c => c == '-'))
                    {
                        insideEntries = true;
                    }

                    continue;
                }

                if (line.StartsWith("Path = ", StringComparison.Ordinal))
                {
                    string name = line["Path = ".Length..].Trim();

                    if (name.Length > 0)
                    {
                        entries.Add(name);
                    }
                }
            }

            return entries;
        }

        /// <summary>条目名是不是"在 <paramref name="folderName"/> 这一层下面"（含这一层目录自己）。</summary>
        public static bool IsWithinFolder(string? entry, string folderName)
        {
            if (string.IsNullOrWhiteSpace(entry) || string.IsNullOrWhiteSpace(folderName))
            {
                return false;
            }

            string normalized = entry.Replace('/', '\\').TrimEnd('\\');

            if (string.Equals(normalized, folderName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return normalized.StartsWith(folderName + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>条目名的最末一段（文件名）。</summary>
        public static string GetEntryFileName(string? entry)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                return string.Empty;
            }

            string normalized = entry.Replace('/', '\\').TrimEnd('\\');
            int index = normalized.LastIndexOf('\\');

            return index >= 0 ? normalized[(index + 1)..] : normalized;
        }

        /// <summary>
        /// 核对：<paramref name="entries"/> 是外层容器里的条目，<paramref name="volumes"/> 是 B 里的分卷。
        /// </summary>
        /// <param name="entries">条目名（rar 用 <c>ParseBareList</c>，7z 用 <c>ParseSevenZipSltList</c>）。</param>
        /// <param name="folderName">B 的目录名（归档里该有的那一层）。</param>
        /// <param name="volumes">B 里实际切出来的分卷。</param>
        /// <param name="artifactName">
        /// 外层产物在句子里的名字（<c>rar</c> / <c>7z</c>）。外层容器三选一之后，"rar 里少了…"这种话
        /// 在 7z 容器下就是错的 —— 结论与失败原因会原样给用户看，所以名字必须跟着容器走。
        /// </param>
        public static PackingVerification Verify(
            IReadOnlyList<string>? entries,
            string folderName,
            IReadOnlyList<PackingVolume>? volumes,
            string artifactName = "rar")
        {
            var problems = new List<string>();
            IReadOnlyList<string> list = entries ?? Array.Empty<string>();
            IReadOnlyList<PackingVolume> expected = volumes ?? Array.Empty<PackingVolume>();
            string artifact = string.IsNullOrWhiteSpace(artifactName) ? "外层容器" : artifactName;

            if (list.Count == 0)
            {
                return new PackingVerification
                {
                    Ok = false,
                    Detail = $"{artifact} 里一条条目都读不出来，没法确认它装的是什么。",
                    Problems = new[] { "条目表为空" }
                };
            }

            if (expected.Count == 0)
            {
                return new PackingVerification
                {
                    Ok = false,
                    Detail = "B 里一个分卷都没有，产物不完整。",
                    Problems = new[] { "B 里没有分卷" }
                };
            }

            var expectedNames = new HashSet<string>(
                expected.Select(v => v.FileName),
                StringComparer.OrdinalIgnoreCase);

            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outsideFolder = new List<string>();

            foreach (string entry in list)
            {
                string fileName = GetEntryFileName(entry);

                if (expectedNames.Contains(fileName))
                {
                    seenNames.Add(fileName);
                    continue;
                }

                if (!IsWithinFolder(entry, folderName))
                {
                    outsideFolder.Add(entry);
                }
            }

            var missing = expected
                .Where(v => !seenNames.Contains(v.FileName))
                .Select(v => v.FileName)
                .ToList();

            if (missing.Count > 0)
            {
                problems.Add($"{artifact} 里少了这些分卷：" + string.Join('、', missing));
            }

            if (outsideFolder.Count > 0)
            {
                problems.Add($"{artifact} 里有 B 这一层之外的东西：" + string.Join('、', outsideFolder.Take(5)));
            }

            if (problems.Count > 0)
            {
                return new PackingVerification
                {
                    Ok = false,
                    Detail = "产物校验不通过：" + string.Join("；", problems),
                    Problems = problems
                };
            }

            return new PackingVerification
            {
                Ok = true,
                Detail = $"{artifact} 里数出 {seenNames.Count} 个分卷，与 B 里的 {expected.Count} 个一致，且都在「{folderName}」这一层下",
                Problems = Array.Empty<string>()
            };
        }
    }
}
