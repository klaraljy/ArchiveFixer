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
        public async Task 普通压缩包不会被当成分卷组()
        {
            Touch("normal.7z");
            Touch("normal.zip");

            List<ArchiveTask> tasks = await ScanAsync();

            Assert.Equal(2, tasks.Count);
            Assert.All(tasks, t => Assert.False(t.IsVolumeGroup));
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
