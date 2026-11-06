using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Models;

namespace ArchiveFixer.Extraction
{
    /// <summary>
    /// 一条用户导入的源根。身份用**导入那一刻的规范化原始路径**，不随改名/搬移改变。
    /// </summary>
    public sealed class SourceRootRecord
    {
        public string RootId { get; init; } = string.Empty;

        public string TaskId { get; init; } = string.Empty;

        public string OriginalPath { get; init; } = string.Empty;

        public string CurrentPath { get; init; } = string.Empty;

        public string PlacementKey { get; init; } = string.Empty;

        public bool IsFolderSelection { get; init; }

        public string SelectionRoot { get; init; } = string.Empty;

        public SourceHandlingMode SourceHandling { get; init; } = SourceHandlingMode.KeepInPlace;
    }

    /// <summary>
    /// 第一大步/递归层产出的一个文件。候选分卷片在这里持有租约，交界前不得回收。
    /// </summary>
    public sealed class ProducedArtifactRecord
    {
        public string Path { get; init; } = string.Empty;

        public string OriginRootId { get; init; } = string.Empty;

        public string ProducedByTaskId { get; init; } = string.Empty;

        public ArtifactRole Role { get; init; } = ArtifactRole.Unknown;

        public string CandidateLeaseId { get; init; } = string.Empty;

        public string PlacementKey { get; init; } = string.Empty;

        public bool HasCandidateLease => !string.IsNullOrWhiteSpace(CandidateLeaseId);
    }

    /// <summary>
    /// **本批的源根 → 产出物 → 内容组账本骨架**。
    ///
    /// <para>第一阶段登记源根；第二阶段登记产出物并给候选分卷片发租约。
    /// ⛔ 这里不判组、不碰盘、不删除。</para>
    /// </summary>
    public sealed class ExtractionContentGraph
    {
        private readonly Dictionary<string, SourceRootRecord> _rootsByRootId =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, string> _rootIdByTaskId =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, string> _rootIdByPath =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, ProducedArtifactRecord> _artifactsByPath =
            new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<SourceRootRecord> SourceRoots => _rootsByRootId.Values;

        public IReadOnlyCollection<ProducedArtifactRecord> Artifacts => _artifactsByPath.Values;

        public int Count => _rootsByRootId.Count;

        public int ArtifactCount => _artifactsByPath.Count;

        /// <summary>批首清空；⛔ 不把上一批的源根/产出物带进下一批。</summary>
        public void Clear()
        {
            _rootsByRootId.Clear();
            _rootIdByTaskId.Clear();
            _rootIdByPath.Clear();
            _artifactsByPath.Clear();
        }

        /// <summary>
        /// 登记一个源根。重复登记同一原始路径时保留同一条 RootId，只刷新当前路径与落点域。
        /// </summary>
        public SourceRootRecord RegisterSourceRoot(
            ArchiveTask? task,
            string? placementKey,
            SourceHandlingMode sourceHandling)
        {
            if (task == null)
            {
                throw new ArgumentNullException(nameof(task));
            }

            string originalPath = NormalizePath(task.OriginalPath);

            if (originalPath.Length == 0)
            {
                originalPath = NormalizePath(task.CurrentPath);
            }

            if (originalPath.Length == 0)
            {
                throw new ArgumentException("源根没有可规范化的原始路径。", nameof(task));
            }

            var record = new SourceRootRecord
            {
                RootId = originalPath,
                TaskId = task.TaskId,
                OriginalPath = originalPath,
                CurrentPath = NormalizePath(task.CurrentPath),
                PlacementKey = placementKey ?? string.Empty,
                IsFolderSelection = task.SourceSelectionKind == SourceSelectionKind.Folder,
                SelectionRoot = NormalizePath(task.SourceSelectionRoot),
                SourceHandling = sourceHandling
            };

            _rootsByRootId[record.RootId] = record;

            if (!string.IsNullOrWhiteSpace(record.TaskId))
            {
                _rootIdByTaskId[record.TaskId] = record.RootId;
            }

            IndexPath(record.OriginalPath, record.RootId);
            IndexPath(record.CurrentPath, record.RootId);

            return record;
        }

        /// <summary>
        /// 按任务、当前路径或链根路径找源根。找不到返回 false —— 调用方必须按“归属不明”处理。
        /// </summary>
        public bool TryGetSourceRoot(ArchiveTask? task, out SourceRootRecord root)
        {
            root = null!;

            if (task == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(task.TaskId)
                && _rootIdByTaskId.TryGetValue(task.TaskId, out string? byTask)
                && _rootsByRootId.TryGetValue(byTask, out SourceRootRecord? taskRoot))
            {
                root = taskRoot;
                return true;
            }

            foreach (string? path in new[]
                     {
                         task.OriginalPath,
                         task.CurrentPath,
                         task.RootSourcePath,
                         task.ChainRootIdentity
                     })
            {
                if (TryGetByPath(path, out root))
                {
                    return true;
                }
            }

            return false;
        }

