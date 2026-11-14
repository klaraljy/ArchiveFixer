using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 一次直读解压的**写盘会话**：逐条目落盘、逐条 CRC 比对、成功后改名落位、失败/取消整体回滚，最后汇总结果。
///
/// <para><b>生命周期只有一条</b>：<see cref="Run"/> 之前一个字节都不写（解析、路径预检、资源预算全部前置），
/// <see cref="Run"/> 里写的每一个文件都先写临时名、验完才改名落位；<see cref="Run"/> 抛出（取消 / 未预期的异常）
/// 或返回失败时，本次写出去的东西全部删掉 —— "半截文件"是最坏的结果（名字像真东西、大小也不为 0）。</para>
///
/// <para>回滚只删**我们自己刚创建**的那些：已存在的同名文件我们本来就没碰过（改名落位），
/// 所以不会误删用户的文件。</para>
///
/// <para>⛔ 不认格式、不判偏移：条目清单与数据布局由扫描 / 解析给，真正的字节管道在
/// <see cref="EmbeddedZipEntryWriter"/>。</para>
/// </summary>
internal sealed class EmbeddedZipExtractSession
{
    /// <summary>
    /// 写盘时的临时后缀。刻意**不像**归档名：万一留下（进程被杀），
    /// 用户与后续流程都不会把它当成一个真东西。
    /// </summary>
    private const string TempSuffix = ".afx-part";

    private readonly string _sourcePath;
    private readonly long _offset;
    private readonly long _end;
    private readonly EmbeddedZipProbeResult _plan;
    private readonly string _resolvedPassword;
    private readonly string _targetRoot;
    private readonly BudgetTracker _tracker;
    private readonly IProgress<int>? _progress;
    private readonly CancellationToken _cancellationToken;
    private readonly List<string> _written = new();
    private readonly List<string> _createdDirectories = new();

