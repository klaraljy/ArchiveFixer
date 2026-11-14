using System;
using System.IO;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **同一份文件不许数两次** —— 快照的"路径清单"必须与拍快照那一刻同形同序。
    ///
    /// <para><b>真机现场（2026-10-10，EEEE）</b>：`111.z11111111110删除3` 被改名成 `111.z03` 之后，
    /// 归组服务把它写进自己的 <see cref="ArchiveTask.VolumePaths"/>（1 卷组 = 这一单自己），
    /// 而源文件快照是**更早**拍的（那时 VolumePaths 还空着）。⇒
    /// <see cref="ArchiveTask.GetSnapshotPaths"/> 返回 2 条（<c>CurrentPath</c> + <c>VolumePaths[0]</c>，
    /// **同一个文件**），快照里只有 1 条，而 <c>SourceFileSnapshot.Compare</c> 是**按下标**比的
    /// ⇒ 第 2 条被当成"后来新出现的文件"⇒ 报「源文件已变化（修改时间 没有记录 → …）」
    /// ⇒ 那一单一次引擎都没调就被拦下。</para>
    ///
    /// <para><b>运行期证据（不是推断）</b>：debug-mcp 停在
    /// <c>ExtractionCoordinator.cs:498</c> 时读到 <c>CurrentPath == VolumePaths[0]</c>、
    /// <c>VolumePaths.Count == 1</c>、<c>SourceSnapshot.Files.Count == 1</c>、
    /// <c>comparison.Changes[0].IsVolume == true</c>、<c>PreviousLength == 0</c>、
    /// <c>PreviousLastWriteTimeUtc.Ticks == 0</c>（正是 `before == null` 那一支）。</para>
    /// </summary>
    public class SourceSnapshotDedupTests : IDisposable
    {
        private readonly string _root;

        public SourceSnapshotDedupTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerSnapshotDedup", Guid.NewGuid().ToString("N"));
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

        /// <summary>
        /// 快照拍完之后，归组才把"这一卷就是这一单自己"写进账里 ⇒ **不许**因此判成"源文件变了"。
        /// 判据：<see cref="ArchiveTask.GetSnapshotPaths"/> 同一个物理路径只出现一次。
        /// </summary>
        [Fact]
        public void 一卷组就是这一单自己_账写晚了_也不许判成源文件变了()
        {
            string path = CreateFile("111.z03", 4096);

            var task = new ArchiveTask(path, 1);

            // ① 识别完成那一刻拍快照（那时还没有分卷账）。
            task.CaptureSourceSnapshot();

            // ② 归组服务随后把"这一卷"写进账里（`VolumeGroupingService.ApplyGroupInfo` 就是这么写的）。
            task.VolumePaths.Add(path);

            Assert.Single(task.GetSnapshotPaths());

            SourceChangeResult? comparison = task.CompareWithSourceSnapshot();

            Assert.NotNull(comparison);
            Assert.False(
                comparison!.Changed,
                "同一个文件被数两次 ⇒ 会被误判成「源文件已变化」。Changes=" + comparison.Changes.Count);
        }

        /// <summary>
        /// **哨兵**：真的多出来一份**另一个文件**时，那条红线一个字都不许松 —— 照样判变化。
        /// （修去重时最容易顺手把这条一起放行，所以专门钉住。）
        /// </summary>
        [Fact]
        public void 账里真的多出另一个文件_照样判得出变化()
        {
            string first = CreateFile("111.z03", 4096);
            string second = CreateFile("111.z04", 4096);

            var task = new ArchiveTask(first, 1);
            task.CaptureSourceSnapshot();

            task.VolumePaths.Add(second);

            SourceChangeResult? comparison = task.CompareWithSourceSnapshot();

            Assert.NotNull(comparison);
            Assert.True(comparison!.Changed, "后来才出现的另一个文件必须照样判成变化");
            Assert.Contains(
                comparison.Changes,
                change => string.Equals(
                    Path.GetFileName(change.Path),
                    "111.z04",
                    StringComparison.OrdinalIgnoreCase));
        }

        private string CreateFile(string name, int bytes)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllBytes(path, new byte[bytes]);

            return path;
        }
    }
}
