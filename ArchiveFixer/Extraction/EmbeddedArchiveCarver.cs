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
    /// 唯一可靠的做法就是把 <c>[offset, EOF)</c> 这一段**原样**复制成一个新文件 —— 一个字节都不改，
    /// 复制完偏移自然就对齐了。（不要试图"修正" ZIP 内部偏移：那要重写中央目录，代价和风险都远大于复制。）
    ///
    /// 落点约定：产物写进工作区（<c>%AppData%\ArchiveFixer\work\...</c>），
    /// **绝不写回源目录**，也**绝不覆盖**用户已有的同名文件（AGENTS.md §6 第 3、12 条）。
    /// </summary>
    public static class EmbeddedArchiveCarver
    {
        /// <summary>复制缓冲区大小。顺序流式复制，几百 MB 的文件也只占这么点内存。</summary>
        private const int CopyBufferSize = 81920;

        /// <summary>
        /// 把 <c>[offset, EOF)</c> 这段原样复制到 <paramref name="targetPath"/>。
        /// 目标已存在时用 <see cref="SafePathHelper.AutoRenameFilePath"/> 改名，绝不覆盖。
        ///
        /// 异常一律吞成失败结果（不抛）：调用方在解压管线上，需要的是"这一单失败了、原因是什么"，
        /// 而不是一个把整批任务带下水的异常。
        /// </summary>
        public static CarveResult Carve(string sourcePath, long offset, string targetPath, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
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

                long written = CopyRange(sourcePath, offset, finalPath, progress, cancellationToken);

                return new CarveResult
                {
                    Success = true,
                    OutputPath = finalPath,
                    BytesWritten = written,
                    Message = $"已从偏移 {offset} 处取出 {written} 字节，落点 {finalPath}"
                };
            }
            catch (Exception ex)
            {
                return Failure(finalPath, "取出内嵌归档失败：" + ex.Message);
            }
        }

        /// <summary>顺序流式复制，返回写出的字节数。</summary>
        private static long CopyRange(string sourcePath, long offset, string targetPath, IProgress<int>? progress, CancellationToken cancellationToken)
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
                long totalBytes = source.Length - offset;
                long nextReportAt = 16L * 1024 * 1024;
                int read;

                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
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
