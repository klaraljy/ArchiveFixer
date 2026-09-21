using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「一组分卷 = 一个任务」的集成测试（AGENTS.md §9.3）。
    ///
    /// 用真实的临时文件跑 <see cref="FileScanService"/>，验证扫描结果里分卷被归成一行；
    /// 文件内容是占位字节 —— 归组只看名字，不需要真归档。
    /// </summary>
    public class VolumeGroupingServiceTests : IDisposable
    {
        private readonly string _root;

        public VolumeGroupingServiceTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerVolumeTests", Guid.NewGuid().ToString("N"));
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

        private void Touch(string relativeName, int size = 16)
        {
            string path = Path.Combine(_root, relativeName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[size]);
        }

        private async Task<List<ArchiveTask>> ScanAsync()
        {
            var options = new ScanOptions
            {
                RecursiveScan = true,
                ScanMode = "ScanAllFiles"
            };

            return await new FileScanService().ScanPathsAsync(new[] { _root }, options, CancellationToken.None);
        }

        [Fact]
        public async Task 数字分卷归成一个任务并只从001启动()
        {
            Touch("volume.7z.001");
            Touch("volume.7z.002");
            Touch("volume.7z.003");

            List<ArchiveTask> tasks = await ScanAsync();

            ArchiveTask task = Assert.Single(tasks);
            Assert.True(task.IsVolumeGroup, "应被标记为分卷组");
            Assert.Equal(3, task.VolumeCount);
            Assert.True(task.IsVolumeComplete);
            Assert.Empty(task.MissingVolumeNames);
            Assert.EndsWith("volume.7z.001", task.CurrentPath, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task 缺号时必须报出缺哪一卷且标记不完整()
        {
            Touch("archive.7z.001");
            Touch("archive.7z.003");

            ArchiveTask task = Assert.Single(await ScanAsync());

            Assert.True(task.IsVolumeGroup);
            Assert.False(task.IsVolumeComplete);
            Assert.Contains("archive.7z.002", task.MissingVolumeNames);
            Assert.Contains("缺", task.VolumeInfoText, StringComparison.Ordinal);
        }

        [Fact]
        public async Task 不同基名的分卷不串组()
        {
            Touch("a.7z.001");
            Touch("a.7z.002");
            Touch("b.7z.001");
            Touch("b.7z.002");

            List<ArchiveTask> tasks = await ScanAsync();

            Assert.Equal(2, tasks.Count);
            Assert.All(tasks, t => Assert.True(t.IsVolumeGroup));
            Assert.Equal(2, tasks.Count(t => t.VolumeCount == 2));
        }

        [Fact]
        public async Task 普通压缩包不会被当成分卷组()
        {
            Touch("normal.7z");
            Touch("normal.zip");

            List<ArchiveTask> tasks = await ScanAsync();

            Assert.Equal(2, tasks.Count);
            Assert.All(tasks, t => Assert.False(t.IsVolumeGroup));
        }

        [Fact]
        public async Task 分卷与其他包混在一起时互不影响()
        {
            Touch("movie.part1.rar");
            Touch("movie.part2.rar");
            Touch("photo.jpg");
            Touch("other.7z.001");

            List<ArchiveTask> tasks = await ScanAsync();

            // movie 一组（2 卷）+ other 一组（1 卷）+ photo.jpg 一个普通任务
            Assert.Equal(3, tasks.Count);

            ArchiveTask movie = Assert.Single(tasks, t => t.CurrentPath.EndsWith("movie.part1.rar", StringComparison.OrdinalIgnoreCase));
            Assert.True(movie.IsVolumeGroup);
            Assert.Equal(2, movie.VolumeCount);

            ArchiveTask other = Assert.Single(tasks, t => t.CurrentPath.EndsWith("other.7z.001", StringComparison.OrdinalIgnoreCase));
            Assert.True(other.IsVolumeGroup);
            Assert.Equal(1, other.VolumeCount);

            Assert.Single(tasks, t => t.CurrentPath.EndsWith("photo.jpg", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void 分卷任务默认字段是安全的()
        {
            var task = new ArchiveTask(@"C:\t\normal.7z");

            Assert.False(task.IsVolumeGroup);
            Assert.Equal(0, task.VolumeCount);
            Assert.True(task.IsVolumeComplete);
            Assert.Empty(task.MissingVolumeNames);
            Assert.Equal(string.Empty, task.VolumeInfoText);
        }
    }
}