    internal EmbeddedZipExtractSession(
        string sourcePath,
        long offset,
        long end,
        EmbeddedZipProbeResult plan,
        string resolvedPassword,
        string targetRoot,
        BudgetTracker tracker,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        _sourcePath = sourcePath;
        _offset = offset;
        _end = end;
        _plan = plan;
        _resolvedPassword = resolvedPassword;
        _targetRoot = targetRoot;
        _tracker = tracker;
        _progress = progress;
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// 执行写盘。取消时抛 <see cref="OperationCanceledException"/>（调用方落成"已取消"，不变量 6），
    /// 抛出前会把本次写出去的东西全部删掉；数据层失败返回 <c>Success = false</c> 时同样不留半成品。
    /// </summary>
    internal EmbeddedZipExtractResult Run()
    {
        try
        {
            return WriteAll();
        }
        catch
        {
            /*
             * 取消 / 任何异常：把本次写出去的东西全删掉再抛。
             * "半截文件"是最坏的结果 —— 它名字像真东西、大小也不为 0，
             * 用户与后续流程都会把它当成解压产物（不变量 6 的同一口径）。
             */
            Rollback();
            throw;
        }
    }

    private EmbeddedZipExtractResult WriteAll()
    {
        /*
         * 源文件用 FileShare.Read（而不是探查时的 ReadWrite）：写出去的必须是**一致**的一段字节，
         * 别人正在写这个文件时宁可失败，也不要把"改了一半"的内容当成产物 —— 那会变成一个更难查的"文件损坏"。
         */
        /*
         * ⚠ 最后那个 FileOptions **必须**是随机访问、不能改成 SequentialScan：直读的读法本身就是随机的 ——
         * 先读文件末尾的中央目录，再按每个条目的本地头 seek 过去读数据（条目之间还可能跳着走）。
         * 标成顺序扫描会让缓存管理器不做"按需预读"（反而更慢）。
         * 真正省时间的是上面那个 4 MiB 缓冲（用户 2026-09-25 第 38 条：不换盘也要省）。
         */
        using var source = new FileStream(
            _sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            EmbeddedZipStreamFormat.CopyBufferSize,
            FileOptions.RandomAccess);

        EmbeddedZipProgressReporter reporter = EmbeddedZipProgressReporter.Start(_progress, _plan.TotalBytes);
        int fileCount = 0;

        foreach (EmbeddedZipEntry entry in _plan.Entries)
        {
            _cancellationToken.ThrowIfCancellationRequested();

            if (entry.IsDirectory)
            {
                string directory = string.IsNullOrWhiteSpace(entry.OutputPath)
                    ? Path.Combine(_targetRoot, entry.RelativePath)
                    : entry.OutputPath;

                EnsureDirectory(directory);
                continue;
            }

            string finalPath = string.IsNullOrWhiteSpace(entry.OutputPath)
                ? Path.Combine(_targetRoot, entry.RelativePath)
                : entry.OutputPath;

            // 绝不复核/覆盖已有文件（不变量 3）：同名一律改名落位。
            if (File.Exists(finalPath) || Directory.Exists(finalPath))
            {
                finalPath = SafePathHelper.AutoRenameFilePath(finalPath);
            }

            if (!ArchivePathGuard.IsInsideRoot(_targetRoot, finalPath, out string insideReason))
            {
                Rollback();

                return EmbeddedZipOutcomes.Failure(
                    $"条目落点不在目标根之内，已停止：{entry.Name} —— {insideReason}",
                    unsupported: false);
            }

            string? budgetReason = _tracker.Record(entry.Size);

            if (!string.IsNullOrWhiteSpace(budgetReason))
            {
                Rollback();

                return EmbeddedZipOutcomes.Failure("超出资源预算：" + budgetReason, unsupported: false);
            }

            EnsureDirectory(Path.GetDirectoryName(finalPath));

            string tempPath = finalPath + TempSuffix;

            // 上一次留下的同名临时文件（进程被杀）：先清掉，避免 CreateNew 直接失败。
            TryDeleteFile(tempPath);

            try
            {
                using (var target = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    EmbeddedZipStreamFormat.CopyBufferSize,
                    FileOptions.SequentialScan))
                using (var crcTarget = new Crc32WriteStream(target))
                {
                    long entryWritten = EmbeddedZipEntryWriter.WriteEntry(
                        source,
                        _offset,
                        entry,
                        _resolvedPassword,
                        _plan.ResolvedPasswordEncoding,
                        crcTarget,
                        reporter,
                        _cancellationToken);

                    if (entryWritten != entry.Size)
                    {
                        throw new IOException(
                            $"条目 {entry.Name} 解出的字节数与清单不符：清单 {entry.Size} 字节，实际 {entryWritten} 字节");
                    }

                    /*
                     * AES 的完整性由认证码负责（中央目录里的 CRC 在 AES 包里可能是占位 0）；
                     * 未加密条目必须逐条比对中央目录声明，防止“结构自洽但数据坏了”被当成成功。
                     */
                    if (entry.AesStrength == 0 && crcTarget.Value != entry.Crc32)
                    {
                        throw new Crc32MismatchException(
                            $"条目 {entry.Name} 的 CRC32 对不上：清单 0x{entry.Crc32:X8}，实际 0x{crcTarget.Value:X8}");
                    }
                }

                // 先写临时名 → 成功后改名落位。中途失败/取消时只删临时名，不留半截产物。
                FileOperations.Move(tempPath, finalPath);
            }
            catch (OperationCanceledException)
            {
                TryDeleteFile(tempPath);
                throw;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                /*
                 * **数据层面**的失败（认证码对不上 / 解出的字节数与清单不符 / 文件被截断 / 盘写不进去）
                 * 落成"失败结果"，不往上抛异常。
                 *
                 * 为什么（2026-09-25 第 29 条）：本类的约定是"失败不留半成品，并如实告诉调用方为什么"。
                 * 抛出去的话，调用方看到的是管线里一次未处理异常，而失败原因（加密包被改过、源文件在
                 * 解压途中变了）恰恰是用户最需要看到的那句话。取消仍然照旧抛（那是另一套语义）。
                 */
                TryDeleteFile(tempPath);
                Rollback();

                return EmbeddedZipOutcomes.Failure(
                    $"{entry.Name} 解压失败：{ex.Message}",
                    unsupported: false,
                    corrupted: ex is Crc32MismatchException);
            }
            catch
            {
                TryDeleteFile(tempPath);
                throw;
            }

            _written.Add(finalPath);
            fileCount++;
        }

        /*
         * 最后一个条目写完也可能已被取消（取消信号正好落在最后一块上）。
         * 不查这一下就会"用户按了取消、界面报解压成功"—— 不变量 6 的同一口径。
         */
        _cancellationToken.ThrowIfCancellationRequested();

        reporter.Complete();

        int aesStrength = _plan.Entries.FirstOrDefault(e => !e.IsDirectory && e.AesStrength != 0)?.AesStrength ?? 0;

        return new EmbeddedZipExtractResult
        {
            Success = true,
            Message = aesStrength == 0
                ? $"直读解出 {fileCount} 个文件 / {reporter.WrittenBytes} 字节（区间 {_offset}–{_end}，没有生成临时副本）"
                : $"直读解出 {fileCount} 个文件 / {reporter.WrittenBytes} 字节（区间 {_offset}–{_end}，没有生成临时副本；" +
                  $"加密包 {ZipAesCrypto.DescribeStrength(aesStrength)}，第 {_plan.ResolvedPasswordIndex} 个候选密码按" +
                  $"{ZipAesCrypto.DescribeEncoding(_plan.ResolvedPasswordEncoding)}通过校验）",
            FileCount = fileCount,
            WrittenBytes = reporter.WrittenBytes,
            Entries = _plan.Entries,
            ResolvedPasswordIndex = _plan.ResolvedPasswordIndex,
            ResolvedPasswordEncoding = _plan.ResolvedPasswordEncoding,
            WasEncrypted = aesStrength != 0,
            NamingSummary = _plan.NamingSummary
        };
    }

