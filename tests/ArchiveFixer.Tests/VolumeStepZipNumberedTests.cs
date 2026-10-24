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
    /// **ZIP 通用分片族算法**（第二大步 P3）的纯逻辑守门：
    /// 3 片纯内容定序 / 首片本地头 / 末片 EOCD / 规范名 .zip.00N / 判不出就不动。
    /// </summary>
    public sealed class VolumeStepZipNumberedTests
    {
        [Fact]
        public void 三片_首片本地头_末片EOCD_判完整_规范名zip001到003()
        {
            string layer = NewLayer();

            var unit = NewUnit(
                "组",
                layer,
                Path.Combine(layer, "组.zip.001"),
                Fact(Path.Combine(layer, "组.zip.001"), localHeader: true),
                Fact(Path.Combine(layer, "组.zip.002")),
                Fact(Path.Combine(layer, "组.zip.003"), endRecord: true, meansSplit: true));

            FamilyResult result = new ZipNumberedFamilyAlgorithm().Evaluate(unit);

            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Complete, result.Verdict);
            Assert.Equal(
                new[] { "组.zip.001", "组.zip.002", "组.zip.003" },
                result.NamePlan!.Items.Select(item => item.TargetName).ToArray());
            Assert.True(result.NamePlan.Items[0].IsEntry);
        }

        [Fact]
        public void 三片缺本地头证据_判不出()
        {
            string layer = NewLayer();

            var unit = NewUnit(
                "组",
                layer,
                Path.Combine(layer, "组.zip.001"),
                Fact(Path.Combine(layer, "组.zip.001")),
                Fact(Path.Combine(layer, "组.zip.002")),
                Fact(Path.Combine(layer, "组.zip.003"), endRecord: true, meansSplit: true));

            FamilyResult result = new ZipNumberedFamilyAlgorithm().Evaluate(unit);

            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Unknown, result.Verdict);
        }

        [Fact]
        public void 四片_名字序号齐全_首尾对上_判完整()
        {
            string layer = NewLayer();

            var unit = NewUnit(
                "组",
                layer,
                Path.Combine(layer, "组.zip.001"),
                Fact(Path.Combine(layer, "组.zip.001"), localHeader: true),
                Fact(Path.Combine(layer, "组.zip.002")),
                Fact(Path.Combine(layer, "组.zip.003")),
                Fact(Path.Combine(layer, "组.zip.004"), endRecord: true, meansSplit: true));

            FamilyResult result = new ZipNumberedFamilyAlgorithm().Evaluate(unit);

            Assert.Equal(GroupAnalysis.GroupAnalysisVerdict.Complete, result.Verdict);
            Assert.Equal(4, result.NamePlan!.Items.Count);
        }

        private static string NewLayer() =>
            Path.Combine(Path.GetTempPath(), "af-zipn-test-" + Guid.NewGuid().ToString("N"));

        private static VolumeGroupUnit NewUnit(
            string baseName,
            string layer,
            string entry,
            params GroupPieceFact[] pieces) =>
            new(baseName, VolumeNamingFamily.ZipNumbered, layer, entry) { Pieces = pieces };

        private static GroupPieceFact Fact(
            string path,
            bool localHeader = false,
            bool endRecord = false,
            bool meansSplit = false) =>
            new(
                path,
                10,
                VolumeContentFormat.Zip,
                ArchiveBodyKind.VolumePiece,
                null,
                false,
                VolumeNumberFromContent.VolumeMembershipVerdict.Unknown,
                VolumeNumberFail.None,
                0,
                0,
                null,
                Path.GetDirectoryName(path) ?? string.Empty,
                VolumeHandoffSource.UserImported,
                true,
                endRecord,
                meansSplit,
                localHeader);
    }
}
