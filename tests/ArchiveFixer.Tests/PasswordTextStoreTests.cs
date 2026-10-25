using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 用户 2026-10-25：密码列表改为程序自带的英文名明文清单，顺序就是尝试顺序。
    /// 这里钉住三件事：顺序原样写回、旧 DPAPI 记忆能迁移、外部路径不再是加载来源。
    /// </summary>
    public sealed class PasswordTextStoreTests : IDisposable
    {
        private readonly string _root;

        public PasswordTextStoreTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerPasswordText", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }

        [Fact]
        public void 内置清单_顺序原样写回_重启后按同一顺序恢复()
        {
            var service = new PasswordService
            {
                DataRootDirectory = _root,
                UseTextStore = true
            };

            Assert.True(service.AddPassword("first"));
            Assert.True(service.AddPassword("second"));
            Assert.True(service.AddPassword("third"));

            // 上下移动就是尝试顺序；集合变更会立即写 txt。
            Assert.True(service.MoveDown(service.Passwords[0]));

            string[] lines = File.ReadAllLines(Path.Combine(_root, PasswordTextStore.FileName));
            Assert.Equal(new[] { "second", "first", "third" }, lines);

            var restarted = new PasswordService
            {
                DataRootDirectory = _root,
                UseTextStore = true
            };

            Assert.Equal(PasswordListLoadStatus.Loaded, restarted.LoadRememberedList());
            Assert.Equal(new[] { "second", "first", "third" }, restarted.Passwords.Select(item => item.Value));
        }

        [Fact]
        public void 旧DPAPI记忆_首次读取后迁移到passwords_txt()
        {
            var oldStore = new PasswordListStore { DataRootDirectory = _root };
            var snapshot = new PasswordListSnapshot();
            snapshot.Entries.Add(new PasswordListEntry { Value = "old-a", Source = "ManualList", Enabled = true });
            snapshot.Entries.Add(new PasswordListEntry { Value = "old-b", Source = "ManualList", Enabled = true });

            Assert.True(oldStore.Save(snapshot).Success);
            Assert.False(File.Exists(Path.Combine(_root, PasswordTextStore.FileName)));

            var service = new PasswordService
            {
                DataRootDirectory = _root,
                UseTextStore = true
            };

            Assert.Equal(PasswordListLoadStatus.Loaded, service.LoadRememberedList());
            Assert.Equal(new[] { "old-a", "old-b" }, service.Passwords.Select(item => item.Value));

            string textPath = Path.Combine(_root, PasswordTextStore.FileName);
            Assert.True(File.Exists(textPath));
            Assert.Equal(new[] { "old-a", "old-b" }, File.ReadAllLines(textPath));
            Assert.True(File.Exists(oldStore.FilePath), "旧 DPAPI 记忆要保留，不能迁移后删掉");
        }
    }
}
