using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class EncryptedHeaderManifestRecoveryTests
    {
        [Fact]
        public async Task 命中密码后重新列目录_拿回可用清单()
        {
            var engine = new ListEngine(new ArchiveListResult
            {
                Success = true,
                IsEncrypted = true,
                FileCount = 1,
                TotalUncompressedSize = 8,
                Entries = new[]
                {
                    new ArchiveEntry { Path = "a.bin", Size = 8, IsEncrypted = true }
                }
            });

            EncryptedHeaderManifestRecoveryResult result = await EncryptedHeaderManifestRecovery.TryRelistAsync(
                engine,
                @"H:\a.7z",
                "pw");

            Assert.True(result.Manifest.Available);
            Assert.Equal(1, result.Manifest.FileCount);
            Assert.Equal(8, result.Manifest.TotalSize);
            Assert.Single(result.Entries);
        }

        [Fact]
        public async Task 重新列目录失败_仍然判不出()
        {
            EncryptedHeaderManifestRecoveryResult result = await EncryptedHeaderManifestRecovery.TryRelistAsync(
                new ListEngine(new ArchiveListResult { Success = false }),
                @"H:\a.7z",
                "pw");

            Assert.False(result.Manifest.Available);
            Assert.Empty(result.Entries);
        }

        private sealed class ListEngine : IArchiveEngine
        {
            private readonly ArchiveListResult _listed;

            public ListEngine(ArchiveListResult listed) => _listed = listed;

            public string Id => "fake";
            public string DisplayName => "fake";
            public string Version => "1";
            public bool IsAvailable => true;
            public EngineCapabilities Capabilities { get; } = new();

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveProbeResult { IsArchive = true });

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(_listed);

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveOperationResult { Success = false });

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ArchiveFixer.Models.ExtractOptions options,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveOperationResult { Success = false });
        }
    }
}
