using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;

namespace ArchiveFixer.Extraction
{
    /// <summary>层准备：确定实际归档、解压选项、密码候选和本层日志标签。</summary>
    internal sealed class RecursiveLayerPlanner
    {
        private readonly Func<string, IReadOnlyList<string>> _buildCandidates;
        private readonly Action<string, string> _log;

        internal RecursiveLayerPlanner(
            Func<string, IReadOnlyList<string>> buildCandidates,
            Action<string, string> log)
        {
            _buildCandidates = buildCandidates;
            _log = log;
        }

        internal LayerPlan Prepare(
            int depth,
            string itemArchivePath,
            string? carvedSource,
            IProgress<ArchiveProgress>? progress,
            bool verboseLog)
        {
            string archivePath = string.IsNullOrWhiteSpace(carvedSource)
                ? itemArchivePath
                : carvedSource!;

            archivePath = RecursiveExtractor.PreferCanonicalSiblingName(archivePath);

            var options = new ExtractOptions
            {
                OverwriteMode = "SkipExisting",
                KeepArchiveNameFolder = false
            };

            options.Normalize();

            progress?.Report(new ArchiveProgress
            {
                Percent = 0,
                Layer = depth,
                CurrentEntry = $"第 {depth} 层：{Path.GetFileName(archivePath)}"
            });

            IReadOnlyList<string> candidates = _buildCandidates(archivePath);

            string layerLabel = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                StatusText.RecursionLayerLogPrefixFormat,
                depth) + Path.GetFileName(archivePath);

            if (verboseLog)
            {
                _log(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.RecursionLayerAttemptLogFormat,
                        layerLabel,
                        depth,
                        Path.GetFileName(archivePath)));
            }

            return new LayerPlan(archivePath, options, candidates, layerLabel);
        }
    }
}
