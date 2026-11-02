using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 2026-11-01 真机：父包（`2.exe`，完整 RAR5）与它解出来的 `2.part1/2/3.rar` 基名同为 "2"，
    /// 旧逻辑只比基名+族 ⇒ 父包被收进子分卷组，组永远判不出、过程物也删不掉。
    ///
    /// <para>守门三件事：① 自证完整单卷的父包绝不当分卷成员；② 有片没有卷号/名字槽
    /// 就不许判 `Complete`（真机 `2` 组是 4 片只有 2 个槽、却被判 Complete）；
    /// ③ 普通单卷 zip 不许再出 `名字卷标记连成 1..1` 的假分卷组。</para>
    /// </summary>
    public sealed class VolumeCandidateDiscoveryParentPackageTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "af-parent-group-" + Guid.NewGuid().ToString("N"));

        public VolumeCandidateDiscoveryParentPackageTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // 临时目录清不掉不影响结论。
            }
        }

        [Fact]
        public void 自证完整的父包_不得混进子分卷组()
        {
            string parent = Create("2.exe", 100);
            string part1 = Create("2.part1.rar", 500);
            string part2 = Create("2.part2.rar", 500);
            string part3 = Create("2.part3.rar", 50);

            var handoffs = new[]
            {
                new VolumeHandoffEntry
                {
                    Path = parent,
                    BaseName = "2",
                    Format = VolumeContentFormat.Rar,
                    Size = 100,
                    BodyKind = ArchiveBodyKind.SingleComplete,
                    Membership = VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember,
                    ContentFail = VolumeNumberFail.None,
                    SourceLayer = _root,
                    Source = VolumeHandoffSource.UserImported
                },
                RarPart(part1, 500, 1, firstPiece: true),
                RarPart(part2, 500, 2),
                RarPart(part3, 50, 3)
            };

            IReadOnlyList<VolumeCandidateDiscovery.VolumeCandidate> groups =
                VolumeCandidateDiscovery.Analyze(handoffs, new[] { _root });

            VolumeCandidateDiscovery.VolumeCandidate? group = groups
                .FirstOrDefault(candidate => candidate.Pieces.Contains(part1));

            Assert.NotNull(group);
            Assert.DoesNotContain(parent, group!.Pieces);
            Assert.Equal(3, group.Pieces.Count);
            Assert.Equal(VolumeCandidateDiscovery.VolumeCandidateVerdict.Complete, group.Verdict);
        }

        [Fact]
        public void 有片没有卷号或名字槽_不许判Complete()
        {
            string first = Create("组.7z.001", 1000);
            string second = Create("组.7z.002", 200);
            string extra = Create("组.bin", 10);

            var handoffs = new[]
            {
                SevenZipPart(first, 1000, 1, firstPiece: true),
                SevenZipPart(second, 200, 2),
                new VolumeHandoffEntry
                {
                    Path = extra,
                    BaseName = "组",
                    Format = VolumeContentFormat.Unknown,
                    Size = 10,
                    BodyKind = ArchiveBodyKind.Unknown,
                    Membership = VolumeNumberFromContent.VolumeMembershipVerdict.Unknown,
                    ContentFail = VolumeNumberFail.None,
                    SourceLayer = _root,
                    Source = VolumeHandoffSource.UserImported
                }
            };

            VolumeCandidateDiscovery.VolumeCandidate candidate = Assert.Single(
                VolumeCandidateDiscovery.Analyze(handoffs, new[] { _root }));

            Assert.Equal(3, candidate.Pieces.Count);
            Assert.Equal(VolumeCandidateDiscovery.VolumeCandidateVerdict.Unknown, candidate.Verdict);
            Assert.Contains("没有卷号", candidate.Evidence, StringComparison.Ordinal);
        }

        [Fact]
        public void 普通单卷zip_不许再出1片完整分卷组()
        {
            string zip = Create("上老王论坛当老王.zip", 50);

            var handoffs = new[]
            {
                new VolumeHandoffEntry
                {
                    Path = zip,
                    BaseName = "上老王论坛当老王",
                    Format = VolumeContentFormat.Zip,
                    Size = 50,
                    BodyKind = ArchiveBodyKind.SingleComplete,
                    Membership = VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember,
                    ContentFail = VolumeNumberFail.None,
                    SourceLayer = _root,
                    Source = VolumeHandoffSource.UserImported
                }
            };

            IReadOnlyList<VolumeCandidateDiscovery.VolumeCandidate> groups =
                VolumeCandidateDiscovery.Analyze(handoffs, new[] { _root });

            Assert.DoesNotContain(
                groups,
                candidate => candidate.Verdict == VolumeCandidateDiscovery.VolumeCandidateVerdict.Complete);
        }

        [Fact]
        public void 根任务源包_内容卡Unknown_也要按来源排除出子分卷组()
        {
            string parent = Create("2.exe", 100);
            string part1 = Create("2.part1.rar", 500);
            string part2 = Create("2.part2.rar", 500);
            string part3 = Create("2.part3.rar", 50);

            var handoffs = new[]
            {
                new VolumeHandoffEntry
                {
                    Path = parent,
                    BaseName = "2",
                    Format = VolumeContentFormat.Unknown,
                    Size = 100,
                    BodyKind = ArchiveBodyKind.Unknown,
                    Membership = VolumeNumberFromContent.VolumeMembershipVerdict.Unknown,
                    ContentFail = VolumeNumberFail.None,
                    SourceLayer = _root,
                    Source = VolumeHandoffSource.UserImported
                },
                RarPart(part1, 500, 1, firstPiece: true),
                RarPart(part2, 500, 2),
                RarPart(part3, 50, 3)
            };

            IReadOnlyList<VolumeCandidateDiscovery.VolumeCandidate> groups =
                VolumeCandidateDiscovery.Analyze(handoffs, new[] { _root }, extraSeeds: null, excludedSources: new[] { parent });

            VolumeCandidateDiscovery.VolumeCandidate? group = groups
                .FirstOrDefault(candidate => candidate.Pieces.Contains(part1));

            Assert.NotNull(group);
            Assert.DoesNotContain(parent, group!.Pieces);
            Assert.Equal(3, group.Pieces.Count);
            Assert.Equal(VolumeCandidateDiscovery.VolumeCandidateVerdict.Complete, group.Verdict);
        }

        [Fact]
        public void 裸rar父包_卷号器会给slot1_也不许混进子分卷组()
        {
            string parent = Create("2.rar", 100);
            string part1 = Create("2.part1.rar", 500);
            string part2 = Create("2.part2.rar", 500);
            string part3 = Create("2.part3.rar", 50);

            var handoffs = new[]
            {
                new VolumeHandoffEntry
                {
                    Path = parent,
                    BaseName = "2",
                    Format = VolumeContentFormat.Rar,
                    Size = 100,
                    BodyKind = ArchiveBodyKind.SingleComplete,
                    Membership = VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember,
                    ContentFail = VolumeNumberFail.None,
                    // 真机现场：裸 `.rar` 被卷号器当成第 1 卷（slot=1）。
                    NameVolumeIndex = 1,
                    SourceLayer = _root,
                    Source = VolumeHandoffSource.UserImported
                },
                RarPart(part1, 500, 1, firstPiece: true),
                RarPart(part2, 500, 2),
                RarPart(part3, 50, 3)
            };

            IReadOnlyList<VolumeCandidateDiscovery.VolumeCandidate> groups =
                VolumeCandidateDiscovery.Analyze(handoffs, new[] { _root });

            VolumeCandidateDiscovery.VolumeCandidate? group = groups
                .FirstOrDefault(candidate => candidate.Pieces.Contains(part1));

            Assert.NotNull(group);
            Assert.DoesNotContain(parent, group!.Pieces);
            Assert.Equal(3, group.Pieces.Count);
            Assert.Equal(VolumeCandidateDiscovery.VolumeCandidateVerdict.Complete, group.Verdict);
        }

        [Fact]
        public void 首片自述整包_按字节补齐配出同目录唯一尾片()
        {
            string first = Create("风景01.mp4", 1000);
            string tail = Create("风景02.mp4", 200);

            var handoffs = new[]
            {
                SevenZipFirst(first, 1000, 1200, "风景01"),
                UnknownTail(tail, 200, "风景02")
            };

            VolumeCandidateDiscovery.VolumeCandidate candidate = Assert.Single(
                VolumeCandidateDiscovery.Analyze(handoffs, new[] { _root }));

            Assert.Equal(VolumeCandidateDiscovery.VolumeCandidateVerdict.Complete, candidate.Verdict);
            Assert.Equal(new[] { first, tail }, candidate.Pieces.ToArray());
            Assert.Equal(first, candidate.EntryPath);
            Assert.Contains("自述整包对账", candidate.Evidence, StringComparison.Ordinal);
        }

        [Fact]
        public void 两个目录各自字节补齐_配出两组而不是并成一组()
        {
            string dirA = Path.Combine(_root, "2916");
            string dirB = Path.Combine(_root, "3100");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);

            string a1 = CreateIn(dirA, "风景01.mp4", 1000);
            string a2 = CreateIn(dirA, "风景02.mp4", 200);
            string b1 = CreateIn(dirB, "风景01.mp4", 500);
            string b2 = CreateIn(dirB, "风景02.mp4", 100);

            var handoffs = new[]
            {
                SevenZipFirst(a1, 1000, 1200, "风景01"),
                UnknownTail(a2, 200, "风景02"),
                SevenZipFirst(b1, 500, 600, "风景01"),
                UnknownTail(b2, 100, "风景02")
            };

            IReadOnlyList<VolumeCandidateDiscovery.VolumeCandidate> groups =
                VolumeCandidateDiscovery.Analyze(handoffs, new[] { _root });

            Assert.Equal(2, groups.Count);
            Assert.All(groups, group => Assert.Equal(VolumeCandidateDiscovery.VolumeCandidateVerdict.Complete, group.Verdict));
            Assert.Contains(groups, group => group.Pieces.Contains(a1) && group.Pieces.Contains(a2));
            Assert.Contains(groups, group => group.Pieces.Contains(b1) && group.Pieces.Contains(b2));
        }

        private string Create(string name, int length)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllBytes(path, new byte[length]);
            return path;
        }

        private static string CreateIn(string directory, string name, int length)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, name);
            File.WriteAllBytes(path, new byte[length]);
            return path;
        }

        private VolumeHandoffEntry SevenZipFirst(string path, long size, long declaredTotal, string baseName) =>
            new()
            {
                Path = path,
                BaseName = baseName,
                Format = VolumeContentFormat.SevenZip,
                Size = size,
                BodyKind = ArchiveBodyKind.VolumeFirstPiece,
                FirstVolumeFlag = true,
                Membership = VolumeNumberFromContent.VolumeMembershipVerdict.Unknown,
                ContentFail = VolumeNumberFail.None,
                DeclaredTotalBytes = declaredTotal,
                SourceLayer = Path.GetDirectoryName(path) ?? string.Empty,
                Source = VolumeHandoffSource.UserImported
            };

        private VolumeHandoffEntry UnknownTail(string path, long size, string baseName) =>
            new()
            {
                Path = path,
                BaseName = baseName,
                Format = VolumeContentFormat.Unknown,
                Size = size,
                BodyKind = ArchiveBodyKind.Unknown,
                Membership = VolumeNumberFromContent.VolumeMembershipVerdict.Unknown,
                ContentFail = VolumeNumberFail.None,
                SourceLayer = Path.GetDirectoryName(path) ?? string.Empty,
                Source = VolumeHandoffSource.UserImported
            };

        private VolumeHandoffEntry RarPart(string path, long size, int slot, bool firstPiece = false) =>
            new()
            {
                Path = path,
                BaseName = "2",
                Format = VolumeContentFormat.Rar,
                Size = size,
                BodyKind = firstPiece ? ArchiveBodyKind.VolumeFirstPiece : ArchiveBodyKind.VolumePiece,
                FirstVolumeFlag = firstPiece,
                NameVolumeIndex = slot,
                Membership = VolumeNumberFromContent.VolumeMembershipVerdict.Unknown,
                ContentFail = VolumeNumberFail.None,
                SourceLayer = _root,
                Source = VolumeHandoffSource.UserImported
            };

        private VolumeHandoffEntry SevenZipPart(string path, long size, int slot, bool firstPiece = false) =>
            new()
            {
                Path = path,
                BaseName = "组",
                Format = VolumeContentFormat.SevenZip,
                Size = size,
                BodyKind = firstPiece ? ArchiveBodyKind.VolumeFirstPiece : ArchiveBodyKind.VolumePiece,
                FirstVolumeFlag = firstPiece,
                NameVolumeIndex = slot,
                Membership = VolumeNumberFromContent.VolumeMembershipVerdict.Unknown,
                ContentFail = VolumeNumberFail.None,
                SourceLayer = _root,
                Source = VolumeHandoffSource.UserImported
            };
    }
}
