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
        public void 交界登记内容组_接上来源根和共同落点域_同组可反复更新()
        {
            var ledger = new FirstStepFileLedger();

            for (int number = 1; number <= 3; number++)
            {
                ledger.AddOrReplace(new FirstStepFileLedgerEntry
                {
                    Path = $@"C:\out\part{number}\A.7z.{number:000}",
                    BaseName = "A",
                    Size = 10,
                    OriginRootId = $@"C:\in\root{number}.7z",
                    ProducedByTaskId = $"task-{number}",
                    PlacementKey = "placement-A",
                    Role = ArtifactRole.CandidateVolumePiece,
                    CandidateLeaseId = "lease-" + number
                });
            }

            var prepared = new PreparedVolumeGroup
            {
                BaseName = "A",
                Family = VolumeNamingFamily.SevenZipNumbered,
                OriginalSourceLayer = @"C:\in",
                OriginalEntryPath = @"C:\out\part1\A.7z.001",
                EntryCanonicalName = "A.7z.001",
                State = PreparedGroupState.Complete,
                Pieces = new[]
                {
                    new PreparedVolumePiece { CanonicalName = "A.7z.001", OriginalPath = @"C:\out\part1\A.7z.001" },
                    new PreparedVolumePiece { CanonicalName = "A.7z.002", OriginalPath = @"C:\out\part2\A.7z.002" },
                    new PreparedVolumePiece { CanonicalName = "A.7z.003", OriginalPath = @"C:\out\part3\A.7z.003" }
                }
            };

            var graph = new ExtractionContentGraph();
            ContentGroup group = graph.RegisterContentGroup(prepared, ledger);

            Assert.Equal(1, graph.GroupCount);
            Assert.Equal("A", group.BaseName);
            Assert.Equal(PreparedGroupState.Complete, group.State);
            Assert.Equal("placement-A", group.PlacementKey);
            Assert.Equal(3, group.SourceRootIds.Count);
            Assert.Equal(3, group.MemberPaths.Count);
            Assert.Contains("A.7z.001", group.Describe());

            group.AttachOwner("owner.7z");
            group.SetFinalOutput(
                @"C:\out\A",
                @"C:\out\A\content.bin",
                FinalContentState.Verified,
                fileCount: 1,
                bytes: 10,
                evidence: "L3");

            Assert.Equal("owner.7z", group.OwnerTaskFileName);
            Assert.True(group.FinalContentVerified);
            Assert.Equal(@"C:\out\A\content.bin", group.FinalContentPath);

            ContentGroup same = graph.RegisterContentGroup(prepared, ledger);

            Assert.Same(group, same);
            Assert.Equal("owner.7z", same.OwnerTaskFileName);

            var shrunk = new PreparedVolumeGroup
            {
                BaseName = "A",
                Family = VolumeNamingFamily.SevenZipNumbered,
                OriginalSourceLayer = @"C:\in",
                OriginalEntryPath = @"C:\out\part1\A.7z.001",
                State = PreparedGroupState.Unknown,
                Pieces = new[]
                {
                    new PreparedVolumePiece { CanonicalName = "A.7z.001", OriginalPath = @"C:\out\part1\A.7z.001" }
                }
            };

            ContentGroup stillComplete = graph.RegisterContentGroup(shrunk, ledger);

            Assert.Equal(PreparedGroupState.Complete, stillComplete.State);
            Assert.Equal(3, stillComplete.MemberPaths.Count);
        }

        [Fact]
        public void 内容组落点域冲突_不硬合并成共同落点()
        {
            var ledger = new FirstStepFileLedger();
            ledger.AddOrReplace(new FirstStepFileLedgerEntry
            {
                Path = @"C:\out\part1\A.7z.001",
                BaseName = "A",
                OriginRootId = @"C:\in\root1.7z",
                PlacementKey = "placement-A"
            });
            ledger.AddOrReplace(new FirstStepFileLedgerEntry
            {
                Path = @"C:\out\part2\A.7z.002",
                BaseName = "A",
                OriginRootId = @"C:\in\root2.7z",
                PlacementKey = "placement-B"
            });

            var prepared = new PreparedVolumeGroup
            {
                BaseName = "A",
                Family = VolumeNamingFamily.SevenZipNumbered,
                OriginalSourceLayer = @"C:\in",
                State = PreparedGroupState.Complete,
                Pieces = new[]
                {
                    new PreparedVolumePiece { CanonicalName = "A.7z.001", OriginalPath = @"C:\out\part1\A.7z.001" },
                    new PreparedVolumePiece { CanonicalName = "A.7z.002", OriginalPath = @"C:\out\part2\A.7z.002" }
                }
            };

            var graph = new ExtractionContentGraph();
            ContentGroup group = graph.RegisterContentGroup(prepared, ledger);

            Assert.True(group.HasPlacementConflict);
            Assert.Equal("placement-A", group.PlacementKey);
        }

        [Fact]
        public void 最终内容判决_取最深内容任务_空输出不判成功()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "archivefixer-final-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(root);

            try
            {
                string ownerRoot = Path.Combine(root, "out");
                string finalDirectory = Path.Combine(ownerRoot, "deep");
                string emptyRoot = Path.Combine(root, "empty");
                Directory.CreateDirectory(finalDirectory);
                Directory.CreateDirectory(emptyRoot);
                File.WriteAllText(Path.Combine(finalDirectory, "content.bin"), "ok");

                RecursionFixHarness harness = RecursionFixHarness.Create(
                    root,
                    Array.Empty<string>(),
                    attemptLimit: 1);

                var prepared = new PreparedVolumeGroup
                {
                    BaseName = "A",
                    Family = VolumeNamingFamily.SevenZipNumbered,
                    OriginalSourceLayer = root,
                    State = PreparedGroupState.Complete,
                    Pieces = Array.Empty<PreparedVolumePiece>()
                };

                ContentGroup group = harness.Coordinator.ContentGraph.RegisterContentGroup(
                    prepared,
                    harness.Coordinator.FirstStepLedger);

                var owner = new ArchiveTask(Path.Combine(root, "A.7z.001"), 1)
                {
                    OutputPath = ownerRoot,
                    ContentDirectoryPath = ownerRoot,
                    ContentFileCount = 1,
                    Outcome = TaskOutcome.Succeeded,
                    OutputVerification = OutputVerificationOutcome.Passed,
                    OutputManifestCrossChecked = true
                };

                var finalTask = new ArchiveTask(Path.Combine(root, "inner.7z"), 2)
                {
                    OutputPath = ownerRoot,
                    ContentDirectoryPath = finalDirectory,
                    ContentFileCount = 1,
                    Outcome = TaskOutcome.Succeeded,
                    OutputVerification = OutputVerificationOutcome.Passed,
                    OutputManifestCrossChecked = true
                };

                group.AttachOwner(owner.FileName, owner.CurrentPath);

                Assert.Equal(
                    1,
                    harness.Coordinator.FinalizeContentGroups(new[] { owner, finalTask }));

                Assert.Equal(FinalContentState.Verified, group.FinalState);
                Assert.True(group.FinalContentVerified);
                Assert.Equal(finalDirectory, group.FinalContentPath);
                Assert.Equal(ownerRoot, group.FinalOutputRoot);
                Assert.True(owner.IsContentGroupProcessTask);
                Assert.Equal(StatusText.ContentGroupProcessTask, owner.StatusDisplayText);
                Assert.Equal("—（组内过程任务）", owner.OutputDisplayPath);
                Assert.False(finalTask.IsContentGroupProcessTask);
                Assert.True(finalTask.FinalContentVerified);
                Assert.Equal(ownerRoot, finalTask.OutputDisplayPath);
                Assert.Equal(finalDirectory, finalTask.FinalContentPath);

                var emptyOwner = new ArchiveTask(Path.Combine(root, "B.7z.001"), 3)
                {
                    OutputPath = emptyRoot,
                    ContentDirectoryPath = emptyRoot,
                    ContentFileCount = 0,
                    Outcome = TaskOutcome.Succeeded,
                    OutputVerification = OutputVerificationOutcome.Passed,
                    OutputManifestCrossChecked = true
                };

                var emptyPrepared = new PreparedVolumeGroup
                {
                    BaseName = "B",
                    Family = VolumeNamingFamily.SevenZipNumbered,
                    OriginalSourceLayer = root,
                    State = PreparedGroupState.Complete,
                    Pieces = Array.Empty<PreparedVolumePiece>()
                };

                ContentGroup emptyGroup = harness.Coordinator.ContentGraph.RegisterContentGroup(
                    emptyPrepared,
                    harness.Coordinator.FirstStepLedger);

                emptyGroup.AttachOwner(emptyOwner.FileName, emptyOwner.CurrentPath);

                harness.Coordinator.FinalizeContentGroups(new[] { emptyOwner });

                Assert.Equal(FinalContentState.Missing, emptyGroup.FinalState);
                Assert.False(emptyGroup.FinalContentVerified);
                Assert.Equal(StatusText.ContentGroupMissing, emptyOwner.StatusDisplayText);
                Assert.Equal(StatusText.ExtractFailed, emptyOwner.StatusColorKey);
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
        public void 组最终内容已证_才按来源根删除_未证组一个字节不动()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "archivefixer-settle-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(root);

            try
            {
                string sourceA = Path.Combine(root, "A.7z.001");
                string sourceB = Path.Combine(root, "B.7z.001");
                File.WriteAllText(sourceA, "source-a");
                File.WriteAllText(sourceB, "source-b");

                RecursionFixHarness harness = RecursionFixHarness.Create(
                    root,
                    Array.Empty<string>(),
                    attemptLimit: 1);

                var taskA = new ArchiveTask(sourceA, 1)
                {
                    OutputPath = root,
                    SourcePackageMove = SourcePackageMoveState.DeferredToChainEnd
                };
                var taskB = new ArchiveTask(sourceB, 2)
                {
                    OutputPath = root,
                    SourcePackageMove = SourcePackageMoveState.DeferredToChainEnd
                };
                harness.Vm.Tasks.Add(taskA);
                harness.Vm.Tasks.Add(taskB);

                harness.Coordinator.FirstStepLedger.AddOrReplace(new FirstStepFileLedgerEntry
                {
                    Path = sourceA,
                    BaseName = "A",
                    OriginRootId = sourceA,
                    PlacementKey = "placement-A"
                });
                harness.Coordinator.FirstStepLedger.AddOrReplace(new FirstStepFileLedgerEntry
                {
                    Path = sourceB,
                    BaseName = "B",
                    OriginRootId = sourceB,
                    PlacementKey = "placement-A"
                });

                ContentGroup verified = harness.Coordinator.ContentGraph.RegisterContentGroup(
                    new PreparedVolumeGroup
                    {
                        BaseName = "A",
                        Family = VolumeNamingFamily.SevenZipNumbered,
                        OriginalSourceLayer = root,
                        State = PreparedGroupState.Complete,
                        Pieces = new[]
                        {
                            new PreparedVolumePiece { CanonicalName = "A.7z.001", OriginalPath = sourceA }
                        }
                    },
                    harness.Coordinator.FirstStepLedger);

                verified.SetFinalOutput(root, root, FinalContentState.Verified, 1, 1, "test");

                ContentGroup partial = harness.Coordinator.ContentGraph.RegisterContentGroup(
                    new PreparedVolumeGroup
                    {
                        BaseName = "B",
                        Family = VolumeNamingFamily.SevenZipNumbered,
                        OriginalSourceLayer = root,
                        State = PreparedGroupState.Complete,
                        Pieces = new[]
                        {
                            new PreparedVolumePiece { CanonicalName = "B.7z.001", OriginalPath = sourceB }
                        }
                    },
                    harness.Coordinator.FirstStepLedger);

                partial.SetFinalOutput(root, root, FinalContentState.Partial, 0, 0, "test");

                int deleted = harness.Coordinator.SettleVerifiedContentGroupSources(
                    new OneClickRunOptions { SourceHandling = SourceHandlingMode.DeleteInPlace });

                Assert.True(
                    deleted == 1,
                    $"deleted={deleted}; AExists={File.Exists(sourceA)}; BExists={File.Exists(sourceB)};"
                    + $" moveA={taskA.SourcePackageMove}; moveB={taskB.SourcePackageMove};"
                    + $" logs={string.Join(" || ", harness.LogTexts)}");

                Assert.False(File.Exists(sourceA));
                Assert.True(File.Exists(sourceB));
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
        public void 临时入口根缺失_不回落盘根()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "archivefixer-entryroot-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(root);

            try
            {
                string piecePath = Path.Combine(root, "A.7z.001");
                File.WriteAllText(piecePath, "piece");

                var group = new PreparedVolumeGroup
                {
                    BaseName = "A",
                    Family = VolumeNamingFamily.SevenZipNumbered,
                    OriginalSourceLayer = root,
                    EntryCanonicalName = "A.7z.001",
                    State = PreparedGroupState.Complete,
                    Pieces = new[]
                    {
                        new PreparedVolumePiece { CanonicalName = "A.7z.001", OriginalPath = piecePath }
                    }
                };

                PreparedVolumeGroup? materialized = VolumeGroupEntryDirectory.TryMaterialize(
                    group,
                    entryRoot: null,
                    log: null,
                    out string error);

                Assert.Null(materialized);
                Assert.Contains("不回落到盘根", error);
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
