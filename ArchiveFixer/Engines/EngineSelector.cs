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
    /// 引擎选择与回退（设计.md §十八/§二十）。
    ///
    /// 两条规则必须分开写清楚，因为它们回答的是不同问题：
    /// 1. **选谁**：按格式能力挑（能解这个格式的、当前可用的）；
    /// 2. **失败了要不要换一个**：只有"换个引擎可能就行"的错误才换 —— 密码错、缺分卷、没权限、磁盘满，
    ///    换一百个引擎也还是不行，只会让用户多等一遍、把真实原因埋掉。
    /// </summary>
    public sealed class EngineSelector
    {
        private readonly EngineRegistry _registry;

        public EngineSelector(EngineRegistry registry)
        {
            _registry = registry ?? EngineRegistry.CreateDefault();
        }

        public static EngineSelector CreateDefault() => new(EngineRegistry.CreateDefault());

        /// <summary>按格式挑一个可用引擎；挑不到返回 null（调用方据此报"没有引擎能处理这个格式"，不要抛异常）。</summary>
        public IArchiveEngine? SelectFor(string? detectedFormat, EngineOperation operation)
        {
            return Candidates(detectedFormat, operation).FirstOrDefault();
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
                case "UnsupportedFormat":
                case "UnsupportedFeature":
                case "EngineUnavailable":
                case "SevenZipMissing":
                case "ParserRejected":
                case "KnownCompatibilityIssue":
                    return true;

                default:
                    return false;
            }
        }

        private IEnumerable<IArchiveEngine> Candidates(string? detectedFormat, EngineOperation operation)
        {
            return _registry.Engines
                .Where(e => e.IsAvailable)
                .Where(e => CanHandle(e, detectedFormat, operation))
                // 能力更全的排前面；同分时保持注册顺序，结果稳定可复现。
                .OrderByDescending(e => e.Capabilities.Formats.Count);
        }

        private static bool CanHandle(IArchiveEngine engine, string? detectedFormat, EngineOperation operation)
        {
            EngineCapabilities capabilities = engine.Capabilities;

            EngineFormatCapability? format = capabilities.ForFormat(detectedFormat);

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
}
