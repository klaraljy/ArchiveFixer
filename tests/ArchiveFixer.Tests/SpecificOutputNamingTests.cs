using System;
using System.IO;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class SpecificOutputNamingTests
    {
        [Fact]
        public void 最后一层只有单一文件夹_用文件夹名()
        {
            string root = CreateRoot();
            Directory.CreateDirectory(Path.Combine(root, "22222"));

            string name = SpecificOutputNaming.Resolve(
                "合集\\2222.rar",
                root);

            Assert.Equal("22222", name);
            Directory.Delete(root, true);
        }

        [Fact]
        public void 最后一层是散文件_用最后一层压缩包完整名()
        {
            string root = Path.Combine(Path.GetTempPath(), "SpecificNaming_" + Guid.NewGuid().ToString("N"), "layer-000");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "01.jpg"), "x");

            string name = SpecificOutputNaming.Resolve(
                "合集\\弦音sic NO.001 Alter EGO egoist [9P-92MB].rar",
                root);

            Assert.Equal("弦音sic NO.001 Alter EGO egoist [9P-92MB]", name);
            Directory.Delete(Path.GetDirectoryName(root)!, true);
        }

        [Fact]
        public void 最后一层是文件夹加散文件_用最后一层压缩包名()
        {
            string root = CreateRoot();
            Directory.CreateDirectory(Path.Combine(root, "33333"));
            File.WriteAllText(Path.Combine(root, "a.jpg"), "x");

            string name = SpecificOutputNaming.Resolve("合集\\3333.rar", root);

            Assert.Equal("3333", name);
            Directory.Delete(root, true);
        }

        [Fact]
        public void 标准分卷第一卷_去掉归档后缀和卷标记()
        {
            Assert.Equal("111", SpecificOutputNaming.StripVolumeSuffix("111.7z.001"));
            Assert.Equal("111", SpecificOutputNaming.StripVolumeSuffix("111.part1.rar"));
        }

        [Fact]
        public void 后缀里所有中文都删掉_不是只删一个()
        {
            Assert.Equal("111.rar", SpecificOutputNaming.RemoveChineseFromExtension("111.rar删除中"));
            Assert.Equal("111.7z", SpecificOutputNaming.RemoveChineseFromExtension("111.删除中7z"));
            Assert.Equal("111.rar", SpecificOutputNaming.RemoveChineseFromExtension("111.rar中"));
        }

        [Fact]
        public void 后缀带中文的最后一层_输出名用清洗后的基名()
        {
            string root = Path.Combine(Path.GetTempPath(), "SpecificNaming_" + Guid.NewGuid().ToString("N"), "layer-000");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "01.jpg"), "x");

            string name = SpecificOutputNaming.Resolve("合集\\111.rar删除中", root);

            Assert.Equal("111", name);
            Directory.Delete(Path.GetDirectoryName(root)!, true);
        }

        [Fact]
        public void 文件夹导入_嵌套包输出到选中的合集根()
        {
            string root = CreateRoot();
            string nested = Path.Combine(root, "333");
            Directory.CreateDirectory(nested);
            string source = Path.Combine(nested, "3333.rar");
            File.WriteAllText(source, "x");

            string outputRoot = SpecificOutputNaming.ResolveOutputRoot(
                source,
                SourceSelectionKind.Folder,
                root);

            Assert.Equal(root, outputRoot);
            Directory.Delete(root, true);
        }

        [Fact]
        public void 单文件导入_输出仍在文件自己那一层()
        {
            string root = CreateRoot();
            string source = Path.Combine(root, "111.rar");
            File.WriteAllText(source, "x");

            string outputRoot = SpecificOutputNaming.ResolveOutputRoot(
                source,
                SourceSelectionKind.File,
                source);

            Assert.Equal(root, outputRoot);
            Directory.Delete(root, true);
        }

        private static string CreateRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "SpecificNaming_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }
    }
}
