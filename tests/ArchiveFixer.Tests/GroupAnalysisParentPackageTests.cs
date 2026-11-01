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
    public sealed class GroupAnalysisParentPackageTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "af-parent-group-" + Guid.NewGuid().ToString("N"));

        public GroupAnalysisParentPackageTests()
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

            IReadOnlyList<GroupAnalysis.GroupCandidate> groups =
                GroupAnalysis.Analyze(handoffs, new[] { _root });

            GroupAnalysis.GroupCandidate? group = groups
                .FirstOrDefault(candidate => candidate.Pieces.Contains(part1));

            Assert.NotNull(group);
            Assert.DoesNotContain(parent, group!.Pieces);
            Assert.Equal(3, group.Pieces.Count);
            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Complete, group.Verdict);
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

            GroupAnalysis.GroupCandidate candidate = Assert.Single(
                GroupAnalysis.Analyze(handoffs, new[] { _root }));

            Assert.Equal(3, candidate.Pieces.Count);
            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Unknown, candidate.Verdict);
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

            IReadOnlyList<GroupAnalysis.GroupCandidate> groups =
                GroupAnalysis.Analyze(handoffs, new[] { _root });

            Assert.DoesNotContain(
                groups,
                candidate => candidate.Verdict == GroupAnalysis.GroupAnalysisVerdict.Complete);
        }

        private string Create(string name, int length)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllBytes(path, new byte[length]);
            return path;
        }

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
