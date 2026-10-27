using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 其余物/工作区这道源层红线只挡**链产出/暂存**的片；用户明确从其余物导入的原件，
    /// 其余物就是用户的源层（2026-10-26 真机 9953 的根因）。
    /// </summary>
    public sealed class VolumeGroupServiceSourceLayerTests
    {
        [Fact]
        public void 用户从其余物导入_允许建组()
        {
            const string layer = @"C:\in\其余物";
            var service = new VolumeGroupService();

            VolumeGroupUnit? unit = service.TryCommit(
                "9953",
                VolumeNamingFamily.RarPart,
                layer,
                Path.Combine(layer, "9953.part1.rar"),
                ownerTaskFileName: null,
                Pieces(layer, VolumeHandoffSource.UserImported),
                missingNumbers: null,
                evidence: "用户从其余物导入",
                declaredPieceCount: null,
                declaredTotalBytes: null,
                outputLayer: layer);

            Assert.NotNull(unit);
            Assert.Equal(VolumeGroupState.Complete, unit!.State);
        }

        [Fact]
        public void 链产出的其余物片_仍然拒绝建组()
        {
            const string layer = @"C:\in\其余物";
            var service = new VolumeGroupService();

            VolumeGroupUnit? unit = service.TryCommit(
                "9953",
                VolumeNamingFamily.RarPart,
                layer,
                Path.Combine(layer, "9953.part1.rar"),
                ownerTaskFileName: null,
                Pieces(layer, VolumeHandoffSource.ChainProduced),
                missingNumbers: null,
                evidence: "链产出的其余物片",
                declaredPieceCount: null,
                declaredTotalBytes: null,
                outputLayer: layer);

            Assert.Null(unit);
        }

        private static IReadOnlyList<GroupPieceFact> Pieces(
            string layer,
            VolumeHandoffSource source)
        {
            return new List<GroupPieceFact>
            {
                Piece(layer, 1, first: true, source),
                Piece(layer, 2, first: false, source),
                Piece(layer, 3, first: false, source)
            };
        }

        private static GroupPieceFact Piece(
            string layer,
            int number,
            bool first,
            VolumeHandoffSource source)
        {
            return new GroupPieceFact(
                Path.Combine(layer, $"9953.part{number}.rar"),
                100,
                VolumeContentFormat.Rar,
                first ? ArchiveBodyKind.VolumeFirstPiece : ArchiveBodyKind.VolumePiece,
                number,
                first,
                VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember,
                VolumeNumberFail.None,
                0,
                0,
                number,
                layer,
                source,
                FromHandoff: true);
        }
    }
}
