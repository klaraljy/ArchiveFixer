using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **落点 = 入口包所在的那一层**（用户 2026-10-05 真机第九批 `CCCC` 定的口径，他连着说了三遍；
    /// 逐句原话见 <see cref="GroupVolumeDirectory"/> 的类注释）。
    ///
    /// <para>这一档钉两件事：</para>
    /// <list type="number">
    /// <item><description><b>哪一份是入口包</b>：引擎要打开、要对它输密码的那一份 —— 跨盘 ZIP 族 = 本体
    /// <c>X.zip</c>（⛔ 不是 <c>.z01</c>：那一族里引擎打开的是本体，用户原话「这个 zip 分卷是对后缀为
    /// <c>.zip</c>（`111.zip`）进行操作」）、7z 族 = <c>X.001</c>、RAR 族 = 第 1 卷。</description></item>
    /// <item><description><b>落点跟着它走**而不是跟着任务表顺序走**</b>：这一单自己的文件散在 `111(4)\`、
    /// 而入口包在 `111\111\` ⇒ 产物落在 `111\111\111\`。</description></item>
    /// </list>
    /// </summary>
    public class GroupVolumeDirectoryTests : IDisposable
    {
        private readonly string _root;

        public GroupVolumeDirectoryTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerEntryVolume", Guid.NewGuid().ToString("N"));
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
                // 清不掉只是脏一点。
            }
        }

        // ════════════════ A. 哪一份是入口包：三族各一格 ════════════════

        /// <summary>
        /// <b>跨盘 ZIP 族</b>：入口包 = <b>本体 <c>111.zip</c></b>（引擎要打开、要对它输密码的那一份），
        /// ⛔ 不是 <c>.z01</c>、⛔ 更不是散在别处的 <c>.z0删除2/.z0删除3</c>。
        ///
        /// <para>真机 CCCC 里本体是从 `111.rar` 解出来的，落在 `111\111\`；
        /// 产物因此该落在 `111\111\111\`，而不是被搬到 `111(4)` 之后落在 `111(4)\111\`。</para>
        /// </summary>
        [Fact]
        public void 跨盘zip族_入口包是本体zip_不是散着的续卷()
        {
            string homeDirectory = Path.Combine(_root, "111", "111");
            string third = Path.Combine(_root, "111(3)");
            string fourth = Path.Combine(_root, "111(4)");

            Directory.CreateDirectory(homeDirectory);
            Directory.CreateDirectory(third);
            Directory.CreateDirectory(fourth);

            string tail = Touch(homeDirectory, "111.zip");
            string diskOne = Touch(third, "111.z0删除1");
            string diskThree = Touch(fourth, "111.z0删除3");

            GroupVolumeDirectory.Entry entry = GroupVolumeDirectory.Resolve(new[] { diskThree, diskOne, tail });

            Assert.Equal(tail, entry.Path, ignoreCase: true);
            Assert.Equal(homeDirectory, entry.Directory, ignoreCase: true);
        }

        /// <summary>
        /// **入口包不在盘上时如实返回空**：本体正压在 `111.rar` 里、盘上只剩散着的续卷 ⇒
        /// ⛔ 不许拿"散着的那一片"冒充入口（那正是旧口径把落点带到 `111(4)` 的机制），
        /// 由调用方退回"产出它的那条链"（见 `ExtractionCoordinator.ResolveOwnerChainDirectory`）。
        /// </summary>
        [Fact]
        public void 入口包不在盘上_如实返回空()
        {
            string third = Path.Combine(_root, "111(3)");
            string fourth = Path.Combine(_root, "111(4)");

            Directory.CreateDirectory(third);
            Directory.CreateDirectory(fourth);

            Touch(third, "111.z0删除2");
            Touch(fourth, "111.z0删除3");

            GroupVolumeDirectory.Entry entry = GroupVolumeDirectory.Resolve(new[]
            {
                Path.Combine(third, "111.z0删除2"),
                Path.Combine(fourth, "111.z0删除3")
            });

            Assert.Equal(string.Empty, entry.Path);
            Assert.Equal(string.Empty, entry.Directory);
        }

        /// <summary>
        /// **7-Zip 切的 zip 通用分片**（`.zip.001` / `.zip.002`）：用户 2026-10-05 明确点名这一族也在内
        /// （「zip 分卷是 `.zip` 或者 `.zip.001`（这个是 7z 压缩的 zip 分卷）」）⇒ 入口包 = `.zip.001`。
        /// </summary>
        [Fact]
        public void zip通用分片_入口包是zip001()
        {
            string firstDirectory = Path.Combine(_root, "split-first");
            string scattered = Path.Combine(_root, "split-scattered");

            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(scattered);

            string first = Touch(firstDirectory, "111.zip.001");
            string second = Touch(scattered, "111.zip.002");

            GroupVolumeDirectory.Entry entry = GroupVolumeDirectory.Resolve(new[] { second, first });

            Assert.Equal(first, entry.Path, ignoreCase: true);
            Assert.Equal(firstDirectory, entry.Directory, ignoreCase: true);
        }

        /// <summary>**7z 族**：入口包 = <c>X.7z.001</c>，与它同组的续卷都在别的层。</summary>
        [Fact]
        public void 七z族_入口包是001()
        {
            string firstDirectory = Path.Combine(_root, "first");
            string scattered = Path.Combine(_root, "scattered");

            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(scattered);

            string first = Touch(firstDirectory, "set.7z.001");
            string second = Touch(scattered, "set.7z.002");
            string third = Touch(scattered, "set.7z.003");

            GroupVolumeDirectory.Entry entry = GroupVolumeDirectory.Resolve(new[] { third, second, first });

            Assert.Equal(first, entry.Path, ignoreCase: true);
            Assert.Equal(firstDirectory, entry.Directory, ignoreCase: true);
        }

        /// <summary>
        /// **RAR 族**：新式 <c>partN</c> 族的入口是 <c>part1.rar</c>、老式族的本体是 <c>.rar</c>
        /// —— 两格都钉住（用户原话「7z 和 rar 都是对第一卷操作的」）。
        /// </summary>
        [Fact]
        public void RAR族_入口包_新式part1与老式本体都对()
        {
            string partDirectory = Path.Combine(_root, "part");
            string oldDirectory = Path.Combine(_root, "old");

            Directory.CreateDirectory(partDirectory);
            Directory.CreateDirectory(oldDirectory);

            string part1 = Touch(partDirectory, "风景01.part1.rar");
            string part2 = Touch(partDirectory, "风景01.part2.rar");

            GroupVolumeDirectory.Entry modern = GroupVolumeDirectory.Resolve(new[] { part2, part1 });

            Assert.Equal(part1, modern.Path, ignoreCase: true);
            Assert.Equal(partDirectory, modern.Directory, ignoreCase: true);

            string body = Touch(oldDirectory, "风景02.rar");
            string r00 = Touch(oldDirectory, "风景02.r00");

            GroupVolumeDirectory.Entry legacy = GroupVolumeDirectory.Resolve(new[] { r00, body });

            Assert.Equal(body, legacy.Path, ignoreCase: true);
            Assert.Equal(oldDirectory, legacy.Directory, ignoreCase: true);
        }

        /// <summary>不是卷成员（普通的 `111.rar` / `111(2)_.zip`）⇒ 返回空。</summary>
        [Fact]
        public void 不是卷成员_如实返回空()
        {
            Directory.CreateDirectory(Path.Combine(_root, "111(2)"));

            GroupVolumeDirectory.Entry entry = GroupVolumeDirectory.Resolve(new[]
            {
                Path.Combine(_root, "111.rar"),
                Path.Combine(_root, "111(2)", "111(2)_.zip")
            });

            Assert.Equal(string.Empty, entry.Path);
            Assert.Equal(string.Empty, entry.Directory);
        }

        /// <summary>
        /// **别族的同名文件不许顶掉这一组的入口**：同一层里躺着 `111.7z.001`（另一组）与 `111.zip` 时，
        /// 答案必须是 `111.zip`（靠既有那把"同一组"的尺子：族 + 基名）。
        /// </summary>
        [Fact]
        public void 别族的同名文件不许顶掉这一组的入口()
        {
            string directory = Path.Combine(_root, "cross-family");

            Directory.CreateDirectory(directory);

            string tail = Touch(directory, "111.zip");
            string sevenZipFirst = Touch(directory, "111.7z.001");
            string zipDiskOne = Touch(directory, "111.z01");

            GroupVolumeDirectory.Entry entry = GroupVolumeDirectory.Resolve(new[]
            {
                sevenZipFirst,
                zipDiskOne,
                tail
            });

            Assert.Equal(tail, entry.Path, ignoreCase: true);
        }

        /// <summary>
        /// **"自己算出来是 1"的单卷不算入口**：孤零零一个 <c>set.7z.001</c>（旁边没有同组第二个成员）
        /// ⇒ 返回空（⛔ 不靠单文件自己的卷序下结论）。
        /// </summary>
        [Fact]
        public void 孤零零一个001不算入口()
        {
            string directory = Path.Combine(_root, "lonely");

            Directory.CreateDirectory(directory);

            Touch(directory, "set.7z.001");

            GroupVolumeDirectory.Entry entry = GroupVolumeDirectory.Resolve(new[]
            {
                Path.Combine(directory, "set.7z.001")
            });

            Assert.Equal(string.Empty, entry.Path);
            Assert.Equal(string.Empty, entry.Directory);
        }

        /// <summary>空输入 / 全是空串：如实返回空，⛔ 不抛。</summary>
        [Fact]
        public void 空输入如实返回空()
        {
            Assert.Equal(string.Empty, GroupVolumeDirectory.Resolve(null).Path);
            Assert.Equal(string.Empty, GroupVolumeDirectory.Resolve(Array.Empty<string>()).Path);
            Assert.Equal(string.Empty, GroupVolumeDirectory.Resolve(new string?[] { null, string.Empty }).Path);
        }

        // ════════════════ B. 落点跟着入口包走 ════════════════

        /// <summary>
        /// **落点 = 入口包所在那一层**：这一单自己的文件在 `111(4)\`（散着的那一片），
        /// 而入口包 `111.zip` 在 `111\111\` ⇒ 产物落在 `111\111\111\`。
        ///
        /// <para>⛔ 判据里**没有**"任务表第一单"这一档（旧口径就是它：谁排在前面落点跟谁走，
        /// 真机上就落到了 `111(4)\111`）。</para>
        /// </summary>
        [Fact]
        public void 落点取入口包所在那一层_不取这一单自己那一层()
        {
            string homeDirectory = Path.Combine(_root, "111", "111");
            string ownDirectory = Path.Combine(_root, "111(4)");

            Directory.CreateDirectory(homeDirectory);
            Directory.CreateDirectory(ownDirectory);

            string tail = Touch(homeDirectory, "111.zip");
            string own = Touch(ownDirectory, "111.z0删除3");

            var task = new ArchiveTask(own)
            {
                IsVolumeGroup = true,
                VolumeGroupKey = "111"
            };

            task.VolumePaths.Add(own);
            task.VolumePaths.Add(tail);

            var pathService = new PathService { DataRootDirectory = Path.Combine(_root, "data") };

            OutputPlacementResult placement = pathService.ResolveOutputPlacement(task, new ExtractOptions());

            Assert.True(placement.Success, placement.Message);
            Assert.Equal(
                Path.Combine(homeDirectory, "111"),
                placement.DestinationDirectory,
                ignoreCase: true);
        }

        /// <summary>
        /// **对照**：入口包与这一单自己的文件**在同一层**时，结果与改动前逐字相同
        /// （⛔ 这一档不许有任何惊喜 —— 绝大多数调用点就是它）。
        /// </summary>
        [Fact]
        public void 对照_入口与自己在同一层_落点一个字不变()
        {
            string directory = Path.Combine(_root, "same");

            Directory.CreateDirectory(directory);

            string first = Touch(directory, "set.7z.001");

            var task = new ArchiveTask(first)
            {
                IsVolumeGroup = true,
                VolumeGroupKey = "set.7z"
            };

            task.VolumePaths.Add(first);

            var pathService = new PathService { DataRootDirectory = Path.Combine(_root, "data") };

            OutputPlacementResult placement = pathService.ResolveOutputPlacement(task, new ExtractOptions());

            Assert.True(placement.Success, placement.Message);
            Assert.Equal(Path.Combine(directory, "set"), placement.DestinationDirectory, ignoreCase: true);
        }

        /// <summary>入口包已经不在盘上（还压在包里）⇒ 落点退回这一单自己那一层（行为与改动前逐字相同）。</summary>
        [Fact]
        public void 入口包不在盘上_落点退回这一单自己那一层()
        {
            string ownDirectory = Path.Combine(_root, "111(4)");
            string missingHome = Path.Combine(_root, "111", "111");

            Directory.CreateDirectory(ownDirectory);

            string own = Touch(ownDirectory, "111.z0删除3");

            var task = new ArchiveTask(own)
            {
                IsVolumeGroup = true,
                VolumeGroupKey = "111"
            };

            task.VolumePaths.Add(own);
            task.VolumePaths.Add(Path.Combine(missingHome, "111.zip"));

            var pathService = new PathService { DataRootDirectory = Path.Combine(_root, "data") };

            OutputPlacementResult placement = pathService.ResolveOutputPlacement(task, new ExtractOptions());

            Assert.True(placement.Success, placement.Message);
            Assert.Equal(Path.Combine(ownDirectory, "111"), placement.DestinationDirectory, ignoreCase: true);
        }

        /// <summary>
        /// **指定位置那一档与入口包无关**：用户自己选了根，落点就跟着那个根走
        /// （⛔ 入口包只影响"未指定位置"那一档）。
        /// </summary>
        [Fact]
        public void 指定位置那一档_不受入口包影响()
        {
            string homeDirectory = Path.Combine(_root, "111", "111");
            string ownDirectory = Path.Combine(_root, "111(4)");
            string customRoot = Path.Combine(_root, "out");

            Directory.CreateDirectory(homeDirectory);
            Directory.CreateDirectory(ownDirectory);
            Directory.CreateDirectory(customRoot);

            string tail = Touch(homeDirectory, "111.zip");
            string own = Touch(ownDirectory, "111.z0删除3");

            var task = new ArchiveTask(own)
            {
                IsVolumeGroup = true,
                VolumeGroupKey = "111"
            };

            task.VolumePaths.Add(own);
            task.VolumePaths.Add(tail);

            var options = new ExtractOptions
            {
                ExtractToOriginalDirectory = false,
                CustomOutputDirectory = customRoot
            };

            options.Normalize();

            var pathService = new PathService { DataRootDirectory = Path.Combine(_root, "data") };

            OutputPlacementResult placement = pathService.ResolveOutputPlacement(task, options);

            Assert.True(placement.Success, placement.Message);
            Assert.StartsWith(customRoot, placement.DestinationDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("111(4)", placement.DestinationDirectory, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 纯函数那一档：`OutputPlacement` 只在入口包与源包**不同层**时才改目标根；同层 / 空串一律原样。
        /// </summary>
        [Fact]
        public void 目标根_只在入口包与源包不同层时才改()
        {
            string homeDirectory = Path.Combine(_root, "111", "111");
            string ownDirectory = Path.Combine(_root, "111(4)");
            string tail = Path.Combine(homeDirectory, "111.zip");
            string own = Path.Combine(ownDirectory, "111.z0删除3");

            OutputPlacementResult byEntry = OutputPlacement.ResolveDestinationDirectory(
                own,
                OutputPlacementMode.PerArchiveSubfolder,
                entryArchivePath: tail);

            Assert.True(byEntry.Success, byEntry.Message);
            Assert.Equal(
                Path.Combine(homeDirectory, "111"),
                byEntry.DestinationDirectory,
                ignoreCase: true);

            OutputPlacementResult empty = OutputPlacement.ResolveDestinationDirectory(
                own,
                OutputPlacementMode.PerArchiveSubfolder,
                entryArchivePath: string.Empty);

            Assert.True(empty.Success, empty.Message);
            Assert.Equal(Path.Combine(ownDirectory, "111"), empty.DestinationDirectory, ignoreCase: true);

            OutputPlacementResult sameLayer = OutputPlacement.ResolveDestinationDirectory(
                own,
                OutputPlacementMode.PerArchiveSubfolder,
                entryArchivePath: Path.Combine(ownDirectory, "111.zip"));

            Assert.True(sameLayer.Success, sameLayer.Message);
            Assert.Equal(Path.Combine(ownDirectory, "111"), sameLayer.DestinationDirectory, ignoreCase: true);
        }

        /// <summary>包名那一层仍然是这一组的基名 `111`（换根不许把文件夹名字也换掉）。</summary>
        [Fact]
        public void 包名那一层仍然是这一组的基名()
        {
            string nested = Path.Combine(_root, "111", "111", "inner");
            string ownDirectory = Path.Combine(_root, "111(3)");

            Directory.CreateDirectory(nested);
            Directory.CreateDirectory(ownDirectory);

            string tail = Touch(nested, "111.zip");
            string own = Touch(ownDirectory, "111.z0删除2");

            var task = new ArchiveTask(own)
            {
                IsVolumeGroup = true,
                VolumeGroupKey = "111"
            };

            task.VolumePaths.Add(own);
            task.VolumePaths.Add(tail);

            var pathService = new PathService { DataRootDirectory = Path.Combine(_root, "data") };

            OutputPlacementResult placement = pathService.ResolveOutputPlacement(task, new ExtractOptions());

            Assert.True(placement.Success, placement.Message);
            Assert.Equal(Path.Combine(nested, "111"), placement.DestinationDirectory, ignoreCase: true);
        }

        /// <summary>
        /// 落点那一行的文案（唯一出口 `StatusText.VolumeGroupFirstVolumeLandingFormat`）：
        /// ⛔ 用户可见字符串里不许写 Markdown（`UserFacingTextTests` 全量扫），强调一律用「」。
        /// </summary>
        [Fact]
        public void 落点那一行的文案_不带Markdown标记()
        {
            Assert.DoesNotContain("**", StatusText.VolumeGroupFirstVolumeLandingFormat, StringComparison.Ordinal);
            Assert.Contains("{0}", StatusText.VolumeGroupFirstVolumeLandingFormat, StringComparison.Ordinal);
            Assert.Contains("{1}", StatusText.VolumeGroupFirstVolumeLandingFormat, StringComparison.Ordinal);
            Assert.Contains("{2}", StatusText.VolumeGroupFirstVolumeLandingFormat, StringComparison.Ordinal);
            Assert.Contains("{3}", StatusText.VolumeGroupFirstVolumeLandingFormat, StringComparison.Ordinal);
        }

        /// <summary>判据只读名字与"在不在"，⛔ 不改名、不移动、不建链接（跑完盘上一个字节没变）。</summary>
        [Fact]
        public void 判据一个字节都不改()
        {
            string directory = Path.Combine(_root, "readonly");

            Directory.CreateDirectory(directory);

            string tail = Touch(directory, "111.zip");
            string diskOne = Touch(directory, "111.z01");

            string[] before = Directory.GetFiles(directory).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()!;
            var sizes = before.ToDictionary(x => x, x => new FileInfo(x).Length, StringComparer.OrdinalIgnoreCase);

            GroupVolumeDirectory.Entry entry = GroupVolumeDirectory.Resolve(new[] { diskOne, tail });

            Assert.Equal(tail, entry.Path, ignoreCase: true);

            string[] after = Directory.GetFiles(directory).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()!;

            Assert.Equal(before, after);

            foreach ((string path, long size) in sizes)
            {
                Assert.Equal(size, new FileInfo(path).Length);
            }
        }

        private static string Touch(string directory, string name)
        {
            string path = Path.Combine(directory, name);

            File.WriteAllBytes(path, new byte[] { 0x50, 0x4B, 0x03, 0x04 });

            return path;
        }
    }
}
