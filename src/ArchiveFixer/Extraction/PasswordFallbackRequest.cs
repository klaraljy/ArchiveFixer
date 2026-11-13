using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.WinRar;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 一次兜底的输入（两个挂点各建一份，执行体只有这一处）。
    /// </summary>
    public sealed class PasswordFallbackRequest
    {
        /// <summary>要交给 WinRAR 的那一份归档（与主引擎拿到的是**同一份**：可能是抠出来的副本）。</summary>
        public string ArchivePath { get; init; } = string.Empty;

        /// <summary>**同一批候选**：候选循环刚才试过的那些，保序（⛔ 不重排、⛔ 不扩表）。</summary>
        public IReadOnlyList<string> Candidates { get; init; } = Array.Empty<string>();

        /// <summary>产物落点 = 这一层的暂存（产物）目录（与主引擎同一个落点，产物因此天然接得上管线）。</summary>
        public string TargetDirectory { get; init; } = string.Empty;

        /// <summary>日志行首的标签（单层路 = 任务名，递归路 = 「第 N 层：包名」）。</summary>
        public string Label { get; init; } = string.Empty;

        /// <summary>刚才报密码错的那个引擎的显示名（结论里必须点名是谁报的，⛔ 不许含糊）。</summary>
        public string FailedEngineName { get; init; } = string.Empty;

        /// <summary>主引擎报的结构化错误类型（<c>WrongPassword</c> / <c>PasswordOrCorrupted</c>）。</summary>
        public string? FailedErrorType { get; init; }

        /// <summary>
        /// 每次候选开工前清空这一层的产物目录（清法由调用方给 —— 单层路是 <c>DiscardStageProductsAsync</c>、
        /// 递归路是 <c>TryResetLayerOutputDirectory</c>，⛔ 本类绝不自己再写一份"怎么清"）。
        /// </summary>
        public Func<CancellationToken, Task>? ResetTarget { get; init; }

        /// <summary>
        /// 候选来源描述器（<c>候选值, 第几项</c> → 一句脱敏说明）—— **必须**是既有那一个
        /// （<c>PasswordService.BuildTryPasswordLogText</c>），两处的措辞才会逐字一致（§9.5）。
        /// </summary>
        public Func<string, int, string>? DescribeCandidate { get; init; }

        /// <summary>日志出口（level, message）；可空 = 不写日志（测试用）。</summary>
        public Action<string, string>? Log { get; init; }

        /// <summary>主引擎（只用于溯源：兜底真跑成时告诉 <see cref="EngineRouter"/>「这一份是 WinRAR 解的」）。</summary>
        public IArchiveEngine? MainEngine { get; init; }

        /// <summary>取消令牌。</summary>
        public CancellationToken CancellationToken { get; init; }

        /// <summary>测试注入用（不给 = 走本机 <see cref="ToolLocator.Default"/>）。</summary>
        internal WinRarProcessRunner? Runner { get; init; }
    }
}
