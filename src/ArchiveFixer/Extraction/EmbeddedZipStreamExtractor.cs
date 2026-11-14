using ArchiveFixer.Security;
using System.Collections.Generic;
using System.Threading;

namespace ArchiveFixer.Extraction;

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
/// <para><b>安全</b>：条目名走既有的 <c>ArchivePathGuard</c>（与正常解压**同一处实现**）+
/// <c>FileNameHelper</c> 清洗，落盘前再用 <c>ArchivePathGuard.IsInsideRoot</c> 复核，
/// 越界一律拒绝整包；资源上限走既有的 <c>ResourceBudget</c>；
/// 写文件一律"先写临时名 → 成功后改名落位"，取消/失败把本次写出去的东西全删掉。</para>
///
/// <para>写出时逐条目计算 CRC32 并比对中央目录；不一致按**已确认损坏**收场，不再回落重复读一遍。
/// AES 条目由认证码负责完整性，中央目录的 CRC 字段可能是占位 0，不拿来判损坏。</para>
///
/// <para><b>实现按职责拆在同目录的协作类里</b>（本类只保留公开 API 的薄转发，一行判断都不放）：
/// 区间归一化与一轮编排 → <see cref="EmbeddedZipDirectReader"/>；
/// 扫描（找 EOCD + 结构自洽）→ <see cref="EmbeddedZipCentralDirectoryScanner"/>；
/// 流解析（中央目录 → 条目清单）→ <see cref="EmbeddedZipCentralDirectoryParser"/>；
/// 落点规划 → <see cref="EmbeddedZipEntryPlanner"/>；
/// AES 密码与数据布局 → <see cref="EmbeddedZipAesAccess"/>；
/// 条目解压（stored / deflate / AES）→ <see cref="EmbeddedZipEntryWriter"/>；
/// 写盘会话（临时名 / 改名落位 / 回滚 / 结果）→ <see cref="EmbeddedZipExtractSession"/>；
/// 进度节流 → <see cref="EmbeddedZipProgressReporter"/>；
/// 结果与失败模型 → <see cref="EmbeddedZipCandidateEvaluation"/>、<see cref="EmbeddedZipOutcomes"/>；
/// 字节原语与格式常量 → <see cref="EmbeddedZipByteReader"/>、<see cref="EmbeddedZipStreamFormat"/>。</para>
///
/// <para>本类（及其协作类）不引用 WPF（AGENTS.md §4 分层铁律）。</para>
/// </summary>
public static class EmbeddedZipStreamExtractor
{
    /// <summary>报告里用的读取器名字（不变量 14：结果要能追到"是谁干的活"）。</summary>
    public const string ReaderDisplayName = "内置 ZIP 直读";

    /// <summary>
    /// 只读探查：能不能直读、清单是什么。**一个字节都不写**，也不改源文件。
    ///
    /// <para><paramref name="targetDirectory"/> 给了才会算每个条目的落盘路径并复核"在本任务输出根之内"；
    /// 识别阶段（空间核算）拿不到目标目录，传 null 即可 —— 那时只做条目名层面的预检。</para>
    ///
    /// <para><paramref name="passwords"/> 是候选密码表（按尝试顺序，可含空密码）。加密条目（AES）
    /// 会在这里被**逐个候选 × 两种字节编码**试一遍（校验值 + 认证码两道，见 <c>ZipAesCrypto</c>）：
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
        return EmbeddedZipDirectReader.Probe(
            sourcePath,
            offset,
            archiveEnd,
            targetDirectory,
            passwords,
            cancellationToken,
            confirmResolvedPassword);
    }

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
    /// <param name="budgetOptions">资源上限；null = 默认上限。</param>
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
        return EmbeddedZipDirectReader.Extract(
            sourcePath,
            offset,
            archiveEnd,
            targetDirectory,
            progress,
            cancellationToken,
            budgetOptions,
            passwords);
    }
}
