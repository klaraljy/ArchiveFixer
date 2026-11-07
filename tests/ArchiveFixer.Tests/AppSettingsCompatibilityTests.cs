using System;
using System.IO;
using System.Text.Json;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class AppSettingsCompatibilityTests : IDisposable
    {
        private readonly string _root;

        public AppSettingsCompatibilityTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "af-settings-compat-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [Fact]
        public void 未知设置键加载后仍保留_保存不会把它丢掉()
        {
            var pathService = new PathService { DataRootDirectory = _root };
            var service = new SettingsService(pathService);

            File.WriteAllText(
                pathService.SettingsFilePath,
                """
                {
                  "RecursiveScan": false,
                  "FutureSetting": 123,
                  "FutureComplex": { "enabled": true }
                }
                """);

            AppSettings loaded = service.Load();

            Assert.NotNull(loaded.UnknownSettings);
            Assert.True(loaded.UnknownSettings!.ContainsKey("FutureSetting"));
            Assert.True(loaded.UnknownSettings.ContainsKey("FutureComplex"));

            Assert.True(service.Save(loaded));

            using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(pathService.SettingsFilePath));
            Assert.True(saved.RootElement.TryGetProperty("FutureSetting", out JsonElement future));
            Assert.Equal(123, future.GetInt32());
            Assert.True(saved.RootElement.TryGetProperty("FutureComplex", out _));
        }
    }
}
