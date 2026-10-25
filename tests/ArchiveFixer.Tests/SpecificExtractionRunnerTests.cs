using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    public sealed class SpecificExtractionRunnerTests : IDisposable
    {
        private readonly string _root;

        public SpecificExtractionRunnerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "SpecificRunner_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch
            {
            }
        }

        [Fact]
        public async Task 两层_最后一层是散图片_用最后一层压缩包名()
        {
            string source = Path.Combine(_root, "outer.7z");
            File.WriteAllText(source, "x");

            var engine = new ScriptedEngine();
            engine.Add("outer.7z", dir => File.WriteAllText(Path.Combine(dir, "inner.rar"), "x"));
            engine.Add("inner.rar", dir => File.WriteAllText(Path.Combine(dir, "01.jpg"), "x"));

            var runner = new SpecificExtractionRunner(engine);
            SpecificExtractionRunResult result = await runner.RunAsync(
                source,
                Path.Combine(_root, "out"),
                Path.Combine(_root, "work"),
                new string?[] { "pwd" });

            Assert.True(result.Success, result.Message);
            Assert.True(File.Exists(Path.Combine(result.OutputPath, "01.jpg")));
            Assert.Equal("inner", Path.GetFileName(result.OutputPath));
        }

        [Fact]
        public async Task 最后一层只有一个文件夹_用文件夹名()
        {
            string source = Path.Combine(_root, "outer.7z");
            File.WriteAllText(source, "x");

            var engine = new ScriptedEngine();
            engine.Add("outer.7z", dir => File.WriteAllText(Path.Combine(dir, "inner.rar"), "x"));
            engine.Add("inner.rar", dir =>
            {
                Directory.CreateDirectory(Path.Combine(dir, "22222"));
                File.WriteAllText(Path.Combine(dir, "22222", "01.jpg"), "x");
            });

            var runner = new SpecificExtractionRunner(engine);
            SpecificExtractionRunResult result = await runner.RunAsync(
                source,
                Path.Combine(_root, "out"),
                Path.Combine(_root, "work"),
                new string?[] { "pwd" });

            Assert.True(result.Success, result.Message);
            Assert.Equal("22222", Path.GetFileName(result.OutputPath));
            Assert.True(File.Exists(Path.Combine(result.OutputPath, "01.jpg")));
        }

        [Fact]
        public async Task 每一层都从密码候选开始_无密码排在最前()
        {
            string source = Path.Combine(_root, "outer.7z");
            File.WriteAllText(source, "x");

            var engine = new ScriptedEngine();
            engine.Add("outer.7z", dir => File.WriteAllText(Path.Combine(dir, "inner.rar"), "x"));
            engine.Add("inner.rar", dir => File.WriteAllText(Path.Combine(dir, "01.jpg"), "x"));

            var runner = new SpecificExtractionRunner(engine);
            await runner.RunAsync(
                source,
                Path.Combine(_root, "out"),
                Path.Combine(_root, "work"),
                new string?[] { null, "", "pwd" });

            // 每层都要重新走候选链；无密码排在最前，成功后不再烧后面的候选。
            Assert.True(engine.Requests.Count >= 2);
            Assert.All(engine.Requests, request => Assert.Null(request.Password));
        }

        private sealed class ScriptedEngine : IArchiveEngine
        {
            private readonly Dictionary<string, Action<string>> _writers = new(StringComparer.OrdinalIgnoreCase);

            public List<ArchiveRequest> Requests { get; } = new();

            public void Add(string fileName, Action<string> writer) => _writers[fileName] = writer;

            public string Id => "scripted";
            public string DisplayName => "Scripted";
            public string Version => "1";
            public bool IsAvailable => true;
            public EngineCapabilities Capabilities => new();

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new ArchiveProbeResult { IsArchive = true });

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new ArchiveListResult { Success = true });

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new ArchiveOperationResult { Success = true });

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                Requests.Add(new ArchiveRequest { ArchivePath = request.ArchivePath, Password = request.Password });

                string fileName = Path.GetFileName(request.ArchivePath);

                if (_writers.TryGetValue(fileName, out Action<string>? writer))
                {
                    writer(options.CustomOutputDirectory);
                }

                return Task.FromResult(new ArchiveOperationResult { Success = true });
            }
        }
    }
}
