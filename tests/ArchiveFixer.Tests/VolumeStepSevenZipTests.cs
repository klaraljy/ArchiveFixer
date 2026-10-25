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
    /// **7z 分卷族算法**（第二大步 P4）的纯逻辑守门：
    /// 首卷自述片数/整包字节 → 片数 + 字节和双对账；槽位按名字，读不出才按“末片更小”补位。
    /// </summary>
    public sealed class VolumeStepSevenZipTests
    {
        [Fact]
        public void 首卷没有片数字段_两片字节和相等_仍判完整()
        {
            string layer = NewLayer();

            var unit = NewUnit(
                "组",
                layer,
                Path.Combine(layer, "组.7z.001"),
                Fact(Path.Combine(layer, "组.7z.001"), 100, first: true, declaredCount: 0, declaredTotal: 250),
                Fact(Path.Combine(layer, "组.7z.002"), 150));

            FamilyResult result = new SevenZipFamilyAlgorithm().Evaluate(unit);

            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Complete, result.Verdict);
            Assert.Equal(
                new[] { "组.7z.001", "组.7z.002" },
                result.NamePlan!.Items.Select(item => item.TargetName).ToArray());
        }

        [Fact]
        public void 首卷自述3片_字节和相等_判完整_规范名7z001到003()
        {
            string layer = NewLayer();

            var unit = NewUnit(
                "组",
                layer,
                Path.Combine(layer, "组.7z.001"),
                Fact(Path.Combine(layer, "组.7z.001"), 100, first: true, declaredCount: 3, declaredTotal: 250),
                Fact(Path.Combine(layer, "组.7z.002"), 100),
                Fact(Path.Combine(layer, "组.7z.003"), 50));

            FamilyResult result = new SevenZipFamilyAlgorithm().Evaluate(unit);

            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Complete, result.Verdict);
            Assert.Equal(
                new[] { "组.7z.001", "组.7z.002", "组.7z.003" },
                result.NamePlan!.Items.Select(item => item.TargetName).ToArray());
            Assert.True(result.NamePlan.Items[0].IsEntry);
        }

        [Fact]
        public void 首卷自述3片但只收到2片_判缺片()
        {
            string layer = NewLayer();

            var unit = NewUnit(
                "组",
                layer,
                Path.Combine(layer, "组.7z.001"),
                Fact(Path.Combine(layer, "组.7z.001"), 100, first: true, declaredCount: 3, declaredTotal: 250),
                Fact(Path.Combine(layer, "组.7z.002"), 100));

            FamilyResult result = new SevenZipFamilyAlgorithm().Evaluate(unit);

            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Missing, result.Verdict);
            Assert.Null(result.NamePlan);
        }

        [Fact]
        public void 片数够但字节和小于自述整包_判缺片()
        {
            string layer = NewLayer();

            var unit = NewUnit(
                "组",
                layer,
                Path.Combine(layer, "组.7z.001"),
                Fact(Path.Combine(layer, "组.7z.001"), 100, first: true, declaredCount: 3, declaredTotal: 300),
                Fact(Path.Combine(layer, "组.7z.002"), 100),
                Fact(Path.Combine(layer, "组.7z.003"), 50));

            FamilyResult result = new SevenZipFamilyAlgorithm().Evaluate(unit);

            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Missing, result.Verdict);
        }

        [Fact]
        public void 两片名字读不出_末片严格更小_按大小补位()
        {
            string layer = NewLayer();

            var unit = NewUnit(
                "组",
                layer,
                Path.Combine(layer, "组.7z.001"),
                Fact(Path.Combine(layer, "组.7z.001"), 100, first: true, declaredCount: 3, declaredTotal: 250),
                Fact(Path.Combine(layer, "甲"), 100),
                Fact(Path.Combine(layer, "乙"), 50));

            FamilyResult result = new SevenZipFamilyAlgorithm().Evaluate(unit);

            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Complete, result.Verdict);
            Assert.Equal(
                new[] { "组.7z.001", "组.7z.002", "组.7z.003" },
                result.NamePlan!.Items.Select(item => item.TargetName).ToArray());
        }

        [Fact]
        public void 两片名字读不出且一样大_顺序不明判不出()
        {
            string layer = NewLayer();

            var unit = NewUnit(
                "组",
                layer,
                Path.Combine(layer, "组.7z.001"),
                Fact(Path.Combine(layer, "组.7z.001"), 100, first: true, declaredCount: 3, declaredTotal: 250),
                Fact(Path.Combine(layer, "甲"), 75),
                Fact(Path.Combine(layer, "乙"), 75));

            FamilyResult result = new SevenZipFamilyAlgorithm().Evaluate(unit);

            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Unknown, result.Verdict);
        }

        private static string NewLayer() =>
            Path.Combine(Path.GetTempPath(), "af-7z-test-" + Guid.NewGuid().ToString("N"));

        private static VolumeGroupUnit NewUnit(
            string baseName,
            string layer,
            string entry,
            params GroupPieceFact[] pieces) =>
            new(baseName, VolumeNamingFamily.SevenZipNumbered, layer, entry) { Pieces = pieces };

        private static GroupPieceFact Fact(
            string path,
            long size,
            bool first = false,
            int declaredCount = 0,
            long declaredTotal = 0) =>
            new(
                path,
                size,
                VolumeContentFormat.SevenZip,
                first ? ArchiveBodyKind.VolumeFirstPiece : ArchiveBodyKind.VolumePiece,
                null,
                first,
                VolumeNumberFromContent.VolumeMembershipVerdict.Unknown,
                VolumeNumberFail.None,
                declaredTotal,
                declaredCount,
                null,
                Path.GetDirectoryName(path) ?? string.Empty,
                VolumeHandoffSource.UserImported,
                true);
    }
}