    /// <summary>
    /// 把本次写出去的东西全部删掉（深目录优先），只删**我们自己刚创建**的那些：
    /// 已存在的同名文件我们本来就没碰过（改名落位），所以这里不会误删用户的文件。
    /// </summary>
    private void Rollback()
    {
        for (int i = _written.Count - 1; i >= 0; i--)
        {
            TryDeleteFile(_written[i]);
        }

        _written.Clear();

        for (int i = _createdDirectories.Count - 1; i >= 0; i--)
        {
            try
            {
                if (Directory.Exists(_createdDirectories[i]) &&
                    !Directory.EnumerateFileSystemEntries(_createdDirectories[i]).Any())
                {
                    Directory.Delete(_createdDirectories[i]);
                }
            }
            catch
            {
                // 删不掉（被占用 / 非空）不影响结论：产物已经没了，目录留着无害。
            }
        }

        _createdDirectories.Clear();
    }

    private void EnsureDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || Directory.Exists(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        _createdDirectories.Add(directory);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                FileOperations.Delete(path);
            }
        }
        catch
        {
            // 删不掉也没别的办法；调用方拿到的是失败/取消结论，不会当成成功。
        }
    }

    /// <summary>把写出的字节同时喂给 CRC32；不关闭内层流。</summary>
    private sealed class Crc32WriteStream : Stream
    {
        private static readonly uint[] Table = BuildTable();

        private readonly Stream _inner;
        private uint _state = uint.MaxValue;

        public Crc32WriteStream(Stream inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public uint Value => ~_state;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count <= 0)
            {
                return;
            }

            uint state = _state;

            for (int i = 0; i < count; i++)
            {
                state = Table[(state ^ buffer[offset + i]) & 0xFF] ^ (state >> 8);
            }

            _state = state;
            _inner.Write(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Flush();
            }

            base.Dispose(disposing);
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];

            for (int i = 0; i < table.Length; i++)
            {
                uint value = (uint)i;

                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                }

                table[i] = value;
            }

            return table;
        }
    }

    private sealed class Crc32MismatchException : IOException
    {
        public Crc32MismatchException(string message) : base(message)
        {
        }
    }
}
