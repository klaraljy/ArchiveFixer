using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace ArchiveFixer.Packing
{
    /// <summary>
    /// 一个分卷（7-Zip 自己切出来的那一个文件）。
    ///
    /// <para>名字由 7-Zip 产生（<c>-v</c> → <c>名字.7z.001/.002/…</c>），
    /// 这里只**认**磁盘上的结果，不自己拼 —— 自己拼会在"内容物恰好是分卷大小的整数倍"
    /// 这种边界上多切一卷出来（docs/打包功能.md §2）。</para>
    /// </summary>
    public sealed class PackingVolume
    {
        /// <summary>文件名（含 <c>.7z.001</c> 这一段，不带目录）。</summary>
        public string FileName { get; init; } = string.Empty;

        /// <summary>全路径。</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>分卷序号（<c>.001</c> → 1）；认不出来时是 0。</summary>
        public int Index { get; init; }

        /// <summary>字节数（读不到时是 0）。</summary>
        public long Bytes { get; init; }

        public override string ToString()
        {
            return $"{FileName}（{TaskSpaceEstimate.FormatSize(Bytes)}）";
        }
    }

    /// <summary>
    /// 一次打包的**规划**：源文件夹量出来的大小 → 分卷大小 → 预计几个分卷 → 需要多少空间。
    ///
    /// <para>纯计算 + 落点校验，**不碰进程、不写盘**（落点校验只读文件系统的元数据）。
    /// 之所以把它从 <see cref="PackingService"/> 里分出来：这些数字是界面上要给用户看的摘要
    /// （内容大小 / 预计分卷数 / 需要空间），必须在按下「开始打包」之前就能算出来 ——
    /// 而真正开跑时要用的判断与界面用的是**同一份**，不能各算一套。</para>
    ///
    /// <para><b>分卷数为什么是"预计"</b>：7z 先压缩再切卷，卷数取决于**压缩后**的大小。
    /// 内容是不可压缩的资源（视频 / 图片 / 已压过的包）时与 <see cref="ContentBytes"/> 同量级，
    /// 所以按内容大小算出来的是**上界**（用户给的两个例子 1 GB → 2 卷、1.5 GB → 3 卷，
    /// 正是这个口径）。可压缩的内容会切出更少的卷 —— 这是对的方向，不是出错。</para>
    /// </summary>
    public sealed class PackingPlan
    {
        /// <summary>默认分卷大小：512 MiB（用户两个例子的唯一自洽解，docs/打包功能.md §2）。</summary>
        public const long DefaultVolumeSizeBytes = 512L * 1024 * 1024;

        /// <summary>界面允许的分卷大小下限。</summary>
        public const long MinVolumeSizeBytes = 64L * 1024 * 1024;

        /// <summary>界面允许的分卷大小上限。</summary>
        public const long MaxVolumeSizeBytes = 4096L * 1024 * 1024;

        /// <summary>
        /// 服务层的硬下限（1 MiB）。比界面下限松，理由有两条，都不是"给测试开后门"：
        /// ① <c>7z -v</c> 的单位是 MiB，比 1 MiB 更小的值没有意义（<c>-v0m</c> 是无效参数）；
        /// ② 64 MiB 是**给人的推荐范围**（挡住"手滑填 1"），而"这台机器上真的能不能跑通"
        /// 要靠端到端验证 —— 验收要求用 1 MiB 跑一次真 7z（docs/打包功能.md §9 第 3 条），
        /// 服务层如果按 64 MiB 硬拒，那条验收就永远跑不了。
        /// </summary>
        public const long HardMinVolumeSizeBytes = 1024 * 1024;

        /// <summary>
        /// 空间需求系数：内容物自己一份（×1.02 的压缩后估算）＋ 外层 rar 里再存一份。
        ///
        /// <para>与 docs/打包功能.md §6 的最后一行同一口径：<c>内容大小 × 1.02 + 内容大小</c> = ×2.02。
        /// 取 1.02 是因为 7z 建出来的分卷总量**可能略大于**内容（容器头 + 分卷边界），
        /// 空间判断宁可高估（与 <c>SpaceChecker</c> 同一条价值观：不敢说够）。</para>
        /// </summary>
        public const double SpaceFactor = 2.02d;

        /// <summary>额外的固定余量（外层 rar 的头、目录项、以及别处同时在写的零碎）。</summary>
        public const long SpaceMarginBytes = 32L * 1024 * 1024;

        /// <summary>源文件夹 A（全路径）。</summary>
        public string SourceFolder { get; init; } = string.Empty;

        /// <summary>A 的目录名（分卷名、rar 里的那一层都用它）。</summary>
        public string SourceFolderName { get; init; } = string.Empty;

        /// <summary>输出文件夹 B（全路径）。</summary>
        public string OutputFolder { get; init; } = string.Empty;

        /// <summary>结果 rar 的全路径（默认与 B 同级：<c>&lt;B&gt;.rar</c>）。</summary>
        public string RarPath { get; init; } = string.Empty;

        /// <summary>分卷的基名（<c>&lt;A 名&gt;.7z</c>）；实际的卷名由 7z 在它后面加 <c>.001</c>。</summary>
        public string VolumeBaseName => string.IsNullOrWhiteSpace(SourceFolderName)
            ? string.Empty
            : SourceFolderName + ".7z";

        /// <summary>分卷大小（字节）。</summary>
        public long VolumeSizeBytes { get; init; } = DefaultVolumeSizeBytes;

        /// <summary>内容物字节数（递归扫 A 加起来；读不到大小的文件按 0 计但会如实报数）。</summary>
        public long ContentBytes { get; init; }

        /// <summary>内容物文件数。</summary>
        public int FileCount { get; init; }

        /// <summary>扫不动 / 读不到大小的条目数（进日志，不拦）。</summary>
        public int UnreadableCount { get; init; }

        /// <summary>预计分卷数（<c>ceil(内容 / 分卷大小)</c>，内容为 0 时是 0 —— 那种请求会被拒）。</summary>
        public long PlannedVolumeCount => ComputeVolumeCount(ContentBytes, VolumeSizeBytes);

        /// <summary>需要多少空余空间（内容 × 2.02 + 32 MiB 余量）。</summary>
        public long RequiredSpaceBytes => ComputeRequiredSpace(ContentBytes);

        /// <summary>分卷大小的人读写法（<c>512 MiB</c>）。</summary>
        public string VolumeSizeText => FormatVolumeSize(VolumeSizeBytes);

        /// <summary>分卷数的人读一句话（日志 / 结果区用）。</summary>
        public string DescribeVolumePlan()
        {
            return $"按每个分卷上限 {VolumeSizeText} 估算，会切出约 {PlannedVolumeCount} 卷"
                 + $"（内容 {TaskSpaceEstimate.FormatSize(ContentBytes)}；实际卷数取决于压缩后的体积）";
        }

        /// <summary>
        /// 界面输入框的校验（64 MiB – 4096 MiB，且必须是整数 MiB）。
        /// 返回空串 = 通过；否则是给用户看的那句话。
        /// </summary>
        public static string ValidateVolumeSize(long bytes)
        {
            if (bytes <= 0)
            {
                return $"分卷大小必须是正数（{FormatVolumeSize(MinVolumeSizeBytes)} – {FormatVolumeSize(MaxVolumeSizeBytes)}）。";
            }

            if (bytes % (1024 * 1024) != 0)
            {
                return "分卷大小必须是整数 MiB（7z 的 -v 参数以 MiB 为单位）。";
            }

            if (bytes < MinVolumeSizeBytes || bytes > MaxVolumeSizeBytes)
            {
                return $"分卷大小要在 {FormatVolumeSize(MinVolumeSizeBytes)} – {FormatVolumeSize(MaxVolumeSizeBytes)} 之间"
                     + $"（当前 {FormatVolumeSize(bytes)}）。";
            }

            return string.Empty;
        }

        /// <summary>预计分卷数：<c>ceil(内容 / 分卷大小)</c>；内容为 0 或分卷大小无效时是 0。</summary>
        public static long ComputeVolumeCount(long contentBytes, long volumeSizeBytes)
        {
            if (contentBytes <= 0 || volumeSizeBytes <= 0)
            {
                return 0;
            }

            // 先除再补：content + size - 1 在两者都接近 long.MaxValue 时会溢出成负数，
            // 于是"要切几万卷"被算成"一卷也不用"。饱和加法在这里比公式省事。
            long quotient = contentBytes / volumeSizeBytes;

            return contentBytes % volumeSizeBytes == 0
                ? quotient
                : TaskSpaceEstimate.SaturatingSum(quotient, 1);
        }

        /// <summary>空间需求：内容 × 2.02 + 32 MiB（饱和加法，绝不回绕成负数）。</summary>
        public static long ComputeRequiredSpace(long contentBytes)
        {
            if (contentBytes <= 0)
            {
                return SpaceMarginBytes;
            }

            double scaled = contentBytes * SpaceFactor;

            long content = scaled >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(scaled);

            return TaskSpaceEstimate.SaturatingSum(content, SpaceMarginBytes);
        }

        /// <summary>默认的输出文件夹 B：与 A 同级，名字加后缀 <c>_打包</c>。</summary>
        public static string DefaultOutputFolder(string sourceFolder)
        {
            if (string.IsNullOrWhiteSpace(sourceFolder))
            {
                return string.Empty;
            }

            string full = SafeGetFullPath(sourceFolder);
            string? parent = Path.GetDirectoryName(full);

            if (string.IsNullOrWhiteSpace(parent))
            {
                return string.Empty;
            }

            string name = Path.GetFileName(full);

            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            // 根目录（D:\）下的文件夹没有父目录名可用时，退回落点仍与它同级。
            return Path.Combine(parent, name + "_打包");
        }

        /// <summary>默认的结果 rar：与 B 同级，<c>&lt;B 名&gt;.rar</c>（B 是 <c>X_打包</c> 时就是 <c>X_打包.rar</c>）。</summary>
        public static string DefaultRarPath(string outputFolder)
        {
            if (string.IsNullOrWhiteSpace(outputFolder))
            {
                return string.Empty;
            }

            string full = SafeGetFullPath(outputFolder);
            string? parent = Path.GetDirectoryName(full);
            string name = Path.GetFileName(full);

            if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            return Path.Combine(parent, name + ".rar");
        }

        /// <summary>
        /// 校验请求并算出规划。返回 false 时 <paramref name="error"/> 是给用户看的那句话
        /// （**必须具体**：说清哪一条不合法、为什么）。
        /// </summary>
        public static bool TryCreate(PackingRequest request, out PackingPlan? plan, out string error)
        {
            plan = null;
            error = string.Empty;

            if (request == null)
            {
                error = "没有打包请求。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.SourceFolder))
            {
                error = "请先选择源文件夹 A。";
                return false;
            }

            string source = SafeGetFullPath(request.SourceFolder);

            if (!Directory.Exists(source))
            {
                error = $"源文件夹不存在：{source}";
                return false;
            }

            if (request.VolumeSizeBytes < HardMinVolumeSizeBytes ||
                request.VolumeSizeBytes > MaxVolumeSizeBytes ||
                request.VolumeSizeBytes % (1024 * 1024) != 0)
            {
                error = $"分卷大小不合法：{FormatVolumeSize(request.VolumeSizeBytes)}。"
                      + $"要在 {FormatVolumeSize(HardMinVolumeSizeBytes)} – {FormatVolumeSize(MaxVolumeSizeBytes)} 之间，"
                      + "且是整数 MiB（7z 的 -v 参数以 MiB 为单位）。";
                return false;
            }

            ScanResult scan = Scan(source);

            if (scan.FileCount == 0 || scan.Bytes <= 0)
            {
                error = $"源文件夹里没有可打包的内容（{source}）：扫到 {scan.FileCount} 个文件、"
                      + $"{TaskSpaceEstimate.FormatSize(scan.Bytes)}。请选一个里面有东西的文件夹。";
                return false;
            }

            string output = string.IsNullOrWhiteSpace(request.OutputFolder)
                ? DefaultOutputFolder(source)
                : SafeGetFullPath(request.OutputFolder);

            if (string.IsNullOrWhiteSpace(output))
            {
                error = "推不出输出文件夹 B，请手动指定一个。";
                return false;
            }

            string rarPath = string.IsNullOrWhiteSpace(request.RarPath)
                ? DefaultRarPath(output)
                : SafeGetFullPath(request.RarPath);

            if (string.IsNullOrWhiteSpace(rarPath))
            {
                error = "推不出结果 rar 的路径，请手动指定一个。";
                return false;
            }

            string name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            if (string.IsNullOrWhiteSpace(name))
            {
                error = $"源文件夹的名字取不出来（{source}），没法给分卷命名。";
                return false;
            }

            string? reject = ValidatePlacement(source, output, rarPath);

            if (reject != null)
            {
                error = reject;
                return false;
            }

            plan = new PackingPlan
            {
                SourceFolder = source,
                SourceFolderName = name,
                OutputFolder = output,
                RarPath = rarPath,
                VolumeSizeBytes = request.VolumeSizeBytes,
                ContentBytes = scan.Bytes,
                FileCount = scan.FileCount,
                UnreadableCount = scan.UnreadableCount
            };

            return true;
        }

        /// <summary>
        /// 落点校验（docs/打包功能.md §4）。返回 null = 通过，否则是拒绝的那句话。
        ///
        /// <para>四条，每一条都对应一个真实会出事的情形：</para>
        /// <list type="number">
        /// <item><description><b>B 在 A 里面</b>：分卷会落进被扫描的源文件夹里 —— 越打越多，
        /// 而且 7z 正在读的目录正在被自己写（用户点名要拒的那一条）。</description></item>
        /// <item><description><b>rar 在 B 里面</b>：外层 rar 会把自己装进去（用户点名要拒的那一条）。</description></item>
        /// <item><description><b>rar 在 A 里面</b>：不变量 12 —— 源目录里一个字节都不许写。</description></item>
        /// <item><description><b>A 在 B 里面</b>：rar 装的是整个 B，于是源文件被原样再存一份
        /// （结果比预期大一倍，且"结果 = 压缩包 B"这句话就不成立了）。</description></item>
        /// </list>
        /// </summary>
        public static string? ValidatePlacement(string sourceFolder, string outputFolder, string rarPath)
        {
            string source = SafeGetFullPath(sourceFolder);
            string output = SafeGetFullPath(outputFolder);
            string rar = SafeGetFullPath(rarPath);

            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(rar))
            {
                return "路径推不出来（源文件夹 / 输出文件夹 / 结果 rar 三样都要有）。";
            }

            if (PathEquals(source, output))
            {
                return $"输出文件夹 B 不能就是源文件夹 A：{output}。请另选一个（默认是 A 同级的「{Path.GetFileName(source)}_打包」）。";
            }

            if (IsInside(output, source))
            {
                return $"输出文件夹 B 不能放在源文件夹 A 里面（A：{source}；B：{output}）——"
                     + "分卷会落进正在打包的目录，越打越多。请把 B 放到 A 外面（默认位置就在 A 的同级）。";
            }

            if (IsInside(rar, output))
            {
                return $"结果 rar 不能放在输出文件夹 B 里面（B：{output}；rar：{rar}）——"
                     + "外层 rar 会把自己装进去。默认位置是 B 的同级。";
            }

            if (IsInside(rar, source))
            {
                return $"结果 rar 不能放在源文件夹 A 里面（A：{source}；rar：{rar}）——"
                     + "源目录里不写任何东西（避免把打包结果又打进去一次）。";
            }

            if (IsInside(source, output))
            {
                return $"源文件夹 A 在输出文件夹 B 里面（A：{source}；B：{output}）——"
                     + "外层 rar 装的是整个 B，会把源文件原样再存一份。请把 B 放到 A 外面。";
            }

            if (Directory.Exists(rar))
            {
                return $"结果 rar 的路径被一个同名文件夹占着：{rar}。请换一个名字。";
            }

            if (File.Exists(rar))
            {
                // 与不变量 3 同一口径：冲突**不默认覆盖**。
                return $"结果文件已存在（不会默认覆盖）：{rar}。请先把旧的改名 / 移走，或换一个输出文件夹。";
            }

            if (Directory.Exists(output) && !IsDirectoryEmpty(output))
            {
                return $"输出文件夹已存在且不为空：{output}。"
                     + "打包只往**空文件夹**里写（B 里的东西最后会被整个装进 rar，混进旧文件会让结果不对）。"
                     + "请先清空它、或另选一个文件夹。";
            }

            return null;
        }

        /// <summary>
        /// <paramref name="candidate"/> 是不是落在 <paramref name="directory"/> 里面（同级 / 自身不算）。
        /// 纯路径比较，OrdinalIgnoreCase（Windows 口径），末尾分隔符先归一。
        /// </summary>
        public static bool IsInside(string candidate, string directory)
        {
            string child = SafeGetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string parent = SafeGetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (child.Length == 0 || parent.Length == 0 || PathEquals(child, parent))
            {
                return false;
            }

            return child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>两个路径是不是同一个（Windows 口径）。</summary>
        public static bool PathEquals(string left, string right)
        {
            string a = SafeGetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string b = SafeGetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>目录是不是空的（取不到 → 当作"不空"，宁可拦下也不要静默覆盖别人的东西）。</summary>
        public static bool IsDirectoryEmpty(string directory)
        {
            try
            {
                using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();

                return !entries.MoveNext();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>把字节数说成 MiB/GiB（分卷大小专用：它不是"内容物大小"，用整数 MiB 更好读）。</summary>
        public static string FormatVolumeSize(long bytes)
        {
            if (bytes <= 0)
            {
                return "0 MiB";
            }

            long mib = bytes / (1024 * 1024);

            if (mib < 1024)
            {
                return mib.ToString(CultureInfo.InvariantCulture) + " MiB";
            }

            return (bytes / 1024d / 1024d / 1024d).ToString("0.##", CultureInfo.InvariantCulture) + " GiB";
        }

        private static string SafeGetFullPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path.Trim());
            }
            catch
            {
                // 非法路径（含非法字符 / 过长）：返回原样，由调用方按"推不出来"处理。
                return string.Empty;
            }
        }

        /// <summary>递归扫一遍：文件数 + 总字节数。**不抛** —— 扫不动就如实记数。</summary>
        private static ScanResult Scan(string folder)
        {
            long bytes = 0;
            int files = 0;
            int unreadable = 0;

            var pending = new Stack<string>();
            pending.Push(folder);

            while (pending.Count > 0)
            {
                string current = pending.Pop();
                string[] entries;

                try
                {
                    entries = Directory.GetFileSystemEntries(current);
                }
                catch
                {
                    // 权限不足 / 目录刚被删：记一笔，继续扫别的分支。
                    unreadable++;
                    continue;
                }

                foreach (string entry in entries)
                {
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(entry);

                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            /*
                             * 不跟重解析点（符号链接 / 联接）：跟进去可能成环，也可能扫到源文件夹外面，
                             * 于是"要打包的内容"与用户以为的不一样。7z 默认也不跟链接。
                             */
                            if ((attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                continue;
                            }

                            pending.Push(entry);
                            continue;
                        }

                        bytes = TaskSpaceEstimate.SaturatingSum(bytes, new FileInfo(entry).Length);
                        files++;
                    }
                    catch
                    {
                        unreadable++;
                    }
                }
            }

            return new ScanResult(files, bytes, unreadable);
        }

        private readonly struct ScanResult
        {
            public ScanResult(int fileCount, long bytes, int unreadableCount)
            {
                FileCount = fileCount;
                Bytes = bytes;
                UnreadableCount = unreadableCount;
            }

            public int FileCount { get; }

            public long Bytes { get; }

            public int UnreadableCount { get; }
        }
    }
}
