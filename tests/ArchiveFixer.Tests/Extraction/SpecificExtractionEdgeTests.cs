using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Models;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 特定解压的边界形状：分类器与 Runner 在不支持/失败/超层时都必须停下，
    /// 不能把“解不出来”伪装成成功，也不能继续猜下一层。
    /// </summary>
    public sealed class SpecificExtractionEdgeTests : IDisposable
    {
        private readonly string _root;

        public SpecificExtractionEdgeTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "SpecificEdge_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        [Fact]
        public void 层分类_单归档_分卷组_内容物_多归档()
        {
            string single = Path.Combine(_root, "single");
            Directory.CreateDirectory(single);
            File.WriteAllText(Path.Combine(single, "a.7z"), "x");
            Assert.Equal(SpecificLayerKind.SingleArchive, SpecificLayerClassifier.Classify(single).Kind);

            string volume = Path.Combine(_root, "volume");
            Directory.CreateDirectory(volume);
            File.WriteAllText(Path.Combine(volume, "set.7z.001"), "x");
            File.WriteAllText(Path.Combine(volume, "set.7z.002"), "x");
            SpecificLayerDecision volumeDecision = SpecificLayerClassifier.Classify(volume);
            Assert.Equal(SpecificLayerKind.StandardVolumeGroup, volumeDecision.Kind);
            Assert.Equal(2, volumeDecision.VolumePaths.Count);

            string content = Path.Combine(_root, "content");
            Directory.CreateDirectory(content);
            File.WriteAllText(Path.Combine(content, "01.jpg"), "x");
            Assert.Equal(SpecificLayerKind.Content, SpecificLayerClassifier.Classify(content).Kind);

            string twoArchives = Path.Combine(_root, "two-archives");
            Directory.CreateDirectory(twoArchives);
            File.WriteAllText(Path.Combine(twoArchives, "a.7z"), "x");
            File.WriteAllText(Path.Combine(twoArchives, "b.rar"), "x");
            Assert.Equal(SpecificLayerKind.Unsupported, SpecificLayerClassifier.Classify(twoArchives).Kind);
        }

        [Fact]
        public async Task Runner_源不存在_直接失败()
        {
            var runner = new SpecificExtractionRunner(new ShapeEngine());
            SpecificExtractionRunResult result = await runner.RunAsync(
                Path.Combine(_root, "missing.7z"),
                Path.Combine(_root, "out"),
                Path.Combine(_root, "work"),
                Array.Empty<string?>());

            Assert.False(result.Success);
            Assert.Contains("不存在", result.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Runner_所有密码都失败_返回失败而不是成功()
        {
            string source = Path.Combine(_root, "locked.7z");
            File.WriteAllText(source, "x");

            var engine = new ShapeEngine { AlwaysFail = true };
            var runner = new SpecificExtractionRunner(engine);
            SpecificExtractionRunResult result = await runner.RunAsync(
                source,
                Path.Combine(_root, "out"),
                Path.Combine(_root, "work"),
                new string?[] { null, "wrong" });

            Assert.False(result.Success);
            Assert.Contains("解压失败", result.Message, StringComparison.Ordinal);
            Assert.True(result.Depth == 0);
        }

        [Fact]
        public async Task Runner_三层仍是归档_超过上限失败()
        {
            string source = Path.Combine(_root, "a.7z");
            File.WriteAllText(source, "x");

            var engine = new ShapeEngine();
            engine.NextArchive["a.7z"] = "b.7z";
            engine.NextArchive["b.7z"] = "c.7z";
            engine.NextArchive["c.7z"] = "d.7z";

            var runner = new SpecificExtractionRunner(engine);
            SpecificExtractionRunResult result = await runner.RunAsync(
                source,
                Path.Combine(_root, "out"),
                Path.Combine(_root, "work"),
                Array.Empty<string?>());

            Assert.False(result.Success);
            Assert.Contains("超过 3 层", result.Message, StringComparison.Ordinal);
            Assert.Equal(3, engine.ExtractCalls);
        }

        [Fact]
        public async Task Runner_一层两个归档_不支持并停止()
        {
            string source = Path.Combine(_root, "outer.7z");
            File.WriteAllText(source, "x");

            var engine = new ShapeEngine { WriteTwoArchives = true };
            var runner = new SpecificExtractionRunner(engine);
            SpecificExtractionRunResult result = await runner.RunAsync(
                source,
                Path.Combine(_root, "out"),
                Path.Combine(_root, "work"),
                Array.Empty<string?>());

            Assert.False(result.Success);
            Assert.Contains("支持范围", result.Message, StringComparison.Ordinal);
            Assert.Equal(1, engine.ExtractCalls);
        }

        private sealed class ShapeEngine : IArchiveEngine
        {
            public Dictionary<string, string> NextArchive { get; } = new(StringComparer.OrdinalIgnoreCase);

            public bool AlwaysFail { get; init; }

            public bool WriteTwoArchives { get; init; }

            public int ExtractCalls { get; private set; }

            public string Id => "shape";
            public string DisplayName => "Shape";
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
                ExtractCalls++;
                if (AlwaysFail)
                {
                    return Task.FromResult(new ArchiveOperationResult { Success = false, Message = "密码不对" });
                }

                string fileName = Path.GetFileName(request.ArchivePath);
                string dir = options.CustomOutputDirectory;
                Directory.CreateDirectory(dir);

                if (WriteTwoArchives)
                {
                    File.WriteAllText(Path.Combine(dir, "one.7z"), "x");
                    File.WriteAllText(Path.Combine(dir, "two.7z"), "x");
                }
                else if (NextArchive.TryGetValue(fileName, out string? next))
                {
                    File.WriteAllText(Path.Combine(dir, next), "x");
                }
                else
                {
                    File.WriteAllText(Path.Combine(dir, "01.jpg"), "x");
                }

                return Task.FromResult(new ArchiveOperationResult { Success = true });
            }
        }
    }
}
