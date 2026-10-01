using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **"账上的分卷清单过期"这一档的守门用例**（用户 2026-10-01 第三、第四次真机）：
    /// 批首整组改名把 `222.zscip` → `222.zip`、`222.z删除01` → `222.z01` 都改对了，
    /// 可任务账上的清单还是**扫描期那一份**（那时 `222.zscip` 还没被识别成归档 ⇒ 只归出 1 卷），
    /// 而源包搬运读的就是那份清单 ⇒ **400 MB 的 `222.z01` 留在源目录里**（用户看到的就是这个）。
    ///
    /// <para>两条一起钉：① 按名字能把同目录那一组补出来（唯一出口
    /// <see cref="OneClickCoordinator.ResolveVolumeGroupFromDirectoryByName"/>）；② 补出来之后再问
    /// 源包清单（<see cref="SourcePackageMover.ResolveSourceGroup"/>），**两卷都在**。</para>
    ///
    /// <para>⛔ 对照组：没有卷兄弟的普通包不许被误判成分卷组（否则"整组搬"会把无关文件卷进来）。</para>
    /// </summary>
    public sealed class VolumeGroupingRefreshTests : IDisposable
    {
        private readonly string _root;

        public VolumeGroupingRefreshTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerGroupRefresh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // 临时目录清不掉不影响结论。
            }
        }

        /// <summary>
        /// **真机那个形状**：`222.zip`（真机里是改好名的本体）+ `222.z01`（改好名的续卷），
        /// 任务账上只有本体一卷 ⇒ 按名字补清单必须补出**两卷**，源包清单也必须跟着变成两卷。
        /// </summary>
        [Fact]
        public void 账上只有一卷_按名字能补出两卷_源包清单跟着变两卷()
        {
            string directory = NewDirectory("case-222");

            string body = WriteFile(directory, "222.zip", 64);
            string part = WriteFile(directory, "222.z01", 64);

            var task = new ArchiveTask(body, 1)
            {
                IsArchive = true,
                DetectedFormat = "ZIP"
            };

            // 扫描期那一份：只有本体一卷（正是真机账上的样子）。
            task.IsVolumeGroup = true;
            task.VolumePaths.Add(body);

            ArchiveFixer.Detection.VolumeGroup? group = OneClickCoordinator.ResolveVolumeGroupFromDirectoryByName(task, body);

            Assert.NotNull(group);
            Assert.Equal(2, group!.Volumes.Count);
            Assert.Contains(group.Volumes, v => string.Equals(v.Path, body, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(group.Volumes, v => string.Equals(v.Path, part, StringComparison.OrdinalIgnoreCase));

            // 把它补到账上（与管线里同一条路：只增不减的闸门在调用方）。
            new ArchiveFixer.Services.VolumeGroupingService().ApplyGroupInfo(task, group);

            IReadOnlyList<string> sources = SourcePackageMover.ResolveSourceGroup(task);

            Assert.Equal(2, sources.Count);
            Assert.Contains(sources, path => path.EndsWith("222.zip", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(sources, path => path.EndsWith("222.z01", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// **脏名字那一档**：续卷叫 `222.z0sc1`（网盘把 `0` 塞成了 `0sc`）也要认出来
        /// —— 判据转调 <see cref="ExtensionHelper.TrySplitVolumeSegmentTolerant"/> 这个唯一出口。
        /// </summary>
        [Fact]
        public void 续卷名字被塞了垃圾_也照样补得出来()
        {
            string directory = NewDirectory("case-dirty");

            string body = WriteFile(directory, "222.zip", 64);
            string dirty = WriteFile(directory, "222.z0sc1", 64);

            var task = new ArchiveTask(body, 1) { IsArchive = true, DetectedFormat = "ZIP" };

            ArchiveFixer.Detection.VolumeGroup? group = OneClickCoordinator.ResolveVolumeGroupFromDirectoryByName(task, body);

            Assert.NotNull(group);
            Assert.Equal(2, group!.Volumes.Count);
            Assert.Contains(group.Volumes, v => string.Equals(v.Path, dirty, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// **对照组（⛔ 不许误报）**：目录里没有卷兄弟的普通包 ⇒ 补不出组（返回 null），
        /// 这样"重新归组"那一段就不会为一个普通单卷包白跑一趟，更不会把它跟无关文件凑成一组。
        /// </summary>
        [Fact]
        public void 对照组_没有卷兄弟的普通包_补不出组()
        {
            string directory = NewDirectory("case-plain");

            string plain = WriteFile(directory, "movie.7z", 64);
            WriteFile(directory, "readme.txt", 8);

            var task = new ArchiveTask(plain, 1) { IsArchive = true, DetectedFormat = "7Z" };

            Assert.Null(OneClickCoordinator.ResolveVolumeGroupFromDirectoryByName(task, plain));
        }

        /// <summary>
        /// **判据本身**（这一轮真正改对的地方）：`222.zip` + `222.z01` 这一单**必须**被判成"要按名字重算清单"。
        ///
        /// <para>第三报那一轮白改的原因就在这里：当时写的条件是"自己的名字里带卷标记"，
        /// 而 `.zip` 不是卷标记 ⇒ 恒为 false ⇒ 补清单永远跑不到。这条用例钉的就是它。</para>
        /// </summary>
        [Fact]
        public void 本体叫222点zip_也必须判成要重算清单()
        {
            string directory = NewDirectory("case-needs-refresh");

            string body = WriteFile(directory, "222.zip", 64);
            WriteFile(directory, "222.z01", 64);

            var task = new ArchiveTask(body, 1) { IsArchive = true, DetectedFormat = "ZIP" };

            // ⛔ 前提：`.zip` 不是卷标记（这正是老判据为什么会失效）。
            Assert.False(FileNameHelper.IsVolumePartFileName("222.zip"));

            Assert.True(OneClickCoordinator.NeedsVolumeGroupRefreshByName(task, body));

            // 对照组：没有卷兄弟的普通包**不必**重算（省掉白跑一趟）。
            string plain = WriteFile(directory, "movie.7z", 64);

            Assert.False(OneClickCoordinator.NeedsVolumeGroupRefreshByName(
                new ArchiveTask(plain, 2) { IsArchive = true, DetectedFormat = "7Z" },
                plain));
        }

        // ================================================================ 辅助

        private string NewDirectory(string name)
        {
            string path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);

            return path;
        }

        private static string WriteFile(string directory, string name, long size)
        {
            string path = Path.Combine(directory, name);
            File.WriteAllBytes(path, new byte[size]);

            return path;
        }
    }
}
