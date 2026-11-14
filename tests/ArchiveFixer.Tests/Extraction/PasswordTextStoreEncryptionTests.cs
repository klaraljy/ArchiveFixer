using System;
using System.IO;
using System.Linq;
using System.Text;
using ArchiveFixer.Storage;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class PasswordTextStoreEncryptionTests : IDisposable
    {
        private readonly string _root;

        public PasswordTextStoreEncryptionTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "af-password-enc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [Fact]
        public void 加密写入后磁盘上不再是明文且能原样读回()
        {
            var store = new PasswordTextStore
            {
                DataRootDirectory = _root,
                EncryptAtRest = true
            };

            var values = new[] { "第一层密码", "second-pass", "" };

            Assert.True(store.TrySave(values, out string saveError), saveError);

            string raw = File.ReadAllText(store.FilePath);
            Assert.DoesNotContain("第一层密码", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("second-pass", raw, StringComparison.Ordinal);
            Assert.StartsWith(PasswordTextStore.EncryptedHeader, raw, StringComparison.Ordinal);

            Assert.True(store.TryLoad(out var loaded, out string loadError), loadError);
            Assert.Equal(new[] { "第一层密码", "second-pass" }, loaded);
        }

        [Fact]
        public void 加密文件被编辑器加BOM后仍能读()
        {
            var store = new PasswordTextStore
            {
                DataRootDirectory = _root,
                EncryptAtRest = true
            };

            Assert.True(store.TrySave(new[] { "bom-secret" }, out string saveError), saveError);

            byte[] raw = File.ReadAllBytes(store.FilePath);
            File.WriteAllBytes(
                store.FilePath,
                new byte[] { 0xEF, 0xBB, 0xBF }.Concat(raw).ToArray());

            Assert.True(store.TryLoad(out var loaded, out string loadError), loadError);
            Assert.Equal(new[] { "bom-secret" }, loaded);
        }

        [Fact]
        public void 加密保存时备份也不留明文()
        {
            string path = Path.Combine(_root, PasswordTextStore.FileName);
            File.WriteAllText(path, "old-plain-secret\r\n", new UTF8Encoding(true));

            var store = new PasswordTextStore
            {
                DataRootDirectory = _root,
                EncryptAtRest = true
            };

            Assert.True(store.TrySave(new[] { "new-secret" }, out string saveError), saveError);

            string backup = File.ReadAllText(path + ".bak");
            Assert.DoesNotContain("old-plain-secret", backup, StringComparison.Ordinal);
            Assert.StartsWith(PasswordTextStore.EncryptedHeader, backup, StringComparison.Ordinal);
        }

        [Fact]
        public void 导出明文副本不移动也不删除程序内部密码本()
        {
            var store = new PasswordTextStore
            {
                DataRootDirectory = _root,
                EncryptAtRest = true
            };

            Assert.True(store.TrySave(new[] { "internal-secret" }, out string saveError), saveError);
            string internalPath = store.FilePath;
            string exportPath = Path.Combine(_root, "导出副本.txt");

            Assert.True(PasswordTextStore.TryExportPlaintext(
                exportPath,
                new[] { "internal-secret" },
                out string exportError), exportError);

            Assert.True(File.Exists(internalPath), "内部密码本不能被移动/删除");
            Assert.Contains("internal-secret", File.ReadAllText(exportPath), StringComparison.Ordinal);
            Assert.DoesNotContain("internal-secret", File.ReadAllText(internalPath), StringComparison.Ordinal);
        }

        [Fact]
        public void 旧明文仍可读取_下一次加密保存后不再明文()
        {
            string path = Path.Combine(_root, PasswordTextStore.FileName);
            File.WriteAllText(path, "old-a\r\nold-b\r\n", new UTF8Encoding(true));

            var store = new PasswordTextStore
            {
                DataRootDirectory = _root,
                EncryptAtRest = true
            };

            Assert.True(store.TryLoad(out var loaded, out string loadError), loadError);
            Assert.Equal(new[] { "old-a", "old-b" }, loaded);

            Assert.True(store.TrySave(loaded, out string saveError), saveError);
            Assert.DoesNotContain("old-a", File.ReadAllText(path), StringComparison.Ordinal);
        }

        [Fact]
        public void 加密文件损坏时读取失败并标记为加密失败_调用方不能拿旧记忆覆盖()
        {
            string path = Path.Combine(_root, PasswordTextStore.FileName);
            File.WriteAllText(
                path,
                PasswordTextStore.EncryptedHeader + "not-a-valid-dpapi-payload",
                new UTF8Encoding(false));

            var store = new PasswordTextStore { DataRootDirectory = _root };

            Assert.Throws<InvalidDataException>(() => store.TryLoad(out _, out _));
            Assert.True(store.LastLoadWasEncryptedFailure);
        }
    }
}
