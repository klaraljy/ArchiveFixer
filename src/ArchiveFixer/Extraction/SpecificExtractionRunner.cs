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

        public SpecificExtractionRunner(IArchiveEngine engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        public async Task<SpecificExtractionRunResult> RunAsync(
            string sourceArchive,
            string outputRoot,
            string workRoot,
            IReadOnlyList<string?> passwordCandidates,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(sourceArchive) || !File.Exists(sourceArchive))
            {
                return new SpecificExtractionRunResult { Message = "输入压缩包不存在" };
            }

            string currentArchive = sourceArchive;
            Directory.CreateDirectory(outputRoot);
            Directory.CreateDirectory(workRoot);

            for (int depth = 0; depth < MaxDepth; depth++)
            {
                string layerRoot = Path.Combine(workRoot, "layer-" + depth.ToString("D2"));
                Directory.CreateDirectory(layerRoot);

                bool extracted = false;
                string lastError = string.Empty;

                foreach (string? password in NormalizePasswords(passwordCandidates))
                {
                    var request = new ArchiveRequest
                    {
                        ArchivePath = currentArchive,
                        Password = password,
                        OutputPath = layerRoot
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
                        break;
                    }

                    lastError = result.Message;
                }

                if (!extracted)
                {
                    return new SpecificExtractionRunResult
                    {
                        Message = "解压失败：" + lastError,
                        Depth = depth
                    };
                }

                SpecificLayerDecision decision = SpecificLayerClassifier.Classify(layerRoot);

                if (decision.Kind == SpecificLayerKind.Content)
                {
                    string name = SpecificOutputNaming.Resolve(currentArchive, layerRoot);
                    string finalPath = Path.Combine(outputRoot, name);

                    MoveContent(layerRoot, finalPath);
                    string restDirectory = PreserveLastProcessArtifact(
                        currentArchive,
                        workRoot,
                        outputRoot);

                    return new SpecificExtractionRunResult
                    {
                        Success = true,
                        OutputPath = finalPath,
                        RestDirectory = restDirectory,
                        Depth = depth
                    };
                }

                if (decision.Kind == SpecificLayerKind.SingleArchive)
                {
                    currentArchive = decision.ArchivePath;
                    continue;
                }

                return new SpecificExtractionRunResult
                {
                    Message = decision.Kind == SpecificLayerKind.StandardVolumeGroup
                        ? "标准分卷：本轮独立管线暂未接入接片"
                        : "这一层的形状不在特定解压支持范围内",
                    Depth = depth
                };
            }

            return new SpecificExtractionRunResult
            {
                Message = "超过 3 层仍未得到内容物",
                Depth = MaxDepth
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

        private static void MoveContent(string layerRoot, string finalPath)
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
    }
}
