using System;
using System.IO;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class SpecificLayerClassifierTests
    {
        [Fact]
        public void 直接图片_判内容物()
        {
            string root = CreateRoot();
            File.WriteAllText(Path.Combine(root, "01.jpg"), "x");

            Assert.Equal(SpecificLayerKind.Content, SpecificLayerClassifier.Classify(root).Kind);
            Cleanup(root);
        }

        [Fact]
        public void 只有一个完整压缩包_继续下一层()
        {
            string root = CreateRoot();
            File.WriteAllText(Path.Combine(root, "inner.rar"), "x");

            SpecificLayerDecision decision = SpecificLayerClassifier.Classify(root);

            Assert.Equal(SpecificLayerKind.SingleArchive, decision.Kind);
            Assert.EndsWith("inner.rar", decision.ArchivePath, StringComparison.OrdinalIgnoreCase);
            Cleanup(root);
        }

        [Fact]
        public void 后缀里带多个中文字符_仍判单一压缩包()
        {
            string root = CreateRoot();
            File.WriteAllText(Path.Combine(root, "inner.rar删除中"), "x");

            SpecificLayerDecision decision = SpecificLayerClassifier.Classify(root);

            Assert.Equal(SpecificLayerKind.SingleArchive, decision.Kind);
            Assert.EndsWith("inner.rar删除中", decision.ArchivePath, StringComparison.OrdinalIgnoreCase);
            Cleanup(root);
        }

        [Fact]
        public void 标准同组分卷_判分卷组()
        {
            string root = CreateRoot();
            File.WriteAllText(Path.Combine(root, "111.7z.001"), "x");
            File.WriteAllText(Path.Combine(root, "111.7z.002"), "x");

            Assert.Equal(SpecificLayerKind.StandardVolumeGroup, SpecificLayerClassifier.Classify(root).Kind);
            Cleanup(root);
        }

        [Fact]
        public void 文件夹加散文件_判内容物()
        {
            string root = CreateRoot();
            Directory.CreateDirectory(Path.Combine(root, "folder"));
            File.WriteAllText(Path.Combine(root, "a.jpg"), "x");

            Assert.Equal(SpecificLayerKind.Content, SpecificLayerClassifier.Classify(root).Kind);
            Cleanup(root);
        }

        private static string CreateRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "SpecificLayer_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static void Cleanup(string root)
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
            }
        }
    }
}
