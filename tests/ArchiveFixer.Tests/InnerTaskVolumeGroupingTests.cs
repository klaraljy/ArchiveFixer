using System;
using System.IO;
using ArchiveFixer.Models;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 内层包（续解层加进来的任务）也必须**归组**：`IsVolumeGroup` + 整组 `VolumePaths`。
    ///
    /// <para>2026-09-28 真机最后一环：内层任务是走 `AddPathsAsync(..., suppressAutoScan: true)` 加进来的，
    /// 那条路不跑扫描期的分卷归组 → 内层分卷组 `IsVolumeGroup = false` → 链尾"把内层包收进其余物"的
    /// 安全闸门（拿不到整组就不搬）把它挡住 → 用户的 `amb.7z.001..004` 一直赖在成品目录里。
    /// 这条用例钉的就是"按目录真实文件归组"这一步。</para>
    /// </summary>
    public class InnerTaskVolumeGroupingTests : IDisposable
    {
        private readonly string _root;

        public InnerTaskVolumeGroupingTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "af-innergroup-" + Guid.NewGuid().ToString("N"));
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
                // 清理失败不影响判据
            }
        }

        [Fact]
        public void 内层分卷组_按目录归组后带着整组三卷()
        {
            // 标准名的一组分卷（末卷更小 —— 尺寸规律那一层证据要成立）
            var volumes = new (string Name, int Size)[]
            {
                ("amb.7z.001", 1024),
                ("amb.7z.002", 1024),
                ("amb.7z.003", 512)
            };

            foreach ((string name, int size) in volumes)
            {
                File.WriteAllBytes(Path.Combine(_root, name), new byte[size]);
            }

            var task = new ArchiveTask(Path.Combine(_root, "amb.7z.001"), 1);

            OneClickCoordinator.ApplyVolumeGroupingFromDirectory(task);

            Assert.True(task.IsVolumeGroup, "内层分卷组必须被认成一组（否则链尾搬运会被安全闸门挡住）");
            Assert.Equal(3, task.VolumePaths.Count);
        }

        [Fact]
        public void 内层单独一个包_不会被硬凑成分卷组()
        {
            File.WriteAllBytes(Path.Combine(_root, "solo.7z"), new byte[256]);

            var task = new ArchiveTask(Path.Combine(_root, "solo.7z"), 1);

            OneClickCoordinator.ApplyVolumeGroupingFromDirectory(task);

            Assert.False(task.IsVolumeGroup);
        }
    }
}
