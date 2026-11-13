using System.Collections.Generic;
using ArchiveFixer.Models;
namespace ArchiveFixer.ViewModels
{
    /// <summary>
    /// 一个任务解压的**跨阶段状态**：准备/入仓段算出来的落点与暂存目录，交给后面的候选循环与收尾阶段。
    ///
    /// <para>⛔ 只装"准备段交给后续阶段"的那几个值；候选循环自己的账（lastResult/attempted 等）仍就近局部。</para>
    /// </summary>
    internal sealed class ExtractionRunState
    {
        /// <summary>准备段是否成功（false = 这一单已经停下，原因已写进任务与日志）。</summary>
        internal bool Prepared { get; init; }

        /// <summary>原定输出目录（落点解析结果，未做占位改名之前）。</summary>
        internal string RequestedOutputPath { get; init; } = string.Empty;

        /// <summary>实际最终输出目录（可能因同名冲突占位改名）。</summary>
        internal string OutputPath { get; init; } = string.Empty;

        /// <summary>落点改名说明（空 = 没改名）。</summary>
        internal string OutputRedirectNote { get; init; } = string.Empty;

        /// <summary>落点是不是源包自己的目录（手动档「解压到当前文件夹」）。</summary>
        internal bool LandsInSourceDirectory { get; init; }

        /// <summary>落点是否与其他包共用同一目标目录（每包一个目录档下恒 false）。</summary>
        internal bool SharedOutputRoot { get; init; }

        /// <summary>暂存目录（引擎写盘处；定稿时才搬进 OutputPath）。</summary>
        internal string StageDirectory { get; init; } = string.Empty;

        /// <summary>引擎输出目录（= StageDirectory，局部变量名沿用旧代码）。</summary>
        internal string EngineOutputPath { get; init; } = string.Empty;

        /// <summary>交给引擎的那一份入口路径（⛔ 不是 task.CurrentPath）。</summary>
        internal string EngineArchivePath { get; init; } = string.Empty;

        /* ===== 候选循环交给失败结论收尾的账（只在本阶段边界上写一次）===== */

        internal bool ExtractSuccess { get; set; }

        internal bool HasWrongPassword { get; set; }

        internal ArchiveFixer.Engines.ArchiveOperationResult? AmbiguousResult { get; set; }

        internal bool PasswordProvenDataFailed { get; set; }

        internal bool PasswordIrrelevant { get; set; }

        internal bool StoppedByStopRequest { get; set; }

        internal ArchiveFixer.Engines.ArchiveOperationResult? LastResult { get; set; }

        internal int AttemptedCandidates { get; set; }

        internal int MaxPasswordAttempts { get; set; }

        internal bool CandidatesTruncated { get; set; }

        internal string PasswordConclusionNote { get; set; } = string.Empty;

        internal bool PreflightSaidEncryptedHeaders { get; set; }

        internal string? EncryptedHeadersMessage { get; set; }





        internal int AttemptLimit { get; set; }

        internal List<PasswordItem> Candidates { get; set; } = new();

        internal ArchiveFixer.Engines.ArchiveListResult? PreflightList { get; set; }

        internal bool AnyCandidateWroteBytes { get; set; }

        internal string SelectedPassword { get; set; } = string.Empty;

        internal IReadOnlyList<(string Path, long Size)>? LastLayerManifestEntries { get; set; }

        internal ArchiveFixer.Extraction.OutputVerificationResult? LastVerification { get; set; }

        internal string? PasswordIrrelevantReason { get; set; }

        internal (int Files, long Bytes) ProvenProducts { get; set; }

        internal bool SkippedOnlyEmptyPasswordBecauseEncrypted { get; set; }

        internal bool PasswordConfirmed { get; set; }

        internal ArchiveFixer.Engines.ArchiveOperationResult? AmbiguousTestResult { get; set; }
    }
}

