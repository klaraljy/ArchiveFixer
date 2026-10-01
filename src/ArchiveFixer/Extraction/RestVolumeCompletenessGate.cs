using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 链尾「按档处理其余物」之前最后一道闸门：**其余物里的分卷不许是"半套"**。
    ///
    /// <para><b>现场（用户 2026-09-30 真机 `一只顶美合集`，25 GB 被永久删除）</b>：
    /// 那一单的内层包是一组 6 片跨盘 ZIP（`一只顶美.z01`…`.z05` + 末片 `一只顶美.z删除ip`）。
    /// 末片当时**没被认出来是归档**（ZIP64 收尾那两条闸门，见 `docs/真机事故复盘.md` §44.2），
    /// 于是被当成**内容物**留在成品目录里，而同组的 5 卷被当成**过程物**收进了其余物。
    /// 链尾这一档只看到"任务成功 + 校验通过 + 链上没人失败" ⇒ 把其余物**整份彻底删除** ——
    /// 结果是一组包被拆开：末片 1.62 GB 留在成品目录、另外 25 GB 永久消失，谁都再也解不开。</para>
    ///
    /// <para><b>判据（只读盘上事实，⛔ 不猜、不调引擎）</b>：其余物**顶层**的每一个"归档件"
    /// （分卷的一片 / 归档本体 / 名字被改坏的归档本体）算出一个**基名**；
    /// 只要成品目录这一棵树里（**其余物之外**）还存在**同基名的归档件**，
    /// 就说明这一组被拆在两边 ⇒ **整份处理其余物等于把这一组毁掉** ⇒ 什么都不做。</para>
    ///
    /// <para>⛔ 只认"看起来是归档件"的东西：普通内容文件（`X.mp4` / `X.jpg`）**不算伙伴** ——
    /// 包基名与内容文件名撞车是常态（`111\111\内容物`），拿它当伙伴会把正常的清理全拦死。</para>
    ///
    /// <para>⚠ 保守方向是刻意的：判不出 / 读不动 ⇒ 返回拦下的理由（**什么都不做**），
    /// 而不是放行 —— 删除是不可恢复的，这一档宁可多留一份过程物。</para>
    /// </summary>
    public static class RestVolumeCompletenessGate
    {
        /// <summary>
        /// 其余物能不能整份处理。返回 <c>null</c> = 可以；返回一段话 = **拦下的具体理由**（调用方必须原样写进日志）。
        /// </summary>
        /// <param name="restDirectory">其余物目录（`<成品目录>\其余物`）。</param>
        public static string? DescribeBlocker(string? restDirectory)
        {
            if (string.IsNullOrWhiteSpace(restDirectory) || !Directory.Exists(restDirectory))
            {
                // 其余物不在 / 拿不到：上游已有的判据会拦住，这里不越权。
                return null;
            }

            string fullRest = SafePathHelper.GetFullPathSafe(restDirectory);

            string? outputRoot = Path.GetDirectoryName(fullRest.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            if (string.IsNullOrWhiteSpace(outputRoot) || !Directory.Exists(outputRoot))
            {
                return null;
            }

            // 其余物自己造出来的那些卷：基名 -> 第一个见到的文件名（用来点名）
            var restPieces = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string file in EnumerateTopLevelFiles(fullRest))
            {
                if (TryGetArchivePieceBaseName(file, out string baseName))
                {
                    restPieces.TryAdd(baseName, Path.GetFileName(file));
                }
            }

            if (restPieces.Count == 0)
            {
                return null;   // 其余物里没有分卷/归档件（例如全是别的东西）⇒ 不拦
            }

            foreach (string file in EnumerateFilesSafe(outputRoot))
            {
                if (IsInside(fullRest, file))
                {
                    continue;   // 其余物自己里面的不算"在外面"
                }

                if (!TryGetArchivePieceBaseName(file, out string baseName)
                    || !restPieces.TryGetValue(baseName, out string? restName))
                {
                    continue;
                }

                return $"其余物里的「{restName}」是一组分卷的一片，而同组的另一片「{Path.GetFileName(file)}」"
                    + $"还在成品目录里（两边基名都是「{baseName}」）—— 整份处理其余物就等于把这一组拆开，"
                    + "所以这一档什么都不做（删除不可恢复；要清就先确认这一组到底还要不要）";
            }

            return null;
        }

        /// <summary>
        /// 这个文件名像不像"归档件"（分卷的一片 / 归档本体 / 名字被改坏的归档本体）？
        /// 像 ⇒ 给出它的**包基名**（<c>222.z01</c> → <c>222</c>、<c>222.zscip</c> → <c>222</c>）。
        ///
        /// <para>⛔ 判据全部转调既有唯一出口：分卷标记 <see cref="FileNameHelper.IsVolumePartFileName"/>、
        /// 归档后缀 <see cref="ExtensionHelper.IsKnownArchiveExtension"/>、
        /// 去杂质 <see cref="ExtensionHelper.TryRecoverDisguisedArchiveBody"/>、基名
        /// <see cref="FileNameHelper.GetArchiveBaseName"/> —— 这里不另写一套名字规则。</para>
        /// </summary>
        private static bool TryGetArchivePieceBaseName(string path, out string baseName)
        {
            baseName = string.Empty;

            string fileName = Path.GetFileName(path);

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            bool looksLikeArchivePiece =
                FileNameHelper.IsVolumePartFileName(fileName)
                || ExtensionHelper.IsKnownArchiveExtension(Path.GetExtension(fileName));

            if (!looksLikeArchivePiece)
            {
                // `一只顶美.z删除ip` 这种：最后一段去杂质之后是已知归档后缀。
                string lastSegment = fileName[(fileName.LastIndexOf('.') + 1)..];

                looksLikeArchivePiece = ExtensionHelper.TryRecoverDisguisedArchiveBody(
                    lastSegment,
                    out _,
                    out _);
            }

            if (!looksLikeArchivePiece)
            {
                return false;
            }

            baseName = FileNameHelper.GetArchiveBaseName(fileName);

            return !string.IsNullOrWhiteSpace(baseName);
        }

        /// <summary>其余物**顶层**的文件（卷都是摆在这一层的，⛔ 不递归下去翻用户的东西）。</summary>
        private static IEnumerable<string> EnumerateTopLevelFiles(string directory)
        {
            string[] files;

            try
            {
                files = Directory.GetFiles(directory);
            }
            catch
            {
                yield break;
            }

            foreach (string file in files)
            {
                yield return file;
            }
        }

        /// <summary>成品这一棵树里的所有文件；读不动就当"没有外面"（上游判据与保守方向兜底）。</summary>
        private static IEnumerable<string> EnumerateFilesSafe(string directory)
        {
            string[] files;

            try
            {
                files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
            }
            catch
            {
                yield break;
            }

            foreach (string file in files)
            {
                yield return file;
            }
        }

        /// <summary>这个路径是不是在那个目录之下（含相等判定用不上：这里比的是文件与目录）。</summary>
        private static bool IsInside(string directory, string path)
        {
            string full = SafePathHelper.GetFullPathSafe(path);
            string root = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
    }
}
