using System;
using System.IO;
using System.Text;

namespace ArchiveFixer.Helpers
{
    /// <summary>
    /// 文件名处理工具。
    /// </summary>
    public static class FileNameHelper
    {
        /// <summary>
        /// 获取文件名。
        /// </summary>
        public static string GetFileName(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFileName(path);
            }
            catch
            {
                return path;
            }
        }

        /// <summary>
        /// 获取不带最后一个后缀的文件名。
        /// </summary>
        public static string GetFileNameWithoutExtension(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFileNameWithoutExtension(path);
            }
            catch
            {
                return GetFileName(path);
            }
        }

        /// <summary>
        /// 获取目录路径。
        /// </summary>
        public static string GetDirectoryName(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetDirectoryName(path) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 清理非法文件名字符。
        /// </summary>
        public static string SanitizeFileName(string? fileName, string replacement = "_")
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return "未命名";
            }

            string safeReplacement = replacement ?? "_";
            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(fileName.Length);

            foreach (char ch in fileName)
            {
                if (Array.IndexOf(invalidChars, ch) >= 0)
                {
                    builder.Append(safeReplacement);
                }
                else
                {
                    builder.Append(ch);
                }
            }

            string result = builder.ToString().Trim();

            if (string.IsNullOrWhiteSpace(result))
            {
                result = "未命名";
            }

            result = TrimEndingDotsAndSpaces(result);

            if (IsReservedDeviceName(result))
            {
                result = "_" + result;
            }

            return result;
        }

        /// <summary>
        /// 清理非法路径片段。
        /// </summary>
        public static string SanitizePathPart(string? name)
        {
            return SanitizeFileName(name);
        }

        /// <summary>
        /// 去掉 Windows 不允许的结尾点和空格。
        /// </summary>
        public static string TrimEndingDotsAndSpaces(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "未命名";
            }

            string result = name.TrimEnd(' ', '.');

            if (string.IsNullOrWhiteSpace(result))
            {
                return "未命名";
            }

            return result;
        }

        /// <summary>
        /// 判断是否是 Windows 保留设备名。
        /// </summary>
        public static bool IsReservedDeviceName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            string fileName = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();

            return fileName is
                "CON" or
                "PRN" or
                "AUX" or
                "NUL" or
                "COM1" or
                "COM2" or
                "COM3" or
                "COM4" or
                "COM5" or
                "COM6" or
                "COM7" or
                "COM8" or
                "COM9" or
                "LPT1" or
                "LPT2" or
                "LPT3" or
                "LPT4" or
                "LPT5" or
                "LPT6" or
                "LPT7" or
                "LPT8" or
                "LPT9";
        }

        /// <summary>
        /// 生成压缩包基础名。
        /// 会处理 .tar.gz / .tar.bz2 / .tar.xz，以及**分卷标记**
        /// （<c>222.7z.001</c> / <c>222.zip.001</c> / <c>222.part1.rar</c> / <c>222.z01</c> / <c>222.r00</c>
        /// 一律得到 <c>222</c>）。
        ///
        /// <para>
        /// ⚠️ 分卷这一步是**必修**（2026-09-21 用户在真实产物里看到的目录就是 <c>17274362.7z\</c>）。
        /// 旧实现只剥**一层**后缀：<c>222.7z.001</c> → <c>222.7z</c>、<c>222.part1.rar</c> → <c>222.part1</c>，
        /// 于是分卷包的落点变成 <c>111\222.7z\</c>，分卷组里每一卷还会各建一个目录 ——
        /// 用户的原话是"你给我多弄了四个文件夹、文件一多根本就分不清"。
        /// </para>
        ///
        /// <para>
        /// "什么算分卷标记"只有一份定义（<see cref="ExtensionHelper.IsVolumePartExtension"/>），
        /// 剥法也只有一份实现（<see cref="StripVolumeMarkers"/>），本方法只负责在剥完之后照旧剥一层普通后缀。
        /// （<c>Extraction.OutputPlacement.ResolveArchiveBaseName</c> 有一份更严格的同类实现：
        /// 它只剥**已知归档后缀**，用于落点公式；它现在也转调 <see cref="StripVolumeMarkers"/>，
        /// 两者对分卷的剥法**是同一段代码**。）
        /// </para>
        /// </summary>
        public static string GetArchiveBaseName(string filePath)
        {
            string fileName = GetFileName(filePath);

            if (string.IsNullOrWhiteSpace(fileName))
            {
                return "未命名";
            }

            fileName = StripVolumeMarkers(fileName);

            string lower = fileName.ToLowerInvariant();

            if (lower.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                return fileName[..^7];
            }

            if (lower.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase))
            {
                return fileName[..^8];
            }

            if (lower.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase))
            {
                return fileName[..^7];
            }

            if (lower.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                return fileName[..^4];
            }

            if (lower.EndsWith(".tbz2", StringComparison.OrdinalIgnoreCase))
            {
                return fileName[..^5];
            }

            if (lower.EndsWith(".txz", StringComparison.OrdinalIgnoreCase))
            {
                return fileName[..^4];
            }

            string withoutExt = Path.GetFileNameWithoutExtension(fileName);

            if (string.IsNullOrWhiteSpace(withoutExt))
            {
                return fileName;
            }

            return withoutExt;
        }

        /// <summary>
        /// 剥掉文件名末尾的**分卷标记**：<c>222.7z.001</c> → <c>222.7z</c>、
        /// <c>222.part1.rar</c> → <c>222</c>、<c>222.z01</c> → <c>222</c>、<c>222.r00</c> → <c>222</c>。
        ///
        /// 判据只有一份：<see cref="ExtensionHelper.IsVolumePartExtension"/>（.001~.999 / .z01 / .r00 / .partN）。
        /// 循环有守卫（最多 3 轮）：<c>.part1.rar</c> 这种"分卷段后面还挂着 .rar"的名字要连剥两次，
        /// 同时保证不会在 <c>222.rar</c> 上把 <c>222</c> 当成三位数字分卷段吃光整个名字。
        ///
        /// <para>
        /// ⚠ <b>实现只有这一处</b>（体检报告 §2 第 2 条）：<c>Extraction.OutputPlacement</c> 的包基名
        /// 也转调本方法，不再自带一份逐字相同的副本。理由不是"省 40 行"，而是两条路必须**永远**剥得一样 ——
        /// 归档基名与包基名对同一个包算出不同的名字，落点公式就会指向两个不同的目录。
        /// 改这里之前先想清楚两边的用途，别只改一半。
        /// </para>
        /// </summary>
        public static string StripVolumeMarkers(string fileName)
        {
            string current = fileName;

            for (int guard = 0; guard < 3; guard++)
            {
                int lastDot = current.LastIndexOf('.');

                if (lastDot <= 0)
                {
                    break;
                }

                string tail = current[(lastDot + 1)..];

                // ⚠ 用 TrySplitVolumeSegment 而不是 IsVolumePartExtension：分卷标记后面粘着垃圾的
                // （百度网盘那种 222.7z.001删除）也算分卷标记，**连同垃圾一起剥掉** ——
                // 不剥的话 GetArchiveBaseName 会把它当成"普通后缀"剥，剥出来的基名对着整个包，
                // 改名时就会把 .001 这一段吃掉（2026-09-28 真机事故的根因）。
                if (ExtensionHelper.TrySplitVolumeSegment(tail, out _, out _))
                {
                    current = current[..lastDot];
                    continue;
                }

                // xxx.part1.rar：分卷段在倒数第二段上，光看最后一段（.rar）看不出来。
                // ⚠ 必须要求分卷段**前面还有内容**（previousDot > 0），否则 "222.rar" 里的 "222"
                // 会被当成三位数字分卷段，整个名字被吃光。
                if (tail.Equals("rar", StringComparison.OrdinalIgnoreCase))
                {
                    int previousDot = current.LastIndexOf('.', lastDot - 1);

                    if (previousDot > 0 &&
                        ExtensionHelper.IsVolumePartExtension("." + current[(previousDot + 1)..lastDot]))
                    {
                        current = current[..previousDot];
                        continue;
                    }
                }

                break;
            }

            return current;
        }

        /// <summary>
        /// 判断是否为多重伪装文件名。
        /// </summary>
        public static bool IsMultiExtensionSuspicious(string fileName, string suggestedExtension)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            // 分卷不是伪装。
            // 例：volume.7z.001 的最后一段是分卷标记，不是"伪装上去的图片后缀"，
            // 不能报成"多重后缀疑似伪装"（设计.md §七 要求分卷单独归类，M2 补"分卷后缀"状态）。
            if (IsVolumePartFileName(fileName))
            {
                return false;
            }

            suggestedExtension = ExtensionHelper.NormalizeExtension(suggestedExtension);

            if (string.IsNullOrWhiteSpace(suggestedExtension))
            {
                return false;
            }

            if (!ExtensionHelper.HasMultipleExtensions(fileName))
            {
                return false;
            }

            string lastExt = ExtensionHelper.GetLastExtension(fileName);

            if (string.Equals(lastExt, suggestedExtension, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return ExtensionHelper.ContainsExtension(fileName, suggestedExtension);
        }

        /// <summary>
        /// 判断文件名是否是分卷的一部分。
        ///
        /// 例：
        ///   volume.7z.001 / data.zip.002   —— .001 分卷
        ///   movie.part1.rar                —— rar 新式分卷
        ///   old.rar / old.r00              —— rar 老式分卷
        ///   x.z01                          —— zip 分卷
        ///
        /// 用途：**拦住会破坏分卷链的改名**。7z 只认 ".001" 这一套命名，
        /// 一旦把 volume.7z.001 改成 volume.7z，整个分卷组就解不开了。
        /// </summary>
        public static bool IsVolumePartFileName(string? fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            string[] parts = fileName.Split('.');

            if (parts.Length < 2)
            {
                return false;
            }

            // xxx.001 / xxx.z01 / xxx.part1
            // ⚠ 分卷标记后面粘着垃圾的（xxx.7z.001删除）也算：百度网盘给每个分卷名缀「删除」，
            //    老判据不认它 → 整组被当成几个独立压缩包 → 一键处理改名把分卷链切断（2026-09-28 事故）。
            if (ExtensionHelper.TrySplitVolumeSegment(parts[^1], out _, out _))
            {
                return true;
            }

            // xxx.part1.rar
            if (parts.Length >= 3 &&
                ExtensionHelper.TrySplitVolumeSegment(parts[^2], out _, out _) &&
                string.Equals(parts[^1], "rar", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 获取安全的压缩包基础名。
        /// </summary>
        public static string GetSafeArchiveBaseName(string filePath)
        {
            return SanitizeFileName(GetArchiveBaseName(filePath));
        }
    }
}
