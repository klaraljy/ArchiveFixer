using ArchiveFixer.Engines;
using ArchiveFixer.Packing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests;

/// <summary>
/// 用截图里的真实样本做一次只读造包验收：
/// 内层 7z 分卷 + 一个外层 RAR（或没有 WinRAR 时一个外层 7z），源文件不动。
///
/// <para>只在显式设置 <c>ARCHIVEFIXER_REAL_PACK_SOURCE</c> 时跑；默认全量不会碰 H:。
/// 输出目录用 <c>ARCHIVEFIXER_REAL_PACK_OUTPUT</c>，未设置时用临时目录。</para>
/// </summary>
public sealed class PackingRealSampleTests
{
    private readonly ITestOutputHelper _output;

    public PackingRealSampleTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task 真样本_固定流程_三卷加单个外层_源文件不变()
    {
        string source = Environment.GetEnvironmentVariable("ARCHIVEFIXER_REAL_PACK_SOURCE") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            _output.WriteLine("跳过：没有设置 ARCHIVEFIXER_REAL_PACK_SOURCE，或样本目录不存在。");
            return;
        }

        string output = Environment.GetEnvironmentVariable("ARCHIVEFIXER_REAL_PACK_OUTPUT")
                        ?? Path.Combine(Path.GetTempPath(), "ArchiveFixerPackReal", Guid.NewGuid().ToString("N"));

        if (!Path.IsPathRooted(output))
        {
            output = Path.Combine(Path.GetTempPath(), output);
        }

        Directory.CreateDirectory(output);

        try
        {
            var tools = new ToolLocator();

            if (!tools.SevenZipExists)
            {
                _output.WriteLine("跳过：测试机上没有内置 7z.exe。");
                return;
            }

            Snapshot before = SnapshotDirectory(source);
            var service = new PackingService(tools);
            const string password = "Packing-Real-Sample-2026";

            PackingResult result = await service.PackAsync(
                    new PackingRequest
                    {
                        SourceFolder = source,
                        Password = password,
                        RunOptions = new PackingRunOptions
                        {
                            TargetMode = PackingTargetMode.Custom,
                            CustomOutputDirectory = output,
                            SourceHandling = PackingSourceHandling.KeepInPlace,
                            RestHandling = PackingRestHandling.Delete
                        }
                    },
                    line => _output.WriteLine(line))
                .WaitAsync(TimeSpan.FromMinutes(15));

            _output.WriteLine(result.Describe());

            Assert.True(result.Success, result.Describe());

            PackOuterContainer expectedContainer = tools.RarExists
                ? PackOuterContainer.Rar
                : PackOuterContainer.SevenZip;

            Assert.Equal(expectedContainer, result.OuterContainer);
            Assert.Equal(3, result.Volumes.Count);
            Assert.All(result.Volumes, volume => Assert.True(volume.Bytes > 0, volume.FileName));
            Assert.True(File.Exists(result.OuterPath), "外层产物不存在：" + result.OuterPath);

            Snapshot after = SnapshotDirectory(source);
            Assert.Equal(before.FileCount, after.FileCount);
            Assert.Equal(before.TotalBytes, after.TotalBytes);
            Assert.False(
                Directory.Exists(Path.Combine(source, ".ArchiveFixer.work")),
                "源目录里不应出现工作区");

            // 默认收尾：内层分卷已按档彻底删除，输出目录只剩那一个外层文件。
            string[] outputFiles = Directory
                .GetFiles(output, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray()!;

            Assert.Equal(
                new[] { Path.GetFileName(result.OuterPath) },
                outputFiles);
        }
        finally
        {
            try
            {
                if (Directory.Exists(output))
                {
                    Directory.Delete(output, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static Snapshot SnapshotDirectory(string path)
    {
        List<string> files = Directory
            .EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .ToList();

        return new Snapshot(files.Count, files.Sum(file => new FileInfo(file).Length));
    }

    private readonly struct Snapshot
    {
        public Snapshot(int fileCount, long totalBytes)
        {
            FileCount = fileCount;
            TotalBytes = totalBytes;
        }

        public int FileCount { get; }

        public long TotalBytes { get; }
    }
}
