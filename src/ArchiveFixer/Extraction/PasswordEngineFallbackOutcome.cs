using ArchiveFixer.Engines;
using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction;

/// <summary>
/// 兜底的结果：**命中的候选 + 结论**。⛔ 里面**绝不含明文密码**（只回传值给调用方自己用，
/// 任何写进日志 / 结论的文本都经过描述器或 <see cref="Models.StatusText"/> 常量）。
/// </summary>
public sealed class PasswordEngineFallbackOutcome
{
    /// <summary>兜底**真的跑过**（找到了 WinRAR 且至少试了一个候选）。</summary>
    public bool Attempted { get; init; }

    /// <summary>兜底**跑成**了（WinRAR 退出码 0，产物已经在目标目录里）。</summary>
    public bool Succeeded { get; init; }

    /// <summary>解开的那个候选（明文，**只给调用方**；⛔ 不进日志、不进结论）。</summary>
    public string? HitPassword { get; init; }

    /// <summary>命中的是第几个候选（1 起）。</summary>
    public int HitOrdinal { get; init; }

    /// <summary>试了几个候选。</summary>
    public int AttemptedCandidates { get; init; }

    /// <summary>最后一次调用的退出码（没跑过是 0）。</summary>
    public int LastExitCode { get; init; }

    /// <summary>WinRAR 的版本（报告与日志都要带，不变量 14）。</summary>
    public string EngineVersion { get; init; } = string.Empty;

    /// <summary>
    /// 兜底成功之后，用**实际解压的那个引擎**（UnRAR 命令行）重新列出的清单。
    /// 空 = 列不出来 / 该引擎不可用；调用方这时才回落到主引擎清单。
    /// </summary>
    public ArchiveListResult? ManifestListing { get; init; }

    /// <summary>
    /// 写进**结论**的那一句（<see cref="StatusText"/> 常量拼的；兜底跑过 / 没跑成两种）。
    /// 空串 = 什么都没有可说（判据不成立时）。
    /// </summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>判据成立、但本机没有 WinRAR（结论里要如实说清"没换引擎再试"）。</summary>
    public bool EngineMissing { get; init; }
}