        public bool TryGetByPath(string? path, out SourceRootRecord root)
        {
            root = null!;
            string normalized = NormalizePath(path);

            if (normalized.Length == 0
                || !_rootIdByPath.TryGetValue(normalized, out string? rootId)
                || !_rootsByRootId.TryGetValue(rootId, out SourceRootRecord? record))
            {
                return false;
            }

            root = record;
            return true;
        }

        public bool TryGetByRootId(string? rootId, out SourceRootRecord root)
        {
            root = null!;

            if (string.IsNullOrWhiteSpace(rootId)
                || !_rootsByRootId.TryGetValue(rootId, out SourceRootRecord? record))
            {
                return false;
            }

            root = record;
            return true;
        }

        /// <summary>产出物/账本项要挂的来源根身份；找不到就返回空串，⛔ 不编一个。</summary>
        public string ResolveRootId(ArchiveTask? task)
        {
            return TryGetSourceRoot(task, out SourceRootRecord root) ? root.RootId : string.Empty;
        }

        public string ResolvePlacementKey(ArchiveTask? task)
        {
            return TryGetSourceRoot(task, out SourceRootRecord root) ? root.PlacementKey : string.Empty;
        }

        /// <summary>
        /// 登记一个产出物；同一路径保留后到的事实，但**绝不把已有租约降级成无租约**。
        /// </summary>
        public ProducedArtifactRecord RegisterArtifact(
            string? path,
            string? originRootId,
            string? producedByTaskId,
            ArtifactRole role,
            string? candidateLeaseId,
            string? placementKey)
        {
            string normalized = NormalizePath(path);

            if (normalized.Length == 0)
            {
                throw new ArgumentException("产出物路径为空。", nameof(path));
            }

            _artifactsByPath.TryGetValue(normalized, out ProducedArtifactRecord? previous);

            string lease = string.IsNullOrWhiteSpace(candidateLeaseId)
                ? previous?.CandidateLeaseId ?? string.Empty
                : candidateLeaseId;

            ArtifactRole effectiveRole = role == ArtifactRole.Unknown && lease.Length > 0
                ? previous?.Role ?? role
                : role;

            var record = new ProducedArtifactRecord
            {
                Path = normalized,
                OriginRootId = string.IsNullOrWhiteSpace(originRootId)
                    ? previous?.OriginRootId ?? string.Empty
                    : originRootId,
                ProducedByTaskId = string.IsNullOrWhiteSpace(producedByTaskId)
                    ? previous?.ProducedByTaskId ?? string.Empty
                    : producedByTaskId,
                Role = effectiveRole,
                CandidateLeaseId = lease,
                PlacementKey = string.IsNullOrWhiteSpace(placementKey)
                    ? previous?.PlacementKey ?? string.Empty
                    : placementKey
            };

            _artifactsByPath[normalized] = record;
            return record;
        }

        /// <summary>给候选分卷片发租约；返回租约 id。重复调用同一路径只刷新，不叠加。</summary>
        public string LeaseCandidate(
            string? path,
            string? originRootId,
            string? producedByTaskId,
            string? placementKey)
        {
            ProducedArtifactRecord record = RegisterArtifact(
                path,
                originRootId,
                producedByTaskId,
                ArtifactRole.CandidateVolumePiece,
                Guid.NewGuid().ToString("N"),
                placementKey);

            return record.CandidateLeaseId;
        }

        public bool TryGetArtifact(string? path, out ProducedArtifactRecord artifact)
        {
            artifact = null!;
            string normalized = NormalizePath(path);

            return normalized.Length > 0
                && _artifactsByPath.TryGetValue(normalized, out ProducedArtifactRecord? record)
                && (artifact = record) != null;
        }

        /// <summary>这一份现在是否被交界前租约保护。</summary>
        public bool IsLeased(string? path) =>
            TryGetArtifact(path, out ProducedArtifactRecord artifact) && artifact.HasCandidateLease;

        private void IndexPath(string? path, string rootId)
        {
            string normalized = NormalizePath(path);

            if (normalized.Length > 0)
            {
                _rootIdByPath[normalized] = rootId;
            }
        }

        private static string NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path.Trim();
            }
        }
    }
}
