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

        private string Create(string name, int length)
        {
            string path = Path.Combine(_root, name);
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
