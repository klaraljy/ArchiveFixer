using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using System;
using System.IO;

namespace ArchiveFixer.Packing
{    /// 落点规划（用户 2026-09-26 第 5/7 条）：最终 rar 与"装分卷的文件夹"分别落在哪、叫什么。
     ///
     /// <para>默认**本地** = 源（文件夹或文件）**旁边**；选"指定位置"就用那个目录。
     /// 两者同名重名时都自动让位（<c>1111(1)</c> / <c>1111(1).rar</c>），⛔ 绝不覆盖。</para>
     /// </summary>
    public static class PackingPaths
    {
        /// <summary>落点目录（**默认 = 源的父目录**；指定位置 = 用户挑的那个）。拿不到时返回空串，由调用方报错。</summary>
        public static string ResolveTargetDirectory(PackingSourceResolution source, PackingRunOptions options)
        {
            if (source == null || !source.Success)
            {
                return string.Empty;
            }

            options ??= new PackingRunOptions();

            if (options.TargetMode == PackingTargetMode.Custom &&
                !string.IsNullOrWhiteSpace(options.CustomOutputDirectory))
            {
                try
                {
                    return Path.GetFullPath(options.CustomOutputDirectory);
                }
                catch
                {
                    return string.Empty;
                }
            }

            string? parent = Path.GetDirectoryName(
                source.SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            return string.IsNullOrWhiteSpace(parent) ? string.Empty : parent!;
        }

        /// <summary>
        /// 装 7z 分卷的文件夹（= 其余物）：源名，重名时 <c>源名(1)</c>。
        /// <paramref name="reservedPaths"/> 见 <see cref="PackingNaming.ResolveUniqueFolderPath"/>。
        /// </summary>
        public static string ResolveVolumesFolder(
            string targetDirectory,
            string sourceName,
            IReadOnlyCollection<string>? reservedPaths = null) =>
            PackingNaming.ResolveUniqueFolderPath(targetDirectory, sourceName, reservedPaths);

        /// <summary>最终 rar：源名 + <c>.rar</c>，重名时 <c>源名(1).rar</c>。</summary>
        public static string ResolveRarPath(string targetDirectory, string sourceName) =>
            PackingNaming.ResolveUniqueFilePath(targetDirectory, sourceName, ".rar");

        /// <summary>外层容器是 7z 时的产物：源名 + <c>.7z</c>（与 rar 一一对应）。</summary>
        public static string ResolveSevenZipOuterPath(string targetDirectory, string sourceName) =>
            PackingNaming.ResolveUniqueFilePath(targetDirectory, sourceName, ".7z");

        /// <summary>
        /// 分卷的基路径（<c>&lt;落点&gt;\&lt;源名&gt;.7z</c>，撞名时让位成 <c>源名(1).7z</c>）。
        ///
        /// <para>⛔ **撞名判据必须看真正的第一卷**（<c>…7z.001</c>）：只看 <c>源名.7z</c> 是看不出来的
        /// （那个文件根本不存在），而 7z 带 <c>-y</c> 会把已有的分卷直接覆盖掉。</para>
        /// </summary>
        public static string ResolveUniqueVolumeBasePath(string directory, string baseName)
        {
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(baseName))
            {
                return string.Empty;
            }

            string candidate = Path.Combine(directory, baseName + ".7z");

            if (!IsVolumeBaseTaken(candidate))
            {
                return candidate;
            }

            for (int index = 1; index < 10000; index++)
            {
                candidate = Path.Combine(directory, $"{baseName}({index}).7z");

                if (!IsVolumeBaseTaken(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(directory, $"{baseName}({ArchiveFixer.Helpers.Clock.Now:yyyyMMddHHmmss}).7z");
        }

        private static bool IsVolumeBaseTaken(string basePath) =>
            File.Exists(basePath) ||
            Directory.Exists(basePath) ||
            File.Exists(basePath + ".001") ||
            Directory.Exists(basePath + ".001");

        /// <summary>把源文件夹/文件搬进其余物时，它在里面的目标路径。</summary>
        public static string ResolveSourceMoveTarget(string volumesFolder, string sourceName) =>
            Path.Combine(volumesFolder, sourceName);

        /// <summary>路径 <paramref name="candidate"/> 是不是在 <paramref name="root"/> 里面（容器内校验用）。</summary>
        public static bool IsInside(string? root, string? candidate)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            try
            {
                string fullRoot = Path.GetFullPath(root).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

                return Path.GetFullPath(candidate).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 外层容器"实际会用哪一个"的结论（用户 2026-09-26 第 46 条：界面不再让用户选，
    /// 没装 WinRAR 时自动改用 7z）。
    ///
    /// <para>⛔ 为什么要单独有这么一处：这个结论**界面也要用** —— ⑤页摘要、确认弹窗里那句
    /// "最终产物：…"都得写实际会产出的那个文件名。以前只有 <c>PackingService</c> 内部算它，
    /// 于是没装 WinRAR 的机器上弹窗会写"最终产物：<c>1111.rar</c>"、跑完却得到
    /// <c>1111.7z</c> —— **界面撒谎**（第 36/45 条那类）。现在服务与界面引同一份计算。</para>
    /// </summary>
}
