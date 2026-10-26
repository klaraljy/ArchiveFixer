using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Helpers;

namespace ArchiveFixer.Extraction
{
    public enum SpecificLayerKind
    {
        /// <summary>直接就是内容物（图片/视频/普通文件）。</summary>
        Content,

        /// <summary>根上只有一个完整标准压缩包，继续下一层。</summary>
        SingleArchive,

        /// <summary>根上是同一组标准分卷，接片后继续下一层。</summary>
        StandardVolumeGroup,

        /// <summary>形状超出特定解压支持的三类，停下。</summary>
        Unsupported
    }

    public sealed class SpecificLayerDecision
    {
        public SpecificLayerKind Kind { get; init; }

        public string ArchivePath { get; init; } = string.Empty;

        public IReadOnlyList<string> VolumePaths { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// 特定解压每层解完后的三选一：直接内容物 / 单一完整标准压缩包 / 同目录标准分卷。
    /// 只读目录形状，不猜名字、不碰第一/第二大步。
    /// </summary>
    public static class SpecificLayerClassifier
    {
        public static SpecificLayerDecision Classify(string contentRoot)
        {
            if (string.IsNullOrWhiteSpace(contentRoot) || !Directory.Exists(contentRoot))
            {
                return new SpecificLayerDecision { Kind = SpecificLayerKind.Content };
            }

            List<string> entries = Directory
                .GetFileSystemEntries(contentRoot)
                .Where(path => !string.Equals(
                    Path.GetFileName(path),
                    ".ArchiveFixer.work",
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            List<string> archiveFiles = entries
                .Where(File.Exists)
                .Where(path => SpecificOutputNaming.IsKnownArchiveFileName(Path.GetFileName(path)))
                .ToList();

            if (archiveFiles.Count == 1 && entries.Count == 1)
            {
                return new SpecificLayerDecision
                {
                    Kind = SpecificLayerKind.SingleArchive,
                    ArchivePath = archiveFiles[0]
                };
            }

            List<string> volumePieces = entries
                .Where(File.Exists)
                .Where(path => FileNameHelper.IsVolumePartFileName(
                    SpecificOutputNaming.RemoveChineseFromExtension(Path.GetFileName(path))))
                .ToList();

            if (volumePieces.Count >= 2 && volumePieces.Count == entries.Count)
            {
                string? baseName = FileNameHelper.SecondStepBaseName(volumePieces[0]);

                if (volumePieces.All(path => string.Equals(
                        FileNameHelper.SecondStepBaseName(path),
                        baseName,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    return new SpecificLayerDecision
                    {
                        Kind = SpecificLayerKind.StandardVolumeGroup,
                        VolumePaths = volumePieces
                    };
                }
            }

            if (archiveFiles.Count == 0)
            {
                return new SpecificLayerDecision { Kind = SpecificLayerKind.Content };
            }

            /*
             * 有归档但还混着别的条目（文件夹/散文件）⇒ 最后一层就是内容物，
             * 用命名规则决定输出名，不再继续解。
             */
            if (entries.Count > archiveFiles.Count)
            {
                return new SpecificLayerDecision { Kind = SpecificLayerKind.Content };
            }

            return new SpecificLayerDecision { Kind = SpecificLayerKind.Unsupported };
        }
    }
}
