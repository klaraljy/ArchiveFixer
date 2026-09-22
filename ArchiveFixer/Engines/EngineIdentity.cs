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

    /// <summary>
    /// 把"当前该用哪个引擎"翻成 <see cref="EngineIdentity"/>。
    ///
    /// 唯一来源是 <see cref="EngineRegistry"/>（AGENTS.md §3.1：引擎选择只在注册表/选择器里做），
    /// 报告层不许自己 new 一个 <c>SevenZipEngine</c>、更不许自己拼 7z 路径。
    /// </summary>
    public static class EngineIdentityResolver
    {
        /// <summary>
        /// 按当前注册表解析"通用引擎"（能处理格式最多的那个可用引擎）。
        ///
        /// ⚠ 它**不是**"优先级第一名"：优先级只在**具体格式**上决定分派
        /// （RAR→UnRAR、zip→7-Zip）。这里回答的是"不知道格式时该报谁"，
        /// 所以取通用引擎，免得把 RAR 专用引擎写到 zip 任务的报告里。
        /// </summary>
        public static EngineIdentity ResolveDefault()
        {
            try
            {
                return FromEngine(EngineRegistry.CreateDefault().Default);
            }
            catch
            {
                // 报告层不该因为"引擎信息取不到"整个失败：退回"未检测到可用引擎"。
                return Unavailable;
            }
        }

        /// <summary>
        /// 按格式挑一个能处理它的引擎（**能力优先 + 优先级 tiebreaker**，与真正执行时同一份选择逻辑）；
        /// 挑不到时退回通用引擎。
        /// </summary>
        public static EngineIdentity ResolveFor(string? detectedFormat)
        {
            try
            {
                IArchiveEngine? engine = EngineSelector
                    .CreateDefault()
                    .SelectFor(detectedFormat, EngineOperation.Extract);

                return engine == null ? ResolveDefault() : FromEngine(engine);
            }
            catch
            {
                return Unavailable;
            }
        }

        /// <summary>
        /// 把一个引擎实例翻成身份（给"结果里已经带了引擎"的路径用：不猜、不查表，直接用事实）。
        /// </summary>
        public static EngineIdentity From(IArchiveEngine? engine)
        {
            return FromEngine(engine);
        }

        /// <summary>解析不出引擎时的常量实例。</summary>
        public static EngineIdentity Unavailable { get; } = new EngineIdentity
        {
            EngineId = string.Empty,
            DisplayName = EngineIdentity.UnknownEngineText,
            Version = string.Empty,
            IsAvailable = false
        };

        private static EngineIdentity FromEngine(IArchiveEngine? engine)
        {
            if (engine == null)
            {
                return Unavailable;
            }

            return new EngineIdentity
            {
                EngineId = engine.Id ?? string.Empty,
                DisplayName = engine.DisplayName ?? string.Empty,
                Version = engine.Version ?? string.Empty,
                IsAvailable = engine.IsAvailable
            };
        }
    }
}
