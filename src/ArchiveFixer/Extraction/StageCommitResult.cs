using System;
using System.Collections.Generic;

namespace ArchiveFixer.Extraction
{
    /// <summary>定稿搬运的一句话结论（给日志与任务字段用）。</summary>
    internal sealed class StageCommitResult
    {
        public bool Attempted { get; init; }

        /// <summary>搬运过程中被取消（已把搬过去的搬回来，最终目录不留半成品）。</summary>
        public bool Cancelled { get; init; }

        /// <summary>计划里的内容物搬运条数（一条可能是一整个目录）。</summary>
        public int PlannedContentCount { get; init; }

        /// <summary>真的搬过去的内容物条数。</summary>
        public int MovedContentCount { get; init; }

        /// <summary>真的搬过去的其余物条数。</summary>
        public int MovedProcessCount { get; init; }

        /// <summary>
        /// 因为同名冲突被**跳过**的条数（用户选了「跳过」/「跳过全部」）。
        ///
        /// 与"没能搬运"分开记：跳过是用户的选择，不是失败；但"一条内容物都没落位"时
        /// 必须靠它把任务落成「已跳过」而不是「解压成功」（不变量 6 的反面同样成立）。
        /// </summary>
        public int SkippedCount { get; init; }

        /// <summary>其中被跳过的是内容物的条数。</summary>
        public int SkippedContentCount { get; init; }

        /// <summary>因为用户选了「覆盖」而被顶掉的同名条目数。</summary>
        public int OverwrittenCount { get; init; }

        /// <summary>被顶掉的落点（写日志用：**哪些文件被覆盖**必须留痕）。</summary>
        public IReadOnlyList<string> OverwrittenPaths { get; init; } = Array.Empty<string>();

        /// <summary>内容物文件数（来自定稿计划，不是"搬了几条"）。</summary>
        public int ContentFileCount { get; init; }

        /// <summary>其余物目录（定稿计划算出来的那个）。源包搬运要用它当目标根（决策 D-10/D-12）。</summary>
        public string ProcessArtifactDirectory { get; init; } = string.Empty;

        /// <summary>
        /// 内容物实际落地的那一层目录（＝定稿计划的 <c>DestinationDirectory</c>）。
        ///
        /// <para>第 35 条：续解产物必须落进**父任务内容物所在的那一层**，而不是父任务的 OutputPath ——
        /// 共用输出根模式下两者不是一个目录（前者是 <c>BBB\111\2222\</c>，后者是 <c>BBB\111</c>）。</para>
        /// </summary>
        public string ContentDirectory { get; init; } = string.Empty;

        public long ProcessArtifactBytes { get; init; }

        public int RenamedCount { get; init; }

        public int FailedCount { get; init; }

        public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();

        public string Message { get; init; } = string.Empty;

        /// <summary>要带回 UI 线程写日志的行（后台线程不许直接写界面集合）。</summary>
        public List<(string Level, string Message)> LogEntries { get; init; } = new();
    }
}
