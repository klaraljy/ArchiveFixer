using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 内嵌 ZIP 直读的**编排**：把"源文件 + 区间"归一化、只读探查、资源预算、密码定位、
/// 写盘会话串成一轮；<see cref="EmbeddedZipStreamExtractor"/> 的公开 API 只薄转发到这里。
///
/// <para><b>不支持的时机</b>：一切"不支持"都发生在写任何字节之前（解析、方法/加密位/ZIP64/路径预检、
/// 资源预算全部前置），所以调用方看到 <c>Unsupported</c> 时可以放心地原地回落到"抠取 + 7z"。</para>
///
/// <para><b>失败语义</b>：取消抛 <see cref="OperationCanceledException"/>（写出去的东西先删干净）；
/// 数据层失败返回 <c>Success = false</c>（同样不留半成品）；回落信号是 <c>Unsupported = true</c>。
/// 具体装配见 <see cref="EmbeddedZipOutcomes"/>。</para>
///
/// <para>⛔ 本类不认 ZIP 字节布局、不做解密/解压：扫描在 <see cref="EmbeddedZipCentralDirectoryScanner"/>，
/// 流解析在 <see cref="EmbeddedZipCentralDirectoryParser"/>，条目解压在
/// <see cref="EmbeddedZipEntryWriter"/>，写盘与回滚在 <see cref="EmbeddedZipExtractSession"/>。</para>
/// </summary>
internal static class EmbeddedZipDirectReader
{
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
    internal static EmbeddedZipProbeResult Probe(
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
                return EmbeddedZipOutcomes.Unsupported(rangeError);
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

            return EmbeddedZipCentralDirectoryScanner.Parse(
                stream,
                offset,
                end,
                targetDirectory,
                passwords,
                cancellationToken,
                confirmResolvedPassword);
        }
        catch (Exception ex)
        {
            return EmbeddedZipOutcomes.Unsupported("直读探查失败：" + ex.Message);
        }
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
    /// <param name="budgetOptions">资源上限；null = <see cref="ResourceBudgetOptions.Default"/>。</param>
    /// <param name="passwords">候选密码表（加密的 AES 内嵌包要用；顺序 = 尝试顺序）。</param>
    internal static EmbeddedZipExtractResult Extract(
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
            return EmbeddedZipOutcomes.Failure(rangeError, unsupported: false);
        }

        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            return EmbeddedZipOutcomes.Failure("没有指定直读解压的落点目录", unsupported: false);
        }

        string targetRoot = SafePathHelper.GetFullPathSafe(targetDirectory);

        if (!SafePathHelper.EnsureDirectoryExists(targetRoot))
        {
            return EmbeddedZipOutcomes.Failure($"无法创建直读解压的落点目录：{targetDirectory}", unsupported: false);
        }

        /*
         * confirmResolvedPassword: false —— 命中的候选不再单独读一遍密文去验认证码：
         * 紧接着的写盘会话会把每个条目的认证码都验一遍（那是必须做的一步），
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
                Entries = plan.Entries,
                NamingSummary = plan.NamingSummary
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
            return EmbeddedZipOutcomes.Failure("资源预算未通过：" + budget.Reason, unsupported: false);
        }

        /*
         * 运行时预算（同一个实现）：解析出来的清单可能被改过的源文件"骗"到，
         * 所以真正写盘时再累计一遍。返回非 null 就停下并回滚 —— 与引擎路径同一口径。
         */
        BudgetTracker tracker = new ResourceBudget(budgetOptions).CreateTracker();

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

        return new EmbeddedZipExtractSession(
            source,
            offset,
            end,
            plan,
            resolvedPassword,
            targetRoot,
            tracker,
            progress,
            cancellationToken).Run();
    }

    /// <summary>
    /// 把"源文件路径 + 区间"归一化：<c>ArchiveEnd</c> 缺失（0 / ≤ 起点 / 超出文件）时回落文件末尾 ——
    /// 与 <c>EmbeddedArchiveCarver</c> **同一口径**（老调用点与"不知道归档到哪儿结束"的场合都走这条）。
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
}
