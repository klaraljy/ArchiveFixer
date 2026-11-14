using System;

namespace ArchiveFixer.Engines
{
    /// <summary>
    /// "这次用的是哪个引擎、哪个版本" —— 结果可追溯的载体（AGENTS.md §6 第 14 条：
    /// 每个最终结果都能追到具体任务与具体引擎，报告里必须带引擎名 + 版本）。
    ///
    /// 为什么不能只写死一句"7-Zip"：同一引擎的不同版本能力不同（设计.md §十八），
    /// 报告里没有版本号，事后就无法回答"当时那个包是被哪个版本处理的"。
    /// </summary>
    public sealed class EngineIdentity
    {
        /// <summary>引擎标识（<c>sevenzip</c>），用于机器判定，不用于显示。</summary>
        public string EngineId { get; init; } = string.Empty;

        /// <summary>显示名（<c>7-Zip 命令行</c>）。</summary>
        public string DisplayName { get; init; } = string.Empty;

        /// <summary>引擎版本（取不到时是 <c>unknown</c>，<b>不编造</b>）。</summary>
        public string Version { get; init; } = string.Empty;

        /// <summary>这个引擎当前是否可用（程序目录里找不找得到）。</summary>
        public bool IsAvailable { get; init; }

        /// <summary>报告 / 日志里的一句话："7-Zip 命令行 26.01.0.0"。</summary>
        public string Describe()
        {
            if (string.IsNullOrWhiteSpace(DisplayName))
            {
                return string.IsNullOrWhiteSpace(EngineId) ? UnknownEngineText : EngineId;
            }

            if (string.IsNullOrWhiteSpace(Version) ||
                string.Equals(Version, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                // 版本取不到时只说名字：报告里写"26.01"是编的，写"unknown"用户看不懂。
                return DisplayName;
            }

            return $"{DisplayName} {Version}";
        }

        public override string ToString() => Describe();

        /// <summary>一个引擎都解析不出来时的落点，**不编造引擎名**。</summary>
        public const string UnknownEngineText = "未检测到可用引擎";
    }
}
