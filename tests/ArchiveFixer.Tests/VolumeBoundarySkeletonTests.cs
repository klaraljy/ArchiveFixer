using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **共用骨架的守门用例**（2026-10-27 第一段）：
    /// 账本去重、族仲裁、边界管线空跑、标准名映射、临时入口目录硬链接与清理。
    ///
    /// <para>这一组只验骨架，不接任何族算法；⛔ 不碰真样本、不调引擎。</para>
    /// </summary>
    public sealed class VolumeBoundarySkeletonTests
    {
        [Fact]
        public void 账本_同基名收齐_同路径去重_按基名列出()
        {
            var ledger = new FirstStepFileLedger();

            ledger.AddOrReplace(Entry(@"C:\in\111.7z.001", "111", VolumeContentFormat.SevenZip));
            ledger.AddOrReplace(Entry(@"C:\in\111.7z.002", "111", VolumeContentFormat.Unknown));
            ledger.AddOrReplace(Entry(@"C:\in\222.7z.001", "222", VolumeContentFormat.SevenZip));

            // 同一路径登记第二次：保留后到的那一项，不新增。
            ledger.AddOrReplace(Entry(@"C:\in\111.7z.001", "111", VolumeContentFormat.SevenZip, size: 99));

            Assert.Equal(3, ledger.Count);
            Assert.Equal(2, ledger.SameBase("111").Count);
            Assert.Single(ledger.SameBase("222"));
            Assert.Equal(new[] { "111", "222" }, ledger.BaseNames());
        }

        [Fact]
        public void 族仲裁_七z首片_RAR成员_跨盘末片_通用末片()
        {
            Assert.Equal(
                VolumeNamingFamily.SevenZipNumbered,
                VolumeFamilyArbiter.Select(
                    Ledger(Entry(@"C:\in\111.7z.001", "111", VolumeContentFormat.SevenZip, body: ArchiveBodyKind.VolumeFirstPiece)),
                    "111").Family);

            Assert.Equal(
                VolumeNamingFamily.RarPart,
                VolumeFamilyArbiter.Select(
                    Ledger(Entry(
                        @"C:\in\111.part1.rar",
                        "111",
                        VolumeContentFormat.Rar,
                        membership: VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember)),
                    "111").Family);

            Assert.Equal(
                VolumeNamingFamily.ZipSpanned,
                VolumeFamilyArbiter.Select(
                    Ledger(Entry(
                        @"C:\in\111.zip",
                        "111",
                        VolumeContentFormat.Zip,
                        membership: VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember,
                        number: 3)),
                    "111").Family);

            Assert.Equal(
                VolumeNamingFamily.ZipNumbered,
                VolumeFamilyArbiter.Select(
                    Ledger(Entry(
                        @"C:\in\111.zip.003",
                        "111",
                        VolumeContentFormat.Zip,
                        hasLocalHeader: true,
                        zipEndMeansSplit: true)),
                    "111").Family);
        }

        [Fact]
        public void 族仲裁_同基名同时出现七z与RAR_冲突不混组()
        {
            var ledger = new FirstStepFileLedger();

            ledger.AddOrReplace(Entry(
                @"C:\in\111.7z.001",
                "111",
                VolumeContentFormat.SevenZip,
                body: ArchiveBodyKind.VolumeFirstPiece));

            ledger.AddOrReplace(Entry(
                @"C:\in\111.part1.rar",
                "111",
                VolumeContentFormat.Rar,
                membership: VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember));

            VolumeFamilySelection selection = VolumeFamilyArbiter.Select(ledger, "111");

            Assert.True(selection.Conflict);
            Assert.Null(selection.Family);
        }

        [Fact]
        public async Task 边界管线_没有resolver时为空_有resolver时按族调用()
        {
            var ledger = new FirstStepFileLedger();

            ledger.AddOrReplace(Entry(
                @"C:\in\111.7z.001",
                "111",
                VolumeContentFormat.SevenZip,
                body: ArchiveBodyKind.VolumeFirstPiece));

            var empty = new VolumeBoundaryPipeline();

            Assert.Empty(await empty.ResolveAllAsync(
                ledger,
                baseName => new VolumeBoundaryContext { BaseName = baseName }));

            var pipeline = new VolumeBoundaryPipeline();
            pipeline.Register(new FakeResolver());

            IReadOnlyList<PreparedVolumeGroup> groups = await pipeline.ResolveAllAsync(
                ledger,
                baseName => new VolumeBoundaryContext { BaseName = baseName });

            PreparedVolumeGroup group = Assert.Single(groups);

            Assert.Equal("111", group.BaseName);
            Assert.Equal(VolumeNamingFamily.SevenZipNumbered, group.Family);
            Assert.Equal(PreparedGroupState.Complete, group.State);
        }

        [Fact]
        public void PreparedGroup_标准名到原始路径的映射()
        {
            var group = new PreparedVolumeGroup
            {
                BaseName = "111",
                Family = VolumeNamingFamily.SevenZipNumbered,
                OriginalEntryPath = @"C:\in\111.7z.001",
                EntryCanonicalName = "111.7z.001",
                State = PreparedGroupState.Complete,
                Pieces = new[]
                {
                    new PreparedVolumePiece
                    {
                        CanonicalName = "111.7z.001",
                        OriginalPath = @"C:\in\111.7z.001",
                        Size = 10
                    },
                    new PreparedVolumePiece
                    {
                        CanonicalName = "111.7z.002",
                        OriginalPath = @"C:\in\111.7z.002",
                        Size = 5
                    }
                }
            };

            IReadOnlyDictionary<string, string> map = group.BuildOriginalMap();

            Assert.Equal(@"C:\in\111.7z.001", map["111.7z.001"]);
            Assert.Equal(@"C:\in\111.7z.002", map["111.7z.002"]);
        }

        [Fact]
        public void GroupAnalysis_吃prepared组_只收标准名_不扫用户源目录()
        {
            string root = Path.Combine(Path.GetTempPath(), "af-prepared-group-" + Guid.NewGuid().ToString("N"));

            try
            {
                string entryLayer = Path.Combine(root, "entry");
                Directory.CreateDirectory(entryLayer);

                string canonical = Path.Combine(entryLayer, "111.7z.001");
                File.WriteAllBytes(canonical, new byte[] { 1, 2, 3 });

                var group = new PreparedVolumeGroup
                {
                    BaseName = "111",
                    Family = VolumeNamingFamily.SevenZipNumbered,
                    OriginalEntryPath = Path.Combine(root, "src", "111.7z.001"),
                    EntryCanonicalName = "111.7z.001",
                    EntryLayer = entryLayer,
                    State = PreparedGroupState.Complete,
                    Pieces = new[]
                    {
                        new PreparedVolumePiece
                        {
                            CanonicalName = "111.7z.001",
                            OriginalPath = Path.Combine(root, "src", "111.7z.001"),
                            Size = 3
                        }
                    }
                };

                GroupAnalysis.GroupCandidate candidate = Assert.Single(
                    GroupAnalysis.AnalyzePrepared(new[] { group }));

                Assert.Equal("111", candidate.BaseName);
                Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Complete, candidate.SuggestedVerdict);
                Assert.Equal(canonical, candidate.EntryPath);
                Assert.Equal(group.OriginalEntryPath, candidate.OriginalEntryPath);
                Assert.Same(group, candidate.PreparedGroup);
                Assert.Single(candidate.Pieces);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }

        [Fact]
        public async Task 七z_resolver_标准三片_字节和与中间片大小对上_Complete()
        {
            var ledger = new FirstStepFileLedger();

            ledger.AddOrReplace(SevenZipEntry(@"C:\in\111.7z.001", size: 100, declaredBytes: 250, declaredCount: 3, first: true));
            ledger.AddOrReplace(SevenZipEntry(@"C:\in\111.7z.002", size: 100));
            ledger.AddOrReplace(SevenZipEntry(@"C:\in\111.7z.003", size: 50));

            PreparedVolumeGroup? group = await new SevenZipBoundaryResolver().ResolveAsync(
                new VolumeBoundaryContext { Ledger = ledger, BaseName = "111" });

            Assert.NotNull(group);
            Assert.Equal(PreparedGroupState.Complete, group!.State);
            Assert.Equal(3, group.Pieces.Count);
            Assert.Equal("111.7z.001", group.EntryCanonicalName);
            Assert.Equal(@"C:\in\111.7z.001", group.OriginalEntryPath);
        }

        [Fact]
        public async Task 七z_resolver_数量少于自述_SizeAnomaly()
        {
            var ledger = new FirstStepFileLedger();

            ledger.AddOrReplace(SevenZipEntry(@"C:\in\111.7z.001", size: 100, declaredBytes: 250, declaredCount: 3, first: true));
            ledger.AddOrReplace(SevenZipEntry(@"C:\in\111.7z.002", size: 100));

            PreparedVolumeGroup? group = await new SevenZipBoundaryResolver().ResolveAsync(
                new VolumeBoundaryContext { Ledger = ledger, BaseName = "111" });

            Assert.NotNull(group);
            Assert.Equal(PreparedGroupState.SizeAnomaly, group!.State);
        }

        [Fact]
        public async Task 七z_resolver_字节和不等于自述_SizeAnomaly()
        {
            var ledger = new FirstStepFileLedger();

            ledger.AddOrReplace(SevenZipEntry(@"C:\in\111.7z.001", size: 100, declaredBytes: 250, declaredCount: 3, first: true));
            ledger.AddOrReplace(SevenZipEntry(@"C:\in\111.7z.002", size: 100));
            ledger.AddOrReplace(SevenZipEntry(@"C:\in\111.7z.003", size: 49));

            PreparedVolumeGroup? group = await new SevenZipBoundaryResolver().ResolveAsync(
                new VolumeBoundaryContext { Ledger = ledger, BaseName = "111" });

            Assert.NotNull(group);
            Assert.Equal(PreparedGroupState.SizeAnomaly, group!.State);
        }

        [Fact]
        public async Task 七z_resolver_中间片大小不等于首卷_SizeAnomaly()
        {
            var ledger = new FirstStepFileLedger();

            ledger.AddOrReplace(SevenZipEntry(@"C:\in\111.7z.001", size: 100, declaredBytes: 250, declaredCount: 3, first: true));
            ledger.AddOrReplace(SevenZipEntry(@"C:\in\111.7z.002", size: 99));
            ledger.AddOrReplace(SevenZipEntry(@"C:\in\111.7z.003", size: 51));

            PreparedVolumeGroup? group = await new SevenZipBoundaryResolver().ResolveAsync(
                new VolumeBoundaryContext { Ledger = ledger, BaseName = "111" });

            Assert.NotNull(group);
            Assert.Equal(PreparedGroupState.SizeAnomaly, group!.State);
        }

        [Fact]
        public void VolumeGroupService_prepared组_直接登记结论_不再调族算法()
        {
            var service = new VolumeGroupService();

            var prepared = new PreparedVolumeGroup
            {
                BaseName = "111",
                Family = VolumeNamingFamily.SevenZipNumbered,
                OriginalSourceLayer = @"C:\in",
                OriginalEntryPath = @"C:\in\111.7z.001",
                EntryCanonicalName = "111.7z.001",
                EntryLayer = @"C:\tmp\entry\111",
                State = PreparedGroupState.Complete,
                Pieces = new[]
                {
                    new PreparedVolumePiece
                    {
                        CanonicalName = "111.7z.001",
                        OriginalPath = @"C:\in\111.7z.001",
                        Size = 10
                    }
                }
            };

            VolumeGroupUnit? unit = service.TryCommitPrepared(prepared, "111.7z.001", @"C:\out");

            Assert.NotNull(unit);
            Assert.Equal(VolumeGroupState.OwnerAssigned, unit!.State);
            Assert.Equal("111.7z.001", unit.OwnerTaskFileName);
            Assert.Single(unit.OrderedPieces);

            VolumeGroupUnit? same = service.TryCommitPrepared(prepared, "another", @"C:\out");
            Assert.Same(unit, same);
        }

        [Fact]
        public void 临时入口目录_只建硬链接_源文件不变_跑完清理()
        {
            string root = Path.Combine(Path.GetTempPath(), "af-volume-entry-" + Guid.NewGuid().ToString("N"));

            try
            {
                string sourceDirectory = Path.Combine(root, "src");
                Directory.CreateDirectory(sourceDirectory);

                string original = Path.Combine(sourceDirectory, "111.7z.001");
                byte[] content = { 1, 2, 3, 4, 5 };
                File.WriteAllBytes(original, content);

                string entryRoot = Path.Combine(root, ".ArchiveFixer.work", "group-entry");
                Directory.CreateDirectory(entryRoot);

                var group = new PreparedVolumeGroup
                {
                    BaseName = "111",
                    Family = VolumeNamingFamily.SevenZipNumbered,
                    OriginalSourceLayer = sourceDirectory,
                    OriginalEntryPath = original,
                    EntryCanonicalName = "111.7z.001",
                    State = PreparedGroupState.Complete,
                    Pieces = new[]
                    {
                        new PreparedVolumePiece
                        {
                            CanonicalName = "111.7z.001",
                            OriginalPath = original,
                            Size = content.Length
                        }
                    }
                };

                PreparedVolumeGroup? materialized = VolumeGroupEntryDirectory.TryMaterialize(
                    group,
                    entryRoot,
                    null,
                    out string error);

                Assert.True(materialized != null, error);
                Assert.NotNull(materialized);
                Assert.True(Directory.Exists(materialized!.EntryLayer));

                string link = Path.Combine(materialized.EntryLayer, "111.7z.001");
                Assert.True(File.Exists(link));
                Assert.Equal(content, File.ReadAllBytes(link));
                Assert.Equal(content, File.ReadAllBytes(original));

                Assert.True(VolumeGroupEntryDirectory.TryDelete(materialized.EntryLayer, entryRoot));
                Assert.False(Directory.Exists(materialized.EntryLayer));

                // 源文件一个字节都不许少。
                Assert.True(File.Exists(original));
                Assert.Equal(content, File.ReadAllBytes(original));
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root))
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }

        private static FirstStepFileLedger Ledger(params FirstStepFileLedgerEntry[] entries)
        {
            var ledger = new FirstStepFileLedger();

            foreach (FirstStepFileLedgerEntry entry in entries)
            {
                ledger.AddOrReplace(entry);
            }

            return ledger;
        }

        private static FirstStepFileLedgerEntry SevenZipEntry(
            string path,
            long size,
            long declaredBytes = 0,
            int declaredCount = 0,
            bool first = false)
        {
            return new FirstStepFileLedgerEntry
            {
                Path = path,
                BaseName = "111",
                Size = size,
                Format = VolumeContentFormat.SevenZip,
                BodyKind = first ? ArchiveBodyKind.VolumeFirstPiece : ArchiveBodyKind.Unknown,
                Membership = VolumeNumberFromContent.VolumeMembershipVerdict.Unknown,
                DeclaredTotalBytes = declaredBytes,
                DeclaredVolumeCount = declaredCount,
                SourceLayer = Path.GetDirectoryName(path) ?? string.Empty,
                IsGatePassed = true
            };
        }

        private static FirstStepFileLedgerEntry Entry(
            string path,
            string baseName,
            VolumeContentFormat format,
            long size = 10,
            ArchiveBodyKind body = ArchiveBodyKind.Unknown,
            VolumeNumberFromContent.VolumeMembershipVerdict membership =
                VolumeNumberFromContent.VolumeMembershipVerdict.Unknown,
            int? number = null,
            bool hasLocalHeader = false,
            bool zipEndMeansSplit = false)
        {
            return new FirstStepFileLedgerEntry
            {
                Path = path,
                BaseName = baseName,
                Size = size,
                Format = format,
                BodyKind = body,
                Membership = membership,
                VolumeNumber = number,
                HasZipLocalHeader = hasLocalHeader,
                ZipEndRecordMeansSplit = zipEndMeansSplit,
                SourceLayer = Path.GetDirectoryName(path) ?? string.Empty,
                IsGatePassed = true
            };
        }

        private sealed class FakeResolver : IVolumeBoundaryResolver
        {
            public VolumeNamingFamily Family => VolumeNamingFamily.SevenZipNumbered;

            public Task<PreparedVolumeGroup?> ResolveAsync(
                VolumeBoundaryContext context,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult<PreparedVolumeGroup?>(new PreparedVolumeGroup
                {
                    BaseName = context.BaseName,
                    Family = Family,
                    State = PreparedGroupState.Complete,
                    Evidence = "fake"
                });
            }
        }
    }
}
