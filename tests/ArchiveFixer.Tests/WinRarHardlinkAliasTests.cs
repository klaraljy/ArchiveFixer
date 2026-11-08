using System;
using System.IO;
using System.Text;
using ArchiveFixer.Engines.WinRar;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class WinRarHardlinkAliasTests : IDisposable
    {
        private readonly string _root;

        public WinRarHardlinkAliasTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "WinRarAlias-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
            }
        }

        [Fact]
        public void 同卷硬链接别名_源文件不动_删别名后源文件还在()
        {
            string source = Path.Combine(_root, "source.mp4");
            string alias = Path.Combine(_root, "alias.zip");
            File.WriteAllText(source, "alias payload", new UTF8Encoding(false));

            bool created = WinRarHardlinkAlias.TryCreate(source, alias, out string error);

            Assert.True(created, error);
            Assert.True(File.Exists(alias));
            Assert.Equal(new FileInfo(source).Length, new FileInfo(alias).Length);

            WinRarHardlinkAlias.TryDelete(alias);

            Assert.False(File.Exists(alias));
            Assert.True(File.Exists(source));
            Assert.Equal("alias payload", File.ReadAllText(source));
        }

        [Fact]
        public void 别名目标已存在_不覆盖()
        {
            string source = Path.Combine(_root, "source.mp4");
            string alias = Path.Combine(_root, "alias.zip");
            File.WriteAllText(source, "source", new UTF8Encoding(false));
            File.WriteAllText(alias, "existing", new UTF8Encoding(false));

            bool created = WinRarHardlinkAlias.TryCreate(source, alias, out string error);

            Assert.False(created);
            Assert.Contains("已存在", error, StringComparison.Ordinal);
            Assert.Equal("existing", File.ReadAllText(alias));
        }

        [Fact]
        public void 跨卷别名_拒绝()
        {
            string source = Path.Combine(_root, "source.mp4");
            string tempRoot = Path.GetPathRoot(_root) ?? string.Empty;
            string otherRoot = string.Equals(tempRoot, @"C:\", StringComparison.OrdinalIgnoreCase)
                ? @"D:\"
                : @"C:\";
            string alias = Path.Combine(otherRoot, ".af-cross-volume-" + Guid.NewGuid().ToString("N") + ".zip");
            File.WriteAllText(source, "source", new UTF8Encoding(false));

            bool created = WinRarHardlinkAlias.TryCreate(source, alias, out string error);

            Assert.False(created);
            Assert.Contains("同卷", error, StringComparison.Ordinal);
        }
    }
}
