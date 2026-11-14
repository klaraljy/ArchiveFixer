using System;
using System.IO;
using System.Text;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 2026-11-01 真机事故的守门用例：`passwords.txt` 丢失后，程序不许拿很旧的 DPAPI 记忆
    /// 把它写回来（真机 13 条用户密码被 1 条测试密码覆盖）。
    ///
    /// <para>三档：① 有产品自己的 `.bak` ⇒ 按 `.bak` 恢复；② 只有"主清单存在过"的元数据
    /// ⇒ 本次一个字节都不写（<see cref="PasswordService.TextStoreWriteBlocked"/>）；
    /// ③ 真正的首次运行（无 txt / 无 bak / 无元数据）⇒ 仍然允许从旧记忆迁移一次。</para>
    /// </summary>
    public class PasswordTextStoreRecoveryTests : IDisposable
    {
        private readonly string _root;

        public PasswordTextStoreRecoveryTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerPwRecovery", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论。
            }
        }

        private string TextPath => Path.Combine(_root, "passwords.txt");

        private string BackupPath => TextPath + ".bak";

        private string MetadataPath => Path.Combine(_root, "passwords.meta.json");

        private PasswordService NewService() => new()
        {
            DataRootDirectory = _root,
            UseTextStore = true,
            RememberPasswordList = true
        };

        private void WriteOldMemory(params string[] values)
        {
            var store = new PasswordListStore { DataRootDirectory = _root };
            var snapshot = new PasswordListSnapshot();

            foreach (string value in values)
            {
                snapshot.Entries.Add(new PasswordListEntry
                {
                    Value = value,
                    Source = "ManualList",
                    Enabled = true
                });
            }

            PasswordListSaveResult result = store.Save(snapshot);
            Assert.True(result.Success, result.FailureReason);
        }

        [Fact]
        public void 主清单丢失_有bak时按bak恢复_不拿旧记忆覆盖()
        {
            File.WriteAllLines(BackupPath, new[] { "备份一", "备份二", "备份三" }, new UTF8Encoding(true));
            WriteOldMemory("记忆里的测试密码");

            PasswordService service = NewService();
            PasswordListLoadStatus status = service.LoadRememberedList();

            Assert.Equal(PasswordListLoadStatus.Loaded, status);
            Assert.Equal(3, service.Passwords.Count);
            Assert.DoesNotContain(service.Passwords, item => item.Value == "记忆里的测试密码");

            // 红线：txt 按 .bak 重建，且内容就是 .bak 那三行（不是旧记忆）。
            Assert.True(File.Exists(TextPath));
            Assert.Equal(3, File.ReadAllLines(TextPath).Length);
        }

        [Fact]
        public void 主清单丢失_只有元数据没有bak_本次一个字节都不写()
        {
            File.WriteAllText(
                MetadataPath,
                "[{\"Value\":\"元数据里的密码\",\"IsEnabled\":true,\"Remark\":\"\"}]",
                new UTF8Encoding(false));

            WriteOldMemory("记忆里的测试密码");

            PasswordService service = NewService();
            PasswordListLoadStatus status = service.LoadRememberedList();

            Assert.Equal(PasswordListLoadStatus.NoFile, status);
            Assert.True(service.TextStoreWriteBlocked);
            Assert.False(File.Exists(TextPath), "红线：这一档绝不许重建 passwords.txt（那正是真机 13→1 的成因）");
            Assert.Empty(service.Passwords);
        }

        [Fact]
        public void 首次运行_没有txt没有bak没有元数据_仍然只迁移一次()
        {
            WriteOldMemory("旧记忆一", "旧记忆二");

            PasswordService service = NewService();
            PasswordListLoadStatus status = service.LoadRememberedList();

            Assert.Equal(PasswordListLoadStatus.Loaded, status);
            Assert.Equal(2, service.Passwords.Count);
            Assert.True(File.Exists(TextPath));
            Assert.Equal(2, File.ReadAllLines(TextPath).Length);
        }
    }
}
