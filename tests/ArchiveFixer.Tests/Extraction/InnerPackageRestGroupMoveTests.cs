using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 守门用例（用户 2026-09-28 真机报的缺陷）：**内层包本身是分卷组时，其余物要整组一起搬**。
    ///
    /// <para>现场：外层包解出来一个 4 卷内层包（`amb.7z.001删除` … `.004删除`），一键处理只把
    /// **第一卷**搬进其余物并彻底删掉，`.002/.003/.004` 留在成品目录里 —— 用户原话
    /// "同样犯了 winrar 会犯的问题：只删除 .001 为首的分卷头文件，其他分卷还残留着"。</para>
    ///
    /// <para>这里钉两件在修复里真正承重的事：
    /// ① 归组之后任务身上确实带着**整组**（链尾搬运就是按 <c>VolumePaths</c> 取清单的）；
    /// ② 万一没拿到整组，兜底判据 <c>HasSiblingVolumeBeside</c> 必须认出"同目录还有同组的卷"，
    ///    从而把"搬一半"拦下来（宁可不动）。</para>
    /// </summary>
    public class InnerPackageRestGroupMoveTests : IDisposable
    {
        private readonly string _root;

        public InnerPackageRestGroupMoveTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "af-restgroup-" + Guid.NewGuid().ToString("N"));
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

        /// <summary>造一个"像真分卷组"的目录：整卷大小一致 + 尾卷更小（尺寸规律那一层证据要成立）。</summary>
        private string[] MakeDisguisedVolumeSet(string baseName = "amb.7z", int volumeCount = 4)
        {
            var paths = new List<string>();

            for (int i = 1; i <= volumeCount; i++)
            {
                string name = $"{baseName}.{i:D3}删除";
                string path = Path.Combine(_root, name);
                long size = i == volumeCount ? 512 : 1024;
                File.WriteAllBytes(path, new byte[size]);
                paths.Add(path);
            }

            return paths.ToArray();
        }

        [Fact]
        public void 内层包是分卷组时_归组之后任务带着整组四卷()
        {
            string[] volumes = MakeDisguisedVolumeSet();

            var candidates = volumes
                .Select(p => new VolumeCandidate { Path = p, Size = new FileInfo(p).Length })
                .ToList();

            VolumeGroup group = Assert.Single(VolumeGroupDetector.Group(candidates));

            var task = new ArchiveTask(volumes[0], 1);
            new VolumeGroupingService().ApplyGroupInfo(task, group);

            // ⚠ 链尾那一步取的就是 VolumePaths：这里少一卷，其余物那边就会少搬一卷（真机就是这么漏的）。
            Assert.True(task.IsVolumeGroup);
            Assert.Equal(4, task.VolumePaths.Count);
            Assert.Contains(volumes[3], task.VolumePaths);
        }

        [Fact]
        public void 拿不到整组时的兜底判据_能认出同目录还有同组的卷()
        {
            string[] volumes = MakeDisguisedVolumeSet();

            // 危险形态：没有分组信息（IsVolumeGroup=false）却要去搬第一卷 → 兜底必须拦下
            Assert.True(
                ExtractionCoordinator.HasSiblingVolumeBeside(volumes[0]),
                "同目录里躺着同组的 .002/.003/.004，兜底判据必须为真（否则就会只搬走第一卷）");
        }

        [Fact]
        public void 兜底判据_单卷包不误伤()
        {
            string single = Path.Combine(_root, "solo.7z.001删除");
            File.WriteAllBytes(single, new byte[128]);

            Assert.False(
                ExtractionCoordinator.HasSiblingVolumeBeside(single),
                "同目录没有同组的别的卷时不许拦（正常单文件照旧能搬）");

            string plain = Path.Combine(_root, "plain.7z");
            File.WriteAllBytes(plain, new byte[128]);
            Assert.False(ExtractionCoordinator.HasSiblingVolumeBeside(plain), "不是分卷就更不该拦");
        }

        [Fact]
        public void 兜底判据_别组的卷不算同组()
        {
            string[] mine = MakeDisguisedVolumeSet("mine.7z", 2);

            // 另一组（不同基名）躺在同一个目录里 —— 不该被算成"我这组的兄弟卷"
            File.WriteAllBytes(Path.Combine(_root, "other.7z.001删除"), new byte[64]);
            File.WriteAllBytes(Path.Combine(_root, "other.7z.002删除"), new byte[64]);

            string onlyMine = Path.Combine(_root, "solo2.7z.001删除");
            File.WriteAllBytes(onlyMine, new byte[64]);

            Assert.True(ExtractionCoordinator.HasSiblingVolumeBeside(mine[0]));
            Assert.False(
                ExtractionCoordinator.HasSiblingVolumeBeside(onlyMine),
                "solo2 这一组只有一卷，别组的 other.* 不算它的兄弟");
        }
    }
}
