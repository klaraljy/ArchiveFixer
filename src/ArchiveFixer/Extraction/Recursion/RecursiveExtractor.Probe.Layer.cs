using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiveFixer.Extraction
{
    public sealed partial class RecursiveExtractor
    {
        private async Task<LayerOutcome> FinishSuccessfulLayerAsync(
            WorkItem item,
            ArchiveOperationResult? success,
            LayerManifest layerManifest,
            string? succeededPassword,
            CancellationToken cancellationToken)
        {
            /*
             * 这一层的密码被验证成功 ⇒ 如实回传一声（空密码不算"一条密码"，与
             * PasswordService.RecordVerifiedPassword 的既有口径一致：它也不记空密码）。
             *
             * 位置 = **三条成功路的共同出口**（主引擎循环 / 尾部归档直读 / 换引擎兜底），
             * ⛔ 不在某一条路里各写一遍 —— 那个正是本项目禁止的"同一件事两份实现"。
             */
            if (!string.IsNullOrEmpty(succeededPassword))
            {
                PasswordVerified?.Invoke(succeededPassword);
            }
            string layerLabel = DescribeProbeLabel(item);

            (int fileCount, long outputSize) = OutputVerifier.Measure(item.Layer.OutputPath);

            /*
             * 第二道防线（不变量 4）：解压后校验真实落点。
             *
             * 外部引擎（7z.exe / UnRAR.exe）是独立进程，第一道预检挡不住它自己拼出来的落点
             * （符号链接条目、引擎自身的路径处理都在预检视野之外）。越界必须成为**失败结论**：
             * 不发布、不清理源包，报告里写清越界的是哪个产物、落在哪 —— 只写一行日志而结论仍是
             * "解压成功"，等于把不变量 4 降级成一条没人看的提示。
             *
             * ⚠ 措辞与单层路径（ExtractionCoordinator 的落点校验）**逐字对齐**：
             * 同一个口径在两条路径上各说一套，用户会以为是两件事。
             */
            string? landingViolation = FindLandingViolation(item.Layer.OutputPath);

            if (landingViolation != null)
            {
                var violatedReport = new RecursionLayerReport
                {
                    Depth = item.Depth,
                    ArchivePath = item.ArchivePath,
                    OutputPath = item.Layer.OutputPath,
                    Success = false,
                    Status = StatusText.ExtractFailed,
                    LandingViolation = true,
                    Message = "产物越出本层产物目录 —— 按既定口径整包判定失败（不归集产物、不处理源包、其余物不生成）：" +
                              landingViolation +
                              "。⚠ 解压是外部引擎进程写的盘，越界的那一次写入拦不住；本层产物留在工作区、未发布。",
                    InnerArchives = Array.Empty<string>(),
                    OutputFileCount = fileCount,
                    OutputSize = outputSize,
                    UsedPasswordMasked = PasswordMasker.Mask(succeededPassword)
                };

                return LayerOutcome.Stop(violatedReport, RecursionStopReason.UnsafeEntry);
            }

            IReadOnlyList<string> innerArchives = await ProbeInnerArchivesAsync(
                    item.Layer.OutputPath,
                    layerLabel,
                    cancellationToken)
                .ConfigureAwait(false);

            var report = new RecursionLayerReport
            {
                Depth = item.Depth,
                ArchivePath = item.ArchivePath,
                ListingSaidNoEncryption = item.ListingSaidNoEncryption,
                TriedCandidateCount = item.TriedCandidateCount,
                CandidatesProducedIdenticalResults = item.CandidatesProducedIdenticalResults,
                OutputPath = item.Layer.OutputPath,
                Success = true,

                // 状态一律取引擎给的（成功时就是"解压成功"），不在这里另造一套口径。
                Status = string.IsNullOrWhiteSpace(success?.Status)
                    ? StatusText.ExtractSuccess
                    : success!.Status,
                Message = string.Empty,
                InnerArchives = innerArchives,
                OutputFileCount = fileCount,
                OutputSize = outputSize,
                Manifest = layerManifest,
                UsedPasswordMasked = PasswordMasker.Mask(succeededPassword)
            };

            return LayerOutcome.Ok(report);
        }

        /// <summary>
        /// 第一道防线：解压前预检条目名。
        /// 返回 null = 没有发现问题、或者**列不出目录**（加密头 / 损坏，这种情况下不拦）；
        /// 返回非空字符串 = 发现了危险条目，内容是可以直接给用户看的一句话。
        ///
        /// <para>回传的第二个元素是这一次列目录的原始结论 —— **同一份清单不许列两遍**：
        /// 检验等级 L3 要拿本层的清单当预期（见 <see cref="RecursionLayerReport.Manifest"/>），
        /// 而这里本来就已经问过引擎列了一次，⛔ 不许为了拿预期再列一次（那会多花一次全包扫描）。</para>
        /// </summary>
        /// <returns>
        /// 第一个元素 = 预检结论（null = 没发现问题 / 列不出目录）；
        /// 第二个元素 = 这一次的列目录原始结论（列不出来时为 null）。
        /// </returns>
        private async Task<(string? UnsafeSummary, ArchiveListResult? Listed)> CheckEntriesBeforeExtractAsync(
            string archivePath,
            string password,
            CancellationToken cancellationToken)
        {
            ArchiveListResult list;

            try
            {
                list = await _engine
                    .ListAsync(ArchiveRequest.For(archivePath, password), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // 引擎抛异常时按"列不出来"处理：真正的兜底是解压后的落点校验。
                return (null, null);
            }

            if (!list.Success)
            {
                return (null, list);
            }

            PathSafetyReport report = ArchivePathGuard.CheckEntries(list.Entries);

            /*
             * 命名类（保留名 / 结尾点或空格 / 备用数据流）：只记 WARN、照常解压 ——
             * 引擎落盘时会自动改成安全名（用户 2026-10-24 口径：清洗后再落盘，⛔ 不整包拒绝）。
             */
            if (report.NamingHazards.Count > 0)
            {
                _log?.Invoke("WARN", "这一层：" + report.NamingSummary);
            }

            return (report.IsSafe ? null : report.Summary, list);
        }

        /// <summary>
        /// **先只解清单里最小的那个条目**（探针），再决定要不要解整包（用户 2026-10-04 真机）。
        ///
        /// <para>判据全部来自唯一出口 <see cref="PasswordProbe"/>（值不值得探 / 挑哪一个条目 ——
        /// ⛔ 这里不另写一份）：</para>
        /// <list type="bullet">
        /// <item><description>不值得探（没有加密条目 / 包不大）或挑不出条目 ⇒ 返回 <c>null</c>（调用方照旧解整包）；</description></item>
        /// <item><description>探针**解不开且引擎明说密码不对** ⇒ 返回那一份引擎结果（调用方当"这个候选不对"处理，
        /// 一个字节的整包数据都没动）；</description></item>
        /// <item><description>探针解开 / 给不出确定结论 / 探针自己出错 ⇒ 返回 <c>null</c>（照旧解整包，
        /// 结论仍然由整包那一步说了算）。</description></item>
        /// </list>
        ///
        /// <para>探针目录建在**这一层产物目录之外**（同层的兄弟目录）：产物目录里的东西要参与
        /// 结果校验与发布，⛔ 一个字节都不能混进去；用完就整份删掉。</para>
        /// </summary>
        private async Task<ArchiveOperationResult?> ProbeCandidateAsync(
            WorkItem item,
            string archivePath,
            ExtractOptions options,
            ArchiveListResult? listed,
            string candidate,
            IProgress<ArchiveProgress>? progress,
            Action<ArchiveStallNotice>? stalled,
            CancellationToken cancellationToken)
        {
            if (!PasswordProbe.IsWorthProbing(listed, listed?.TotalUncompressedSize ?? 0))
            {
                return null;
            }

            string? probeEntry = PasswordProbe.ChooseProbeEntry(listed);

            if (string.IsNullOrWhiteSpace(probeEntry))
            {
                return null;
            }

            // 落点与单层路径**同一个出口**（2026-10-05 统一）：产物目录的兄弟位置，绝不放产物目录里。
            string probeDirectory = PasswordProbe.ResolveProbeDirectory(item.Layer.OutputPath);

            try
            {
                Directory.CreateDirectory(probeDirectory);
            }
            catch
            {
                // 建不出目录（权限 / 盘）：探针做不了 ⇒ 退回整包试解（结论不会被这里改掉）。
                return null;
            }

            IReadOnlyList<string> previousEntries = options.IncludeEntries;

            try
            {
                options.IncludeEntries = new[] { probeEntry };

                ArchiveOperationResult result = await _engine.ExtractAsync(
                        new ArchiveRequest
                        {
                            ArchivePath = archivePath,
                            OutputPath = probeDirectory,
                            Password = candidate,
                            Progress = progress,
                            Stalled = stalled
                        },
                        options,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (result.DetectedErrorType == "Cancelled" || result.Status == StatusText.Cancelled)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (result.Success)
                {
                    Log(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.PasswordProbePassedLogFormat,
                            DescribeProbeLabel(item),
                            PasswordProbe.Describe(probeEntry, ResolveProbeEntrySize(listed, probeEntry))));

                    return null;
                }

                if (result.IsWrongPassword || result.IsNeedPassword)
                {
                    Log(
                        "INFO",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.PasswordProbeRejectedLogFormat,
                            DescribeProbeLabel(item),
                            PasswordProbe.Describe(probeEntry, ResolveProbeEntrySize(listed, probeEntry))));

                    return result;
                }

                // 给不出确定结论（损坏 / 权限 / 引擎怪话）：不拦，照旧解整包。
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 探针自己出错不是"这个包失败"：退回整包试解。
                return null;
            }
            finally
            {
                options.IncludeEntries = previousEntries;

                try
                {
                    if (Directory.Exists(probeDirectory))
                    {
                        Directory.Delete(probeDirectory, recursive: true);
                    }
                }
                catch
                {
                    // 删不掉只是工作区里多一个空目录（它不进产物、也不进发布）。
                }
            }
        }

        /// <summary>探针日志里的层标签（与候选日志同一个形状：`├ 第 N 层：包名`）。</summary>
        private static string DescribeProbeLabel(WorkItem item) =>
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.RecursionLayerLogPrefixFormat,
                item.Depth) + Path.GetFileName(item.ArchivePath);

        /// <summary>
        /// **媒体前缀 / 双面文件的第一层改用内置 ZIP 直读**（用户 2026-10-26 真机：
        /// AES ZIP 的密码是 UTF-8，7-Zip 只按 ANSI 派生 ⇒ 同一个正确密码在 7-Zip 里全报密码错误）。
        ///
        /// <para>只在 7-Zip **列不出这一份**之后才调用（见候选循环里的挂点）：引擎能正常列目录的
        /// 外层 7z 里也可能恰好藏着一个自洽 ZIP，抢在引擎前面会误判。判据与既有直读同一个出口
        /// <see cref="EmbeddedZipStreamExtractor"/>；失败就返回 null，原样回落到 7-Zip/单条目近路。</para>
        ///
        /// <para>⚠ 2026-11-07：未加密的 stored / deflate 多条目 ZIP 也走这条直读。
        /// 旧实现只给 AES / 单条目开，于是识别阶段已经说“可以 ZIP 直读”的双面文件，
        /// 递归首层仍要抠一份等大副本再交给 7z（真机 4.06 GiB 的 P93 多花近 3 分钟）。
        /// 直读器逐条目计算并校验 CRC32；CRC 不对按损坏收场，不回落再读一遍；
        /// 不支持的方法 / 结构不自洽仍原样回落抠副本 + 引擎。</para>
        /// </summary>
        private async Task<LayerOutcome?> TryTakeEmbeddedZipDirectAsync(
            WorkItem item,
            IReadOnlyList<string> candidates,
            IProgress<ArchiveProgress>? progress,
            CancellationToken cancellationToken)
        {
            string layerLabel = DescribeProbeLabel(item);

            try
            {
                EmbeddedArchiveInfo info = DetectEmbeddedArchive(item.ArchivePath);

                if (!info.Found || info.IsVolumePart || info.Offset <= 0)
                {
                    return null;
                }

                IReadOnlyList<string> limitedCandidates = candidates
                    .Take(Math.Max(1, _limits.MaxPasswordAttemptsPerLayer))
                    .ToList();

                EmbeddedZipProbeResult probe = await Task.Run(
                        () => EmbeddedZipStreamExtractor.Probe(
                            item.ArchivePath,
                            info.Offset,
                            info.ArchiveEnd,
                            item.Layer.OutputPath,
                            limitedCandidates,
                            cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!probe.Supported || probe.List == null)
                {
                    // 不支持 / 结构不自洽 ⇒ 原样回落抠取 + 引擎。
                    return null;
                }

                /*
                 * 直读器现在在流式写出时逐条目比对 CRC32；未加密的 stored/deflate 都可直读。
                 * AES 条目由认证码负责完整性；其余不支持的方法由探针直接返回 Unsupported。
                 */

                Log(
                    "INFO",
                    $"{layerLabel}：尾部内嵌 ZIP 可用内置直读（{probe.List.FileCount} 个文件）；"
                    + (probe.ResolvedPasswordIndex > 0
                        ? $"第 {probe.ResolvedPasswordIndex} 个候选密码按 "
                          + $"{ZipAesCrypto.DescribeEncoding(probe.ResolvedPasswordEncoding)} 通过校验，"
                          + "⛔ 不交给 7-Zip（它只按 ANSI 派生，会一律报密码错误）。"
                        : "未加密，不交给 7-Zip。"));

                IProgress<int>? directProgress = progress == null
                    ? null
                    : new Progress<int>(percent => progress.Report(new ArchiveProgress
                    {
                        Percent = percent,
                        Layer = item.Depth,
                        CurrentEntry = $"第 {item.Depth} 层：{Path.GetFileName(item.ArchivePath)}（内置 ZIP 直读）"
                    }));

                EmbeddedZipExtractResult extract = await Task.Run(
                    () => EmbeddedZipStreamExtractor.Extract(
                        item.ArchivePath,
                        info.Offset,
                        info.ArchiveEnd,
                        item.Layer.OutputPath,
                        progress: directProgress,
                        cancellationToken,
                        budgetOptions: new ResourceBudgetOptions
                        {
                            MaxSingleFileSize = _limits.MaxSingleFileSize,
                            MaxTotalSize = _limits.MaxTotalSize,
                            MaxFileCount = _limits.MaxTotalFiles
                        },
                        passwords: limitedCandidates),
                    cancellationToken).ConfigureAwait(false);

                if (!extract.Success)
                {
                    if (extract.Corrupted)
                    {
                        Log("ERROR", $"{layerLabel}：内置 ZIP 直读发现数据损坏 —— {extract.Message}");

                        /*
                         * CRC 对不上是**确定的损坏**，不是“这条路走不通”：回落再抠一份 10G 副本
                         * 交给 7z 只会重复读一遍并得到同一个结论。直接按损坏收场，保留原包。
                         */
                        return LayerOutcome.Stop(
                            BuildLayerReport(
                                item,
                                result: null,
                                succeededPassword: null,
                                overrideMessage: extract.Message,
                                overrideStatus: StatusText.Corrupted),
                            RecursionStopReason.Corrupted);
                    }

                    Log(
                        "WARN",
                        $"{layerLabel}：内置 ZIP 直读没成功（{extract.Message}）—— 原样回落到引擎路径。");
                    return null;
                }

                DangerousEntriesReported?.Invoke(
                    ViewModels.ExtractionCoordinator.AnalyzeDangerousEntries(
                        probe.List.Entries,
                        ReportDangerousEntries));

                string? succeededPassword = probe.ResolvedPasswordIndex > 0
                    && probe.ResolvedPasswordIndex <= limitedCandidates.Count
                        ? limitedCandidates[probe.ResolvedPasswordIndex - 1]
                        : null;

                item.ListingSaidNoEncryption = probe.ResolvedPasswordIndex == 0;

                return await FinishSuccessfulLayerAsync(
                        item,
                        new ArchiveOperationResult
                        {
                            Success = true,
                            Status = StatusText.ExtractSuccess,
                            EngineDisplayName = EmbeddedZipStreamExtractor.ReaderDisplayName
                        },
                        LayerManifest.From(probe.List),
                        succeededPassword,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                /*
                 * 这条是加法：判据内部任何意外都只能"这条近路走不通"，不许挡住原本能解开的包。
                 */
                Log(
                    "WARN",
                    $"{layerLabel}：内置 ZIP 直读尝试失败（{ex.Message}）—— 原样回落到引擎路径。");

                return null;
            }
        }

    }
}