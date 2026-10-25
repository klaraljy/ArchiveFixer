using System;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 非均匀 7z：首卷可以比后续卷更大；片顺序必须由入口/名字决定，不能按大小排。
    /// </summary>
    public sealed class GroupAnalysisNonUniformTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "af-nonuniform-" + Guid.NewGuid().ToString("N"));

        public GroupAnalysisNonUniformTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch
            {
            }
        }

        [Fact]
        public void 七z首卷更大_片顺序仍是001再002()
        {
            string first = Create("组.7z.001", 1000);
            string second = Create("组.7z.002", 200);

            var handoffs = new[]
            {
                Handoff(first, 1000, firstPiece: true, declaredCount: 2, declaredTotal: 1200),
                Handoff(second, 200)
            };

            GroupAnalysis.GroupCandidate candidate = Assert.Single(
                GroupAnalysis.Analyze(handoffs, new[] { _root }));

            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Complete, candidate.Verdict);
            Assert.Equal(new[] { first, second }, candidate.Pieces.ToArray());
            Assert.Equal(first, candidate.EntryPath);
        }

        [Fact]
        public void 同名同族但在不同目录_必须分成两组()
        {
            string dirA = Path.Combine(_root, "a");
            string dirB = Path.Combine(_root, "b");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);

            string a1 = CreateIn(dirA, "组.7z.001", 1000);
            string a2 = CreateIn(dirA, "组.7z.002", 200);
            string b1 = CreateIn(dirB, "组.7z.001", 500);
            string b2 = CreateIn(dirB, "组.7z.002", 100);

            var handoffs = new[]
            {
                Handoff(a1, 1000, firstPiece: true, declaredCount: 2, declaredTotal: 1200),
                Handoff(a2, 200),
                Handoff(b1, 500, firstPiece: true, declaredCount: 2, declaredTotal: 600),
                Handoff(b2, 100)
            };

            IReadOnlyList<GroupAnalysis.GroupCandidate> groups =
                GroupAnalysis.Analyze(handoffs, new[] { _root });

            Assert.Equal(2, groups.Count);
            Assert.All(groups, group => Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Complete, group.Verdict));
            Assert.Contains(groups, group => group.Pieces.Contains(a1) && group.Pieces.Contains(a2));
            Assert.Contains(groups, group => group.Pieces.Contains(b1) && group.Pieces.Contains(b2));
        }

        private string Create(string name, int length) => CreateIn(_root, name, length);

        private static string CreateIn(string directory, string name, int length)
        {
            string path = Path.Combine(directory, name);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(path, new byte[length]);
            return path;
        }

        private static VolumeHandoffEntry Handoff(
            string path,
            long size,
            bool firstPiece = false,
            int declaredCount = 0,
            long declaredTotal = 0) =>
            new()
            {
                Path = path,
                BaseName = "组",
                Format = VolumeContentFormat.SevenZip,
                Size = size,
                BodyKind = firstPiece ? ArchiveBodyKind.VolumeFirstPiece : ArchiveBodyKind.VolumePiece,
                FirstVolumeFlag = firstPiece,
                Membership = VolumeNumberFromContent.VolumeMembershipVerdict.Unknown,
                ContentFail = VolumeNumberFail.None,
                DeclaredTotalBytes = declaredTotal,
                DeclaredVolumeCount = declaredCount,
                SourceLayer = Path.GetDirectoryName(path) ?? string.Empty,
                Source = VolumeHandoffSource.UserImported
            };
    }
}
