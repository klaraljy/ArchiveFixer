using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Engines
{
    /// <summary>一次引擎选择的完整解释（界面与日志共用；不产生副作用）。</summary>
    public sealed class EngineSelection
    {
        public string Format { get; init; } = string.Empty;

        public EngineOperation Operation { get; init; }

        /// <summary>最终选中谁；null = 没有可用引擎能处理这个格式。</summary>
        public IArchiveEngine? Selected { get; init; }

        /// <summary>有能力且可用的候选（按优先级排好）。</summary>
        public IReadOnlyList<IArchiveEngine> Candidates { get; init; } = new List<IArchiveEngine>();

        /// <summary>有能力但**当前不可用**、因而被跳过的引擎（"排第一但没装"要在界面上说清楚）。</summary>
        public IReadOnlyList<IArchiveEngine> SkippedUnavailable { get; init; } = new List<IArchiveEngine>();

        public string Describe()
        {
            if (Selected == null)
            {
                return string.IsNullOrWhiteSpace(Format)
                    ? "没有可用引擎"
                    : $"没有可用引擎能处理 {Format}";
            }

            string text = $"{Selected.DisplayName} {Selected.Version}".Trim();

            if (SkippedUnavailable.Count > 0)
            {
                text += $"（已跳过不可用：{string.Join('、', SkippedUnavailable.Select(e => e.DisplayName))}）";
            }

            return text;
        }
    }
}
