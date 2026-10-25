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
    /// **跨盘 ZIP 族算法**（第二大步 P2）的纯逻辑守门：
    /// 末片盘号定总片数 / 名字 .z01… 对账 / 缺片点名 / 规范名计划 / 判不出就不动。
    /// </summary>
    public sealed class VolumeStepZipSpannedTests
    {
        [Fact]
        public void 末片盘号3_名字z01_z02_判完整_入口是末片zip()
        {
            string layer = NewLayer();
            string zip = Path.Combine(layer, "组.zip");

            var unit = NewUnit(
                "组",
                layer,
                zip,
                Fact(Path.Combine(layer, "组.z01"), null),
                Fact(Path.Combine(layer, "组.z02"), null),
                Fact(zip, 3));

            FamilyResult result = new ZipSpannedFamilyAlgorithm().Evaluate(unit);

            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Complete, result.Verdict);
            Assert.Equal(
                new[] { "组.z01", "组.z02", "组.zip" },
                result.NamePlan!.Items.Select(item => item.TargetName).ToArray());
            Assert.True(result.NamePlan.Items[2].IsEntry);
        }

        [Fact]
        public void 末片名字不是zip但内容说它是末片_规范名仍按组基名定()
        {
            string layer = NewLayer();
            string malformedTail = Path.Combine(layer, "组.mp4-572");

            var unit = NewUnit(
                "组",
                layer,
                malformedTail,
                Fact(Path.Combine(layer, "组.z01"), null),
                Fact(Path.Combine(layer, "组.z02"), null),
                Fact(malformedTail, 3, declaredVolumeCount: 3));

            FamilyResult result = new ZipSpannedFamilyAlgorithm().Evaluate(unit);

            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Complete, result.Verdict);
            Assert.Equal(
                new[] { "组.z01", "组.z02", "组.zip" },
                result.NamePlan!.Items.Select(item => item.TargetName).ToArray());
            Assert.True(result.NamePlan.Items[2].IsEntry);
        }

        [Fact]
        public void 修名时末片名坏_基名用第二大步基名()
        {
            string layer = NewLayer();
            string tail = Path.Combine(layer, "组.mp4-572");
            string[] ordered =
            {
                Path.Combine(layer, "组.z01"),
                Path.Combine(layer, "组.z02"),
                tail
            };

            Assert.True(VolumeNameRepair.TryDeriveSpannedStem(ordered, tail, 3, out string stem));
            Assert.Equal("组", stem);
        }

        [Fact]
        public void 末片名字带点但结尾zip_规范名仍用第二大步基名()
        {
            string layer = NewLayer();
            string tail = Path.Combine(layer, "组.中文.zip");

            var unit = NewUnit(
                "组",
                layer,
                tail,
                Fact(Path.Combine(layer, "组.z01"), null),
                Fact(Path.Combine(layer, "组.z02"), null),
                Fact(tail, 3, declaredVolumeCount: 3));

            FamilyResult result = new ZipSpannedFamilyAlgorithm().Evaluate(unit);

            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Complete, result.Verdict);
            Assert.Equal(
                new[] { "组.z01", "组.z02", "组.zip" },
                result.NamePlan!.Items.Select(item => item.TargetName).ToArray());
        }

        [Fact]
        public void 末片盘号3但缺z02_判缺片_点名2()
        {
            string layer = NewLayer();
            string zip = Path.Combine(layer, "组.zip");

            var unit = NewUnit(
                "组",
                layer,
                zip,
                Fact(Path.Combine(layer, "组.z01"), null),
                Fact(zip, 3));

            FamilyResult result = new ZipSpannedFamilyAlgorithm().Evaluate(unit);

            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Missing, result.Verdict);
            Assert.Equal(new[] { 2 }, result.MissingNumbers.ToArray());
            Assert.Null(result.NamePlan);
        }

        [Fact]
        public void 末片盘号判不出_不拿名字硬凑_判不出()
        {
            string layer = NewLayer();
            string zip = Path.Combine(layer, "组.zip");

            var unit = NewUnit(
                "组",
                layer,
                zip,
                Fact(Path.Combine(layer, "组.z01"), null),
                Fact(Path.Combine(layer, "组.z02"), null),
                Fact(zip, null));

            FamilyResult result = new ZipSpannedFamilyAlgorithm().Evaluate(unit);

            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Unknown, result.Verdict);
            Assert.Null(result.NamePlan);
        }

        [Fact]
        public void z序号越界_判不出()
        {
            string layer = NewLayer();
            string zip = Path.Combine(layer, "组.zip");

            var unit = NewUnit(
                "组",
                layer,
                zip,
                Fact(Path.Combine(layer, "组.z05"), null),
                Fact(zip, 3));

            FamilyResult result = new ZipSpannedFamilyAlgorithm().Evaluate(unit);

            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Unknown, result.Verdict);
        }

        [Fact]
        public void 一片名字读不出_剩余一个槽位_用消去法补位()
        {
            string layer = NewLayer();
            string zip = Path.Combine(layer, "组.zip");

            var unit = NewUnit(
                "组",
                layer,
                zip,
                Fact(Path.Combine(layer, "组.i_$z01"), null),
                Fact(Path.Combine(layer, "组"), null),
                Fact(zip, 3));

            FamilyResult result = new ZipSpannedFamilyAlgorithm().Evaluate(unit);

            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Complete, result.Verdict);
            Assert.Equal(
                new[] { "组.z01", "组.z02", "组.zip" },
                result.NamePlan!.Items.Select(item => item.TargetName).ToArray());
        }

        [Fact]
        public void 两片名字都读不出_剩两个槽位_顺序不明判不出()
        {
            string layer = NewLayer();
            string zip = Path.Combine(layer, "组.zip");

            var unit = NewUnit(
                "组",
                layer,
                zip,
                Fact(Path.Combine(layer, "甲"), null),
                Fact(Path.Combine(layer, "乙"), null),
                Fact(zip, 3));

            FamilyResult result = new ZipSpannedFamilyAlgorithm().Evaluate(unit);

            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Unknown, result.Verdict);
        }

        private static string NewLayer() =>
            Path.Combine(Path.GetTempPath(), "af-zip-test-" + Guid.NewGuid().ToString("N"));

        private static VolumeGroupUnit NewUnit(
            string baseName,
            string layer,
            string entry,
            params GroupPieceFact[] pieces) =>
            new(baseName, VolumeNamingFamily.ZipSpanned, layer, entry) { Pieces = pieces };

        private static GroupPieceFact Fact(string path, int? volumeNumber, int declaredVolumeCount = 0) =>
            new(
                path,
                10,
                VolumeContentFormat.Zip,
                ArchiveBodyKind.VolumePiece,
                volumeNumber,
                false,
                VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember,
                VolumeNumberFail.None,
                0,
                declaredVolumeCount,
                null,
                Path.GetDirectoryName(path) ?? string.Empty,
                VolumeHandoffSource.UserImported,
                true);
    }
}
