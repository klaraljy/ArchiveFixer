using System;
using System.Globalization;
using System.IO;

namespace ArchiveFixer.Storage
{
    /// <summary>
    /// 目标盘空间与卷格式检查（设计.md §二十一、§二十五）。
    ///
    /// 定位：**解压前**的预检工具。所有方法一律不抛异常 —— 它们跑在解压前的准备阶段，
    /// 一次抛异常就会把"这个包我还没开始解压"变成"整个任务崩了"，
    /// 而这里能拿到的信息本来就只是"参考值"（空间随时在变）。
    ///
    /// 唯一一处 DriveInfo 取法的来源：<see cref="ResourceBudget"/> 也调这里，
    /// 不另写一份，免得两处对"路径还不存在""盘未就绪"给出不同答案。
    /// </summary>
    public static class SpaceChecker
    {
        /// <summary>
        /// FAT32 单文件上限。FAT32 目录项里文件长度是 32 位，最大 4294967295 字节，
        /// 也就是 4 GiB - 1 —— 写第 4294967296 个字节时才会失败，
        /// 所以"等于 4 GiB"不算超限，"超过 4 GiB - 1"才算。
        /// </summary>
        private const long Fat32MaxFileSize = 4L * 1024 * 1024 * 1024 - 1;

        /// <summary>
        /// 取路径所在盘的可用字节数。路径为空 / 不合法 / 盘取不到 → null，不抛。
        /// </summary>
        public static long? GetAvailableFreeSpace(string? path)
        {
            DriveInfo? drive = TryGetDrive(path);

            if (drive == null)
            {
                return null;
            }

            try
            {
                if (!drive.IsReady)
                {
                    return null;
                }

                // 用 AvailableFreeSpace 而不是 TotalFreeSpace：前者把磁盘配额算进去，
                // 更接近"我这个用户还能不能写下这些字节"这个真正要问的问题。
                return drive.AvailableFreeSpace;
            }
            catch
            {
                // 未就绪的光驱、断开的网络盘、权限不足…… 一律"取不到"。
                return null;
            }
        }

        /// <summary>
        /// 剩余空间是否够再写 <paramref name="requiredBytes"/>，并且写完之后仍保留 <paramref name="reserveBytes"/>。
        ///
        /// 取不到可用空间时返回 <c>false</c> 并说明原因 —— <b>不敢说够</b>：
        /// 默认放行等于把"未知"伪装成"充足"，那正是"检查一下就宣称安全"的写法。
        /// </summary>
        public static bool HasEnoughSpace(string? path, long requiredBytes, long reserveBytes, out string reason)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                reason = "目标路径为空，无法检查可用空间";
                return false;
            }

            // 负数是没有意义的输入（解析异常），按 0 处理；不要让它把后面的大小比较变成反向判断。
            long required = requiredBytes > 0 ? requiredBytes : 0L;
            long reserve = reserveBytes > 0 ? reserveBytes : 0L;

            long? freeSpaceBytes = GetAvailableFreeSpace(path);

            if (!freeSpaceBytes.HasValue)
            {
                reason = $"无法取到目标路径所在盘的可用空间（路径：{path}），不能确认空间是否足够";
                return false;
            }

            long available = freeSpaceBytes.Value;

            /*
             * 分两步比，而不是 required + reserve > available：
             * 后面这种写法在 required 和 reserve 都很接近 long.MaxValue 时会溢出成负数，
             * 于是"空间严重不足"被算成"空间充足" —— 预算判断里最不能出的就是这个错。
             */
            if (required > available)
            {
                reason = $"可用空间不足：可用 {available} 字节（{FormatSize(available)}），" +
                         $"需要 {required} 字节（{FormatSize(required)}）";
                return false;
            }

            long remaining = available - required;

            if (remaining < reserve)
            {
                reason = $"解压后剩余空间不足：可用 {available} 字节（{FormatSize(available)}），" +
                         $"需要 {required} 字节（{FormatSize(required)}），" +
                         $"写完后只剩 {remaining} 字节（{FormatSize(remaining)}），" +
                         $"低于需要保留的 {reserve} 字节（{FormatSize(reserve)}）";
                return false;
            }

