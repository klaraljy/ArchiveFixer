using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using ArchiveFixer.Engines;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 直接从源文件里读出来的一个条目。
    /// </summary>
    public sealed class EmbeddedZipEntry
    {
        /// <summary>归档里的原始名字（未做任何改写，方便用户回原始包里去核对）。</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>清洗之后的相对路径（正斜杠换成平台分隔符，非法段已按 <see cref="FileNameHelper"/> 处理）。</summary>
        public string RelativePath { get; init; } = string.Empty;

        /// <summary>落盘路径。**调用方必须用这个值**，不要自己按名字拼（同一条目可能已被改名）。</summary>
        public string OutputPath { get; init; } = string.Empty;

        /// <summary>条目解压后的原始大小（以中央目录为准，不信本地头）。</summary>
        public long Size { get; init; }

        /// <summary>压缩后大小（写出进度的分母用的是 <see cref="Size"/>，这个只用于报告与自洽校验）。</summary>
        public long CompressedSize { get; init; }

        public bool IsDirectory { get; init; }

        /// <summary>压缩方法（0 = stored，8 = deflate）。AES 条目这里是**真方法**（解密之后再按它解压）。</summary>
        public int Method { get; init; }

        /// <summary>本地文件头相对 ZIP 起点的偏移（数据从哪里开始要从它往后数）。</summary>
        public long LocalHeaderOffset { get; init; }

        /// <summary>
        /// AES 强度字节（1/2/3 = AES-128/192/256）；0 = 不是 AES 条目。
        ///
        /// <para>为什么把"是不是 AES"记在条目上：读数据时要用它决定
        /// "密文从哪里开始、有多长" —— 那份开销（盐 16 + 校验值 2 + 认证码 10 = 28 字节）
        /// 已经算在中央目录声明的压缩后大小里了。</para>
        /// </summary>
        public int AesStrength { get; init; }

        /// <summary>这条是不是加密条目（AES 或老式 ZipCrypto 都算）。</summary>
        public bool IsEncrypted { get; init; }

        /// <summary>AES 条目：密文总长（= 压缩后大小 − 28）。不是 AES 条目时为 0。</summary>
        public long CipherLength => AesStrength == 0 ? 0 : CompressedSize - ZipAesCrypto.OverheadLength;
    }

    /// <summary>
    /// "能不能直读这个内嵌 ZIP"的**只读**结论（一个字节都不写）。
    ///
    /// <para>它是"回落信号"的载体：<see cref="Supported"/> 为 false 且 <see cref="PathRejected"/> 为 false
    /// 时，调用方应当原地回落到"抠取 + 7z"；<see cref="PathRejected"/> 为 true 时是**硬失败**
    /// （条目名越界，抠出来交给 7z 也会被同一套路径预检拒掉，白拷一份等大的临时文件）。</para>
    /// </summary>
    public sealed class EmbeddedZipProbeResult
    {
        /// <summary>直读可用。</summary>
        public bool Supported { get; init; }

        /// <summary>不支持的原因（回落信号；<see cref="Supported"/> 为 true 时为空）。</summary>
        public string Reason { get; init; } = string.Empty;

        /// <summary>条目名预检未通过 = **硬失败**，不是回落信号（见类注释）。</summary>
        public bool PathRejected { get; init; }

        /// <summary>
        /// 加密条目：候选密码**一个都没通过校验** = **硬失败**，不是回落信号。
        ///
        /// <para>为什么不能回落：回落那条路是"抠出副本 + 7z"，而 7-Zip 对 AES 包的密码只按
        /// ANSI 字节派生（无视归档里的 UTF-8 声明），UTF-8 打包的中文密码在它那里**永远**是"密码错误"。
        /// 回落只会白拷一份等大的副本，再报一次同样的错 —— 那正是用户 2026-09-25 报的现场。</para>
        /// </summary>
        public bool PasswordRejected { get; init; }

        /// <summary>
        /// 这份归档**只差一个密码就能直读**（里面是 AES 条目，而这次没给候选密码）。
        ///
        /// <para>识别阶段（拿不到密码）用得上它：空间账面上要按"不需要那份等大的临时副本"算，
        /// 否则一个 40 GB 的加密双面文件会被一笔根本不会发生的抠取副本卡在空间门上。
        /// 密码不对时是 <see cref="PasswordRejected"/>（硬失败），同样不会抠副本 —— 所以这个乐观是准的。</para>
        /// </summary>
        public bool RequiresPassword { get; init; }

        /// <summary>硬失败时给用户看的那句话（与 <see cref="PathRejected"/> 同一口径）。</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>条目清单（含目录条目）。</summary>
        public IReadOnlyList<EmbeddedZipEntry> Entries { get; init; } = Array.Empty<EmbeddedZipEntry>();

        /// <summary>
        /// 同一个清单，翻成引擎口径的 <see cref="ArchiveListResult"/> ——
        /// 于是既有那套"路径预检 / 资源预算 / 空间核算 / 危险条目统计"**原样复用**，不需要第二套实现。
        /// </summary>
        public ArchiveListResult? List { get; init; }

        /// <summary>归档区间字节数（<c>End − Offset</c>）：也用在展开比的分母上。</summary>
        public long ArchiveLength { get; init; }

        /// <summary>有效起点（= 调用方给的 Offset）。</summary>
        public long Offset { get; init; }

        /// <summary>有效终点（<c>ArchiveEnd</c> 缺失时回落到文件末尾，与抠取侧同一口径）。</summary>
        public long End { get; init; }

        /// <summary>清单里所有文件条目的原始大小合计（进度分母）。</summary>
        public long TotalBytes { get; init; }

        /// <summary>
        /// 解锁成功的那个候选在调用方给的候选表里的序号（1 起；0 = 这份归档没加密、不需要密码）。
        ///
        /// <para>⛔ 这里刻意**不**放密码明文：调用方拿序号去自己的候选表里取备注（已经是脱敏的），
        /// 日志与结论里都只说"第 N 个候选 + 哪种字节编码"（AGENTS.md §8）。</para>
        /// </summary>
        public int ResolvedPasswordIndex { get; init; }

        /// <summary>解锁成功时用的是哪种密码字节编码（<see cref="ResolvedPasswordIndex"/> 为 0 时无意义）。</summary>
        public ZipAesPasswordEncoding ResolvedPasswordEncoding { get; init; }
    }

    /// <summary>
    /// 直读解压的结果。失败时**不留半成品**（临时名文件与已落位的文件都会被删掉）。
    /// </summary>
    public sealed class EmbeddedZipExtractResult
    {
        public bool Success { get; init; }

        /// <summary>失败原因是"不支持"（调用方可以回落抠取）还是"真的失败了"。</summary>
        public bool Unsupported { get; init; }

        /// <summary>失败原因 / 成功说明（中文，可直接进日志与任务状态）。</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>实际写出的文件数（不含目录）。</summary>
        public int FileCount { get; init; }

        /// <summary>实际写出的字节数（= 清单里的原始大小合计）。</summary>
        public long WrittenBytes { get; init; }

        /// <summary>落盘的条目清单（与 <see cref="EmbeddedZipProbeResult.Entries"/> 同一个口径）。</summary>
        public IReadOnlyList<EmbeddedZipEntry> Entries { get; init; } = Array.Empty<EmbeddedZipEntry>();

        /// <summary>解密用掉的候选密码序号（1 起；0 = 没加密）。⛔ 不放密码明文。</summary>
        public int ResolvedPasswordIndex { get; init; }

        /// <summary>解密时生效的密码字节编码。</summary>
        public ZipAesPasswordEncoding ResolvedPasswordEncoding { get; init; }

        /// <summary>是不是加密包（AES）。报告与日志里要说清"这次是按哪种字节解开的"。</summary>
        public bool WasEncrypted { get; init; }
    }

    /// <summary>
    /// 内嵌 ZIP **直读**：从 <c>[Offset, ArchiveEnd)</c> 这个"虚拟 ZIP"里流式解出条目，
    /// 不产生那份等大的临时副本。
    ///
    /// <para><b>为什么需要它</b>（用户 2026-09-24 拍板）：真实场景是"视频前缀 + 尾部一个完整 ZIP"，
    /// ZIP 段有 487 MB–2.4 GB，而 7-Zip 只容忍 8 MiB 以内的前缀错位（实测 8,388,608 字节可以、
    /// 8,388,609 就报 <c>Cannot open the file as archive</c>）。原来唯一的做法是把
    /// <c>[Offset, ArchiveEnd)</c> 整段抠成一份**等大**的临时副本再交给 7z ——
    /// 峰值空间约等于包大小的 3 倍。直读把 ZIP 内部偏移折算成源文件里的绝对偏移，
    /// 源文件只读、副本为零。</para>
    ///
    /// <para><b>判据（一项不过就回落，绝不猜）</b>：EOCD 找得到且自洽（
    /// <c>中央目录实际起点 == EOCD − ZIP64 收尾 − 中央目录大小 == 声明的中央目录偏移</c>）、
    /// 每个条目的本地头是 <c>PK\x03\x04</c>、压缩方法是 0（stored）或 8（deflate）、
    /// 没有加密位、ZIP64 占位符都能从扩展字段里取到真值、数据区不越过中央目录起点。
    /// 任何一条不成立都返回"不支持"，由调用方**原地回落到今天的"抠取 + 7z"**。</para>
    ///
    /// <para><b>安全</b>：条目名走既有的 <see cref="ArchivePathGuard"/>（与正常解压**同一处实现**）+
    /// <see cref="FileNameHelper"/> 清洗，落盘前再用 <see cref="ArchivePathGuard.IsInsideRoot"/> 复核，
    /// 越界一律拒绝整包；资源上限走既有的 <see cref="ResourceBudget"/>；
    /// 写文件一律"先写临时名 → 成功后改名落位"，取消/失败把本次写出去的东西全删掉。</para>
    ///
    /// <para>本类只做"解出条目"这一件事，不引用 WPF（AGENTS.md §4 分层铁律）。</para>
    /// </summary>
    public static class EmbeddedZipStreamExtractor
    {
        /// <summary>报告里用的读取器名字（不变量 14：结果要能追到"是谁干的活"）。</summary>
        public const string ReaderDisplayName = "内置 ZIP 直读";

        /// <summary>
        /// 在归档区间尾部找 EOCD 的窗口：256 KiB。取法与 <c>EmbeddedArchiveDetector.DefaultTailBytes</c> 一致 ——
        /// 既要放得下 ZIP64 收尾（76 字节）与注释上限（65535），也要覆盖"EOCD 之后还有十几 KB 正常数据"
        /// 那种真实现场（实测 14,350–17,424 字节）。
        /// </summary>
        private const int TailBytes = 262144;

        private const int EndOfCentralDirectoryLength = 22;
        private const int Zip64LocatorLength = 20;
        private const int Zip64EndOfCentralDirectoryLength = 56;
        private const int Zip64FooterLength = Zip64EndOfCentralDirectoryLength + Zip64LocatorLength;
        private const int CentralDirectoryHeaderLength = 46;
        private const int LocalFileHeaderLength = 30;

        /// <summary>
        /// 中央目录字节数上限（解析用的护栏，不是资源预算）：64 MiB 足够放 20 万条目，
        /// 再大就不是"正常归档"了，直接回落让 7z 去处理。
        /// </summary>
        private const long MaxCentralDirectoryBytes = 64L * 1024 * 1024;

        /// <summary>条目数上限（同上，解析护栏）。真正的上限判据在 <see cref="ResourceBudget"/> 里。</summary>
        private const int MaxEntryCount = 1_000_000;

        /// <summary>复制缓冲区。顺序流式，几百 MB 的条目也只占这么点内存。</summary>
        private const int CopyBufferSize = 81920;

        /// <summary>进度最小间隔：250 ms（与既有进度口径一致，不刷日志也不至于看着像卡住）。</summary>
        private const int ProgressIntervalMs = 250;

        /// <summary>
        /// 写盘时的临时后缀。刻意**不像**归档名：万一留下（进程被杀），
        /// 用户与后续流程都不会把它当成一个真东西。
        /// </summary>
        private const string TempSuffix = ".afx-part";

        private static readonly byte[] EndOfCentralDirectorySignature = { 0x50, 0x4B, 0x05, 0x06 };
        private static readonly byte[] CentralDirectorySignature = { 0x50, 0x4B, 0x01, 0x02 };
        private static readonly byte[] LocalFileHeaderSignature = { 0x50, 0x4B, 0x03, 0x04 };
        private static readonly byte[] Zip64EndOfCentralDirectorySignature = { 0x50, 0x4B, 0x06, 0x06 };
        private static readonly byte[] Zip64EndOfCentralDirectoryLocatorSignature = { 0x50, 0x4B, 0x06, 0x07 };

        // ================================================================ 只读探查

        /// <summary>
        /// 只读探查：能不能直读、清单是什么。**一个字节都不写**，也不改源文件。
        ///
        /// <para><paramref name="targetDirectory"/> 给了才会算每个条目的落盘路径并复核"在本任务输出根之内"；
        /// 识别阶段（空间核算）拿不到目标目录，传 null 即可 —— 那时只做条目名层面的预检。</para>
        ///
        /// <para><paramref name="passwords"/> 是候选密码表（按尝试顺序，可含空密码）。加密条目（AES）
        /// 会在这里被**逐个候选 × 两种字节编码**试一遍（校验值 + 认证码两道，见 <see cref="ZipAesCrypto"/>）：
        /// 有一个通过就返回 <c>Supported = true</c> 并记下它是第几个候选；一个都不通过返回
        /// <see cref="EmbeddedZipProbeResult.PasswordRejected"/>（**硬失败，绝不回落抠取**）。
        /// 传 null（识别阶段 / 不加密的包）时，遇到加密条目仍然返回"不支持"这个**回落信号**。</para>
        ///
        /// <para><paramref name="confirmResolvedPassword"/> 为 true 时，命中的候选还要用**认证码**确认
        /// （要顺序读完整段密文，几百 MB 的条目约 1 秒）；<see cref="Extract"/> 内部传 false ——
        /// 它马上就会真正解密，而那一步本来就会逐条验认证码，重复读一遍纯属浪费。</para>
        /// </summary>
        public static EmbeddedZipProbeResult Probe(
            string? sourcePath,
            long offset,
            long archiveEnd,
            string? targetDirectory = null,
            IReadOnlyList<string>? passwords = null,
            CancellationToken cancellationToken = default,
            bool confirmResolvedPassword = true)
        {
            try
            {
                if (!TryResolveRange(sourcePath, offset, archiveEnd, out string source, out long end, out string rangeError))
                {
                    return Unsupported(rangeError);
                }

                /*
                 * 探查用 FileShare.ReadWrite（与识别阶段一致）：这一步只读、不改，
                 * 别的程序正拿着这个文件时也应该能给出结论，而不是报一句"文件被占用"。
                 * 真正抠字节的解压那一步才要求 FileShare.Read（要的是一致的一段字节）。
                 */
                using var stream = new FileStream(
                    source,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    FileOptions.RandomAccess);

                return Parse(stream, offset, end, targetDirectory, passwords, cancellationToken, confirmResolvedPassword);
            }
            catch (Exception ex)
            {
                return Unsupported("直读探查失败：" + ex.Message);
            }
        }

        // ================================================================ 直读解压

        /// <summary>
        /// 把内嵌 ZIP 的条目**流式**解到 <paramref name="targetDirectory"/>。
        ///
        /// <para>取消时抛 <see cref="OperationCanceledException"/>（调用方落成"已取消"，不变量 6），
        /// 抛出前会把本次写出去的东西全部删掉；失败（返回值 <c>Success = false</c>）同样不留半成品。</para>
        ///
        /// <para><b>不支持只可能出现在"还没写任何字节"之前</b>：解析、方法/加密位/ZIP64/路径预检、
        /// 资源预算全部前置。所以调用方看到 <c>Unsupported</c> 时可以放心地原地回落到抠取。</para>
        /// </summary>
        /// <param name="progress">进度回调（0–100，按"已写出字节 / 清单里的总原始字节"算；≥ 每 250 ms 一次）。</param>
        /// <param name="budgetOptions">资源上限；null = <see cref="ResourceBudgetOptions.Default"/>。</param>
        /// <param name="passwords">候选密码表（加密的 AES 内嵌包要用；顺序 = 尝试顺序）。</param>
        public static EmbeddedZipExtractResult Extract(
            string? sourcePath,
            long offset,
            long archiveEnd,
            string targetDirectory,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default,
            ResourceBudgetOptions? budgetOptions = null,
            IReadOnlyList<string>? passwords = null)
        {
            if (!TryResolveRange(sourcePath, offset, archiveEnd, out string source, out long end, out string rangeError))
            {
                return Failure(rangeError, unsupported: false);
            }

            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                return Failure("没有指定直读解压的落点目录", unsupported: false);
            }

            string targetRoot = SafePathHelper.GetFullPathSafe(targetDirectory);

            if (!SafePathHelper.EnsureDirectoryExists(targetRoot))
            {
                return Failure($"无法创建直读解压的落点目录：{targetDirectory}", unsupported: false);
            }

            /*
             * confirmResolvedPassword: false —— 命中的候选不再单独读一遍密文去验认证码：
             * 紧接着的 ExtractCore 会把每个条目的认证码都验一遍（那是必须做的一步），
             * 在这里再读一遍等于把几百 MB 到 2 GB 的密文白白多读一次。
             */
            EmbeddedZipProbeResult plan = Probe(
                source,
                offset,
                end,
                targetRoot,
                passwords,
                cancellationToken,
                confirmResolvedPassword: false);

            if (!plan.Supported)
            {
                /*
                 * 路径硬失败与"密码一个都没对"都走这里：调用方按 PathRejected / PasswordRejected
                 * 区分"回落到抠取"与"直接判失败"（后者回落只会白拷一份等大的副本）。
                 */
                return new EmbeddedZipExtractResult
                {
                    Success = false,
                    Unsupported = !plan.PathRejected && !plan.PasswordRejected,
                    Message = plan.PathRejected || plan.PasswordRejected ? plan.Message : plan.Reason,
                    Entries = plan.Entries
                };
            }

            /*
             * 资源预算（复用 Security/ResourceBudget，不另造）：
             * 清单是解析出来的**全部**条目，所以走"解压前估算"这条最严的路 ——
             * 单文件 / 文件数 / 总大小 / 展开比 / 目标盘空间五项一次判完。
             */
            BudgetCheckResult budget = new ResourceBudget(budgetOptions)
                .CheckBeforeExtract(plan.List, plan.ArchiveLength, targetRoot);

            if (!budget.Allowed)
            {
                return Failure("资源预算未通过：" + budget.Reason, unsupported: false);
            }

            /*
             * 运行时预算（同一个实现）：解析出来的清单可能被改过的源文件"骗"到，
             * 所以真正写盘时再累计一遍。返回非 null 就停下并回滚 —— 与引擎路径同一口径。
             */
            BudgetTracker tracker = new ResourceBudget(budgetOptions).CreateTracker();

            var written = new List<string>();
            var createdDirectories = new List<string>();

            /*
             * 解锁成功的那个密码（列表里的第 ResolvedPasswordIndex 个）取出来往下传：
             * 解密要的是**明文密码 + 编码**，而不是"第几个候选"。
             * 它只活在这一个调用栈里，绝不进日志、报告与异常文本（AGENTS.md §8）。
             */
            string resolvedPassword = string.Empty;

            if (plan.ResolvedPasswordIndex > 0 && passwords != null &&
                plan.ResolvedPasswordIndex <= passwords.Count)
            {
                resolvedPassword = passwords[plan.ResolvedPasswordIndex - 1] ?? string.Empty;
            }

            try
            {
                return ExtractCore(
                    source,
                    offset,
                    end,
                    plan,
                    resolvedPassword,
                    targetRoot,
                    tracker,
                    written,
                    createdDirectories,
                    progress,
                    cancellationToken);
            }
            catch
            {
                /*
                 * 取消 / 任何异常：把本次写出去的东西全删掉再抛。
                 * "半截文件"是最坏的结果 —— 它名字像真东西、大小也不为 0，
                 * 用户与后续流程都会把它当成解压产物（不变量 6 的同一口径）。
                 */
                Rollback(written, createdDirectories);
                throw;
            }
        }

        // ================================================================ 解析

        /// <summary>
        /// 解析虚拟 ZIP：从后往前逐个试 EOCD 候选，第一个**结构自洽**的候选就是结论。
        /// 逐个试而不是"只看最后一个"的理由见 <c>EmbeddedArchiveDetector</c>：
        /// 真实现场里 EOCD 之后还有十几 KB 正常数据，那些数据里完全可能再出现一次同样的字节序列。
        /// </summary>
        private static EmbeddedZipProbeResult Parse(
            FileStream stream,
            long offset,
            long end,
            string? targetDirectory,
            IReadOnlyList<string>? passwords,
            CancellationToken cancellationToken,
            bool confirmResolvedPassword)
        {
            long zipLength = end - offset;
            int windowLength = (int)Math.Min(zipLength, TailBytes);
            long windowStartRel = zipLength - windowLength;
            byte[] window = ReadAt(stream, offset + windowStartRel, windowLength);

            if (window.Length < EndOfCentralDirectoryLength)
            {
                return Unsupported("内嵌归档区间太短，连一个 ZIP 收尾都放不下");
            }

            string structuralReason = "内嵌归档里找不到自洽的 ZIP 收尾（EOCD 与中央目录位置对不上）";
            int eocdIndex = LastIndexOfSignature(
                window,
                EndOfCentralDirectorySignature,
                window.Length - EndOfCentralDirectoryLength);

            while (eocdIndex >= 0)
            {
                CandidateEvaluation evaluation = EvaluateCandidate(
                    stream,
                    window,
                    windowStartRel,
                    eocdIndex,
                    offset,
                    zipLength,
                    targetDirectory,
                    passwords,
                    cancellationToken,
                    confirmResolvedPassword,
                    out string? reason);

                if (reason != null)
                {
                    structuralReason = reason;
                }

                if (evaluation.IsCandidate)
                {
                    return evaluation.Result!;
                }

                eocdIndex = LastIndexOfSignature(window, EndOfCentralDirectorySignature, eocdIndex - 1);
            }

            return Unsupported(structuralReason);
        }

        /// <summary>
        /// 校验一个 EOCD 候选。
        /// <c>IsCandidate = false</c> 表示"这不是那个归档"（继续往前找下一个候选）；
        /// <c>IsCandidate = true</c> 表示"就是它"，此时 <c>Result</c> 可能是"不支持"（加密 / 方法 / ZIP64 缺字段）。
        /// </summary>
        private static CandidateEvaluation EvaluateCandidate(
            FileStream stream,
            byte[] window,
            long windowStartRel,
            int eocdIndex,
            long offset,
            long zipLength,
            string? targetDirectory,
            IReadOnlyList<string>? passwords,
            CancellationToken cancellationToken,
            bool confirmResolvedPassword,
            out string? structuralReason)
        {
            structuralReason = null;

            long eocdRel = windowStartRel + eocdIndex;

            // EOCD 布局：偏移 10 = 条目总数(2)，12 = 中央目录大小(4)，16 = 中央目录偏移(4)，20 = 注释长度(2)。
            long entryCount16 = BitConverter.ToUInt16(window, eocdIndex + 10);
            long cdSize32 = BitConverter.ToUInt32(window, eocdIndex + 12);
            long cdOffset32 = BitConverter.ToUInt32(window, eocdIndex + 16);
            int commentLength = BitConverter.ToUInt16(window, eocdIndex + 20);

            /*
             * 归档区间必须容得下"EOCD + 注释"：注释长度是从文件里读出来的，一个坏值就能把它顶到区间外面。
             * 这一条同时保证后面按终点截断/读取时不会越界。
             */
            if (eocdRel + EndOfCentralDirectoryLength + commentLength > zipLength)
            {
                return CandidateEvaluation.NotACandidate;
            }

            bool hasZip64 = eocdIndex >= Zip64FooterLength
                && Matches(window, eocdIndex - Zip64LocatorLength, Zip64EndOfCentralDirectoryLocatorSignature)
                && Matches(window, eocdIndex - Zip64FooterLength, Zip64EndOfCentralDirectorySignature);

            long cdSize = cdSize32;
            long cdOffset = cdOffset32;
            long entryCount = entryCount16;

            if (hasZip64)
            {
                int recordIndex = eocdIndex - Zip64FooterLength;

                // ZIP64 EOCD 记录：条目数在 +32，CD 大小在 +40，CD 偏移在 +48。
                long zip64EntryCount = ReadUInt64AsLong(window, recordIndex + 32);
                long zip64CdSize = ReadUInt64AsLong(window, recordIndex + 40);
                long zip64CdOffset = ReadUInt64AsLong(window, recordIndex + 48);

                if (zip64CdSize >= 0 && zip64CdOffset >= 0)
                {
                    cdSize = zip64CdSize;
                    cdOffset = zip64CdOffset;

                    if (zip64EntryCount >= 0)
                    {
                        entryCount = zip64EntryCount;
                    }
                }
                else if (cdSize32 == uint.MaxValue || cdOffset32 == uint.MaxValue || entryCount16 == ushort.MaxValue)
                {
                    /*
                     * ZIP64 记录在、真值却拿不到，而 32 位字段又是占位符 —— 没法算，**不猜**。
                     * 这正是"ZIP64 尺寸/偏移字段缺失 → 不支持"的落点。
                     */
                    return CandidateEvaluation.Unsupported(
                        "ZIP64 字段缺失或不可解析（EOCD 写的是占位符，ZIP64 记录里也没有真值）");
                }
            }
            else if (cdSize32 == uint.MaxValue || cdOffset32 == uint.MaxValue || entryCount16 == ushort.MaxValue)
            {
                return CandidateEvaluation.Unsupported("EOCD 用了 ZIP64 占位符，但归档里没有 ZIP64 收尾记录");
            }

            long footer = hasZip64 ? Zip64FooterLength : 0;

            if (cdSize < 0 || cdSize > eocdRel - footer)
            {
                structuralReason = $"中央目录大小 {cdSize} 与归档区间对不上，不是内嵌归档的收尾";
                return CandidateEvaluation.NotACandidate;
            }

            long cdRelStart = eocdRel - footer - cdSize;

            /*
             * 自洽的核心一步：**声明的中央目录偏移必须正好等于"从 EOCD 往前数"得到的实际起点**。
             *
             * 为什么不放松成"允许一个正 delta"：ZIP 里所有偏移都是相对 ZIP 自己起点的，
             * 一旦声明的偏移与实际起点差了一截，说明"这个 ZIP 真正的起点"并不是调用方给的 Offset ——
             * 那时按 offset + localOffset 去取数据会取到错位的字节，产出看着像成功、内容却是坏的。
             * 这种形态交给"抠取 + 7z"（它自带按 EOCD 反推基准偏移的容忍逻辑）才是对的。
             */
            if (cdOffset != cdRelStart)
            {
                structuralReason =
                    $"中央目录声明偏移 {cdOffset} 与从 EOCD 反推的实际起点 {cdRelStart} 不一致，直读不敢认";
                return CandidateEvaluation.NotACandidate;
            }

            if (cdSize > MaxCentralDirectoryBytes)
            {
                return CandidateEvaluation.Unsupported(
                    $"中央目录 {cdSize} 字节，超过直读的解析上限（{MaxCentralDirectoryBytes} 字节）");
            }

            if (entryCount < 0 || entryCount > MaxEntryCount)
            {
                return CandidateEvaluation.Unsupported($"归档声明的条目数 {entryCount} 不合理，直读不支持");
            }

            byte[] centralDirectory = ReadAt(stream, offset + cdRelStart, (int)cdSize);

            if (centralDirectory.Length != cdSize)
            {
                structuralReason = "中央目录读不满（文件可能被截断）";
                return CandidateEvaluation.NotACandidate;
            }

            return BuildEntries(
                stream,
                centralDirectory,
                offset,
                zipLength,
                cdRelStart,
                entryCount,
                targetDirectory,
                passwords,
                cancellationToken,
                confirmResolvedPassword);
        }

        /// <summary>
        /// 逐条读中央目录（<c>PK\x01\x02</c>），再去本地头（<c>PK\x03\x04</c>）确认数据起点。
        ///
        /// <para>两条刻意的规矩：① <b>尺寸一律以中央目录为准</b>（本地头里的尺寸在有数据描述符时是 0，
        /// 9 成以上的工具都会写成 0）；② 本地头长度 = 30 + 名字长 + 扩展区长，**不是**固定 30。</para>
        /// </summary>
        private static CandidateEvaluation BuildEntries(
            FileStream stream,
            byte[] centralDirectory,
            long offset,
            long zipLength,
            long cdRelStart,
            long declaredEntryCount,
            string? targetDirectory,
            IReadOnlyList<string>? passwords,
            CancellationToken cancellationToken,
            bool confirmResolvedPassword)
        {
            var entries = new List<EmbeddedZipEntry>();
            var listEntries = new List<ArchiveEntry>();
            int index = 0;
            long totalBytes = 0;
            bool hasAesEntry = false;

            while (index < centralDirectory.Length)
            {
                if (index + CentralDirectoryHeaderLength > centralDirectory.Length ||
                    !Matches(centralDirectory, index, CentralDirectorySignature))
                {
                    return CandidateEvaluation.NotACandidate;
                }

                int nameLength = BitConverter.ToUInt16(centralDirectory, index + 28);
                int extraLength = BitConverter.ToUInt16(centralDirectory, index + 30);
                int entryCommentLength = BitConverter.ToUInt16(centralDirectory, index + 32);

                if (index + CentralDirectoryHeaderLength + nameLength + extraLength + entryCommentLength >
                    centralDirectory.Length)
                {
                    return CandidateEvaluation.NotACandidate;
                }

                int flags = BitConverter.ToUInt16(centralDirectory, index + 8);
                int method = BitConverter.ToUInt16(centralDirectory, index + 10);
                long compressedSize = BitConverter.ToUInt32(centralDirectory, index + 20);
                long uncompressedSize = BitConverter.ToUInt32(centralDirectory, index + 24);
                long diskStart = BitConverter.ToUInt16(centralDirectory, index + 34);
                long localOffset = BitConverter.ToUInt32(centralDirectory, index + 42);

                int nameIndex = index + CentralDirectoryHeaderLength;
                int extraIndex = nameIndex + nameLength;
                string name = DecodeName(centralDirectory, nameIndex, nameLength, flags);

                // ZIP64 扩展字段 0x0001：只按"哪些字段是占位符"的顺序出现（APPNOTE 4.5.3）。
                Zip64Extras extras = ReadZip64Extras(centralDirectory, extraIndex, extraLength, uncompressedSize, compressedSize, localOffset, diskStart);

                uncompressedSize = extras.UncompressedSize;
                compressedSize = extras.CompressedSize;
                localOffset = extras.LocalOffset;
                diskStart = extras.DiskStart;

                /*
                 * 占位符必须已经被 ZIP64 扩展字段里的真值换掉。
                 *
                 * ⚠ 0xFFFFFFFF / 0xFFFF 是**正整数**，光判 "< 0" 是漏的：漏掉的后果是拿 4 GB 当尺寸去读，
                 * 读出一堆垃圾还可能把盘写满 —— 所以这里逐个字段显式比占位符。
                 */
                if (uncompressedSize == uint.MaxValue ||
                    compressedSize == uint.MaxValue ||
                    localOffset == uint.MaxValue ||
                    diskStart == ushort.MaxValue ||
                    uncompressedSize < 0 ||
                    compressedSize < 0 ||
                    localOffset < 0)
                {
                    return CandidateEvaluation.Unsupported($"条目 {name} 的 ZIP64 尺寸/偏移字段缺失，直读不支持");
                }

                if (diskStart != 0)
                {
                    return CandidateEvaluation.Unsupported($"条目 {name} 声明在别的盘上（分卷 ZIP），直读不支持");
                }

                // 通用位标志 bit0 = 加密。
                bool isEncrypted = (flags & ZipAesCrypto.EncryptedFlag) != 0;
                int aesStrength = 0;

                if (isEncrypted)
                {
                    /*
                     * 加密条目分两种，能不能直读差别很大（2026-09-25 第 29 条）：
                     *
                     * · **AES（method = 99）**：加密方式写在扩展字段 0x9901 里，我们按规范自己解
                     *   （<see cref="ZipAesCrypto"/>）。这条路必须自己走 —— 7-Zip 对 AES 包的密码
                     *   只按 ANSI 字节派生，百度网盘那种"UTF-8 打包"的中文密码在它那里永远报密码错误。
                     * · **老式 ZipCrypto**：本读取器不做，仍是"不支持"（回落到抠取 + 7z 是对的，7z 解它没问题）。
                     *
                     * ⚠ 这里必须**立刻返回**，不能"记一笔、continue 到循环末尾再统一报"：
                     * 中央目录的游标 `index += …` 在循环体末尾，`continue` 会把它跳过 → 死循环空转
                     * （2026-09-25 实测：全量测试卡在这条路上二十多分钟，进程 0.79 核空转、不碰磁盘）。
                     */
                    if (method != ZipAesCrypto.AesMethod)
                    {
                        return CandidateEvaluation.Unsupported($"归档里有加密条目（{name}），直读不支持");
                    }

                    ReadAesExtra(centralDirectory, extraIndex, extraLength, out aesStrength, out int actualMethod);

                    if (ZipAesCrypto.KeyLengthForStrength(aesStrength) == 0)
                    {
                        return CandidateEvaluation.Unsupported(
                            $"条目 {name} 声明了认不出的 AES 强度（{aesStrength}），直读不支持");
                    }

                    if (actualMethod != 0 && actualMethod != 8)
                    {
                        return CandidateEvaluation.Unsupported(
                            $"条目 {name} 的 AES 内层压缩方法 {actualMethod} 直读不支持（只认 stored / deflate）");
                    }

                    if (compressedSize < ZipAesCrypto.OverheadLength + 1)
                    {
                        return CandidateEvaluation.Unsupported(
                            $"条目 {name} 的压缩后大小 {compressedSize} 放不下 AES 的盐与认证码，直读不支持");
                    }

                    // method 换成**真方法**：解密之后要按它解压（下游只看 Method，不关心它原来写着 99）。
                    method = actualMethod;
                    hasAesEntry = true;
                }

                if (method != 0 && method != 8)
                {
                    return CandidateEvaluation.Unsupported($"条目 {name} 用了直读不支持的压缩方法（{method}）");
                }

                bool isDirectory = name.EndsWith("/", StringComparison.Ordinal) ||
                                   name.EndsWith("\\", StringComparison.Ordinal);

                if (!isDirectory)
                {
                    // 数据必须落在中央目录之前：越过去就说明偏移体系不对（防"看着成功、内容错位"）。
                    if (localOffset + LocalFileHeaderLength > cdRelStart ||
                        localOffset + compressedSize > cdRelStart)
                    {
                        return CandidateEvaluation.NotACandidate;
                    }

                    if (!Matches(ReadAt(stream, offset + localOffset, LocalFileHeaderLength), 0, LocalFileHeaderSignature))
                    {
                        return CandidateEvaluation.NotACandidate;
                    }

                    int localNameLength = BitConverter.ToUInt16(ReadAt(stream, offset + localOffset + 26, 2), 0);
                    int localExtraLength = BitConverter.ToUInt16(ReadAt(stream, offset + localOffset + 28, 2), 0);
                    long dataRel = localOffset + LocalFileHeaderLength + localNameLength + localExtraLength;

                    if (dataRel < 0 || dataRel + compressedSize > cdRelStart)
                    {
                        return CandidateEvaluation.NotACandidate;
                    }

                    totalBytes = SaturatingSum(totalBytes, uncompressedSize);
                    listEntries.Add(new ArchiveEntry { Path = name, Size = uncompressedSize, IsDirectory = false });
                }
                else
                {
                    listEntries.Add(new ArchiveEntry { Path = name, Size = 0, IsDirectory = true });
                }

                entries.Add(new EmbeddedZipEntry
                {
                    Name = name,
                    Size = isDirectory ? 0 : uncompressedSize,
                    CompressedSize = compressedSize,
                    IsDirectory = isDirectory,
                    Method = method,
                    LocalHeaderOffset = localOffset,
                    AesStrength = isDirectory ? 0 : aesStrength,
                    IsEncrypted = isEncrypted
                });

                index += CentralDirectoryHeaderLength + nameLength + extraLength + entryCommentLength;
            }

            if (index != centralDirectory.Length ||
                (declaredEntryCount > 0 && entries.Count != declaredEntryCount))
            {
                return CandidateEvaluation.NotACandidate;
            }

            /*
             * 加密包没有候选密码：仍然返回"不支持"这个**回落信号**（外加"只差一个密码"这个标记）。
             * 识别阶段（ArchiveDetectService）就是这条路径 —— 那里拿不到密码，也不该在识别时猜密码；
             * 但空间核算要知道"只要给对密码就能直读"，否则会为一笔不会发生的抠取副本预留空间。
             */
            if (hasAesEntry && (passwords == null || passwords.Count == 0))
            {
                return CandidateEvaluation.NeedsPassword("归档里有加密条目（AES），直读需要密码");
            }

            int resolvedPasswordIndex = 0;
            ZipAesPasswordEncoding resolvedEncoding = ZipAesPasswordEncoding.Utf8;

            if (hasAesEntry)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryResolvePassword(
                        stream,
                        offset,
                        entries,
                        passwords!,
                        cancellationToken,
                        confirmResolvedPassword,
                        out resolvedPasswordIndex,
                        out resolvedEncoding,
                        out string passwordReason))
                {
                    /*
                     * 一个候选都没通过 = **硬失败**，绝不回落抠取：
                     * 抠取 + 7-Zip 这条路对 UTF-8 打包的 AES 中文密码**必然**再报一次"密码错误"，
                     * 只会白拷一份等大的副本（用户 2026-07-… 那种 980 MB 级副本 + 失败残留的现场）。
                     */
                    return CandidateEvaluation.PasswordRejected(
                        passwordReason,
                        entries,
                        listEntries,
                        totalBytes,
                        isEncrypted: true);
                }
            }

            // 路径预检：与正常解压**同一处实现**（AGENTS.md §6 第 4 条），不另写一份。
            PathSafetyReport pathReport = ArchivePathGuard.CheckEntries(listEntries);

            if (!pathReport.IsSafe)
            {
                return CandidateEvaluation.PathRejected(
                    "归档里有不安全的条目，已拒绝解压：" + pathReport.Summary,
                    entries,
                    listEntries,
                    totalBytes);
            }

            if (!TryBuildOutputPaths(entries, targetDirectory, out string pathReason2))
            {
                return CandidateEvaluation.PathRejected(
                    "归档里的条目落点不安全，已拒绝解压：" + pathReason2,
                    entries,
                    listEntries,
                    totalBytes);
            }

            return CandidateEvaluation.Ok(
                entries,
                new ArchiveListResult
                {
                    Success = true,
                    Entries = listEntries,
                    TotalUncompressedSize = totalBytes,
                    FileCount = listEntries.Count(e => !e.IsDirectory),
                    DirectoryCount = listEntries.Count(e => e.IsDirectory),
                    IsEncrypted = hasAesEntry,
                    IsMultiVolume = false,
                    EngineId = "embedded-zip-direct",
                    EngineVersion = EmbeddedZipStreamExtractorVersion,
                    Message = "内置 ZIP 直读解析出的条目清单"
                },
                totalBytes,
                offset,
                offset + zipLength,
                resolvedPasswordIndex,
                resolvedEncoding);
        }

        /// <summary>
        /// 读 AES 扩展字段（<c>0x9901</c>，7 字节）：版本(2) + 厂商标识 "AE"(2) + 强度(1) + **真压缩方法**(2)。
        ///
        /// <para>⚠ 真方法必须从这里读：中央目录与方法字段写的是 99（= "是 AES"），
        /// 数据其实还是先按 stored/deflate 处理再加密的。少了这一步会把密文当 deflate 解，得到垃圾或异常。</para>
        /// </summary>
        private static void ReadAesExtra(
            byte[] extra,
            int extraIndex,
            int extraLength,
            out int strength,
            out int actualMethod)
        {
            strength = 0;
            actualMethod = -1;

            int end = extraIndex + extraLength;
            int cursor = extraIndex;

            while (cursor + 4 <= end)
            {
                int id = BitConverter.ToUInt16(extra, cursor);
                int size = BitConverter.ToUInt16(extra, cursor + 2);
                int body = cursor + 4;

                if (body + size > end)
                {
                    return;
                }

                if (id == 0x9901 && size >= 7)
                {
                    strength = extra[body + 4];
                    actualMethod = BitConverter.ToUInt16(extra, body + 5);
                    return;
                }

                cursor = body + size;
            }
        }

        /// <summary>
        /// 逐个候选密码 × 两种字节编码试开第一条 AES 条目：
        /// 先用两字节校验值筛（PBKDF2 1000 轮，约 0.5 ms 一次，够便宜），
        /// 再用**认证码**确认（HMAC-SHA1 覆盖整段密文 —— 校验值只有 2 字节，1/65536 的巧合必须由它兜住）。
        ///
        /// <para>顺序上先试 UTF-8：百度网盘那种分享包在归档里就声明着 <c>Encrypt UTF8</c>，
        /// 而它恰恰是 7-Zip 打不开的那一类；WinRAR 那类按 ANSI 打包的包会在第二档命中。</para>
        /// </summary>
        private static bool TryResolvePassword(
            FileStream stream,
            long offset,
            List<EmbeddedZipEntry> entries,
            IReadOnlyList<string> passwords,
            CancellationToken cancellationToken,
            bool confirmResolvedPassword,
            out int resolvedIndex,
            out ZipAesPasswordEncoding resolvedEncoding,
            out string reason)
        {
            resolvedIndex = 0;
            resolvedEncoding = ZipAesPasswordEncoding.Utf8;
            reason = string.Empty;

            EmbeddedZipEntry? probeEntry = entries.FirstOrDefault(e => !e.IsDirectory && e.AesStrength != 0);

            if (probeEntry == null)
            {
                return true;
            }

            if (!TryGetAesDataLayout(stream, offset, probeEntry, out AesDataLayout layout, out string layoutError))
            {
                reason = layoutError;
                return false;
            }

            int pvMatched = 0;

            for (int i = 0; i < passwords.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (ZipAesPasswordEncoding encoding in new[]
                         {
                             ZipAesPasswordEncoding.Utf8,
                             ZipAesPasswordEncoding.AnsiCodePage
                         })
                {
                    if (!ZipAesCrypto.TryDeriveKeys(
                            passwords[i] ?? string.Empty,
                            encoding,
                            layout.Salt,
                            probeEntry.AesStrength,
                            out ZipAesKeys? keys,
                            out _))
                    {
                        continue;
                    }

                    if (keys!.PasswordVerifier != layout.PasswordVerifier)
                    {
                        continue;
                    }

                    pvMatched++;

                    /*
                     * 校验值撞上了：用认证码确认。
                     * 这一步要顺序读完整段密文（几百 MB 的条目约 1 秒），所以只在"校验值对了"时才做 ——
                     * 一个候选都不对时，整轮尝试仍是毫秒级。
                     */
                    if (!confirmResolvedPassword || ConfirmByAuthenticationCode(stream, layout, keys, cancellationToken))
                    {
                        resolvedIndex = i + 1;
                        resolvedEncoding = encoding;
                        return true;
                    }
                }
            }

            reason = pvMatched > 0
                ? $"这个内嵌 ZIP 是 AES 加密的（{ZipAesCrypto.DescribeStrength(probeEntry.AesStrength)}），" +
                  $"{passwords.Count} 个候选密码里有一个通过了校验值，但认证码对不上（密文可能被改过 / 源文件变过）——" +
                  "没有解开，也没有生成临时副本。"
                : $"这个内嵌 ZIP 是 AES 加密的（{ZipAesCrypto.DescribeStrength(probeEntry.AesStrength)}），" +
                  $"{passwords.Count} 个候选密码逐个按 UTF-8 与 ANSI(936) 两种字节各试了一遍，都不对。" +
                  "（7-Zip 对 AES 包只按 ANSI 字节派生密码，所以这类包交给它也一样报密码错误。）";

            return false;
        }

        /// <summary>AES 条目的数据布局：盐 / 校验值 / 密文 / 认证码在源文件里的位置。</summary>
        private readonly struct AesDataLayout
        {
            public byte[] Salt { get; init; }

            public ushort PasswordVerifier { get; init; }

            public long CipherStart { get; init; }

            public long CipherLength { get; init; }

            public byte[] AuthenticationCode { get; init; }
        }

        /// <summary>按本地头算出 AES 条目的数据布局（本地头长度每次都重算，理由同 <see cref="WriteEntry"/>）。</summary>
        private static bool TryGetAesDataLayout(
            FileStream stream,
            long offset,
            EmbeddedZipEntry entry,
            out AesDataLayout layout,
            out string error)
        {
            layout = default;
            error = string.Empty;

            long localHeader = offset + entry.LocalHeaderOffset;
            byte[] header = ReadAt(stream, localHeader, LocalFileHeaderLength);

            if (header.Length != LocalFileHeaderLength || !Matches(header, 0, LocalFileHeaderSignature))
            {
                error = $"条目 {entry.Name} 的本地文件头不存在或不是 PK\\x03\\x04";
                return false;
            }

            int localNameLength = BitConverter.ToUInt16(header, 26);
            int localExtraLength = BitConverter.ToUInt16(header, 28);
            long dataOffset = localHeader + LocalFileHeaderLength + localNameLength + localExtraLength;
            long cipherStart = dataOffset + ZipAesCrypto.SaltLength + ZipAesCrypto.PasswordVerifierLength;
            long cipherLength = entry.CompressedSize - ZipAesCrypto.OverheadLength;

            if (cipherLength < 0)
            {
                error = $"条目 {entry.Name} 的压缩后大小 {entry.CompressedSize} 放不下 AES 的盐与认证码";
                return false;
            }

            byte[] prefix = ReadAt(stream, dataOffset, ZipAesCrypto.SaltLength + ZipAesCrypto.PasswordVerifierLength);

            if (prefix.Length != ZipAesCrypto.SaltLength + ZipAesCrypto.PasswordVerifierLength)
            {
                error = $"条目 {entry.Name} 的 AES 盐/校验值读不满（文件可能被截断）";
                return false;
            }

            var salt = new byte[ZipAesCrypto.SaltLength];
            Buffer.BlockCopy(prefix, 0, salt, 0, ZipAesCrypto.SaltLength);

            byte[] authCode = ReadAt(stream, cipherStart + cipherLength, ZipAesCrypto.AuthenticationCodeLength);

            if (authCode.Length != ZipAesCrypto.AuthenticationCodeLength)
            {
                error = $"条目 {entry.Name} 的 AES 认证码读不满（文件可能被截断）";
                return false;
            }

            layout = new AesDataLayout
            {
                Salt = salt,
                PasswordVerifier = BitConverter.ToUInt16(prefix, ZipAesCrypto.SaltLength),
                CipherStart = cipherStart,
                CipherLength = cipherLength,
                AuthenticationCode = authCode
            };

            return true;
        }

        /// <summary>把整段密文按"解密 + 认证"读一遍，只判认证码对不对（不落任何字节）。</summary>
        private static bool ConfirmByAuthenticationCode(
            FileStream stream,
            AesDataLayout layout,
            ZipAesKeys keys,
            CancellationToken cancellationToken)
        {
            using var decrypt = new ZipAesDecryptStream(stream, layout.CipherStart, layout.CipherLength, keys);
            byte[] buffer = new byte[CopyBufferSize];
            long total = 0;

            while (total < layout.CipherLength)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int read = decrypt.Read(buffer, 0, buffer.Length);

                if (read <= 0)
                {
                    return false;
                }

                total += read;
            }

            return decrypt.VerifyAuthenticationCode(layout.AuthenticationCode);
        }

        /// <summary>
        /// 给每个条目算出落盘路径：逐段用 <see cref="FileNameHelper.SanitizeFileName"/> 清洗，
        /// 再复核完整路径确实在目标根之内。
        ///
        /// <para><paramref name="targetDirectory"/> 为 null（识别阶段的探查）时只做"能算出相对路径"这一步 ——
        /// 那时没有目标根，拿不到"在不在根之内"的结论，也就不假装有。</para>
        /// </summary>
        private static bool TryBuildOutputPaths(
            List<EmbeddedZipEntry> entries,
            string? targetDirectory,
            out string reason)
        {
            reason = string.Empty;

            for (int i = 0; i < entries.Count; i++)
            {
                EmbeddedZipEntry entry = entries[i];

                // 统一按正斜杠切段：ZIP 里两种斜杠混用很常见，只认一种就会漏判。
                string normalized = entry.Name.Replace('\\', '/');
                var parts = new List<string>();

                foreach (string segment in normalized.Split('/'))
                {
                    if (segment.Length == 0 || segment == ".")
                    {
                        continue;
                    }

                    // ".." 已经在 ArchivePathGuard 那一关被拒了；这里再兜一次，绝不让它进落点。
                    if (segment == "..")
                    {
                        reason = $"条目名里有 .. 片段：{entry.Name}";
                        return false;
                    }

                    parts.Add(FileNameHelper.SanitizeFileName(segment));
                }

                if (parts.Count == 0)
                {
                    /*
                     * 目录条目指向归档根（名字是 "/" 或 "./" 这种）：落点就是目标根本身，什么都不用建。
                     * 文件条目走到这里说明名字里没有可用的一段 —— 那是真写不出来的东西，拒绝整包
                     * （与"归档里有不安全的条目"同一口径，绝不静默丢一个用户以为解出来的文件）。
                     */
                    if (!entry.IsDirectory)
                    {
                        reason = $"条目名清洗之后没有有效路径：{entry.Name}";
                        return false;
                    }

                    entries[i] = new EmbeddedZipEntry
                    {
                        Name = entry.Name,
                        RelativePath = string.Empty,
                        OutputPath = targetDirectory ?? string.Empty,
                        Size = 0,
                        CompressedSize = entry.CompressedSize,
                        IsDirectory = true,
                        Method = entry.Method,
                        LocalHeaderOffset = entry.LocalHeaderOffset,
                        AesStrength = 0,
                        IsEncrypted = entry.IsEncrypted
                    };

                    continue;
                }

                string relative = string.Join(Path.DirectorySeparatorChar.ToString(), parts);
                string outputPath = string.IsNullOrWhiteSpace(targetDirectory)
                    ? string.Empty
                    : Path.Combine(targetDirectory!, relative);

                if (!string.IsNullOrWhiteSpace(targetDirectory) &&
                    !ArchivePathGuard.IsInsideRoot(targetDirectory, outputPath, out string insideReason))
                {
                    reason = $"{entry.Name} —— {insideReason}";
                    return false;
                }

                entries[i] = new EmbeddedZipEntry
                {
                    Name = entry.Name,
                    RelativePath = relative,
                    OutputPath = outputPath,
                    Size = entry.Size,
                    CompressedSize = entry.CompressedSize,
                    IsDirectory = entry.IsDirectory,
                    Method = entry.Method,
                    LocalHeaderOffset = entry.LocalHeaderOffset,
                    /*
                     * ⚠ 这里是把条目**重建**一遍（要补相对路径与落点），所以每加一个字段都必须在这里跟着抄一次：
                     * 2026-09-25 就漏过 AesStrength（新增字段），后果是"密码明明校验通过了，
                     * 解压时却按未加密路径去读密文" —— 报出来的错是"字节数与清单不符"，与真实原因毫不相干。
                     */
                    AesStrength = entry.AesStrength,
                    IsEncrypted = entry.IsEncrypted
                };
            }

            return true;
        }

        // ================================================================ 写盘

        private static EmbeddedZipExtractResult ExtractCore(
            string sourcePath,
            long offset,
            long end,
            EmbeddedZipProbeResult plan,
            string resolvedPassword,
            string targetRoot,
            BudgetTracker tracker,
            List<string> written,
            List<string> createdDirectories,
            IProgress<int>? progress,
            CancellationToken cancellationToken)
        {
            /*
             * 源文件用 FileShare.Read（而不是探查时的 ReadWrite）：写出去的必须是**一致**的一段字节，
             * 别人正在写这个文件时宁可失败，也不要把"改了一半"的内容当成产物 —— 那会变成一个更难查的"文件损坏"。
             */
            using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.RandomAccess);

            var stopwatch = Stopwatch.StartNew();
            long nextReportAtMs = 0;
            long totalBytes = plan.TotalBytes;
            long writtenBytes = 0;
            int fileCount = 0;

            ReportProgress(progress, 0);

            foreach (EmbeddedZipEntry entry in plan.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (entry.IsDirectory)
                {
                    string directory = string.IsNullOrWhiteSpace(entry.OutputPath)
                        ? Path.Combine(targetRoot, entry.RelativePath)
                        : entry.OutputPath;

                    EnsureDirectory(directory, createdDirectories);
                    continue;
                }

                string finalPath = string.IsNullOrWhiteSpace(entry.OutputPath)
                    ? Path.Combine(targetRoot, entry.RelativePath)
                    : entry.OutputPath;

                // 绝不复核/覆盖已有文件（不变量 3）：同名一律改名落位。
                if (File.Exists(finalPath) || Directory.Exists(finalPath))
                {
                    finalPath = SafePathHelper.AutoRenameFilePath(finalPath);
                }

                if (!ArchivePathGuard.IsInsideRoot(targetRoot, finalPath, out string insideReason))
                {
                    Rollback(written, createdDirectories);

                    return Failure($"条目落点不在目标根之内，已停止：{entry.Name} —— {insideReason}", unsupported: false);
                }

                string? budgetReason = tracker.Record(entry.Size);

                if (!string.IsNullOrWhiteSpace(budgetReason))
                {
                    Rollback(written, createdDirectories);

                    return Failure("超出资源预算：" + budgetReason, unsupported: false);
                }

                EnsureDirectory(Path.GetDirectoryName(finalPath), createdDirectories);

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
                        CopyBufferSize,
                        FileOptions.SequentialScan))
                    {
                        long entryWritten = WriteEntry(
                            source,
                            offset,
                            entry,
                            resolvedPassword,
                            plan.ResolvedPasswordEncoding,
                            target,
                            ref writtenBytes,
                            totalBytes,
                            stopwatch,
                            ref nextReportAtMs,
                            progress,
                            cancellationToken);

                        if (entryWritten != entry.Size)
                        {
                            throw new IOException(
                                $"条目 {entry.Name} 解出的字节数与清单不符：清单 {entry.Size} 字节，实际 {entryWritten} 字节");
                        }
                    }

                    // 先写临时名 → 成功后改名落位。中途失败/取消时只删临时名，不留半截产物。
                    File.Move(tempPath, finalPath);
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
                    Rollback(written, createdDirectories);

                    return Failure($"{entry.Name} 解压失败：{ex.Message}", unsupported: false);
                }
                catch
                {
                    TryDeleteFile(tempPath);
                    throw;
                }

                written.Add(finalPath);
                fileCount++;
            }

            /*
             * 最后一个条目写完也可能已被取消（取消信号正好落在最后一块上）。
             * 不查这一下就会"用户按了取消、界面报解压成功"—— 不变量 6 的同一口径。
             */
            cancellationToken.ThrowIfCancellationRequested();

            ReportProgress(progress, 100);

            int aesStrength = plan.Entries.FirstOrDefault(e => !e.IsDirectory && e.AesStrength != 0)?.AesStrength ?? 0;

            return new EmbeddedZipExtractResult
            {
                Success = true,
                Message = aesStrength == 0
                    ? $"直读解出 {fileCount} 个文件 / {writtenBytes} 字节（区间 {offset}–{end}，没有生成临时副本）"
                    : $"直读解出 {fileCount} 个文件 / {writtenBytes} 字节（区间 {offset}–{end}，没有生成临时副本；" +
                      $"加密包 {ZipAesCrypto.DescribeStrength(aesStrength)}，第 {plan.ResolvedPasswordIndex} 个候选密码按" +
                      $"{ZipAesCrypto.DescribeEncoding(plan.ResolvedPasswordEncoding)}通过校验）",
                FileCount = fileCount,
                WrittenBytes = writtenBytes,
                Entries = plan.Entries,
                ResolvedPasswordIndex = plan.ResolvedPasswordIndex,
                ResolvedPasswordEncoding = plan.ResolvedPasswordEncoding,
                WasEncrypted = aesStrength != 0
            };
        }

        /// <summary>
        /// 写一个条目：stored 原样拷、deflate 解压流、AES 先解密再按真方法处理。返回实际写出的字节数。
        /// </summary>
        private static long WriteEntry(
            FileStream source,
            long offset,
            EmbeddedZipEntry entry,
            string resolvedPassword,
            ZipAesPasswordEncoding passwordEncoding,
            FileStream target,
            ref long writtenBytes,
            long totalBytes,
            Stopwatch stopwatch,
            ref long nextReportAtMs,
            IProgress<int>? progress,
            CancellationToken cancellationToken)
        {
            long entryWritten = 0;

            if (entry.AesStrength != 0)
            {
                return WriteAesEntry(
                    source,
                    offset,
                    entry,
                    resolvedPassword,
                    passwordEncoding,
                    target,
                    ref writtenBytes,
                    totalBytes,
                    stopwatch,
                    ref nextReportAtMs,
                    progress,
                    cancellationToken);
            }

            /*
             * 数据起点 = 本地头偏移 + 30 + 本地头名字长 + 本地头扩展区长。
             * 这里再读一次本地头（而不是把探查处算好的值传下来）：
             * 探查与解压是两次独立的读取，中间源文件可能被换掉（不变量 11 的快照只挡时间戳级的变化），
             * 当场重算才能保证"解的字节"与"认的位置"是同一份文件。
             */
            long localHeader = offset + entry.LocalHeaderOffset;

            byte[] header = ReadAt(source, localHeader, LocalFileHeaderLength);

            if (header.Length != LocalFileHeaderLength || !Matches(header, 0, LocalFileHeaderSignature))
            {
                throw new IOException($"条目 {entry.Name} 的本地文件头不存在或不是 PK\\x03\\x04");
            }

            int localNameLength = BitConverter.ToUInt16(header, 26);
            int localExtraLength = BitConverter.ToUInt16(header, 28);
            long dataOffset = localHeader + LocalFileHeaderLength + localNameLength + localExtraLength;

            source.Seek(dataOffset, SeekOrigin.Begin);

            byte[] buffer = new byte[CopyBufferSize];

            if (entry.Method == 0)
            {
                long remaining = entry.CompressedSize;

                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));

                    if (read <= 0)
                    {
                        throw new IOException($"条目 {entry.Name} 的数据在归档区间里就读完了（文件可能被截断）");
                    }

                    target.Write(buffer, 0, read);

                    remaining -= read;
                    entryWritten += read;
                    writtenBytes += read;

                    ReportIfDue(progress, stopwatch, ref nextReportAtMs, writtenBytes, totalBytes);
                }

                return entryWritten;
            }

            using var bounded = new BoundedReadStream(source, entry.CompressedSize);
            using var inflater = new DeflateStream(bounded, CompressionMode.Decompress, leaveOpen: true);

            int decompressed;

            while ((decompressed = inflater.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                target.Write(buffer, 0, decompressed);

                entryWritten += decompressed;
                writtenBytes += decompressed;

                ReportIfDue(progress, stopwatch, ref nextReportAtMs, writtenBytes, totalBytes);
            }

            return entryWritten;
        }

        /// <summary>
        /// AES 条目的写法：<c>盐(16) + 校验值(2) + 密文 + 认证码(10)</c> ——
        /// 先按 CTR 解密（读到的是明文），再按**真方法**（stored 原样拷 / deflate 解压）落盘，
        /// 最后用认证码确认整段密文没被动过。
        ///
        /// <para>⚠ 认证码这一关不能省：AES 的目的就是"被改过必须发现"。少了它，
        /// 一个被篡改的包会看着成功（不变量 6 的同一口径）。所以顺序是
        /// **先读完所有密文 → 再验认证码 → 才让调用方改名落位**。</para>
        /// </summary>
        private static long WriteAesEntry(
            FileStream source,
            long offset,
            EmbeddedZipEntry entry,
            string resolvedPassword,
            ZipAesPasswordEncoding passwordEncoding,
            FileStream target,
            ref long writtenBytes,
            long totalBytes,
            Stopwatch stopwatch,
            ref long nextReportAtMs,
            IProgress<int>? progress,
            CancellationToken cancellationToken)
        {
            if (!TryGetAesDataLayout(source, offset, entry, out AesDataLayout layout, out string layoutError))
            {
                throw new IOException(layoutError);
            }

            if (!ZipAesCrypto.TryDeriveKeys(
                    resolvedPassword,
                    passwordEncoding,
                    layout.Salt,
                    entry.AesStrength,
                    out ZipAesKeys? keys,
                    out string keyError))
            {
                throw new IOException($"条目 {entry.Name} 的 AES 密钥派生失败：{keyError}");
            }

            long entryWritten = 0;
            byte[] buffer = new byte[CopyBufferSize];

            using var decrypt = new ZipAesDecryptStream(source, layout.CipherStart, layout.CipherLength, keys!);

            if (entry.Method == 0)
            {
                long remaining = layout.CipherLength;

                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int read = decrypt.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));

                    target.Write(buffer, 0, read);

                    remaining -= read;
                    entryWritten += read;
                    writtenBytes += read;

                    ReportIfDue(progress, stopwatch, ref nextReportAtMs, writtenBytes, totalBytes);
                }
            }
            else
            {
                using (var inflater = new DeflateStream(decrypt, CompressionMode.Decompress, leaveOpen: true))
                {
                    int decompressed;

                    while ((decompressed = inflater.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        target.Write(buffer, 0, decompressed);

                        entryWritten += decompressed;
                        writtenBytes += decompressed;

                        ReportIfDue(progress, stopwatch, ref nextReportAtMs, writtenBytes, totalBytes);
                    }
                }

                /*
                 * deflate 流读到结尾**不等于**密文读完（DeflateStream 会在结束标记处停下）。
                 * 认证码是覆盖整段密文的，所以剩下的字节必须也过一遍 —— 否则等于"只认证了一半"。
                 */
                Drain(decrypt, buffer, cancellationToken);
            }

            if (!decrypt.VerifyAuthenticationCode(layout.AuthenticationCode))
            {
                throw new IOException(
                    $"条目 {entry.Name} 的 AES 认证码对不上：密文可能被改过、或源文件在解压过程中变过");
            }

            return entryWritten;
        }

        /// <summary>把流里剩下的字节读完（只为让认证覆盖整段，内容丢弃）。</summary>
        private static void Drain(Stream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (stream.Read(buffer, 0, buffer.Length) <= 0)
                {
                    return;
                }
            }
        }

        // ================================================================ 进度 / 回滚 / 小工具

        private static void ReportProgress(IProgress<int>? progress, int percent)
        {
            if (progress == null)
            {
                return;
            }

            try
            {
                progress.Report(percent < 0 ? 0 : percent > 100 ? 100 : percent);
            }
            catch
            {
                // 接收端炸了不该把解压带下水（进度只是显示）。
            }
        }

        /// <summary>按 250 ms 节流上报：几百 MB 的流式解压里每个缓冲区都报一次没人受得了。</summary>
        private static void ReportIfDue(
            IProgress<int>? progress,
            Stopwatch stopwatch,
            ref long nextReportAtMs,
            long writtenBytes,
            long totalBytes)
        {
            if (progress == null)
            {
                return;
            }

            long elapsed = stopwatch.ElapsedMilliseconds;

            if (elapsed < nextReportAtMs)
            {
                return;
            }

            nextReportAtMs = elapsed + ProgressIntervalMs;

            ReportProgress(progress, totalBytes <= 0 ? 0 : (int)Math.Min(100, writtenBytes * 100 / totalBytes));
        }

        /// <summary>
        /// 把本次写出去的东西全部删掉（深目录优先），只删**我们自己刚创建**的那些：
        /// 已存在的同名文件我们本来就没碰过（改名落位），所以这里不会误删用户的文件。
        /// </summary>
        private static void Rollback(List<string> written, List<string> createdDirectories)
        {
            for (int i = written.Count - 1; i >= 0; i--)
            {
                TryDeleteFile(written[i]);
            }

            written.Clear();

            for (int i = createdDirectories.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (Directory.Exists(createdDirectories[i]) &&
                        !Directory.EnumerateFileSystemEntries(createdDirectories[i]).Any())
                    {
                        Directory.Delete(createdDirectories[i]);
                    }
                }
                catch
                {
                    // 删不掉（被占用 / 非空）不影响结论：产物已经没了，目录留着无害。
                }
            }

            createdDirectories.Clear();
        }

        private static void EnsureDirectory(string? directory, List<string> createdDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory) || Directory.Exists(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            createdDirectories.Add(directory);
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
                // 删不掉也没别的办法；调用方拿到的是失败/取消结论，不会当成成功。
            }
        }

        /// <summary>
        /// 把"源文件路径 + 区间"归一化：<c>ArchiveEnd</c> 缺失（0 / ≤ 起点 / 超出文件）时回落文件末尾 ——
        /// 与 <see cref="EmbeddedArchiveCarver"/> **同一口径**（老调用点与"不知道归档到哪儿结束"的场合都走这条）。
        /// </summary>
        private static bool TryResolveRange(
            string? sourcePath,
            long offset,
            long archiveEnd,
            out string source,
            out long end,
            out string error)
        {
            source = sourcePath ?? string.Empty;
            end = 0;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            {
                error = "源文件不存在，无法直读内嵌归档";
                return false;
            }

            if (offset < 0)
            {
                error = $"内嵌归档偏移不合法：{offset}";
                return false;
            }

            long fileLength;

            try
            {
                fileLength = new FileInfo(source).Length;
            }
            catch (Exception ex)
            {
                error = "读不到源文件大小：" + ex.Message;
                return false;
            }

            if (offset >= fileLength)
            {
                error = $"内嵌归档偏移 {offset} 已超出文件长度 {fileLength}";
                return false;
            }

            end = archiveEnd > offset && archiveEnd <= fileLength ? archiveEnd : fileLength;

            if (end <= offset)
            {
                error = $"内嵌归档区间不合法：[{offset}, {end})";
                return false;
            }

            return true;
        }

        /// <summary>
        /// 从 8 字节小端读一个 64 位无符号数；超过 <see cref="long.MaxValue"/> 或正好是全 1 占位符时返回 -1
        /// （= "这个字段拿不到真实值"），与 <c>EmbeddedArchiveDetector</c> 同一口径。
        /// </summary>
        private static long ReadUInt64AsLong(byte[] buffer, int index)
        {
            if (index < 0 || index + 8 > buffer.Length)
            {
                return -1;
            }

            ulong value = BitConverter.ToUInt64(buffer, index);

            if (value == ulong.MaxValue || value > long.MaxValue)
            {
                return -1;
            }

            return (long)value;
        }

        /// <summary>从指定偏移读一段字节；读不满就返回实际读到的部分（由调用方判失败）。</summary>
        private static byte[] ReadAt(FileStream stream, long offset, int count)
        {
            if (offset < 0 || count <= 0)
            {
                return Array.Empty<byte>();
            }

            stream.Seek(offset, SeekOrigin.Begin);

            byte[] buffer = new byte[count];
            int total = 0;

            while (total < count)
            {
                int read = stream.Read(buffer, total, count - total);

                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            if (total == count)
            {
                return buffer;
            }

            byte[] partial = new byte[total];
            Array.Copy(buffer, partial, total);

            return partial;
        }

        private static bool Matches(byte[] buffer, int index, byte[] signature)
        {
            if (buffer == null || index < 0 || index + signature.Length > buffer.Length)
            {
                return false;
            }

            for (int i = 0; i < signature.Length; i++)
            {
                if (buffer[index + i] != signature[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static int LastIndexOfSignature(byte[] buffer, byte[] signature, int startIndex)
        {
            int start = Math.Min(startIndex, buffer.Length - signature.Length);

            for (int i = start; i >= 0; i--)
            {
                if (Matches(buffer, i, signature))
                {
                    return i;
                }
            }

            return -1;
        }

        private static long SaturatingSum(long left, long right)
        {
            long a = left > 0 ? left : 0L;
            long b = right > 0 ? right : 0L;

            return a > long.MaxValue - b ? long.MaxValue : a + b;
        }

        /// <summary>
        /// 解条目名。通用位标志 bit 11（0x0800）表示"名字是 UTF-8"；没有这一位时先严格试 UTF-8，
        /// 失败再用 GBK（中文 Windows 上打包工具最常用的默认编码），最后才退到 Latin-1（不会抛）。
        ///
        /// <para>不这么做的话，中文名在"没设 UTF-8 位"的包里会变成乱码目录名 ——
        /// 那种产物用户根本认不出来是哪来的。</para>
        /// </summary>
        private static string DecodeName(byte[] buffer, int index, int length, int flags)
        {
            if (length <= 0)
            {
                return string.Empty;
            }

            if ((flags & 0x0800) != 0)
            {
                return Encoding.UTF8.GetString(buffer, index, length);
            }

            try
            {
                return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer, index, length);
            }
            catch
            {
                // 不是合法 UTF-8：按中文 ZIP 的惯例试 GBK。
            }

            CodePageEncodingBootstrap.EnsureRegistered();

            try
            {
                return Encoding.GetEncoding(936).GetString(buffer, index, length);
            }
            catch
            {
                return Encoding.Latin1.GetString(buffer, index, length);
            }
        }

        /// <summary>ZIP64 扩展字段（0x0001）解出来的四个值；不是占位符的字段保持原样。</summary>
        private readonly struct Zip64Extras
        {
            public Zip64Extras(long uncompressedSize, long compressedSize, long localOffset, long diskStart)
            {
                UncompressedSize = uncompressedSize;
                CompressedSize = compressedSize;
                LocalOffset = localOffset;
                DiskStart = diskStart;
            }

            public long UncompressedSize { get; }

            public long CompressedSize { get; }

            public long LocalOffset { get; }

            public long DiskStart { get; }
        }

        /// <summary>
        /// 读 ZIP64 扩展字段：<b>只按"哪些字段是占位符"的顺序</b>出现
        /// （APPNOTE 4.5.3：原始大小 → 压缩后大小 → 本地头偏移 → 盘号）。顺序读错会把偏移读成尺寸，
        /// 那是"看着成功、内容全错"的典型来源，所以一个字段一个字段地按位点推进。
        /// </summary>
        private static Zip64Extras ReadZip64Extras(
            byte[] centralDirectory,
            int extraIndex,
            int extraLength,
            long uncompressedSize,
            long compressedSize,
            long localOffset,
            long diskStart)
        {
            int end = extraIndex + extraLength;
            int cursor = extraIndex;

            while (cursor + 4 <= end)
            {
                int headerId = BitConverter.ToUInt16(centralDirectory, cursor);
                int dataSize = BitConverter.ToUInt16(centralDirectory, cursor + 2);
                int dataIndex = cursor + 4;

                if (dataIndex + dataSize > end)
                {
                    break;
                }

                if (headerId == 0x0001)
                {
                    int at = dataIndex;

                    if (uncompressedSize == uint.MaxValue && at + 8 <= dataIndex + dataSize)
                    {
                        uncompressedSize = ReadUInt64AsLong(centralDirectory, at);
                        at += 8;
                    }

                    if (compressedSize == uint.MaxValue && at + 8 <= dataIndex + dataSize)
                    {
                        compressedSize = ReadUInt64AsLong(centralDirectory, at);
                        at += 8;
                    }

                    if (localOffset == uint.MaxValue && at + 8 <= dataIndex + dataSize)
                    {
                        localOffset = ReadUInt64AsLong(centralDirectory, at);
                        at += 8;
                    }

                    if (diskStart == ushort.MaxValue && at + 4 <= dataIndex + dataSize)
                    {
                        diskStart = BitConverter.ToUInt32(centralDirectory, at);
                    }

                    break;
                }

                cursor = dataIndex + dataSize;
            }

            return new Zip64Extras(uncompressedSize, compressedSize, localOffset, diskStart);
        }

        private static EmbeddedZipProbeResult Unsupported(string reason)
        {
            return new EmbeddedZipProbeResult
            {
                Supported = false,
                Reason = string.IsNullOrWhiteSpace(reason) ? "直读不支持这个内嵌归档" : reason
            };
        }

        private static EmbeddedZipExtractResult Failure(string message, bool unsupported)
        {
            return new EmbeddedZipExtractResult
            {
                Success = false,
                Unsupported = unsupported,
                Message = string.IsNullOrWhiteSpace(message) ? "直读失败" : message
            };
        }

        /// <summary>内置读取器的版本 = 本程序集版本（它随程序一起发布，这就是它的真实版本）。</summary>
        private static string EmbeddedZipStreamExtractorVersion =>
            typeof(EmbeddedZipStreamExtractor).Assembly.GetName().Version?.ToString() ?? string.Empty;

        /// <summary>一个 EOCD 候选的评估结论。</summary>
        private sealed class CandidateEvaluation
        {
            public bool IsCandidate { get; private init; }

            public EmbeddedZipProbeResult? Result { get; private init; }

            /// <summary>不是候选（继续往前找）。</summary>
            public static CandidateEvaluation NotACandidate => new() { IsCandidate = false };

            /// <summary>是候选，但不支持直读（回落信号）。</summary>
            public static CandidateEvaluation Unsupported(string reason) => new()
            {
                IsCandidate = true,
                Result = EmbeddedZipStreamExtractor.Unsupported(reason)
            };

            /// <summary>是候选，但**只差一个密码**（AES 条目在场、这次没给候选密码）。</summary>
            public static CandidateEvaluation NeedsPassword(string reason) => new()
            {
                IsCandidate = true,
                Result = new EmbeddedZipProbeResult
                {
                    Supported = false,
                    RequiresPassword = true,
                    Reason = reason,
                    Message = reason
                }
            };

            /// <summary>是候选，但条目名不安全（硬失败，不回落）。</summary>
            public static CandidateEvaluation PathRejected(
                string message,
                List<EmbeddedZipEntry> entries,
                List<ArchiveEntry> listEntries,
                long totalBytes) => new()
                {
                    IsCandidate = true,
                    Result = new EmbeddedZipProbeResult
                    {
                        Supported = false,
                        PathRejected = true,
                        Message = message,
                        Reason = message,
                        Entries = entries,
                        List = new ArchiveListResult
                        {
                            Success = true,
                            Entries = listEntries,
                            TotalUncompressedSize = totalBytes,
                            FileCount = listEntries.Count(e => !e.IsDirectory),
                            DirectoryCount = listEntries.Count(e => e.IsDirectory)
                        }
                    }
                };

            /// <summary>
            /// 是候选，但加密条目的候选密码一个都没通过校验（硬失败，**不回落** —— 理由见
            /// <see cref="EmbeddedZipProbeResult.PasswordRejected"/>）。
            /// </summary>
            public static CandidateEvaluation PasswordRejected(
                string message,
                List<EmbeddedZipEntry> entries,
                List<ArchiveEntry> listEntries,
                long totalBytes,
                bool isEncrypted) => new()
                {
                    IsCandidate = true,
                    Result = new EmbeddedZipProbeResult
                    {
                        Supported = false,
                        PasswordRejected = true,
                        Message = message,
                        Reason = message,
                        Entries = entries,
                        List = new ArchiveListResult
                        {
                            Success = true,
                            Entries = listEntries,
                            TotalUncompressedSize = totalBytes,
                            FileCount = listEntries.Count(e => !e.IsDirectory),
                            DirectoryCount = listEntries.Count(e => e.IsDirectory),
                            IsEncrypted = isEncrypted
                        }
                    }
                };

            /// <summary>是候选，直读可用。</summary>
            public static CandidateEvaluation Ok(
                List<EmbeddedZipEntry> entries,
                ArchiveListResult list,
                long totalBytes,
                long offset,
                long end,
                int resolvedPasswordIndex,
                ZipAesPasswordEncoding resolvedPasswordEncoding) => new()
                {
                    IsCandidate = true,
                    Result = new EmbeddedZipProbeResult
                    {
                        Supported = true,
                        Entries = entries,
                        List = list,
                        ArchiveLength = end - offset,
                        Offset = offset,
                        End = end,
                        TotalBytes = totalBytes,
                        ResolvedPasswordIndex = resolvedPasswordIndex,
                        ResolvedPasswordEncoding = resolvedPasswordEncoding
                    }
                };
        }

        /// <summary>
        /// 只让读指针前进 <c>length</c> 个字节的包装流：deflate 数据流的"压缩后大小"是中央目录给的，
        /// 不套一层界，DeflateStream 会一路读到下一个条目里去（读到的字节会被当成压缩数据解，得到垃圾或异常）。
        /// </summary>
        private sealed class BoundedReadStream : Stream
        {
            private readonly FileStream _source;
            private long _remaining;

            public BoundedReadStream(FileStream source, long length)
            {
                _source = source;
                _remaining = length > 0 ? length : 0;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_remaining <= 0)
                {
                    return 0;
                }

                int allowed = (int)Math.Min(count, _remaining);
                int read = _source.Read(buffer, offset, allowed);

                _remaining -= read;

                return read;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
