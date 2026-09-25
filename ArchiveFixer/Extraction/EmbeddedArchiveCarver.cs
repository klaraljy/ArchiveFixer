using ArchiveFixer.Helpers;
using System;
using System.IO;
using System.Threading;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 抠出内嵌归档的结果。
    /// </summary>
    public sealed class CarveResult
    {
        /// <summary>是否成功。</summary>
        public bool Success { get; init; }

        /// <summary>实际写出的文件路径。目标已存在时会改名，所以**必须**用这个值，不要用调用时传进来的路径。</summary>
        public string OutputPath { get; init; } = string.Empty;

        /// <summary>写出的字节数。</summary>
        public long BytesWritten { get; init; }

        /// <summary>给人看的中文说明（失败时就是失败原因）。</summary>
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// 把"双面文件"尾部那段真正的归档原样抠出来。
    ///
    /// 为什么需要"抠"这一步（而不是直接把原文件交给 7z）：
    /// 这种文件的 ZIP 内部偏移是**相对 ZIP 自己**的。7-Zip 会尝试用"文件末尾 EOCD 反推出来的基准偏移"
    /// 去容忍这种错位，但**只容忍 8 MiB 以内**（本机 26.01 实测：前置数据 8,388,608 字节可以，
    /// 8,388,609 字节就报 "Cannot open the file as archive"）。用户那个文件前面垫了 17,031,321 字节，
    /// 必然落在拒绝区；把后缀改成 <c>.zip</c> 也没用（资源管理器的 ZIP 读取器容忍这种错位，7z 不容忍）。
    /// 唯一可靠的做法就是把这一段**原样**复制成一个新文件 —— 一个字节都不改，
    /// 复制完偏移自然就对齐了。（不要试图"修正" ZIP 内部偏移：那要重写中央目录，代价和风险都远大于复制。）
    ///
    /// <para>
    /// <b>复制范围是 <c>[offset, archiveEnd)</c>，不是 <c>[offset, EOF)</c></b>（2026-09-24 修正）：
    /// 真实资源包的 ZIP 后面还跟着十几 KB 正常数据（用户机器上 5 个真文件实测 14,350–17,424 字节，
    /// EOCD 并不在文件末尾），归档本身到 <c>EOCD + 22 + 注释长度</c> 就结束了。
    /// 把那截尾巴一起抠进去虽然多数读取器能忍，但它是**别的数据**，不该混进产物：
    /// 产物大小、空间核算、后续引擎读到的字节都会跟着不准。
    /// </para>
    ///
    /// <paramref name="archiveEnd"/> 缺省（0、或 ≤ offset、或超出文件）时回落到 EOF ——
    /// 与这个参数出现之前的行为逐字节一致（老调用点、以及"不知道归档到哪儿结束"的场合都走这条）。
    ///
    /// 落点约定：产物写进工作区（<c>%AppData%\ArchiveFixer\work\...</c>），
    /// **绝不写回源目录**，也**绝不覆盖**用户已有的同名文件（AGENTS.md §6 第 3、12 条）。
    /// </summary>
    public static class EmbeddedArchiveCarver
    {
        /// <summary>
        /// 复制缓冲区大小（4 MiB）。
        ///
        /// <para>与直读器同一个理由（用户 2026-09-25 第 38 条："有其他的方法来节约 zip 直读的时间吗"）：
        /// 抠取也是"读一块 → 写一块"交替跑，块越小、在 U 盘 / 机械盘上换向（寻道）越频繁。
        /// 实测同一块 H 盘上把 80 KB 提到 4 MiB，400 MB 的直读从约 20 秒降到 4.7 秒。
        /// 内存代价 4 MiB/任务，并发 4 时 16 MiB，可以接受。</para>
        /// </summary>
        private const int CopyBufferSize = 4 * 1024 * 1024;

        /// <summary>
        /// 把 <c>[offset, archiveEnd)</c> 这段原样复制到 <paramref name="targetPath"/>。
        /// 目标已存在时用 <see cref="SafePathHelper.AutoRenameFilePath"/> 改名，绝不覆盖。
        ///
        /// 异常一律吞成失败结果（不抛）：调用方在解压管线上，需要的是"这一单失败了、原因是什么"，
        /// 而不是一个把整批任务带下水的异常。
        /// </summary>
        /// <param name="sourcePath">源文件（"双面文件"）。</param>
        /// <param name="offset">归档起始偏移。</param>
        /// <param name="targetPath">产物落点。</param>
        /// <param name="archiveEnd">
        /// 归档结束位置（不含）。0 / ≤ <paramref name="offset"/> / 超出文件长度时回落到 EOF。
        /// </param>
        /// <param name="progress">进度回调（按区间长度算百分比）。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        public static CarveResult Carve(
            string sourcePath,
            long offset,
            string targetPath,
            long archiveEnd = 0,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            string finalPath = targetPath ?? string.Empty;

            try
            {
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                {
                    return Failure(finalPath, "源文件不存在，无法取出内嵌归档");
                }

                if (offset < 0)
                {
                    return Failure(finalPath, $"内嵌归档偏移不合法：{offset}");
                }

                long sourceLength = new FileInfo(sourcePath).Length;

                if (offset >= sourceLength)
                {
                    return Failure(
                        finalPath,
                        $"内嵌归档偏移 {offset} 已超出文件长度 {sourceLength}，无法取出");
                }

                if (string.IsNullOrWhiteSpace(targetPath))
                {
                    return Failure(finalPath, "没有指定内嵌归档的落点路径");
                }

                string? directory = Path.GetDirectoryName(SafePathHelper.GetFullPathSafe(targetPath));

                if (string.IsNullOrWhiteSpace(directory) || !SafePathHelper.EnsureDirectoryExists(directory))
                {
                    return Failure(finalPath, $"无法创建内嵌归档的落点目录：{directory}");
                }

                finalPath = File.Exists(targetPath) ? SafePathHelper.AutoRenameFilePath(targetPath) : targetPath;

                /*
                 * 归档区间的右端点：只认"落在 (offset, sourceLength] 里的 archiveEnd"，别的一律回落 EOF。
                 *
                 * 为什么这么宽：archiveEnd 是识别阶段从文件字节里算出来的，而**源文件可能在两次动作之间变过**
                 * （虽然不变量 11 的快照会挡住大多数情况）。宁可退化成"抠到文件末尾"（= 这个参数出现之前的行为），
                 * 也不要因为一个越界的右端点直接失败 —— 后者会让本来能解开的包彻底没救。
                 */
                long effectiveEnd = archiveEnd > offset && archiveEnd <= sourceLength ? archiveEnd : sourceLength;

                long written = CopyRange(sourcePath, offset, effectiveEnd, finalPath, progress, cancellationToken);

                return new CarveResult
                {
                    Success = true,
                    OutputPath = finalPath,
                    BytesWritten = written,
                    Message = $"已从偏移 {offset} 处取出 {written} 字节（区间 {offset}–{effectiveEnd}），落点 {finalPath}"
                };
            }
            catch (Exception ex)
            {
                return Failure(finalPath, "取出内嵌归档失败：" + ex.Message);
            }
        }

        /// <summary>顺序流式复制 <c>[offset, endExclusive)</c>，返回写出的字节数。</summary>
        private static long CopyRange(
            string sourcePath,
            long offset,
            long endExclusive,
            string targetPath,
            IProgress<int>? progress,
            CancellationToken cancellationToken)
        {
            /*
             * 源文件用 FileShare.Read（而不是识别时用的 ReadWrite）：
             * 复制出来的必须是**一致**的一段字节，别人正在写这个文件时宁可失败，
             * 也不要把"改了一半"的内容当成归档交出去 —— 那会变成一个更难查的"文件损坏"。
             */
            using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.SequentialScan);

            source.Seek(offset, SeekOrigin.Begin);

            /*
             * 用 CreateNew：万一 AutoRename 与真正落盘之间有人塞了个同名文件，
             * 这里会抛 IOException 而不是覆盖 —— 覆盖掉的是别人的文件，不可逆（AGENTS.md §6 第 3 条）。
             */
            using var target = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.SequentialScan);

            try
            {
                byte[] buffer = new byte[CopyBufferSize];
                long written = 0;
                long totalBytes = endExclusive - offset;
                long nextReportAt = 16L * 1024 * 1024;
                int read;

                /*
                 * 循环条件带上 totalBytes：归档区间有了右端点之后，"读到源文件结尾"不再等于"读够了"。
                 * 多读出去的那些字节是 EOCD 之后的正常数据，不该混进产物（见类注释）。
                 */
                while (written < totalBytes &&
                       (read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, totalBytes - written))) > 0)
                {
                    // 几百 MB 的拷贝要几十秒，必须能中途取消，否则用户只能强杀进程。
                    cancellationToken.ThrowIfCancellationRequested();

                    target.Write(buffer, 0, read);
                    written += read;

                    if (progress != null && (written >= nextReportAt || written >= totalBytes))
                    {
                        nextReportAt = written + (16L * 1024 * 1024);
                        progress.Report(totalBytes <= 0 ? 0 : (int)(written * 100 / totalBytes));
                    }
                }

                /*
                 * 读到的字节数必须与区间长度一致。
                 *
                 * 少读只有一个原因：源文件在"识别"与"抠取"之间被改短了。这种时候交出一个**短一截**的产物
                 * 是最坏的结果 —— 它看着像个包、其实末尾缺字节，后面会变成一个更难查的"文件损坏"。
                 * 抛出去让下面的 catch 把半截产物删掉并报失败（不变量 11 的同一口径）。
                 */
                if (written != totalBytes)
                {
                    throw new IOException(
                        $"源文件在取出过程中变短了：需要 {totalBytes} 字节，只读到 {written} 字节");
                }

                return written;
            }
            catch
            {
                /*
                 * 复制到一半失败：把半截文件删掉再报失败。
                 * 半截文件是最坏的结果 —— 它名字像归档、大小也不为 0，用户和后续流程都会把它当成真东西。
                 * 只删**本次刚创建的**这个文件，不碰任何别的东西。
                 */
                TryDeleteFile(targetPath);
                throw;
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 删不掉也没别的办法；失败结果已经说清了这一单没成功。
            }
        }

        private static CarveResult Failure(string outputPath, string message)
        {
            return new CarveResult
            {
                Success = false,
                OutputPath = outputPath ?? string.Empty,
                BytesWritten = 0,
                Message = message ?? string.Empty
            };
        }
    }
}