            reason = $"空间充足：可用 {available} 字节（{FormatSize(available)}），" +
                     $"需要 {required} 字节（{FormatSize(required)}），保留 {reserve} 字节（{FormatSize(reserve)}）";
            return true;
        }

        /// <summary>
        /// 目标卷是否是 FAT32（单文件 4 GiB 限制）。不是 FAT32 或卷格式取不到 → false。
        ///
        /// 只认 FAT32：exFAT 没有 4 GiB 单文件限制，FAT16 的卷本身也放不下 4 GiB 的文件
        /// （FAT16 单文件上限 2 GiB，会先在别处暴露），都不该套用这条限制。
        /// </summary>
        public static bool IsFat32(string? path)
        {
            DriveInfo? drive = TryGetDrive(path);

            if (drive == null)
            {
                return false;
            }

            try
            {
                if (!drive.IsReady)
                {
                    return false;
                }

                return string.Equals(drive.DriveFormat, "FAT32", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// FAT32 上单文件超过 4 GiB 时提前拦下。
        ///
        /// 设计.md §二十一 明确要求"不能等写入失败后才提示"：FAT32 要写到第 4 GiB 边界才会报错，
        /// 那时前面几个 GiB 已经落盘了，用户等到的是解压到一半失败 + 一堆需要清理的残留。
        /// 这里用清单里的大小就能提前判出来。
        /// </summary>
        public static bool IsFat32SingleFileLimitExceeded(string? path, long fileSizeBytes, out string reason)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                reason = "目标路径为空，无法判断目标卷是否为 FAT32";
                return false;
            }

            if (fileSizeBytes <= 0)
            {
                reason = "文件大小为 0（或未知），不会触发 FAT32 单文件上限";
                return false;
            }

            if (!IsFat32(path))
            {
                // 这里也覆盖"卷格式取不到"：取不到就**不**按 FAT32 拦，
                // 免得把一个正常的 NTFS 任务误拦下来（写失败时还有引擎报错兜底）。
                reason = $"目标卷不是 FAT32（或卷格式取不到），{FormatSize(fileSizeBytes)} 不套用 FAT32 单文件限制";
                return false;
            }

            if (fileSizeBytes <= Fat32MaxFileSize)
            {
                reason = $"目标卷是 FAT32，文件 {FormatSize(fileSizeBytes)} 未超过单文件 4 GiB 限制";
                return false;
            }

            reason = $"目标卷是 FAT32，单文件上限 {Fat32MaxFileSize} 字节（4 GiB），" +
                     $"该文件解压后 {fileSizeBytes} 字节（{FormatSize(fileSizeBytes)}），" +
                     "写到 4 GiB 边界时才会失败，现在提前拦下（不能等写入失败后才提示）";
            return true;
        }

        /// <summary>
        /// 把路径映射到所在卷。取不到返回 null。
        /// </summary>
        private static DriveInfo? TryGetDrive(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                /*
                 * 先规范化再取盘根。两点原因：
                 * ① Path.GetPathRoot 是**纯字符串**操作，不查文件系统 —— 所以"目标目录还没创建"
                 *    这种情况照样能取到盘根。这正是这里不用 Directory.Exists 的原因：
                 *    目标目录往往要等解压时（甚至解压后）才存在，用存在性判断会把正常情况判成"取不到"。
                 * ② 相对路径（如 "out\a"）的 GetPathRoot 会返回空串，必须先按当前目录展开。
                 */
                string fullPath = Path.GetFullPath(path);
                string? root = Path.GetPathRoot(fullPath);

                if (string.IsNullOrWhiteSpace(root))
                {
                    return null;
                }

                return new DriveInfo(root);
            }
            catch
            {
                // 盘符不存在、路径非法、UNC 取不到…… 全部收敛成"取不到"，绝不抛给调用方。
                return null;
            }
        }

        /// <summary>把字节数说成人话。只用于展示，所有判断都用原始字节数。</summary>
        private static string FormatSize(long bytes)
        {
            long value = bytes > 0 ? bytes : 0L;
            double gib = value / 1024d / 1024d / 1024d;

            if (gib >= 1d)
            {
                return gib.ToString("0.##", CultureInfo.InvariantCulture) + " GiB";
            }

            double mib = value / 1024d / 1024d;

            if (mib >= 1d)
            {
                return mib.ToString("0.##", CultureInfo.InvariantCulture) + " MiB";
            }

            double kib = value / 1024d;

            if (kib >= 1d)
            {
                return kib.ToString("0.##", CultureInfo.InvariantCulture) + " KiB";
            }

            return value.ToString(CultureInfo.InvariantCulture) + " 字节";
        }
    }
}
