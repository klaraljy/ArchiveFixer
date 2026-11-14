using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 直读解压的结果。失败时**不留半成品**（临时名文件与已落位的文件都会被删掉）。
/// </summary>
public sealed class EmbeddedZipExtractResult
{
    public bool Success { get; init; }

    /// <summary>失败原因是"不支持"（调用方可以回落抠取）还是"真的失败了"。</summary>
    public bool Unsupported { get; init; }

    /// <summary>
    /// 数据层已确认损坏（目前是 ZIP 条目 CRC32 对不上）。
    /// 调用方**不许**回落去再读一遍：那只会白费一次 10G I/O，结论仍然是损坏。
    /// </summary>
    public bool Corrupted { get; init; }

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

    /// <summary>命名类警告（保留名 / 结尾点或空格 / 备用数据流）；没有就是空串。</summary>
    public string NamingSummary { get; init; } = string.Empty;
}
