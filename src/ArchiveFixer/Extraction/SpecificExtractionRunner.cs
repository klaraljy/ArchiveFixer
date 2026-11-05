using ArchiveFixer.Detection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction
{
    public sealed class SpecificExtractionRunResult
    {
        public bool Success { get; init; }

        public string OutputPath { get; init; } = string.Empty;

        public int Depth { get; init; }

        public string RestDirectory { get; init; } = string.Empty;

        /// <summary>本次实际密码结论（密码正确 / 不需要密码 / 需要密码 / 密码错误）；空 = 判不出。</summary>
        public string PasswordStatus { get; init; } = string.Empty;

        /// <summary>失败时的结构化诊断（层号、入口、卷数、分类），只进日志，不参与判据。</summary>
        public string DiagnosticDetail { get; init; } = string.Empty;

        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// 特定解压的独立循环（不经过第一/第二大步）：
    /// 每个输入压缩包独立解，最多 3 层；每层只认内容物 / 单一压缩包 / 标准分卷。
    /// </summary>
    public sealed class SpecificExtractionRunner
    {
        public const int MaxDepth = 3;

        private readonly IArchiveEngine _engine;
        private readonly Action<string>? _log;

        public SpecificExtractionRunner(IArchiveEngine engine, Action<string>? log = null)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _log = log;
        }

        public async Task<SpecificExtractionRunResult> RunAsync(
            string sourceArchive,
            string outputRoot,
            string workRoot,
            IReadOnlyList<string?> passwordCandidates,
            CancellationToken cancellationToken = default,
            Func<IReadOnlyList<string?>>? passwordProvider = null,
            Action<string?>? onPasswordSuccess = null,
            Action<int, string>? onLayerStarted = null,
            IProgress<ArchiveProgress>? progress = null)
        {
            if (string.IsNullOrWhiteSpace(sourceArchive) || !File.Exists(sourceArchive))
            {
                return new SpecificExtractionRunResult { Message = "输入压缩包不存在" };
            }

            string currentArchive = sourceArchive;
            bool anyPasswordUsed = false;
            Directory.CreateDirectory(outputRoot);
            Directory.CreateDirectory(workRoot);

            for (int depth = 0; depth < MaxDepth; depth++)
            {
                string layerRoot = Path.Combine(workRoot, "layer-" + depth.ToString("D2"));
                Directory.CreateDirectory(layerRoot);

                onLayerStarted?.Invoke(depth, Path.GetFileName(currentArchive));
                _log?.Invoke(
                    $"第 {depth + 1} 层：解压「{Path.GetFileName(currentArchive)}」");

                IProgress<ArchiveProgress>? layerProgress = progress == null
                    ? null
                    : new LayerProgress(progress, depth + 1);

                bool extracted = false;
                string lastError = string.Empty;
                string lastErrorType = string.Empty;
                IReadOnlyList<string?> candidates = passwordProvider?.Invoke() ?? passwordCandidates;
                List<string?> normalizedPasswords = NormalizePasswords(candidates).ToList();

                for (int passwordIndex = 0; passwordIndex < normalizedPasswords.Count; passwordIndex++)
                {
                    string? password = normalizedPasswords[passwordIndex];
                    string attempt = normalizedPasswords.Count > 1
                        ? $"密码候选 {passwordIndex + 1}/{normalizedPasswords.Count}，{DescribePasswordCandidate(password)}"
                        : DescribePasswordCandidate(password);
                    _log?.Invoke($"第 {depth + 1} 层：开始解压，{attempt}");
                    var request = new ArchiveRequest
                    {
                        ArchivePath = currentArchive,
                        Password = password,
                        OutputPath = layerRoot,
                        Progress = layerProgress
                    };

                    var options = new ExtractOptions
                    {
                        ExtractToOriginalDirectory = false,
                        CustomOutputDirectory = layerRoot,
                        KeepArchiveNameFolder = false,
                        OverwriteMode = "OverwriteAll",
                        UseGlobalPassword = false,
                        TryPasswordList = false,
                        TryEmptyPasswordFirst = false,
                        CancelOnFirstSuccess = true
                    };

                    ArchiveOperationResult result = await _engine
                        .ExtractAsync(request, options, cancellationToken)
                        .ConfigureAwait(false);

                    if (result.Success)
                    {
                        extracted = true;
                        anyPasswordUsed |= !string.IsNullOrEmpty(password);
                        onPasswordSuccess?.Invoke(password);
                        _log?.Invoke($"第 {depth + 1} 层：密码候选 {passwordIndex + 1}/{normalizedPasswords.Count} 通过");
                        break;
                    }

                    lastError = result.Message;
                    lastErrorType = result.DetectedErrorType;
                    _log?.Invoke($"第 {depth + 1} 层：密码候选 {passwordIndex + 1}/{normalizedPasswords.Count} 不对，继续试下一个。");
                }

                if (!extracted)
                {
                    _log?.Invoke($"第 {depth + 1} 层解压失败：{lastError}");

                    return new SpecificExtractionRunResult
                    {
                        Message = "解压失败：" + lastError,
                        PasswordStatus = DescribePasswordStatus(lastErrorType, anyPasswordUsed),
                        DiagnosticDetail =
                            $"层={depth + 1}; 入口={Path.GetFileName(currentArchive)}; 分类=ExtractFailed; 原因={lastError}",
                        RestDirectory = PreserveLastProcessArtifact(currentArchive, workRoot, outputRoot),
                        Depth = depth
                    };
                }

                SpecificLayerDecision decision = SpecificLayerClassifier.Classify(layerRoot);

                _log?.Invoke($"第 {depth + 1} 层分类：{decision.Kind}");

                if (decision.Kind == SpecificLayerKind.Content)
                {
                    string name = SpecificOutputNaming.Resolve(currentArchive, layerRoot);
                    string finalPath = MoveContent(layerRoot, Path.Combine(outputRoot, name));

                    _log?.Invoke($"第 {depth + 1} 层得到内容物：{Path.GetFileName(finalPath)}");

                    /*
                     * 成功链上的内层包是过程物，按固定规则随工作区删除；
                     * ⛔ 不再复制到「其余物」。「最后一层过程物」只在失败 / 部分完成时才保留。
                     */
                    return new SpecificExtractionRunResult
                    {
                        Success = true,
                        OutputPath = finalPath,
                        RestDirectory = string.Empty,
                        PasswordStatus = anyPasswordUsed ? StatusText.PasswordCorrect : StatusText.PasswordNotNeeded,
                        Message = "特定解压成功",
                        Depth = depth
                    };
                }

                if (decision.Kind == SpecificLayerKind.SingleArchive)
                {
                    currentArchive = decision.ArchivePath;
                    continue;
                }

                if (decision.Kind == SpecificLayerKind.StandardVolumeGroup)
                {
                    /*
                     * 特定解压也要接标准分卷：把这一组的第一卷当作下一层的输入，
                     * 同目录的其余卷由引擎按标准卷名自己找。⛔ 不再直接报“暂未接入”失败。
                     */
                    string? firstVolume = decision.VolumePaths
                        .OrderBy(path => LegacyVolumeNames.TryGetIndex(
                            Path.GetFileName(path)) ?? int.MaxValue)
                        .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();

                    if (!string.IsNullOrWhiteSpace(firstVolume))
                    {
                        currentArchive = firstVolume;
                        _log?.Invoke(
                            $"第 {depth + 1} 层标准分卷：入口「{Path.GetFileName(firstVolume)}」，共 {decision.VolumePaths.Count} 片，继续下一层");
                        continue;
                    }
                }

                return new SpecificExtractionRunResult
                {
                    Message = decision.Kind == SpecificLayerKind.StandardVolumeGroup
                        ? "标准分卷：这一层没有可用的第一卷"
                        : "这一层的形状不在特定解压支持范围内",
                    PasswordStatus = anyPasswordUsed ? StatusText.PasswordCorrect : StatusText.PasswordNotNeeded,
                    DiagnosticDetail =
                        $"层={depth + 1}; 入口={Path.GetFileName(currentArchive)}; 分类={decision.Kind}; 卷数={decision.VolumePaths.Count}",
                    RestDirectory = PreserveLastProcessArtifact(currentArchive, workRoot, outputRoot),
                    Depth = depth
                };
            }

            return new SpecificExtractionRunResult
            {
                Message = "超过 3 层仍未得到内容物",
                PasswordStatus = anyPasswordUsed ? StatusText.PasswordCorrect : StatusText.PasswordNotNeeded,
                DiagnosticDetail = $"层={MaxDepth}; 入口={Path.GetFileName(currentArchive)}; 分类=MaxDepth",
                RestDirectory = PreserveLastProcessArtifact(currentArchive, workRoot, outputRoot),
                Depth = MaxDepth
            };
        }

        private static string DescribePasswordCandidate(string? password) =>
            string.IsNullOrEmpty(password) ? "尝试空密码" : "尝试密码候选：******";

        private static string DescribePasswordStatus(string errorType, bool usedPassword)
        {
            if (usedPassword)
            {
                return StatusText.PasswordCorrect;
            }

            return errorType switch
            {
                EngineErrorTypes.WrongPassword or EngineErrorTypes.PasswordOrCorrupted => StatusText.WrongPassword,
                EngineErrorTypes.NeedPassword or EngineErrorTypes.EncryptedHeaders => StatusText.PasswordNeed,
                _ => string.Empty
            };
        }

        private static IEnumerable<string?> NormalizePasswords(IReadOnlyList<string?>? candidates)
        {
            if (candidates == null || candidates.Count == 0)
            {
                yield return null;
                yield break;
            }

            foreach (string? candidate in candidates)
            {
                yield return candidate;
            }
        }

        private static string MoveContent(string layerRoot, string finalPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath) ?? ".");

            if (Directory.Exists(finalPath))
            {
                finalPath = SafePathHelper.AutoRenameDirectoryPath(finalPath);
            }

            string? singleFolder = Directory
                .GetFileSystemEntries(layerRoot)
                .Where(Directory.Exists)
                .Where(path => !string.Equals(
                    Path.GetFileName(path),
                    ".ArchiveFixer.work",
                    StringComparison.OrdinalIgnoreCase))
                .ToList() is { Count: 1 } folders
                && !Directory.EnumerateFiles(layerRoot, "*", SearchOption.TopDirectoryOnly).Any()
                    ? folders[0]
                    : null;

            string source = singleFolder ?? layerRoot;

            Directory.Move(source, finalPath);

            return finalPath;
        }

        /// <summary>
        /// 最后一层的过程压缩包留在 <c>其余物</c>；只看工作区里的那一份，
        /// 用户最外层源包不在这里重复一份。
        /// </summary>
        private static string PreserveLastProcessArtifact(
            string lastArchivePath,
            string workRoot,
            string outputRoot)
        {
            if (string.IsNullOrWhiteSpace(lastArchivePath)
                || !File.Exists(lastArchivePath)
                || !Path.GetFullPath(lastArchivePath).StartsWith(
                    Path.GetFullPath(workRoot),
                    StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            string rest = Path.Combine(outputRoot, "其余物");
            Directory.CreateDirectory(rest);

            string target = Path.Combine(rest, Path.GetFileName(lastArchivePath));

            if (!File.Exists(target))
            {
                File.Copy(lastArchivePath, target);
            }

            return rest;
        }

        /// <summary>把引擎进度补上“这是第几层”，UI 不需要自己猜。</summary>
        private sealed class LayerProgress : IProgress<ArchiveProgress>
        {
            private readonly IProgress<ArchiveProgress> _inner;
            private readonly int _layer;

            public LayerProgress(IProgress<ArchiveProgress> inner, int layer)
            {
                _inner = inner;
                _layer = layer;
            }

            public void Report(ArchiveProgress value)
            {
                _inner.Report(new ArchiveProgress
                {
                    Percent = value.Percent,
                    CurrentEntry = value.CurrentEntry,
                    Layer = _layer,
                    ProcessedBytes = value.ProcessedBytes,
                    TotalBytes = value.TotalBytes,
                    ProcessedEntries = value.ProcessedEntries
                });
            }
        }
    }
}
