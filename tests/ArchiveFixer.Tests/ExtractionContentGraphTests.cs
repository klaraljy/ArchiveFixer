using System;
using System.IO;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using Xunit;

using ArchiveFixer.ViewModels;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 源根账本骨架的守门用例：身份稳定、按路径/任务可查、清批不串批。
    /// ⛔ 不涉及分组、不碰盘、不删除。
    /// </summary>
    public sealed class ExtractionContentGraphTests
    {
        [Fact]
        public void 源根登记_身份用原始路径_改名后仍能按任务和链根查到()
        {
            var graph = new ExtractionContentGraph();
            var rootTask = new ArchiveTask(@"C:\in\A.7z", 1)
            {
                SourceSelectionKind = SourceSelectionKind.Folder,
                SourceSelectionRoot = @"C:\in"
            };

            SourceRootRecord root = graph.RegisterSourceRoot(
                rootTask,
                "placement-A",
                SourceHandlingMode.DeleteInPlace);

            Assert.Equal(Path.GetFullPath(@"C:\in\A.7z"), root.RootId);
            Assert.Equal("placement-A", root.PlacementKey);
            Assert.Equal(SourceHandlingMode.DeleteInPlace, root.SourceHandling);
            Assert.Equal(1, graph.Count);

            rootTask.CurrentPath = @"C:\in\A-renamed.7z";

            /* 同一轮改名后先按任务身份查；路径索引在下一轮重登记时刷新。 */
            Assert.True(graph.TryGetSourceRoot(rootTask, out SourceRootRecord byTask));
            Assert.Equal(root.RootId, byTask.RootId);

            graph.RegisterSourceRoot(rootTask, "placement-A", SourceHandlingMode.DeleteInPlace);

            Assert.Equal(1, graph.Count);
            Assert.True(graph.TryGetByPath(@"C:\in\A-renamed.7z", out SourceRootRecord byCurrentPath));
            Assert.Equal(root.RootId, byCurrentPath.RootId);

            var child = new ArchiveTask(@"C:\out\A-renamed.7z", 2)
            {
                ParentOutputDirectory = @"C:\out\A",
                RootSourcePath = rootTask.OriginalPath
            };

            Assert.True(graph.TryGetSourceRoot(child, out SourceRootRecord byChainRoot));
            Assert.Equal(root.RootId, byChainRoot.RootId);
            Assert.Equal(root.RootId, graph.ResolveRootId(child));
            Assert.Equal("placement-A", graph.ResolvePlacementKey(child));
        }

        [Fact]
        public void 源根重复登记_不新增第二条_只刷新当前路径与落点域()
        {
            var graph = new ExtractionContentGraph();
            var task = new ArchiveTask(@"C:\in\A.7z", 1);

            graph.RegisterSourceRoot(task, "before", SourceHandlingMode.KeepInPlace);
            task.CurrentPath = @"C:\in\A(1).7z";
            graph.RegisterSourceRoot(task, "after", SourceHandlingMode.DeleteInPlace);

            Assert.Equal(1, graph.Count);
            Assert.True(graph.TryGetByRootId(Path.GetFullPath(@"C:\in\A.7z"), out SourceRootRecord root));
            Assert.Equal("after", root.PlacementKey);
            Assert.Equal(SourceHandlingMode.DeleteInPlace, root.SourceHandling);
            Assert.True(graph.TryGetByPath(@"C:\in\A(1).7z", out SourceRootRecord current));
            Assert.Equal(root.RootId, current.RootId);
        }

        [Fact]
        public void 清批_源根全部消失_不串下一批()
        {
            var graph = new ExtractionContentGraph();
            var task = new ArchiveTask(@"C:\in\A.7z", 1);

            graph.RegisterSourceRoot(task, "placement-A", SourceHandlingMode.KeepInPlace);
            graph.Clear();

            Assert.Equal(0, graph.Count);
            Assert.False(graph.TryGetSourceRoot(task, out _));
            Assert.False(graph.TryGetByPath(@"C:\in\A.7z", out _));
        }

        [Fact]
        public void 候选片发租约_普通产出登记不降级_清批释放()
        {
            var graph = new ExtractionContentGraph();
            string path = Path.GetFullPath(@"C:\out\W1\aaa-L4.zip.002");

            string lease = graph.LeaseCandidate(path, "root-1", "task-1", "placement-A");

            Assert.NotEmpty(lease);
            Assert.True(graph.IsLeased(path));
            Assert.True(graph.TryGetArtifact(path, out ProducedArtifactRecord candidate));
            Assert.Equal(ArtifactRole.CandidateVolumePiece, candidate.Role);
            Assert.Equal(lease, candidate.CandidateLeaseId);
            Assert.Equal(1, graph.ArtifactCount);

            graph.RegisterArtifact(
                path,
                "root-1",
                "task-2",
                ArtifactRole.Unknown,
                candidateLeaseId: null,
                placementKey: "placement-A");

            Assert.True(graph.IsLeased(path));
            Assert.True(graph.TryGetArtifact(path, out ProducedArtifactRecord stillLeased));
            Assert.Equal(lease, stillLeased.CandidateLeaseId);
            Assert.Equal(ArtifactRole.CandidateVolumePiece, stillLeased.Role);

            graph.Clear();

            Assert.False(graph.IsLeased(path));
            Assert.Equal(0, graph.ArtifactCount);
        }

        [Fact]
        public void 协调器登记源根后_第一大步账本项挂上源根和产生任务()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "archivefixer-graph-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(root);

            try
            {
                string path = Path.Combine(root, "A.7z");
                File.WriteAllText(path, "not a real archive; this test only checks provenance");

                RecursionFixHarness harness = RecursionFixHarness.Create(
                    root,
                    Array.Empty<string>(),
                    attemptLimit: 1);

                var task = new ArchiveTask(path, 1)
                {
                    SourceSelectionKind = SourceSelectionKind.Folder,
                    SourceSelectionRoot = root
                };

                Assert.Equal(1, harness.Coordinator.RegisterSourceRoots(new[] { task }, runOptions: null));

                FirstStepFileLedgerEntry entry = harness.Coordinator.RegisterFirstStepLedgerEntry(
                    task,
                    FileNature.Inspect(path));

                Assert.True(harness.Coordinator.ContentGraph.TryGetSourceRoot(task, out SourceRootRecord sourceRoot));
                Assert.Equal(sourceRoot.RootId, entry.OriginRootId);
                Assert.Equal(task.TaskId, entry.ProducedByTaskId);
                Assert.Equal(ArtifactRole.SourceRoot, entry.Role);
                Assert.False(entry.HasCandidateLease);
                Assert.Equal(sourceRoot.PlacementKey, entry.PlacementKey);
                Assert.NotEmpty(entry.PlacementKey);
                Assert.Single(harness.Coordinator.FirstStepLedger.ByOriginRoot(sourceRoot.RootId));
            }
            finally
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch
                {
                }
            }
        }

        [Fact]
        public void 账本项迁路径_来源根角色租约和落点域一个字不丢()
        {
            var entry = new FirstStepFileLedgerEntry
            {
                Path = @"C:\in\A.7z.001",
                BaseName = "A",
                Size = 10,
                OriginRootId = @"C:\in\A.7z.001",
                ProducedByTaskId = "task-1",
                Role = ArtifactRole.CandidateVolumePiece,
                CandidateLeaseId = "lease-1",
                PlacementKey = "placement-A",
                IsGatePassed = true
            };

            FirstStepFileLedgerEntry moved = entry.WithPath(@"C:\in\A-renamed.7z.001");

            Assert.Equal(@"C:\in\A-renamed.7z.001", moved.Path);
            Assert.Equal(entry.OriginRootId, moved.OriginRootId);
            Assert.Equal(entry.ProducedByTaskId, moved.ProducedByTaskId);
            Assert.Equal(ArtifactRole.CandidateVolumePiece, moved.Role);
            Assert.Equal("lease-1", moved.CandidateLeaseId);
            Assert.True(moved.HasCandidateLease);
            Assert.Equal("placement-A", moved.PlacementKey);

            var ledger = new FirstStepFileLedger();
            ledger.AddOrReplace(moved);

            Assert.Single(ledger.ByOriginRoot(entry.OriginRootId));
            Assert.Single(ledger.LeasedEntries());
            Assert.Empty(ledger.ByOriginRoot(" "));
        }
    }
}
