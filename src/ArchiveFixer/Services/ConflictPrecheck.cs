using System.Collections.Generic;

namespace ArchiveFixer.Services
{
    /// <summary>定稿前的冲突预检结论（Ask 档要在动最终目录之前先问完）。</summary>
    internal sealed class ConflictPrecheck
    {
        /// <summary>计划里"落点已经被占着"的条目总数。</summary>
        public int TotalCount { get; set; }

        /// <summary>其中属于**内容物**的条数（其余物撞名不算用户的内容被顶掉）。</summary>
        public int ContentCount { get; set; }

        /// <summary>给用户看的样例（最多 <see cref="ConflictStep.MaxConflictSamplesInDialog"/> 条）。</summary>
        public List<(string Path, ConflictTargetKind Kind)> Samples { get; } = new();
    }
}
