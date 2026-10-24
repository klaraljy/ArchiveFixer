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
    /// **RAR 族算法**（第二大步 P1）的纯逻辑守门：
    /// 内容卷号定槽位 / 缺片点名 / 规范名计划 / 判不出就不动。
    /// ⛔ 只造事实对象，不碰盘、不依赖真 7z。
    /// </summary>
    public sealed class VolumeStepRarTests
    {
        [Fact]
        public void 内容卷号连成1到3_判完整_并出part规范名计划()
        {
            string layer = NewLayer();
            string entry = Path.Combine(layer, "组.part1.rar");

            var unit = NewUnit(
                "组",
                layer,
                entry,
                Fact(entry, 1),
                Fact(Path.Combine(layer, "组.part2.rar"), 2),
                Fact(Path.Combine(layer, "组.part3.rar"), 3));

            FamilyResult result = new RarFamilyAlgorithm().Evaluate(unit);

            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Complete, result.Verdict);
            Assert.Equal(3, result.OrderedPieces.Count);
            Assert.Empty(result.MissingNumbers);
            Assert.NotNull(result.NamePlan);
            Assert.False(result.NamePlan!.IsEmpty);
            Assert.Equal(
                new[] { "组.part1.rar", "组.part2.rar", "组.part3.rar" },
                result.NamePlan.Items.Select(item => item.TargetName).ToArray());
            Assert.True(result.NamePlan.Items[0].IsEntry);
        }

        [Fact]
        public void 内容卷号缺第2片_判缺卷_不出规范名计划()
        {
            string layer = NewLayer();
            string entry = Path.Combine(layer, "组.part1.rar");

            var unit = NewUnit(
                "组",
                layer,
                entry,
                Fact(entry, 1),
                Fact(Path.Combine(layer, "组.part3.rar"), 3));

            FamilyResult result = new RarFamilyAlgorithm().Evaluate(unit);

            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Missing, result.Verdict);
            Assert.Equal(new[] { 2 }, result.MissingNumbers.ToArray());
            Assert.Null(result.NamePlan);
        }

        [Fact]
        public void 卷号与名字都判不出_判不出_什么都不做()
        {
            string layer = NewLayer();
            string entry = Path.Combine(layer, "甲.bin");

            var unit = NewUnit(
                "甲",
                layer,
                entry,
                Fact(entry, null),
                Fact(Path.Combine(layer, "乙.bin"), null));

            FamilyResult result = new RarFamilyAlgorithm().Evaluate(unit);

            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Unknown, result.Verdict);
            Assert.Empty(result.OrderedPieces);
            Assert.Null(result.NamePlan);
        }

        [Fact]
        public void 老编号_入口rar_给r00_r01规范名()
        {
            string layer = NewLayer();
            string entry = Path.Combine(layer, "组.rar");

            var unit = NewUnit(
                "组",
                layer,
                entry,
                Fact(entry, 1),
                Fact(Path.Combine(layer, "组.r00"), 2),
                Fact(Path.Combine(layer, "组.r01"), 3));

            FamilyResult result = new RarFamilyAlgorithm().Evaluate(unit);

            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Complete, result.Verdict);
            Assert.Equal(
                new[] { "组.rar", "组.r00", "组.r01" },
                result.NamePlan!.Items.Select(item => item.TargetName).ToArray());
        }

        [Fact]
        public void 片顺序打乱_规范名仍按卷号映射()
        {
            string layer = NewLayer();
            string entry = Path.Combine(layer, "组.part1.rar");
            string part2 = Path.Combine(layer, "组.part2.rar");
            string part3 = Path.Combine(layer, "组.part3.rar");

            var unit = NewUnit(
                "组",
                layer,
                entry,
                Fact(part3, 3),
                Fact(entry, 1),
                Fact(part2, 2));

            FamilyResult result = new RarFamilyAlgorithm().Evaluate(unit);

            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Complete, result.Verdict);
            Assert.Equal(
                new[] { "组.part1.rar", "组.part2.rar", "组.part3.rar" },
                result.NamePlan!.Items.Select(item => item.TargetName).ToArray());
            Assert.Equal(part2, result.NamePlan.Items[1].PiecePath);
            Assert.Equal(part3, result.NamePlan.Items[2].PiecePath);
        }

        private static string NewLayer() =>
            Path.Combine(Path.GetTempPath(), "af-rar-test-" + Guid.NewGuid().ToString("N"));

        private static VolumeGroupUnit NewUnit(
            string baseName,
            string layer,
            string entry,
            params GroupPieceFact[] pieces)
        {
            var unit = new VolumeGroupUnit(baseName, VolumeNamingFamily.RarPart, layer, entry)
            {
                Pieces = pieces
            };

            return unit;
        }

        private static GroupPieceFact Fact(string path, int? volumeNumber) =>
            new(
                path,
                10,
                VolumeContentFormat.Rar,
                ArchiveBodyKind.VolumePiece,
                volumeNumber,
                volumeNumber == 1,
                VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember,
                VolumeNumberFail.None,
                0,
                0,
                null,
                Path.GetDirectoryName(path) ?? string.Empty,
                VolumeHandoffSource.UserImported,
                true);
    }
}
