using System;
using System.Collections.Generic;
using System.Linq;

namespace ArchiveFixer.Engines
{
    public enum EngineOperation
    {
        List,
        Test,
        Extract
    }

    /// <summary>
    /// 引擎选择与回退（设计.md §十八/§二十，用户 2026-09-22 的优先级指示）。
    ///
    /// 三条规则必须分开写清楚，因为它们回答的是不同问题：
    /// 1. **谁能干这活**：按格式能力筛（<c>RAR5</c> 只有 UnRAR 与 7-Zip 能干，zip 只有 7-Zip 能干）；
    /// 2. **同等能力时先用谁**：按 <c>AppSettings.EnginePriority</c>（默认 <c>WinRar → SevenZip</c>）
    ///    做 tiebreaker —— 这是用户"先 winrar、再 7z"的落点；
    /// 3. **失败了要不要换一个**：只有"换个引擎可能就行"的错误才换 —— 密码错、缺分卷、没权限、磁盘满，
    ///    换一百个引擎也还是不行，只会让用户多等一遍、把真实原因埋掉。
    ///
    /// ⚠ **不可用的引擎直接跳过**：优先级排第一但没装（或路径失效）时，
    /// 必须安静地落到下一个能干的引擎上，**绝不许因此打不开包**（AGENTS.md §3.1）。
    /// </summary>
    public sealed class EngineSelector
    {
        private readonly EngineRegistry _registry;
        private readonly IReadOnlyList<string>? _priorityOverride;

        public EngineSelector(EngineRegistry registry)
            : this(registry, null)
        {
        }

        /// <summary>
        /// <paramref name="priorityOverride"/> 只给测试与"显式指定一套顺序"的调用方用；
        /// 传 null 时读运行时设置（<see cref="EngineRuntimeSettings.EnginePriority"/>）。
        /// </summary>
        public EngineSelector(EngineRegistry registry, IReadOnlyList<string>? priorityOverride)
        {
            _registry = registry ?? EngineRegistry.CreateDefault();
            _priorityOverride = priorityOverride;
        }

        public static EngineSelector CreateDefault() => new(EngineRegistry.CreateDefault());

        /// <summary>当前生效的优先级顺序（给定顺序 > 运行时设置）。</summary>
        public IReadOnlyList<string> Priority => _priorityOverride ?? EngineRuntimeSettings.EnginePriority;

        /// <summary>按格式挑一个可用引擎；挑不到返回 null（调用方据此报"没有引擎能处理这个格式"，不要抛异常）。</summary>
        public IArchiveEngine? SelectFor(string? detectedFormat, EngineOperation operation)
        {
            return Candidates(detectedFormat, operation).FirstOrDefault();
        }

        /// <summary>
        /// 解释这次会选谁、为什么（设置界面"当前在用哪个引擎"与排障日志共用这一份结论，
        /// 免得界面自己再写一套判断，出现"界面显示一个、跑起来用另一个"）。
        /// </summary>
        public EngineSelection Explain(string? detectedFormat, EngineOperation operation)
        {
            List<IArchiveEngine> candidates = Candidates(detectedFormat, operation).ToList();
            List<IArchiveEngine> skippedUnavailable = _registry.Engines
                .Where(e => !e.IsAvailable && CanHandle(e, detectedFormat, operation))
                .ToList();

            return new EngineSelection
            {
                Format = detectedFormat ?? string.Empty,
                Operation = operation,
                Selected = candidates.FirstOrDefault(),
                Candidates = candidates,
                SkippedUnavailable = skippedUnavailable
            };
        }

        /// <summary>
        /// 备用引擎（排除已经失败的那一个）。
        /// 注意：**调用方必须先问 <see cref="ShouldTryFallback"/>** —— 不要拿"用户取消""密码错误"去换引擎。
        /// </summary>
        public IReadOnlyList<IArchiveEngine> SelectFallbacks(
            string? detectedFormat,
            EngineOperation operation,
            string? failedEngineId)
        {
            return Candidates(detectedFormat, operation)
                .Where(e => !string.Equals(e.Id, failedEngineId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// 这个错误类型值得换引擎吗（设计.md §二十）。
        ///
        /// 会换：引擎不支持 / 不支持某个特性 / 引擎不可用 / 引擎解析器拒绝 / 已知兼容性问题
        /// 不换：密码错、缺分卷、不安全路径、磁盘满、没权限、源文件变了、设备离线、用户取消、超出输出上限
        ///
        /// 另外注意：**损坏归档默认不换** —— 先如实报告"损坏"，用户明确选择后才去试备用引擎
        /// （设计.md §二十 末段）。
        /// </summary>
        public static bool ShouldTryFallback(string? errorType)
        {
            if (string.IsNullOrWhiteSpace(errorType))
            {
                return false;
            }

            switch (errorType.Trim())
            {
                case EngineErrorTypes.UnsupportedFormat:
                case EngineErrorTypes.UnsupportedFeature:
                case EngineErrorTypes.EngineUnavailable:
                case "SevenZipMissing":
                case EngineErrorTypes.ParserRejected:
                case EngineErrorTypes.KnownCompatibilityIssue:
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// 候选顺序：**先按能力筛（谁能干这活），再用优先级做 tiebreaker**。
        ///
        /// 能力筛选是硬门槛（<c>RAR5</c> 不会交给只认 RAR4 的引擎），
        /// 优先级只在"都能干"的引擎之间决定先后；
        /// 同优先级（或都没被列出）时保持注册顺序，结果稳定可复现。
        /// </summary>
        private IEnumerable<IArchiveEngine> Candidates(string? detectedFormat, EngineOperation operation)
        {
            IReadOnlyList<string> priority = Priority;

            return _registry.Engines
                .Where(e => e.IsAvailable)
                .Where(e => CanHandle(e, detectedFormat, operation))
                .Select((engine, index) => new { engine, index })
                .OrderBy(x => EngineIds.PriorityIndexOf(priority, x.engine.Id))
                .ThenBy(x => x.index)
                .Select(x => x.engine);
        }

        private static bool CanHandle(IArchiveEngine engine, string? detectedFormat, EngineOperation operation)
        {
            EngineCapabilities capabilities = engine.Capabilities;

            EngineFormatCapability? format = capabilities.ForFormat(detectedFormat);

            /*
             * 专用引擎（FormatsAreWhitelist）没登记这个格式 = **明确不支持**。
             *
             * 实测教训（验收第 3 条）：不加这一条时，未登记的 zip / 7z / tar 会退回"总体能力位"，
             * 于是全被路由到只认 RAR 的 UnRAR 上，本来能打开的包反而打不开。
             * 通用引擎（7-Zip）不打开这个开关，行为与以前一致：未登记的格式仍让它去试一把。
             */
            if (format == null && capabilities.FormatsAreWhitelist)
            {
                return false;
            }

            switch (operation)
            {
                case EngineOperation.List:
                    return format?.CanList ?? capabilities.CanList;

                case EngineOperation.Test:
                    return format?.CanTest ?? capabilities.CanTest;

                case EngineOperation.Extract:
                    return format?.CanExtract ?? capabilities.CanExtract;

                default:
                    return false;
            }
        }
    }

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
