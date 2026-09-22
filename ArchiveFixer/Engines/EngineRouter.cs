using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Engines
{
    /// <summary>
    /// 「按格式与优先级分派」的引擎门面 —— 多引擎真正接进流水线的那一环。
    ///
    /// <para><b>它解决什么</b>：流水线要的从来不是"某一个引擎"，而是"这个包该谁解"。
    /// 以前 <c>MainViewModel</c> 直接 <c>new SevenZipEngine()</c> 塞给
    /// <c>ExtractionCoordinator</c>，于是 <c>AppSettings.EnginePriority</c> 与
    /// <c>UnRarEngine</c> 在真实流程里**完全没有生效**：不管优先级怎么排、UnRAR 装没装，
    /// 干活的永远是 7-Zip。</para>
    ///
    /// <para><b>规则一条都不在这里重写</b>（AGENTS.md §3.1）：按能力筛、优先级只做 tiebreaker、
    /// 不可用的引擎直接跳过、哪些错误才值得换引擎，全部来自 <see cref="EngineRegistry"/> /
    /// <see cref="EngineSelector"/>。这个类只做三件事：<b>问格式</b> → <b>转发</b> → <b>记账</b>。</para>
    ///
    /// <para><b>它顺带回答一个以前答不出的问题</b>：这个包**实际**是哪个引擎解的。
    /// 每个引擎都会在自己的结果上盖戳（<see cref="ArchiveOperationResult.EngineId"/> /
    /// <see cref="ArchiveOperationResult.EngineVersion"/>），门面按归档路径记下来，
    /// 报告里逐任务那一行读的就是它（不变量 14）—— 而不是"报告生成那一刻注册表里排第一的是谁"。</para>
    ///
    /// <para><b>落点（2026-09-22 从 <c>ViewModels/MainViewModel.cs</c> 搬来）</b>：它本来就属于
    /// <c>Engines/</c> —— 上一批因为"不许新增文件"的授权限制临时寄住在 ViewModel 文件里。
    /// 搬家是**纯搬家**：一行行为都没改，调用点仍只有 <c>MainViewModel</c> 一处。
    /// ⚠ 分层铁律（AGENTS.md §4）：<c>Engines/</c> 不得引用 WPF，本文件里<b>没有</b>
    /// <c>System.Windows.*</c>，也不许加。</para>
    /// </summary>
    internal sealed class EngineRouter : IArchiveEngine
    {
        /// <summary>格式识别结果缓存的条数上限：超了整表清空（缓存只为省掉重复的头部读取，丢掉不心疼）。</summary>
        private const int FormatCacheLimit = 512;

        /// <summary>"这个包用了谁"记账表的条数上限（同上）。</summary>
        private const int UsedEngineLimit = 2048;

        private readonly EngineRegistry _registry;
        private readonly EngineSelector _selector;
        private readonly ArchiveDetectService _detector;

        /// <summary>归档路径 → 实际执行它的引擎身份（每次调用后覆盖写，成功的那一次自然留在里面）。</summary>
        private readonly ConcurrentDictionary<string, EngineIdentity> _usedByPath =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 归档**基名** → 引擎身份。
        ///
        /// 为什么除路径之外还要按基名记一份：双面文件（内嵌归档）是先按偏移抠到
        /// <c>&lt;工作区&gt;\&lt;任务目录&gt;\&lt;源包基名&gt;.zip</c> 再交给引擎的，
        /// 引擎看到的是那个临时文件，而报告要标的是**源包那个任务** ——
        /// 两者的路径不同、扩展名不同，只有基名是同一个（抠包代码用的就是
        /// <see cref="FileNameHelper.GetArchiveBaseName"/>）。
        /// </summary>
        private readonly ConcurrentDictionary<string, EngineIdentity> _usedByBaseName =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>归档路径 → 格式识别结果（含文件大小与最后写入时间：源文件变了就重算）。</summary>
        private readonly ConcurrentDictionary<string, FormatCacheEntry> _formatCache =
            new(StringComparer.OrdinalIgnoreCase);

        internal EngineRouter(EngineRegistry registry, ArchiveDetectService? detectService = null)
        {
            _registry = registry ?? EngineRegistry.CreateDefault();
            _selector = new EngineSelector(_registry);
            _detector = detectService ?? new ArchiveDetectService();
        }

        /// <summary>
        /// 门面自己的身份：报**通用引擎**（能处理格式最多的那个可用引擎，通常是 7-Zip）。
        ///
        /// 为什么不编一个"multi-engine"之类的 id：<see cref="Id"/> / <see cref="DisplayName"/> /
        /// <see cref="Version"/> 会被写进工作区 report.json 与界面提示，是**溯源字段**（不变量 14）——
        /// 编一个查不到的引擎 id 比"报通用引擎"更糟。某个包实际用了谁，
        /// 由 <see cref="ResolveIdentityFor"/> 逐任务给出，那才是真正的溯源。
        /// </summary>
        public string Id => General?.Id ?? EngineIds.SevenZip;

        public string DisplayName => General?.DisplayName ?? "未检测到可用引擎";

        public string Version => General?.Version ?? "unknown";

        /// <summary>只要有一个引擎可用，流水线就能跑（格式对不上的包由选择器报"没有引擎能处理"）。</summary>
        public bool IsAvailable => _registry.Engines.Any(e => e.IsAvailable);

        /// <summary>门面的能力位取通用引擎的（它代表"这个程序能干什么"）。</summary>
        public EngineCapabilities Capabilities =>
            General?.Capabilities ?? new EngineCapabilities();

        private IArchiveEngine? General => _registry.Default;

        /// <summary>
        /// 探测：转发给通用引擎，并在它答"格式未知"时补上 Detection 层的魔数结论。
        ///
        /// 两个引擎的 <c>ProbeAsync</c> 都如实回答"没有廉价探测能力"（见各自实现），
        /// 真正便宜的识别一直由 Detection 层负责 —— 门面顺手把这份结论带上，
        /// 免得调用方为了知道格式再去问一遍。
        /// </summary>
        public async Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                return new ArchiveProbeResult { IsArchive = false, Format = "Unknown", Confidence = "Unknown", Message = "归档请求为空" };
            }

            IArchiveEngine? engine = General;

            ArchiveProbeResult result = engine == null
                ? new ArchiveProbeResult { IsArchive = false, Format = "Unknown", Confidence = "Unknown", Message = "没有可用引擎" }
                : await engine.ProbeAsync(request, cancellationToken).ConfigureAwait(false);

            if (result != null && string.Equals(result.Format, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                string? format = await DetectFormatAsync(request.ArchivePath, cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(format))
                {
                    return new ArchiveProbeResult
                    {
                        IsArchive = result.IsArchive,
                        Format = format!,
                        SuggestedExtension = result.SuggestedExtension,
                        Confidence = "Medium",
                        IsEncrypted = result.IsEncrypted,
                        IsMultiVolume = result.IsMultiVolume,
                        Message = result.Message
                    };
                }
            }

            return result ?? new ArchiveProbeResult { IsArchive = false, Format = "Unknown", Confidence = "Unknown" };
        }

        public async Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                return ArchiveListResult.Failure(EngineErrorTypes.UnknownError, "归档请求为空", Id, Version);
            }

            string? format = await DetectFormatAsync(request.ArchivePath, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<IArchiveEngine> candidates = Candidates(format, EngineOperation.List);

            if (candidates.Count == 0)
            {
                return ArchiveListResult.Failure(
                    EngineErrorTypes.EngineUnavailable,
                    DescribeNoEngine(format),
                    Id,
                    Version);
            }

            ArchiveListResult? last = null;

            foreach (IArchiveEngine engine in candidates)
            {
                last = await engine.ListAsync(request, cancellationToken).ConfigureAwait(false);

                RememberUsed(request.ArchivePath, IdentityOf(engine, last?.EngineId, last?.EngineVersion, null));

                if (last != null && last.Success)
                {
                    return last;
                }

                /*
                 * 换不换引擎由既有判据说了算（设计.md §二十）：
                 * 只有"换个引擎可能就行"的错误才继续试下一个；密码错、缺分卷、没权限、磁盘满
                 * 换一百个引擎也一样，继续试只会让用户多等一遍、把真实原因埋掉。
                 */
                if (!EngineSelector.ShouldTryFallback(last?.ErrorType))
                {
                    break;
                }
            }

            return last ?? ArchiveListResult.Failure(
                EngineErrorTypes.EngineUnavailable,
                DescribeNoEngine(format),
                Id,
                Version);
        }

        public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
        {
            return RunOperationAsync(
                request,
                EngineOperation.Test,
                (engine, token) => engine.TestAsync(request, token),
                cancellationToken);
        }

        public Task<ArchiveOperationResult> ExtractAsync(
            ArchiveRequest request,
            ExtractOptions options,
            CancellationToken cancellationToken = default)
        {
            return RunOperationAsync(
                request,
                EngineOperation.Extract,
                (engine, token) => engine.ExtractAsync(request, options, token),
                cancellationToken);
        }

        /// <summary>
        /// 这个归档**实际**用的引擎；问不到返回 null（调用方退回通用引擎身份）。
        ///
        /// 先按完整路径找，再按基名找（抠出来的内嵌归档与源包只有基名相同，见
        /// <see cref="_usedByBaseName"/> 的说明）。
        /// </summary>
        internal EngineIdentity? ResolveIdentityFor(string? archivePath)
        {
            if (string.IsNullOrWhiteSpace(archivePath))
            {
                return null;
            }

            if (_usedByPath.TryGetValue(NormalizePath(archivePath!), out EngineIdentity? exact))
            {
                return exact;
            }

            string baseName = FileNameHelper.GetArchiveBaseName(archivePath!);

            return !string.IsNullOrWhiteSpace(baseName) && _usedByBaseName.TryGetValue(baseName, out EngineIdentity? byName)
                ? byName
                : null;
        }

        /// <summary>
        /// "这次会按什么顺序分派"的一句话（启动日志与排障用；与真正执行时同一份选择逻辑）。
        /// </summary>
        internal string DescribeDispatch()
        {
            EngineSelection rar = _selector.Explain("RAR5", EngineOperation.Extract);
            EngineSelection generic = _selector.Explain("ZIP", EngineOperation.Extract);

            string text = $"引擎分派（按格式与优先级，优先级：{string.Join(" → ", _selector.Priority)}）：" +
                          $"RAR → {rar.Describe()}；zip / 7z 等其它格式 → {generic.Describe()}";

            if (rar.SkippedUnavailable.Count > 0 || generic.SkippedUnavailable.Count > 0)
            {
                text += "。排在前面的引擎没检测到时会被自动跳过，不会因此打不开包";
            }

            return text + "。";
        }

        private async Task<ArchiveOperationResult> RunOperationAsync(
            ArchiveRequest request,
            EngineOperation operation,
            Func<IArchiveEngine, CancellationToken, Task<ArchiveOperationResult>> call,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                return ArchiveOperationResult.CreateFailure(
                    -1, string.Empty, string.Empty, StatusText.ExtractFailed, "归档请求为空", EngineErrorTypes.UnknownError, TimeSpan.Zero);
            }

            string? format = await DetectFormatAsync(request.ArchivePath, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<IArchiveEngine> candidates = Candidates(format, operation);

            if (candidates.Count == 0)
            {
                string message = DescribeNoEngine(format);

                return ArchiveOperationResult.CreateFailure(
                    -1,
                    string.Empty,
                    string.Empty,
                    IsAvailable ? StatusText.ExtractFailed : StatusText.SevenZipMissing,
                    message,
                    EngineErrorTypes.EngineUnavailable,
                    TimeSpan.Zero);
            }

            ArchiveOperationResult? last = null;

            foreach (IArchiveEngine engine in candidates)
            {
                last = await call(engine, cancellationToken).ConfigureAwait(false);

                RememberUsed(request.ArchivePath, IdentityOf(engine, last));

                if (last != null && last.Success)
                {
                    return last;
                }

                // 同 ListAsync：换引擎的判据来自 EngineSelector，不在这里另写一套。
                if (!EngineSelector.ShouldTryFallback(last?.DetectedErrorType))
                {
                    break;
                }
            }

            return last ?? ArchiveOperationResult.CreateFailure(
                -1,
                string.Empty,
                string.Empty,
                StatusText.ExtractFailed,
                DescribeNoEngine(format),
                EngineErrorTypes.EngineUnavailable,
                TimeSpan.Zero);
        }

        /// <summary>候选引擎（能力筛 + 优先级 + 可用性），顺序即尝试顺序。</summary>
        private IReadOnlyList<IArchiveEngine> Candidates(string? format, EngineOperation operation)
        {
            return _selector.SelectFallbacks(format, operation, failedEngineId: null);
        }

        /// <summary>
        /// 这个文件到底是什么格式 —— 分派的**唯一前提**。
        ///
        /// 用 Detection 层的魔数识别，不猜扩展名（这个程序存在的意义就是"后缀被改坏"：
        /// 按后缀分派会把一个 RAR 交给 7-Zip，把 zip 交给 UnRAR）。
        /// 识别不出来 / 出任何岔子都返回 null —— 选择器会把"没有格式"落到通用引擎，
        /// **绝不允许**因为识别失败把包卡住。
        /// </summary>
        private async Task<string?> DetectFormatAsync(string? archivePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            {
                return null;
            }

            string key = NormalizePath(archivePath!);

            try
            {
                var info = new FileInfo(archivePath!);

                if (_formatCache.TryGetValue(key, out FormatCacheEntry? cached) &&
                    cached != null &&
                    cached.Length == info.Length &&
                    cached.LastWriteUtc == info.LastWriteTimeUtc)
                {
                    return cached.Format;
                }

                if (_formatCache.Count > FormatCacheLimit)
                {
                    _formatCache.Clear();
                }

                Models.DetectResult detected = await _detector.DetectAsync(archivePath!, cancellationToken).ConfigureAwait(false);

                string? format = detected != null && detected.IsKnownFormat && !string.IsNullOrWhiteSpace(detected.Format)
                    ? detected.Format
                    : null;

                _formatCache[key] = new FormatCacheEntry(info.Length, info.LastWriteTimeUtc, format);

                return format;
            }
            catch
            {
                // 识别失败不是"这个包打不开"的理由：交给通用引擎去试（它本来就会给出真实结论）。
                return null;
            }
        }

        /// <summary>
        /// 记下"这个归档这一次是谁干的"。每次尝试都写，于是：
        /// 成功时留下的是成功那个引擎，全失败时留下的是**最后尝试**的那个 ——
        /// 正是任务上那条错误信息来自的引擎。
        /// </summary>
        private void RememberUsed(string? archivePath, EngineIdentity identity)
        {
            if (string.IsNullOrWhiteSpace(archivePath) || identity == null)
            {
                return;
            }

            if (_usedByPath.Count > UsedEngineLimit)
            {
                _usedByPath.Clear();
                _usedByBaseName.Clear();
            }

            _usedByPath[NormalizePath(archivePath!)] = identity;

            string baseName = FileNameHelper.GetArchiveBaseName(archivePath!);

            if (!string.IsNullOrWhiteSpace(baseName))
            {
                _usedByBaseName[baseName] = identity;
            }
        }

        /// <summary>引擎身份：结果上盖了戳就以戳为准，缺哪一项才回落到引擎实例的当前值。</summary>
        private static EngineIdentity IdentityOf(
            IArchiveEngine engine,
            string? resultEngineId,
            string? resultVersion,
            string? resultDisplayName)
        {
            return new EngineIdentity
            {
                EngineId = string.IsNullOrWhiteSpace(resultEngineId) ? engine.Id : resultEngineId!,
                DisplayName = string.IsNullOrWhiteSpace(resultDisplayName) ? engine.DisplayName : resultDisplayName!,
                Version = string.IsNullOrWhiteSpace(resultVersion) ? engine.Version : resultVersion!,
                IsAvailable = engine.IsAvailable
            };
        }

        private static EngineIdentity IdentityOf(IArchiveEngine engine, ArchiveOperationResult? result)
        {
            return result != null && !string.IsNullOrWhiteSpace(result.EngineId)
                ? IdentityOf(engine, result.EngineId, result.EngineVersion, result.EngineDisplayName)
                : IdentityOf(engine, null, null, null);
        }

        /// <summary>"没有引擎能处理这个格式"的一句话（如实说清是格式的问题还是引擎没装）。</summary>
        private string DescribeNoEngine(string? format)
        {
            string what = string.IsNullOrWhiteSpace(format) ? "这个文件（格式未识别出来）" : format!;

            return IsAvailable
                ? $"没有可用引擎能处理 {what}：现有引擎都不声明支持它"
                : $"没有可用引擎能处理 {what}：7-Zip 与 UnRAR 都没检测到（检查 tools 目录或设置里的路径）";
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        /// <summary>格式识别缓存的一项（键是路径，值是"哪一次的识别结果"）。</summary>
        private sealed class FormatCacheEntry
        {
            internal FormatCacheEntry(long length, DateTime lastWriteUtc, string? format)
            {
                Length = length;
                LastWriteUtc = lastWriteUtc;
                Format = format;
            }

            internal long Length { get; }

            internal DateTime LastWriteUtc { get; }

            internal string? Format { get; }
        }
    }
}
