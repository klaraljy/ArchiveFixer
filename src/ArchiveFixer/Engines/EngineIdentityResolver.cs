namespace ArchiveFixer.Engines
{
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
