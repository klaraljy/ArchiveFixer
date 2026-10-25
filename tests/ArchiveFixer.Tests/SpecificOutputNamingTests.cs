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

        private static string CreateRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "SpecificNaming_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }
    }
}
