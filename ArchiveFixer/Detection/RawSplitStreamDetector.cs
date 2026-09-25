using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Engines;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// 「一组分卷的**第一卷**名字被改坏」这一档（用户 2026-09-25 第 36 条追加，真机取证见 <c>修改日志.md</c>）。
    ///
    /// <para><b>现场</b>：他那个 12.22 GiB 的包里是一组三卷的 7z，第一卷叫
    /// <c>Code Complete-BZ.7z(删掉.001</c> —— 打包者塞了「删掉」两个字、还把右括号吃掉了；
    /// 后两卷叫 <c>Code Complete-BZ.7z.002</c> / <c>.003</c>（名字正常）。于是：</para>
    ///
    /// <list type="number">
    /// <item><description>本程序按名字归组：只有第一卷自己一组（<c>IsVolumeGroup = true</c>、
    /// <c>VolumeCount = 1</c>、起始卷号 1、没有缺号 → <c>IsVolumeComplete = true</c>），
    /// 「缺卷不许开始」那道门**不会**拦它（它看起来是"一个完整的单卷"）；</description></item>
    /// <item><description>7-Zip 也按名字找后续卷，找不到 → 退化成通用分片（<c>Type = Split</c>）：
    /// 清单里只有一条，<b>条目就是文件自己</b>（名字 = 文件名去掉 <c>.001</c>，大小 = 这个卷本身）；</description></item>
    /// <item><description>它**照解不误、退出码 0**（实测：正常的 3 卷解出 <c>a.bin</c> 40000 字节；
    /// 把第一卷改名之后解出的是 16384 字节的垃圾文件 <c>set.7z(删掉</c>）；</description></item>
    /// <item><description>结果校验拿"清单里那一条"比"盘上那一个文件"——两边一样大 → 判**通过** →
    /// 用户拿到一个 5 GB 的垃圾文件，状态却写着「解压成功」。</description></item>
    /// </list>
    ///
    /// <para>所以这一档必须在**写盘之前**拦下来（不变量 7：缺卷不得开始不可完成的任务），
    /// 并如实说清两件事：这一卷是"名字被改坏的分卷"，以及同目录里像后续卷的那几个文件叫什么
    /// （用户把它们改成 <c>X.7z.001</c> 那一套命名就能解开 —— 本类只**指出**，绝不改名，不变量 1）。</para>
    ///
    /// <para>为什么判据里必须有"内容是归档"这一条：<b>通用分片本身是合法的</b> ——
    /// 一个视频被切成人 <c>X.001/X.002</c> 时，"把它们拼起来"正是用户要的（那种文件魔数不是归档，
    /// 本来也不会走到解压管线）。只有"魔数明明认得出是归档，引擎却说这是通用分片"才说明名字被改坏了。</para>
    /// </summary>
    public static class RawSplitStreamDetector
    {
        /// <summary>提示里最多点几个"像后续卷"的文件名（再多就让用户自己去看目录）。</summary>
        private const int MaxSiblingNames = 5;

        /// <summary>
        /// 判据（全部成立才算，缺一条都不拦）：
        /// ①引擎说这是通用分片流（<see cref="ArchiveListResult.IsRawSplitStream"/>）；
        /// ②清单里**只有一条**、而且那一条就是"文件自己"（名字 = 文件名去掉卷号、大小 = 这个文件的大小）；
        /// ③内容魔数认得出是归档（调用方给的 <paramref name="isArchiveByContent"/>）；
        /// ④文件名里带卷号（<c>.001</c> 这一套 —— 它**是**一组分卷的一卷）；
        /// ⑤这一组**只有自己一卷**（<paramref name="knownVolumeCount"/> ≤ 1）——
        ///   卷齐的时候（例如 <c>X.001/X.002</c> 都在）拼起来是对的，绝不能拦。
        ///
        /// <para>⚠ <paramref name="knownVolumeCount"/> 传 0 也算"只有自己一卷"：归组服务没跑过
        /// （测试里直接建的任务、或从别的入口进来的任务）时不能因此漏判。</para>
        /// </summary>
        public static bool IsBrokenVolumeChain(
            ArchiveListResult? list,
            string? archivePath,
            bool isArchiveByContent,
            int knownVolumeCount)
        {
            if (list == null || !list.Success || !list.IsRawSplitStream)
            {
                return false;
            }

            if (!isArchiveByContent || string.IsNullOrWhiteSpace(archivePath))
            {
                return false;
            }

            string fileName = Path.GetFileName(archivePath);

            // 名字里得带卷号：不带卷号的普通包（`X.7z`）走不到这一档。
            if (VolumeGroupDetector.TryGetVolumeIndex(fileName) == null)
            {
                return false;
            }

            /*
             * 只有"单卷组"才拦：名字正常的 `X.001/X.002/...` 会被 7-Zip 当成通用分片（Type = Split 也会出现），
             * 但那是**完整**的一组，拼起来正是用户要的东西 —— 拦它就是破坏既有能力。
             */
            if (knownVolumeCount > 1)
            {
                return false;
            }

            if (list.Entries == null || list.Entries.Count != 1)
            {
                return false;
            }

            ArchiveEntry entry = list.Entries[0];

            if (entry == null || entry.IsDirectory)
            {
                return false;
            }

            string selfBaseName = StripVolumeSuffix(fileName);

            // 条目名 = 文件名去掉卷号（7-Zip 对通用分片的报法），且大小就是这个卷本身。
            if (string.IsNullOrWhiteSpace(selfBaseName) ||
                !string.Equals(entry.Path?.Trim(), selfBaseName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            long fileSize = TryGetFileSize(archivePath);

            // 大小对不上就不是"通用分片"，别硬判（宁可放过，也不要把正常包拦下来）。
            return fileSize > 0 && entry.Size == fileSize;
        }

        /// <summary>
        /// 同目录里"像这一组后续卷"的文件名（**只用来把话说清，不参与判决**）。
        ///
        /// <para>筛选：卷号 ≥ 2（后续卷），且去掉卷号后的基名与自己的基名互为前缀
        /// （打包者那套手法是往名字里塞一段字、或吃掉一个字符，所以前缀关系几乎一定成立）。</para>
        /// </summary>
        public static IReadOnlyList<string> FindSiblingVolumes(
            IEnumerable<string?>? fileNamesInDirectory,
            string? archivePath)
        {
            var result = new List<string>();

            if (fileNamesInDirectory == null || string.IsNullOrWhiteSpace(archivePath))
            {
                return result;
            }

            string selfBaseName = StripVolumeSuffix(Path.GetFileName(archivePath));

            if (string.IsNullOrWhiteSpace(selfBaseName))
            {
                return result;
            }

            foreach (string? name in fileNamesInDirectory)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                int? index = VolumeGroupDetector.TryGetVolumeIndex(name);

                if (index == null || index < 2)
                {
                    continue;
                }

                string otherBaseName = StripVolumeSuffix(name);

                bool related =
                    otherBaseName.StartsWith(selfBaseName, StringComparison.OrdinalIgnoreCase) ||
                    selfBaseName.StartsWith(otherBaseName, StringComparison.OrdinalIgnoreCase);

                if (related)
                {
                    result.Add(name);

                    if (result.Count >= MaxSiblingNames)
                    {
                        break;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 给用户的**改名建议**：这一组本该叫什么（例如 <c>Code Complete-BZ.7z.001</c>）。
        ///
        /// 只用同目录里那些"像后续卷"的文件名推（<see cref="VolumeGroupDetector.TryGetFirstVolumeName"/> 是
        /// 既有实现：从 <c>X.7z.002</c> 推出 <c>X.7z.001</c>）；推不出来返回空串 ——
        /// **宁可不说，也不给一个错的建议**（改错名字比不改更糟）。
        /// </summary>
        public static string SuggestStandardFirstName(IReadOnlyList<string>? siblingVolumes)
        {
            if (siblingVolumes == null)
            {
                return string.Empty;
            }

            foreach (string? sibling in siblingVolumes)
            {
                string? firstName = VolumeGroupDetector.TryGetFirstVolumeName(sibling ?? string.Empty);

                if (!string.IsNullOrWhiteSpace(firstName))
                {
                    return firstName!;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// 去掉文件名末尾的**卷号**（<c>.001</c> / <c>.r00</c> / <c>.z02</c> / <c>.part01</c>）。
        ///
        /// 只认这几种写法（与 <see cref="VolumeGroupDetector"/> 认的是同一批）：
        /// 认不出就原样返回 —— 宁可不匹配，也不要把普通文件名截掉一段。
        /// </summary>
        private static string StripVolumeSuffix(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return string.Empty;
            }

            string name = fileName.Trim();
            int dot = name.LastIndexOf('.');

            if (dot <= 0 || dot == name.Length - 1)
            {
                return name;
            }

            string tail = name[(dot + 1)..];

            bool isDigits = tail.Length > 0 && tail.All(char.IsAsciiDigit);
            bool isRarOld = tail.Length >= 2 &&
                            (tail[0] == 'r' || tail[0] == 'R') &&
                            tail[1..].All(char.IsAsciiDigit);
            bool isZipOld = tail.Length >= 2 &&
                            (tail[0] == 'z' || tail[0] == 'Z') &&
                            tail[1..].All(char.IsAsciiDigit);
            bool isPart = tail.StartsWith("part", StringComparison.OrdinalIgnoreCase) &&
                          tail.Length > 4 &&
                          tail[4..].All(char.IsAsciiDigit);

            return isDigits || isRarOld || isZipOld || isPart ? name[..dot] : name;
        }

        private static long TryGetFileSize(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }
    }
}
