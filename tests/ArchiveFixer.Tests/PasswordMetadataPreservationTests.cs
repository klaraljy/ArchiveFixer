using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class PasswordMetadataPreservationTests : IDisposable
    {
        private readonly string _root;

        public PasswordMetadataPreservationTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "af-meta-orphan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [Fact]
        public void 改密码值后旧值元数据不会静默消失()
        {
            File.WriteAllText(
                Path.Combine(_root, PasswordTextStore.FileName),
                "old-secret\r\n",
                new UTF8Encoding(true));

            File.WriteAllText(
                Path.Combine(_root, PasswordMetadataStore.FileName),
                JsonSerializer.Serialize(new[]
                {
                    new PasswordMetadataEntry
                    {
                        Value = "old-secret",
                        IsEnabled = true,
                        Remark = "改值前的备注"
                    }
                }),
                new UTF8Encoding(false));

            var service = new PasswordService
            {
                DataRootDirectory = _root,
                UseTextStore = true
            };

            Assert.Equal(PasswordListLoadStatus.Loaded, service.LoadRememberedList());
            service.Passwords[0].Value = "new-secret";

            using JsonDocument saved = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(_root, PasswordMetadataStore.FileName)));

            var values = saved.RootElement.EnumerateArray()
                .Select(item => item.GetProperty("Value").GetString())
                .ToHashSet(StringComparer.Ordinal);

            Assert.Contains("old-secret", values);
            Assert.Contains("new-secret", values);
        }

        [Fact]
        public void 删除密码后它的元数据不会静默消失()
        {
            File.WriteAllText(
                Path.Combine(_root, PasswordTextStore.FileName),
                "keep-secret\r\nremove-secret\r\n",
                new UTF8Encoding(true));

            var metadata = new List<PasswordMetadataEntry>
            {
                new() { Value = "keep-secret", IsEnabled = true, Remark = "保留条目" },
                new() { Value = "remove-secret", IsEnabled = false, Remark = "删除后也要留备注" },
                new() { Value = "orphan-secret", IsEnabled = false, Remark = "列表里暂时没有" }
            };

            File.WriteAllText(
                Path.Combine(_root, PasswordMetadataStore.FileName),
                JsonSerializer.Serialize(metadata),
                new UTF8Encoding(false));

            var service = new PasswordService
            {
                DataRootDirectory = _root,
                UseTextStore = true
            };

            Assert.Equal(PasswordListLoadStatus.Loaded, service.LoadRememberedList());

            var remove = service.Passwords.Single(item => item.Value == "remove-secret");
            Assert.True(service.RemovePassword(remove));

            using JsonDocument saved = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(_root, PasswordMetadataStore.FileName)));

            var values = saved.RootElement.EnumerateArray()
                .Select(item => item.GetProperty("Value").GetString())
                .ToHashSet(StringComparer.Ordinal);

            Assert.Contains("keep-secret", values);
            Assert.Contains("remove-secret", values);
            Assert.Contains("orphan-secret", values);
        }
    }
}
