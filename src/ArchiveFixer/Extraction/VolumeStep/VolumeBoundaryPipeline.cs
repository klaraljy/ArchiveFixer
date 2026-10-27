using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// **边界解析管线**（共用骨架）：按基名仲裁族，再交给对应的族 resolver。
    ///
    /// <para>它只负责"找谁办、把一个基名办一次、把异常落成 Unknown"；
    /// 怎么定序/怎么出标准名完全在 <see cref="IVolumeBoundaryResolver"/> 实现里。</para>
    ///
    /// <para>⛔ 本类不扫目录、不读文件内容、不建链接；这些动作属于族 resolver 与
    /// <see cref="VolumeGroupEntryDirectory"/>。</para>
    /// </summary>
    public sealed class VolumeBoundaryPipeline
    {
        private readonly Dictionary<VolumeNamingFamily, IVolumeBoundaryResolver> _resolvers = new();

        public IReadOnlyCollection<IVolumeBoundaryResolver> Resolvers => _resolvers.Values;

        /// <summary>注册一族；同一族重复注册按配置错误处理（⛔ 不静默覆盖）。</summary>
        public void Register(IVolumeBoundaryResolver resolver)
        {
            if (resolver == null)
            {
                throw new ArgumentNullException(nameof(resolver));
            }

            if (_resolvers.ContainsKey(resolver.Family))
            {
                throw new InvalidOperationException($"族 {resolver.Family} 已经有 resolver，不能重复注册。");
            }

            _resolvers[resolver.Family] = resolver;
        }

        /// <summary>
        /// 对账本里每一个基名跑一次；返回所有有结论的组（Complete/Missing/Unknown/SizeAnomaly）。
        /// 没有 resolver 的族跳过（骨架阶段就是空跑，行为不变）。
        /// </summary>
        public async Task<IReadOnlyList<PreparedVolumeGroup>> ResolveAllAsync(
            FirstStepFileLedger ledger,
            Func<string, VolumeBoundaryContext> contextFactory,
            CancellationToken cancellationToken = default)
        {
            if (ledger == null || contextFactory == null)
            {
                return Array.Empty<PreparedVolumeGroup>();
            }

            var result = new List<PreparedVolumeGroup>();

            /*
             * 标准名快通道优先：同目录、同基名、每一片都是标准名的组，
             * 不再让族 resolver 用内容卡先接管；交给既有 StandardNameFastPath
             * 直接按入口试密码/试解。非标准名组仍走下面的 resolver。
             */
            var fastReadyKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (StandardNameFastPath.Decision decision
                     in StandardNameFastPath.Decide(ledger.Entries.Select(entry => entry.Path)))
            {
                if (decision.Kind == StandardNameFastPath.DecisionKind.Ready)
                {
                    fastReadyKeys.Add(decision.Group.BaseName + "|" + decision.Group.Directory);
                }
            }

            foreach (string baseName in ledger.BaseNames())
            {
                cancellationToken.ThrowIfCancellationRequested();

                string sourceLayer = FirstSourceLayer(ledger, baseName);

                if (fastReadyKeys.Contains(baseName + "|" + sourceLayer))
                {
                    continue;
                }

                VolumeFamilySelection selection = VolumeFamilyArbiter.Select(ledger, baseName);

                if (selection.Conflict)
                {
                    result.Add(BuildConflictGroup(ledger, baseName, selection.Evidence));
                    continue;
                }

                if (selection.Family is not VolumeNamingFamily family
                    || !_resolvers.TryGetValue(family, out IVolumeBoundaryResolver? resolver))
                {
                    continue;
                }

                try
                {
                    PreparedVolumeGroup? group = await resolver
                        .ResolveAsync(contextFactory(baseName), cancellationToken)
                        .ConfigureAwait(false);

                    if (group != null)
                    {
                        result.Add(group);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    /*
                     * 族 resolver 抛异常 = 这一组判不出，不是"整批失败"。
                     * ⛔ 不吞成 Complete；落 Unknown 并留下原因，源文件一个字节不动。
                     */
                    result.Add(new PreparedVolumeGroup
                    {
                        BaseName = baseName,
                        Family = family,
                        OriginalSourceLayer = FirstSourceLayer(ledger, baseName),
                        State = PreparedGroupState.Unknown,
                        Evidence = $"族 resolver 异常：{ex.GetType().Name}：{ex.Message}"
                    });
                }
            }

            return result;
        }

        private static PreparedVolumeGroup BuildConflictGroup(
            FirstStepFileLedger ledger,
            string baseName,
            string evidence)
        {
            return new PreparedVolumeGroup
            {
                BaseName = baseName,
                Family = null,
                OriginalSourceLayer = FirstSourceLayer(ledger, baseName),
                State = PreparedGroupState.SizeAnomaly,
                Evidence = evidence,
                SizeAnomalyDetail = evidence
            };
        }

        private static string FirstSourceLayer(FirstStepFileLedger ledger, string baseName)
        {
            return ledger.SameBase(baseName)
                .Select(entry => entry.SourceLayer)
                .FirstOrDefault(layer => !string.IsNullOrWhiteSpace(layer))
                ?? string.Empty;
        }
    }
}
