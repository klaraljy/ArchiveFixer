using System;
using System.IO;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Packing
{
    public static class PackingNaming
    {
        /// <summary>
        /// 装分卷的文件夹：**以源名命名**；重名时依次 <c>1111(1)</c>、<c>1111(2)</c>…
        /// （用户原话："如果用户选择不操作原包，那就是 1111(1) 文件夹"）。
        ///
        /// <para>⛔ 只看**磁盘上有没有**同名文件/文件夹，不看"用户选了哪一档" —— 那样最稳。
        /// <paramref name="reservedPaths"/> 是"这一轮马上要建、但现在还不存在"的路径
        /// （单文件时那个同名文件夹就是它）—— 不把它们算进去，
        /// 分卷文件夹会和"正要打包的那个文件夹"撞名，于是分卷落进被扫描的源文件夹里。</para>
        /// </summary>
        public static string ResolveUniqueFolderPath(
            string parentDirectory,
            string baseName,
            IReadOnlyCollection<string>? reservedPaths = null)
        {
            if (string.IsNullOrWhiteSpace(parentDirectory) || string.IsNullOrWhiteSpace(baseName))
            {
                return string.Empty;
            }

            string candidate = Path.Combine(parentDirectory, baseName);

            if (!Exists(candidate) && !IsReserved(candidate, reservedPaths))
            {
                return candidate;
            }

            for (int index = 1; index < 10000; index++)
            {
                candidate = Path.Combine(parentDirectory, $"{baseName}({index})");

                if (!Exists(candidate) && !IsReserved(candidate, reservedPaths))
                {
                    return candidate;
                }
            }

            return Path.Combine(parentDirectory, $"{baseName}({ArchiveFixer.Helpers.Clock.Now:yyyyMMddHHmmss})");
        }

        /// <summary>
        /// 最终 rar：**永远以源名命名**（<c>1111.rar</c>），重名时同样让位成 <c>1111(1).rar</c>
        /// —— ⛔ 绝不覆盖已有的文件。
        /// </summary>
        public static string ResolveUniqueFilePath(string directory, string baseName, string extension)
        {
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(baseName))
            {
                return string.Empty;
            }

            extension ??= string.Empty;

            string candidate = Path.Combine(directory, baseName + extension);

            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }

            for (int index = 1; index < 10000; index++)
            {
                candidate = Path.Combine(directory, $"{baseName}({index}){extension}");

                if (!File.Exists(candidate) && !Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(directory, $"{baseName}({ArchiveFixer.Helpers.Clock.Now:yyyyMMddHHmmss}){extension}");
        }

        private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

        private static bool IsReserved(string candidate, IReadOnlyCollection<string>? reservedPaths)
        {
            if (reservedPaths == null || reservedPaths.Count == 0)
            {
                return false;
            }

            foreach (string reserved in reservedPaths)
            {
                if (!string.IsNullOrWhiteSpace(reserved) &&
                    string.Equals(
                        Path.GetFullPath(reserved),
                        Path.GetFullPath(candidate),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// 小确认弹窗里的那几个选择（用户 2026-09-26 第 6 条：点「打包」→ 一个小窗，
    /// 只显示"最终 rar 放在哪 + 原包操作 + 其余物操作"）。
    ///
    /// <para>⛔ 与解压侧的 <c>SourceHandling</c> / <c>RestHandlingAfterVerify</c> **完全独立**：
    /// 两边各有各的设置项、各存各的，任何一边改了都不许影响另一边（用户明确要求"两者绝对不能同步"）。</para>
    /// </summary>
}
