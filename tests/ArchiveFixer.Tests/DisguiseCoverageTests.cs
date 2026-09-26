using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 四族"简单伪装手法"的覆盖验证（用户 2026-09-22 口述的那四种）。
    ///
    /// <para>四族与它们的判据：</para>
    /// <list type="number">
    /// <item><description>族 1 <b>图片尾部挂包</b>：<c>.jpg</c>/<c>.png</c> = 真图片头 + 追加完整 ZIP；
    ///     必须是「内嵌归档」、**不许改名**、解压走按偏移抠出。</description></item>
    /// <item><description>族 2 <b>视频尾部挂包</b>：<c>.mp4</c> 同法。</description></item>
    /// <item><description>族 3 <b>分卷 + 乱后缀</b>：<c>资料.zip.001</c> 纯改名成 <c>资料.mp4.001</c>；
    ///     必须被认成**一组分卷**（基名取到分卷标记之前）、**不许改名**、整组一起处理。</description></item>
    /// <item><description>族 4 <b>大前缀双面文件</b>：前缀 ≥ 10 MB（超过 7-Zip 的 8 MiB 容忍上限），
    ///     原文件 7z 直接拒绝，只有「按偏移抠出」才解得开。</description></item>
    /// </list>
    ///
    /// <para>
    /// 样本**全部自己造**（临时目录 + 项目内置 7z.exe；RAR 分卷那一档用本机已装的 <c>Rar.exe</c> 现造，
    /// 绝不入库、绝不复制，见 AGENTS.md §8 / docs/引擎与外部工具.md §4）。
    /// 若环境变量 <c>ARCHIVEFIXER_DISGUISE_DIR</c> 指向一个已经造好的样本目录（<c>_tmp\ArchiveFixer\disguise</c>），
    /// 就直接吃那一份真机样本 —— 命令行造出来的文件与测试造出来的逐字节一致（同一套造法）。
    /// </para>
    ///
    /// <para>
    /// ⚠ 全程**不启动 GUI、不显示任何窗口、不动鼠标键盘**（AGENTS.md §13）。
    /// 改名预览那一段用生产代码同一套服务 + 窗口自己的 ViewModel（<see cref="RenamePreviewViewModel"/>）驱动，
    /// 只有"窗口被 Show 出来"这一步没法在无 UI 宿主里做（见 <c>一键处理_需要改名时会先出预览</c> 的说明）。
    /// </para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class DisguiseCoverageTests : IClassFixture<DisguiseSampleFixture>, IDisposable
    {
        private readonly DisguiseSampleFixture _samples;
        private readonly ITestOutputHelper _output;
        private readonly string _root;

        public DisguiseCoverageTests(DisguiseSampleFixture samples, ITestOutputHelper output)
        {
            _samples = samples;
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerDisguise", Guid.NewGuid().ToString("N"));
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

        // ================================================================ 族 1：图片尾部挂包

        /// <summary>
        /// 族 1：<c>.jpg</c>/<c>.png</c> 里挂着完整 ZIP。
        /// 期望：识别成 ZIP（置信度 80，来自尾部自洽推断）+ 内嵌偏移 = 前缀长度 +
        /// 后缀状态「内嵌归档」+ 改名**一律跳过**。
        /// </summary>
        [Theory]
        [InlineData("照片.jpg", 4096)]
        [InlineData("图片.png", 4096)]
        public async Task 族1_图片尾部挂包_识别为内嵌归档_不改名(string fileName, long expectedOffset)
        {
            string path = _samples.Fam1(fileName);

            await AssertDoubleSidedAsync(path, expectedOffset, "族1 图片尾部挂包");
        }

        // ================================================================ 族 2：视频尾部挂包

        /// <summary>
        /// 族 2：<c>.mp4</c> 里挂着完整 ZIP。
        ///
        /// <para><c>小前缀.mp4</c> 是**故意的边界样本**：前缀只有 1 KB，7z 自己能打开它，
        /// 但我们的结论不变（内嵌归档、不改名）—— 判据是"ZIP 内部偏移相对它自己"，
        /// 不是"7z 打不开"，所以不该按前缀大小分两种处理。</para>
        /// </summary>
        [Theory]
        [InlineData("视频.mp4", 32768)]
        [InlineData("小前缀.mp4", 1024)]
        public async Task 族2_视频尾部挂包_识别为内嵌归档_不改名(string fileName, long expectedOffset)
        {
            string path = _samples.Fam2(fileName);

            await AssertDoubleSidedAsync(path, expectedOffset, "族2 视频尾部挂包");
        }

        // ================================================================ 族 4：大前缀双面文件

        /// <summary>
        /// 族 4：前缀 ≥ 10 MB（这里 12 MiB）—— 超过 7-Zip 的容忍上限（实测 8 MiB）时，
        /// 原文件连**列目录**都做不到，只有按偏移抠出来才解得开。这正是用户手里那种文件的情形。
        /// </summary>
        [Fact]
        public async Task 族4_大前缀双面_识别为内嵌归档_不改名_且原文件七z打不开而抠出可解()
        {
            string path = _samples.Fam4BigPrefix;

            Assert.True(
                new FileInfo(path).Length > 10L * 1024 * 1024,
                "样本必须真的超过 10 MB，否则验不出「绕过 7-Zip 容忍上限」这件事");

            await AssertDoubleSidedAsync(path, _samples.BigPrefixLength, "族4 大前缀双面");

            // 原文件：7z 拒绝（这就是"必须抠出"的硬证据）。
            SevenZipEngine engine = CreateEngine();

            ArchiveListResult onOriginal = await engine.ListAsync(ArchiveRequest.For(path), CancellationToken.None);
            Assert.False(onOriginal.Success, "前缀超过 8 MiB 时 7z 必须拒绝原文件");

            // 抠出 → 列出 → 解出内容物，逐字节核对。
            var task = new ArchiveTask(path);
            await new ArchiveDetectService().ApplyDetectResultAsync(task);

            CarveResult carve = EmbeddedArchiveCarver.Carve(
                path,
                task.EmbeddedArchiveOffset,
                Path.Combine(_root, "carved", "大前缀.zip"));

            Assert.True(carve.Success, carve.Message);
            Assert.Equal(_samples.PlainZipSha256, HashFile(carve.OutputPath));

            ArchiveListResult list = await engine.ListAsync(ArchiveRequest.For(carve.OutputPath), CancellationToken.None);
            Assert.True(list.Success, $"抠出来的包应能被 7z 列出：{list.Message}");
        }

        // ================================================================ 族 3：分卷 + 乱后缀

        /// <summary>
        /// 族 3a：真 ZIP 分卷组 <c>资料.zip.001…</c> **纯改名**成 <c>资料.mp4.001…</c>。
        ///
        /// 期望（逐条对应用户的判据）：
        /// ① 认成**一组分卷**（不是一个文件一个任务、也不是"未知格式"）；
        /// ② 基名取到分卷标记之前（<see cref="VolumeGroupDetector"/> 给 <c>资料.mp4</c>，
        ///    再按落点规则剥掉伪装后缀 <c>.mp4</c> 得 <c>资料</c>）；
        /// ③ 后缀状态是「分卷后缀」而不是「疑似伪装」；
        /// ④ 改名预览里每一卷都是"将跳过" —— 改名会直接切断分卷链。
        /// </summary>
        [Fact]
        public async Task 族3a_伪装后缀分卷组_认成一组分卷_基名到分卷标记之前_且不改名()
        {
            string[] volumes = _samples.Fam3aVolumes();

            Assert.True(volumes.Length >= 2, "样本应该是多卷，否则验不出「一组分卷」");

            // ① 归组：真实扫描管线用的就是这个服务（FileScanService → VolumeGroupingService）。
            List<ArchiveTask> singleTasks = volumes
                .Select(path => new ArchiveTask(path) { IsSelected = true, ExtensionStatus = StatusText.NotChecked })
                .ToList();

            List<ArchiveTask> grouped = new VolumeGroupingService().ApplyVolumeGrouping(singleTasks);

            ArchiveTask group = Assert.Single(grouped);
            Assert.True(group.IsVolumeGroup, "必须被认成一组分卷");
            Assert.Equal(volumes.Length, group.VolumePaths.Count);
            Assert.True(group.IsVolumeComplete);
            Assert.Equal(volumes[0], group.CurrentPath);

            // ② 基名：VolumeGroupDetector 取到"分卷标记之前"= 资料.mp4；落点规则再剥掉伪装后缀 .mp4 = 资料。
            IReadOnlyList<VolumeGroup> detected = VolumeGroupDetector.Group(
                volumes.Select(path => new VolumeCandidate { Path = path }).ToList());

            VolumeGroup volumeGroup = Assert.Single(detected);
            Assert.Equal("资料.mp4", volumeGroup.BaseName);
            Assert.Equal("资料", FileNameHelper.GetArchiveBaseName(volumes[0]));
            Assert.Equal(volumes[0], volumeGroup.FirstVolumePath);
            Assert.Empty(volumeGroup.MissingVolumeNames);

            // ③ 组代表（第一分卷）的识别结论：真实格式 ZIP（分卷的第一卷仍然从 PK 03 04 开头）、
            //    后缀状态「分卷后缀」—— 不是"未知格式"，更不是"疑似伪装"。
            var detect = new ArchiveDetectService();
            var representative = new ArchiveTask(volumes[0]);
            await detect.ApplyDetectResultAsync(representative);

            Assert.Equal("ZIP", representative.DetectedFormat);
            Assert.True(representative.IsArchive);
            Assert.Equal(StatusText.ExtensionVolume, representative.ExtensionStatus);

            /*
             * 后续卷（.002/.003…）本来就是**被切开的字节流**：文件头不是 PK，单独识别必然是"格式未知"。
             * 这不是缺陷 —— 分卷的判据是命名（VolumeGroupDetector），而且真实扫描会把它们并进上面那一组，
             * 它们不会各自变成一个任务（下一段与一键处理用例都钉住了这一点）。
             * 这里只核对"它们按名字都是分卷段、且改名一律跳过"。
             */
            foreach (string volume in volumes)
            {
                Assert.True(FileNameHelper.IsVolumePartFileName(volume), $"{volume} 应被认成分卷段");
            }

            foreach (string volume in volumes.Skip(1))
            {
                _output.WriteLine($"后续卷单独识别（格式 Unknown、后缀=分卷后缀、状态=分卷缺失）：{Path.GetFileName(volume)}");
                var solo = new ArchiveTask(volume);
                await detect.ApplyDetectResultAsync(solo);

                // 格式仍然是 Unknown（它的文件头里没有归档魔数 —— 识别层如实说），
                // 但**后缀状态**是"分卷后缀"、**任务状态**是"分卷缺失"（用户 2026-09-26 拍板：
                // 一个 `.002` 被说成"格式未知"会让人以为文件坏了，真相是缺第 1 卷）。
                Assert.Equal("Unknown", solo.DetectedFormat);
                Assert.Equal(StatusText.ExtensionVolume, solo.ExtensionStatus);
                Assert.Equal(StatusText.VolumeMissing, solo.Status);

                // 即便它看起来"格式未知"，也**不许**被改名 —— 改成 .7z 同样会切断分卷链。
                string newPath = new RenameService().BuildNewPath(
                    solo,
                    RenameOptions.CreateFixByDetectedFormat(AppSettings.CreateDefault()));

                Assert.Equal(volume, newPath);
            }

            // ④ 改名：每一卷都原地不动（切断了分卷链整组就废了）。
            List<RenamePreviewItem> preview = new RenameService().BuildPreview(
                singleTasks,
                RenameOptions.CreateFixByDetectedFormat(AppSettings.CreateDefault()));

            Assert.Equal(volumes.Length, preview.Count);
            Assert.All(preview, item =>
            {
                Assert.False(item.CanRename, $"{item.OriginalFileName} 不该被改名（会切断分卷链）");
                Assert.Equal(item.OriginalPath, item.NewPath);
            });
        }

        /// <summary>
        /// 族 3b：真 RAR5 分卷组 <c>资料2.part1.rar…</c> **纯改名**成 <c>资料2.mp4.part1…</c>
        /// （用户口述里的 <c>.part1.rar</c> 形态）。
        ///
        /// 本机没装 WinRAR（找不到 <c>Rar.exe</c>）时这一条无从造样本 —— 打印原因后跳过，
        /// 与既有 <c>RarSampleSet</c> 的处理一致（RAR 样本不入库，见 docs/引擎与外部工具.md §4）。
        /// </summary>
        [Fact]
        public async Task 族3b_RAR分卷乱后缀_认成一组分卷_且不改名()
        {
            if (!_samples.HasRarVolumeSamples)
            {
                _output.WriteLine(
                    "跳过：本机找不到 WinRAR 的 Rar.exe，造不出 RAR 分卷样本（RAR 样本按许可不入库）。");
                return;
            }

            string[] volumes = _samples.Fam3bVolumes();

            Assert.True(volumes.Length >= 2);

            List<ArchiveTask> tasks = volumes
                .Select(path => new ArchiveTask(path) { IsSelected = true })
                .ToList();

            ArchiveTask group = Assert.Single(new VolumeGroupingService().ApplyVolumeGrouping(tasks));
            Assert.True(group.IsVolumeGroup);
            Assert.Equal(volumes.Length, group.VolumePaths.Count);

            // 基名：分卷标记（.part1）之前 = 资料2.mp4（伪装后缀 .mp4 留在基名里，
            // 落点阶段才按"疑似伪装后缀"剥掉 → 资料2）。
            IReadOnlyList<VolumeGroup> detected = VolumeGroupDetector.Group(
                volumes.Select(path => new VolumeCandidate { Path = path }).ToList());

            VolumeGroup volumeGroup = Assert.Single(detected);
            Assert.Equal("资料2.mp4", volumeGroup.BaseName);
            Assert.Equal("资料2", FileNameHelper.GetArchiveBaseName(volumes[0]));
            Assert.Empty(volumeGroup.MissingVolumeNames);

            // 每一卷：真实格式是 RAR5、后缀状态是分卷后缀、且不被改名。
            var detect = new ArchiveDetectService();

            foreach (ArchiveTask task in tasks)
            {
                await detect.ApplyDetectResultAsync(task);

                Assert.Equal("RAR5", task.DetectedFormat);
                Assert.Equal(StatusText.ExtensionVolume, task.ExtensionStatus);
            }

            List<RenamePreviewItem> preview = new RenameService().BuildPreview(
                tasks,
                RenameOptions.CreateFixByDetectedFormat(AppSettings.CreateDefault()));

            Assert.All(preview, item => Assert.False(item.CanRename, $"{item.OriginalFileName} 不该被改名"));

            // 引擎侧：改名之后第一卷仍能被打开（7z / UnRAR 都认 .partN 起手，命令行已实测）。
            ArchiveListResult list = await CreateEngine().ListAsync(
                ArchiveRequest.For(volumes[0]),
                CancellationToken.None);

            Assert.True(list.Success, $"改名后第一卷应仍能被列目录：{list.Message}");
        }

        /// <summary>
        /// 一键处理族 3b：改了名字的**真 RAR 分卷组**（<c>资料2.mp4.part1…</c>）也要能一组解出来。
        /// 命令行已实测 UnRAR 认这套名字（会自己找 part2…part4），这里验程序里走引擎 + 流程同样成立。
        /// </summary>
        [Fact]
        public async Task 一键处理_RAR分卷乱后缀_一组一个任务_产物正确()
        {
            if (!_samples.HasRarVolumeSamples)
            {
                _output.WriteLine("跳过：本机找不到 WinRAR 的 Rar.exe，造不出 RAR 分卷样本。");
                return;
            }

            string familyDirectory = Path.Combine(_root, "fam3b");
            Directory.CreateDirectory(familyDirectory);

            foreach (string volume in _samples.Fam3bVolumes())
            {
                File.Copy(volume, Path.Combine(familyDirectory, Path.GetFileName(volume)));
            }

            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
                settings.AutoScanAfterDrop = true;
            });

            await harness.Vm.AddPathsAsync(new[] { familyDirectory });

            ArchiveTask task = Assert.Single(harness.Vm.Tasks);

            Assert.True(task.IsVolumeGroup);
            Assert.Equal(StatusText.ExtensionVolume, task.ExtensionStatus);

            await harness.OneClick.RunAsync();

            _output.WriteLine($"RAR 分卷一键处理：状态={task.Status}，消息={task.ErrorMessage}");

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            AssertProductsExtracted(harness.OutputRoot);
        }

        /// <summary>
        /// 族 3 的第三种形态：**整包改后缀**（<c>整包.zip</c> → <c>整包.mp4</c>，单文件、不是分卷）。
        ///
        /// 这一条是**改名预览的正样本**：内容就是普通 ZIP，后缀确实是错的，程序该在预览里给出
        /// <c>整包.zip</c> 这个落点。它同时也是"一键处理必须先出预览"的触发点。
        /// </summary>
        [Fact]
        public async Task 族3_整包改后缀_后缀不匹配_是改名预览的正样本()
        {
            string disguised = CopyToWorkDir(_samples.Fam3bWholeArchiveMp4, "整包.mp4");

            var task = new ArchiveTask(disguised) { IsSelected = true };
            await new ArchiveDetectService().ApplyDetectResultAsync(task);

            Assert.Equal("ZIP", task.DetectedFormat);
            Assert.True(task.IsArchive);
            Assert.Equal(StatusText.ExtensionMismatch, task.ExtensionStatus);
            Assert.Equal(0, task.EmbeddedArchiveOffset);

            List<RenamePreviewItem> preview = new RenameService().BuildPreview(
                new[] { task },
                RenameOptions.CreateFixByDetectedFormat(AppSettings.CreateDefault()));

            RenamePreviewItem item = Assert.Single(preview);
            Assert.True(item.CanRename);
            Assert.Equal(Path.Combine(Path.GetDirectoryName(disguised)!, "整包.zip"), item.NewPath);
        }

        // ================================================================ 一键处理：产物

        /// <summary>
        /// 一键处理（流程层宿主 + 真引擎）跑族 1 / 族 2 / 族 4：双面文件的产物必须是 ZIP 里的内容物，
        /// 而且**源文件一个字节都没动、也绝没有被改名**（改名对这类文件是死路：7z 照样打不开）。
        /// </summary>
        [Theory]
        [InlineData("族1", "照片.jpg")]
        [InlineData("族2", "视频.mp4")]
        [InlineData("族4", "大前缀.mp4")]
        public async Task 一键处理_双面文件_产物正确_源文件没被改名(string family, string fileName)
        {
            string source = CopyToWorkDir(_samples.PathOf(family, fileName), fileName);
            string sourceHash = HashFile(source);
            long sourceLength = new FileInfo(source).Length;

            Harness harness = CreateHarness(settings =>
            {
                // 源包留原地：这一条验的是"产物对不对 + 有没有被改名"，源包搬运另有专测。
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
                settings.AutoScanAfterDrop = true;
            });

            await harness.Vm.AddPathsAsync(new[] { source });

            ArchiveTask task = Assert.Single(harness.Vm.Tasks);

            Assert.Equal("ZIP", task.DetectedFormat);
            Assert.Equal(StatusText.ExtensionEmbedded, task.ExtensionStatus);
            Assert.True(task.EmbeddedArchiveOffset > 0);

            await harness.OneClick.RunAsync();

            // 一键处理跑起来了，而且**没进改名分支**（NeedsRename 对内嵌归档必须是 false）：
            // 这一行日志就是"跳过改名"的直接证据。
            Assert.True(
                harness.LogTexts.Any(line => line.Contains("没有需要修正的后缀，跳过改名", StringComparison.Ordinal)),
                "内嵌归档不该进改名预览（NeedsRename 对它必须是 false）");

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // 产物：ZIP 里的两个条目（说明.txt + 素材\data.bin）内容逐字节一致。
            AssertProductsExtracted(harness.OutputRoot);

            // 源文件：还在原地、名字没变、内容没变。
            Assert.True(File.Exists(source), "源文件必须还在（这一档是 KeepInPlace）");
            Assert.Equal(sourceLength, new FileInfo(source).Length);
            Assert.Equal(sourceHash, HashFile(source));

            string? renamedToZip = Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + ".zip");
            Assert.False(File.Exists(renamedToZip), $"双面文件被错误改名成了 {renamedToZip}");
        }

        /// <summary>
        /// 一键处理族 3a：伪装后缀的分卷组 —— **一组一个任务**、从第一卷起手、产物正确，
        /// 并且整组源包（四卷）**一起**进入 <c>其余物</c>（绝不拆散、绝不覆盖）。
        /// </summary>
        [Fact]
        public async Task 一键处理_伪装后缀分卷组_一组一个任务_产物正确_整组一起进其余物()
        {
            string familyDirectory = Path.Combine(_root, "fam3a");
            Directory.CreateDirectory(familyDirectory);

            foreach (string volume in _samples.Fam3aVolumes())
            {
                File.Copy(volume, Path.Combine(familyDirectory, Path.GetFileName(volume)));
            }

            string[] volumesInPlace = Directory.GetFiles(familyDirectory);

            Harness harness = CreateHarness(settings =>
            {
                // 显式选上"放入其余物"（第 32 条之后设置里的默认档是"留在原地"，本用例要钉的是搬走那条路）
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.AutoScanAfterDrop = true;
            });

            await harness.Vm.AddPathsAsync(new[] { familyDirectory });

            // 一组分卷 = 一个任务（不是四行）。
            ArchiveTask task = Assert.Single(harness.Vm.Tasks);

            Assert.True(task.IsVolumeGroup);
            Assert.Equal(volumesInPlace.Length, task.VolumePaths.Count);
            Assert.Equal(StatusText.ExtensionVolume, task.ExtensionStatus);

            await harness.OneClick.RunAsync();

            Assert.True(
                harness.LogTexts.Any(line => line.Contains("没有需要修正的后缀，跳过改名", StringComparison.Ordinal)),
                "分卷后缀不该进改名预览");

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            AssertProductsExtracted(harness.OutputRoot);

            // 整组源包一起搬走：源目录里一卷都不剩，产物目录的其余物里有整整一组。
            Assert.All(volumesInPlace, volume => Assert.False(File.Exists(volume), $"源卷没被搬走：{volume}"));

            List<string> movedVolumes = Directory
                .GetFiles(harness.OutputRoot, "资料.mp4.0*", SearchOption.AllDirectories)
                .ToList();

            Assert.Equal(volumesInPlace.Length, movedVolumes.Count);

            // 卷号没有重名冲突（(1) 之类），说明是"整组一次搬"，不是逐卷各判一次。
            Assert.All(movedVolumes, path => Assert.DoesNotContain("(", Path.GetFileName(path), StringComparison.Ordinal));
            Assert.All(movedVolumes, path => Assert.Contains("其余物", path, StringComparison.Ordinal));
        }

        /// <summary>
        /// 一键处理遇到"确实需要修正后缀"的样本时，**必须先出改名预览**（不变量 3）。
        ///
        /// <para>
        /// 无 UI 宿主里能验到哪一步：日志会先写「N 个任务需要修正后缀，先出预览。」——这一行就是
        /// <c>RenameCoordinator.SmartRenameAsync()</c> 的调用点，证明流程确实走到预览分支；
        /// 紧接着它停在"把窗口显出来"那一步（无 UI 宿主里没有 <c>Application.Current</c>，
        /// 窗口/模态对话框都不存在），于是本次一键处理以失败告终，**源文件一个字节没动**。
        /// </para>
        ///
        /// <para>
        /// ⚠ 也就是说：「真的弹出一个改名预览窗口、有人点确认」这一段**在无 UI 宿主里验不到**，
        /// 需要可见窗口 + 真人（或 UIA 后台点击），按 AGENTS.md §13 不能由本测试代劳。
        /// 三条路径的**结果**（确认 / 取消 / 冲突）在下面用生产代码同一套服务 + 预览窗口的 ViewModel 验完。
        /// </para>
        /// </summary>
        [Fact]
        public async Task 一键处理_需要修正后缀时自动改名_且手动档仍然先预览()
        {
            string disguised = CopyToWorkDir(_samples.Fam3bWholeArchiveMp4, "整包.mp4");
            string before = HashFile(disguised);

            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
                settings.AutoScanAfterDrop = true;
            });

            await harness.Vm.AddPathsAsync(new[] { disguised });

            ArchiveTask task = Assert.Single(harness.Vm.Tasks);
            Assert.Equal(StatusText.ExtensionMismatch, task.ExtensionStatus);

            await harness.OneClick.RunAsync();

            /*
             * 2026-09-25 用户第 32 条改了这条口径（原话："这个一键处理自己会自动改名自动解压，
             * 为什么遇到这种改名的压缩包还要我两次确认……那种情况只有手动档才会有"）：
             * 一键档**自动改名、不弹预览窗口**（所以在无 UI 宿主里也照样跑得下去），逐个改名仍逐条写日志；
             * 手动档（②页/右键那几条改名命令）**照旧先出预览** —— 那条不变量没有消失，只是不再管一键档。
             */
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("个任务需要修正后缀，自动执行（不弹预览）", StringComparison.Ordinal));

            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("自动修正后缀", StringComparison.Ordinal));

            /*
             * 这个样本是"整个文件其实是个 ZIP、名字却叫 .mp4"（`Fam3b`）→ 属于**该改**的那一类：
             * 一键档现在真的把它改了，而且逐条留痕（旧名 -> 新名）。
             * 对照：族 1 / 族 2 那种"视频尾部挂 ZIP"的双面文件**不改名**，那条红线由本类上面几条用例钉着。
             */
            string renamed = Path.Combine(Path.GetDirectoryName(disguised)!, "整包.zip");

            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("整包.mp4 -> 整包.zip", StringComparison.Ordinal));

            Assert.True(File.Exists(renamed), "该改名的包，一键档要自动改成 .zip");
            Assert.False(File.Exists(disguised), "改名之后旧名字不该还在");

            _output.WriteLine("一键处理日志：" + string.Join(" | ", harness.LogTexts.Where(l => l.Contains("改名", StringComparison.Ordinal) || l.Contains("一键处理", StringComparison.Ordinal))));
        }

        // ================================================================ 改名预览的三条路径

        /// <summary>
        /// 路径 ①「确认改名」：走**生产代码同一套**服务与预览窗口的 ViewModel
        /// （<c>BuildPreview</c> → <see cref="RenamePreviewViewModel.Confirm"/> → <c>GetSelectedItems</c>
        /// → <c>ApplyConflictChoice</c> → <c>ExecuteRenameAsync</c>，与 <c>RenameCoordinator</c> 逐句一致），
        /// 只把"窗口被 Show 出来"换成"直接读它的返回值"。
        /// 判据：文件真的被改名，**内容没动**。
        /// </summary>
        [Fact]
        public async Task 改名预览_路径1确认_文件真的被改名且内容一个字节没动()
        {
            string disguised = CopyToWorkDir(_samples.Fam3bWholeArchiveMp4, "整包.mp4");
            string expectedHash = HashFile(disguised);

            RenamePreviewRun run = await RunRenamePreviewFlowAsync(
                disguised,
                ConflictActions.AutoRename,
                RenamePreviewDecision.Confirm);

            Assert.True(run.DialogResult);
            Assert.Single(run.ExecutedItems);
            Assert.Equal(StatusText.RenameSuccess, run.ExecutedItems[0].Status);

            string renamed = Path.Combine(Path.GetDirectoryName(disguised)!, "整包.zip");

            Assert.False(File.Exists(disguised), "旧名字不该还在");
            Assert.True(File.Exists(renamed), "新名字必须存在");
            Assert.Equal(expectedHash, HashFile(renamed));   // 内容一个字节都不许变
            Assert.Equal(Path.GetFileName(renamed), run.Tasks[0].FileName);
            Assert.Equal(renamed, run.Tasks[0].CurrentPath);
        }

        /// <summary>
        /// 路径 ②「取消 / 关掉预览窗口」：预览窗口的返回值不是 true 时，
        /// <c>RenameCoordinator</c> 直接 return —— 文件必须**原封不动**（名字、内容、修改时间全不变）。
        /// </summary>
        [Fact]
        public async Task 改名预览_路径2取消_文件原封不动()
        {
            string disguised = CopyToWorkDir(_samples.Fam3bWholeArchiveMp4, "整包.mp4");
            string beforeHash = HashFile(disguised);
            DateTime beforeWriteTime = File.GetLastWriteTimeUtc(disguised);

            RenamePreviewRun run = await RunRenamePreviewFlowAsync(
                disguised,
                ConflictActions.AutoRename,
                RenamePreviewDecision.Cancel);

            Assert.False(run.DialogResult);
            Assert.Empty(run.ExecutedItems);

            Assert.True(File.Exists(disguised), "取消之后旧名字必须还在");
            Assert.Equal(beforeHash, HashFile(disguised));
            Assert.Equal(beforeWriteTime, File.GetLastWriteTimeUtc(disguised));

            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(disguised)!, "整包.zip")));
            Assert.DoesNotContain(
                Directory.GetFiles(Path.GetDirectoryName(disguised)!, "*", SearchOption.TopDirectoryOnly),
                path => Path.GetFileName(path).Contains('(', StringComparison.Ordinal));
        }

        /// <summary>
        /// 路径 ③「冲突档」：目标 <c>整包.zip</c> 已经存在时按设置走 ——
        /// 默认档 <c>AutoRename</c> 落成 <c>整包(1).zip</c>、已有文件**一个字节不动**；
        /// <c>Skip</c> 档则这一条直接跳过，两个文件都不动。
        /// **绝不允许覆盖**（不变量 3）。
        /// </summary>
        [Theory]
        [InlineData(ConflictActions.AutoRename, "整包(1).zip")]
        [InlineData(ConflictActions.Skip, null)]
        public async Task 改名预览_路径3冲突_按设置走且绝不覆盖(string conflictAction, string? expectedNewName)
        {
            string disguised = CopyToWorkDir(_samples.Fam3bWholeArchiveMp4, "整包.mp4");
            string zipHash = HashFile(disguised);

            string occupied = Path.Combine(Path.GetDirectoryName(disguised)!, "整包.zip");
            File.WriteAllText(occupied, "这个名字上已经有一个别人的文件，绝不能被覆盖", new UTF8Encoding(false));

            string occupiedHash = HashFile(occupied);

            RenamePreviewRun run = await RunRenamePreviewFlowAsync(disguised, conflictAction, RenamePreviewDecision.Confirm);

            // 已有文件：内容一个字节都没动。
            Assert.True(File.Exists(occupied));
            Assert.Equal(occupiedHash, HashFile(occupied));

            if (expectedNewName == null)
            {
                /*
                 * Skip：预览期就把这一条标成「将跳过」并顺手取消勾选，于是没有任何可执行的改名项，
                 * 协调器连落盘那一步都进不去 —— 两个文件都在原位。
                 */
                Assert.Empty(run.ExecutedItems);
                Assert.Equal(StatusText.RenameWillSkip, run.PreviewItems[0].Status);
                Assert.True(File.Exists(disguised));
                Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(disguised)!, "整包(1).zip")));
                return;
            }

            string expected = Path.Combine(Path.GetDirectoryName(disguised)!, expectedNewName);

            // 预览里给用户看到的结论是「将自动重命名」（执行之后状态才会变成"改名成功"）。
            Assert.Equal(StatusText.WillAutoRename, run.DecidedStatuses[0]);
            Assert.True(File.Exists(expected), $"应落成 {expectedNewName}");
            Assert.Equal(zipHash, HashFile(expected));
            Assert.False(File.Exists(disguised));
        }

        /// <summary>
        /// 「询问」档（<c>ConflictAction = Ask</c>）在改名路径上：预览里逐条待选，
        /// 用户在预览表里选什么就落什么 —— 这里钉住"选覆盖"那一支走的是**两阶段覆盖**（先移开旧文件再落位），
        /// 中途失败不丢文件。
        /// </summary>
        [Fact]
        public async Task 改名预览_询问档选覆盖_走两阶段且旧文件内容被替换()
        {
            string disguised = CopyToWorkDir(_samples.Fam3bWholeArchiveMp4, "整包.mp4");
            string zipHash = HashFile(disguised);

            string occupied = Path.Combine(Path.GetDirectoryName(disguised)!, "整包.zip");
            File.WriteAllText(occupied, "旧内容", new UTF8Encoding(false));

            RenamePreviewRun run = await RunRenamePreviewFlowAsync(
                disguised,
                ConflictActions.Ask,
                RenamePreviewDecision.Confirm,
                item => item.ConflictChoice = "Overwrite");

            Assert.True(run.PreviewItems[0].NeedsConflictChoice == false);
            Assert.Equal(zipHash, HashFile(occupied));
            Assert.Equal(StatusText.RenameSuccess, run.PreviewItems[0].Status);

            // 临时名必须被清干净（af-vacating / af-staging 都不许留在目录里）。
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(disguised)!, "*.af-*", SearchOption.TopDirectoryOnly));
        }

        // ================================================================ 不变量 3 的接线

        /// <summary>
        /// 不变量 3「改名必须先预览，不得被任何代码旁路」的接线检查：
        /// 生产代码里每一处 <c>RenameOptions</c> 都带着 <c>PreviewBeforeRename = true</c>，
        /// 而且预览之后**一定**经过 <c>RenamePreviewWindow.ShowDialog()</c> 的返回值判断。
        ///
        /// 为什么用源码文本而不是反射：这条不变量的语义是"代码里不许出现没预览的改名入口"，
        /// 反射只能看到类型，看不到"有没有一处忘了写 true"。
        /// </summary>
        [Fact]
        public void 不变量3_手动档的每一处改名都必须先预览_一键档是自动改名()
        {
            string source = File.ReadAllText(RepoPath("src", "ArchiveFixer", "ViewModels", "RenameCoordinator.cs"));

            int renameOptionSites = CountOccurrences(source, "new RenameOptions");
            int previewBeforeRename = CountOccurrences(source, "PreviewBeforeRename = true");

            /*
             * ⚠ 这个下限**跟着界面收敛一起降**（2026-09-26 审计：手动那一栏从六颗按钮并成四颗 ——
             * 「添加 / 替换最后一个 / 删除最后一个」合成一颗「改后缀…」，「删除多个后缀」整块退役，
             * 于是构造 RenameOptions 的地方从 5 处降到 2 处：智能修正 + 改后缀）。
             * 它只是"扫描还找得到代码"的哨兵，真正的判据是下面那句相等 ——
             * ⛔ 新增任何改名入口都必须带 PreviewBeforeRename = true。
             */
            Assert.True(renameOptionSites >= 2, $"改名入口比预期少（找到 {renameOptionSites} 处），检查是不是漏了");
            Assert.Equal(renameOptionSites, previewBeforeRename);

            // 手动档：预览 → 拿返回值 → 不是 true 就什么都不做。
            Assert.Contains("new RenamePreviewWindow(previewItems)", source, StringComparison.Ordinal);
            Assert.Contains("window.ShowDialog()", source, StringComparison.Ordinal);
            Assert.Contains("if (dialogResult != true)", source, StringComparison.Ordinal);

            /*
             * 一键档（2026-09-25 第 32 条）：**唯一**允许不弹预览的入口是 AutoFixExtensionsAsync，
             * 而且它必须逐条写日志（"旧名 -> 新名，状态：…"）—— 不弹窗可以，"不留痕"不行。
             * 这里用源码文本钉住这两件事同时成立，防止以后有人顺手再开一条不弹窗也不写日志的路。
             */
            Assert.Contains("internal async Task<bool> AutoFixExtensionsAsync()", source, StringComparison.Ordinal);
            Assert.Contains("ApplyConflictChoice(item)", source, StringComparison.Ordinal);
            Assert.Contains("自动修正后缀", source, StringComparison.Ordinal);
            Assert.Contains("item.OriginalFileName} -> {item.NewFileName}，状态：{item.Status}", source, StringComparison.Ordinal);
        }

        // ================================================================ 共用断言 / 装配

        /// <summary>双面文件的四条结论：识别 / 内嵌偏移 / 后缀状态 / 不改名（+ 统计口径）。</summary>
        private async Task AssertDoubleSidedAsync(string path, long expectedOffset, string family)
        {
            var detect = new ArchiveDetectService();

            DetectResult result = await detect.DetectAsync(path);

            Assert.Equal("ZIP", result.Format);
            Assert.True(result.IsArchive);
            Assert.True(result.IsKnownFormat);
            Assert.Equal(expectedOffset, result.EmbeddedArchiveOffset);
            Assert.Equal(80, result.Confidence);   // 80 = "尾部推断"，不是"文件头直接证据"

            var task = new ArchiveTask(path) { IsSelected = true };
            await detect.ApplyDetectResultAsync(task);

            Assert.Equal(StatusText.ExtensionEmbedded, task.ExtensionStatus);
            Assert.True(task.EmbeddedArchiveOffset > 0);

            // 统计口径：算"已识别"，不是"未知格式"。
            TaskSummary summary = new TaskSummaryService().BuildSummary(new[] { task });
            Assert.Equal(1, summary.RecognizedCount);
            Assert.Equal(0, summary.UnknownCount);

            // 改名：一律"将跳过"，且 NewPath 就是原路径。
            List<RenamePreviewItem> preview = new RenameService().BuildPreview(
                new[] { task },
                RenameOptions.CreateFixByDetectedFormat(AppSettings.CreateDefault()));

            RenamePreviewItem item = Assert.Single(preview);
            Assert.False(item.CanRename, $"{family}：双面文件不该被改名（改成 .zip 后 7z 照样打不开）");
            Assert.Equal(path, item.NewPath);
        }

        /// <summary>产物核对：<c>说明.txt</c> 与 <c>素材\data.bin</c> 都在输出根下，内容与源逐字节一致。</summary>
        private void AssertProductsExtracted(string outputRoot)
        {
            string? extractedText = Directory
                .GetFiles(outputRoot, "说明.txt", SearchOption.AllDirectories)
                .FirstOrDefault();

            string? extractedBinary = Directory
                .GetFiles(outputRoot, "data.bin", SearchOption.AllDirectories)
                .FirstOrDefault();

            Assert.True(extractedText != null, $"没解出 说明.txt（输出根：{outputRoot}）");
            Assert.True(extractedBinary != null, $"没解出 素材\\data.bin（输出根：{outputRoot}）");

            Assert.Equal(
                HashFile(Path.Combine(_samples.PayloadDirectory, "说明.txt")),
                HashFile(extractedText!));

            Assert.Equal(
                HashFile(Path.Combine(_samples.PayloadDirectory, "素材", "data.bin")),
                HashFile(extractedBinary!));
        }

        private SevenZipEngine CreateEngine()
        {
            var engine = new SevenZipEngine();
            Assert.True(engine.IsAvailable, "测试需要项目内置的 7z.exe");
            return engine;
        }

        private string CopyToWorkDir(string source, string fileName)
        {
            string directory = Path.Combine(_root, "work", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            string target = Path.Combine(directory, fileName);
            File.Copy(source, target);

            return target;
        }

        private static string HashFile(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        private static int CountOccurrences(string text, string needle)
        {
            int count = 0;

            for (int index = text.IndexOf(needle, StringComparison.Ordinal); index >= 0;
                 index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        /// <summary>仓库根下的文件路径（从测试输出目录往上找 ArchiveFixer.slnx）。</summary>
        private static string RepoPath(params string[] parts)
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    return Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("找不到仓库根（ArchiveFixer.slnx）。");
        }

        // ---------------------------------------------------------------- 改名预览的流程层宿主

        private enum RenamePreviewDecision
        {
            /// <summary>用户点了「确认改名」。</summary>
            Confirm,

            /// <summary>用户点了「取消」/ 直接关掉预览窗口。</summary>
            Cancel
        }

        private sealed class RenamePreviewRun
        {
            public bool? DialogResult { get; init; }

            public List<RenamePreviewItem> PreviewItems { get; init; } = new();

            /// <summary>冲突结论定下来、但**还没落盘**时每一条的状态（执行会把状态改成"改名成功"）。</summary>
            public List<string> DecidedStatuses { get; init; } = new();

            public List<RenamePreviewItem> ExecutedItems { get; init; } = new();

            public List<ArchiveTask> Tasks { get; init; } = new();
        }

        /// <summary>
        /// 改名预览分支的**流程层宿主**：逐句照抄 <c>RenameCoordinator.RenameByOptionsAsync</c>，
        /// 只有一处不同 —— 它把 <c>RenamePreviewWindow</c> Show 出来等人的点击，
        /// 这里换成"直接以用户的选择驱动窗口自己的 ViewModel"（AGENTS.md §13：不显示窗口、不模拟鼠标）。
        ///
        /// 生产代码里 <c>PreviewBeforeRename</c> 硬编码 true（不变量 3），所以这里也用 true。
        /// </summary>
        private static async Task<RenamePreviewRun> RunRenamePreviewFlowAsync(
            string sourcePath,
            string conflictAction,
            RenamePreviewDecision decision,
            Action<RenamePreviewItem>? actOnPreview = null)
        {
            var task = new ArchiveTask(sourcePath) { IsSelected = true };

            await new ArchiveDetectService().ApplyDetectResultAsync(task);

            AppSettings settings = AppSettings.CreateDefault();
            settings.ConflictAction = conflictAction;

            RenameOptions options = RenameOptions.CreateFixByDetectedFormat(settings);

            Assert.True(options.PreviewBeforeRename, "不变量 3：PreviewBeforeRename 不许被旁路");

            var service = new RenameService();
            var tasks = new List<ArchiveTask> { task };

            List<RenamePreviewItem> previewItems = service.BuildPreview(
                tasks.Where(x => x.IsSelected).ToList(),
                options);

            Assert.NotEmpty(previewItems);

            // 预览窗口的 DataContext（生产代码里就是 new RenamePreviewWindow(previewItems) 的那一个）。
            var viewModel = new RenamePreviewViewModel(previewItems);

            actOnPreview?.Invoke(viewModel.Items[0]);

            if (decision == RenamePreviewDecision.Confirm)
            {
                viewModel.Confirm();
            }
            else
            {
                viewModel.Cancel();
            }

            // ↓↓↓ 从这里往下与 RenameCoordinator.RenameByOptionsAsync 逐句一致 ↓↓↓
            bool? dialogResult = viewModel.DialogResult;

            if (dialogResult != true)
            {
                return new RenamePreviewRun
                {
                    DialogResult = dialogResult,
                    PreviewItems = viewModel.Items.ToList(),
                    Tasks = tasks
                };
            }

            foreach (RenamePreviewItem item in viewModel.Items)
            {
                service.ApplyConflictChoice(item);
            }

            // 冲突结论刚定下、还没落盘：这里的状态才是"预览里给用户看到的那个结论"。
            List<string> decidedStatuses = viewModel.Items.Select(x => x.Status).ToList();

            List<RenamePreviewItem> selectedItems = viewModel.GetSelectedItems();

            if (selectedItems.Count == 0)
            {
                return new RenamePreviewRun
                {
                    DialogResult = dialogResult,
                    PreviewItems = viewModel.Items.ToList(),
                    DecidedStatuses = decidedStatuses,
                    Tasks = tasks
                };
            }

            await service.ExecuteRenameAsync(selectedItems, tasks);

            return new RenamePreviewRun
            {
                DialogResult = dialogResult,
                PreviewItems = viewModel.Items.ToList(),
                DecidedStatuses = decidedStatuses,
                ExecutedItems = selectedItems,
                Tasks = tasks
            };
        }

        // ---------------------------------------------------------------- 一键处理的流程层宿主

        private sealed class Harness
        {
            public required MainViewModel Vm { get; init; }

            public required OneClickCoordinator OneClick { get; init; }

            public required LogService Log { get; init; }

            public required string OutputRoot { get; init; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);
        }

        /// <summary>
        /// 真引擎 + 真协调器的流程层宿主（与其它端到端用例同一套形状）：
        /// 添加 → 一键处理，全程无窗口、无对话框（<see cref="DialogService"/> 在无 UI 宿主下降级为写日志）。
        /// </summary>
        private Harness CreateHarness(Action<AppSettings>? configure = null)
        {
            string dataRoot = Path.Combine(_root, "data", Guid.NewGuid().ToString("N"));
            string outputRoot = Path.Combine(_root, "out", Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CacheRootDirectory = dataRoot;
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.RecursionMode = "SingleLayer";
            settings.AutoScanAfterDrop = true;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);

            configure?.Invoke(settings);
            settingsService.Save(settings);

            var engine = new SevenZipEngine();
            Assert.True(engine.IsAvailable, "一键处理端到端要用真 7z");

            var passwordService = new PasswordService { DataRootDirectory = dataRoot };
            var logService = new LogService(pathService);

            // MainViewModel 的构造会写两个进程级静态（7z 路径 / 递归工作区根目录）：先存后还原。
            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                engine,
                passwordService,
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService());

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            var extraction = new ExtractionCoordinator(vm, engine, passwordService, pathService, new DialogService());
            var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
            var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
            var oneClick = new OneClickCoordinator(vm, scan, rename, extraction, new DialogService());

            return new Harness
            {
                Vm = vm,
                OneClick = oneClick,
                Log = logService,
                OutputRoot = outputRoot
            };
        }
    }

    /// <summary>
    /// 四族伪装样本的构造与自检（只给测试用）。
    ///
    /// <para>造法刻意与命令行脚本 <c>_tmp\ArchiveFixer\disguise\make-samples.ps1</c> 保持一致：
    /// 先把 ZIP 整个建好、**再**在前面拼数据 —— 这样 ZIP 的内部偏移保持相对它自己的起点，
    /// 这才是"双面文件"的全部关键（用"重新打包"造不出来）。</para>
    ///
    /// <para>环境变量 <c>ARCHIVEFIXER_DISGUISE_DIR</c> 指向已造好的目录（真机样本）时直接用它，
    /// 否则在临时目录里现造 —— 两条来源的样本逐字节一致。</para>
    /// </summary>
    public sealed class DisguiseSampleFixture : IDisposable
    {
        internal const string SamplesEnvironmentVariable = "ARCHIVEFIXER_DISGUISE_DIR";

        private readonly bool _ownsRoot;

        public DisguiseSampleFixture()
        {
            SevenZipPath = LocateSevenZip();

            string? configured = Environment.GetEnvironmentVariable(SamplesEnvironmentVariable);

            if (!string.IsNullOrWhiteSpace(configured)
                && File.Exists(Path.Combine(configured, "fam1", "照片.jpg")))
            {
                Root = configured;
                _ownsRoot = false;
            }
            else
            {
                Root = Path.Combine(Path.GetTempPath(), "ArchiveFixerDisguiseSamples", Guid.NewGuid().ToString("N"));
                _ownsRoot = true;
                Directory.CreateDirectory(Root);
            }

            PayloadDirectory = Path.Combine(Root, "_payload");
            PlainZipPath = Path.Combine(Root, "_build", "包.zip");
            Source = _ownsRoot ? "临时目录现造" : "环境变量指向的真机样本目录";

            if (_ownsRoot)
            {
                Build();
            }

            PlainZipSha256 = HashFile(PlainZipPath);

            BigPrefixLength = new FileInfo(Fam4BigPrefix).Length
                              - new FileInfo(PlainZipPath).Length;
        }

        /// <summary>样本根目录。</summary>
        public string Root { get; }

        /// <summary>样本来源（写进测试输出，便于报告引用）。</summary>
        public string Source { get; }

        public string PayloadDirectory { get; }

        public string PlainZipPath { get; }

        public string PlainZipSha256 { get; }

        /// <summary>族 4 的前缀长度（12 MiB）。</summary>
        public long BigPrefixLength { get; }

        /// <summary>本机有没有 Rar.exe（族 3b 的真 RAR 分卷要用它现造；没装就跳过那一档）。</summary>
        public bool HasRarVolumeSamples { get; private set; }

        public string Fam1(string fileName) => Path.Combine(Root, "fam1", fileName);

        public string Fam2(string fileName) => Path.Combine(Root, "fam2", fileName);

        public string Fam4BigPrefix => Path.Combine(Root, "fam4", "大前缀.mp4");

        public string Fam3bWholeArchiveMp4 => Path.Combine(Root, "fam3b", "整包.mp4");

        /// <summary>族 3a 的整组分卷（纯改名之后的 <c>资料.mp4.001…</c>，已按卷序排好）。</summary>
        public string[] Fam3aVolumes() =>
            SortedVolumes(Path.Combine(Root, "fam3a"), "资料.mp4.*");

        /// <summary>族 3b 的整组 RAR 分卷（纯改名之后的 <c>资料2.mp4.partN</c>）。</summary>
        public string[] Fam3bVolumes() =>
            SortedVolumes(Path.Combine(Root, "fam3b"), "资料2.mp4.part*");

        public string PathOf(string family, string fileName) => family switch
        {
            "族1" => Fam1(fileName),
            "族2" => Fam2(fileName),
            "族4" => Fam4BigPrefix,
            _ => throw new ArgumentOutOfRangeException(nameof(family), family, "未知族")
        };

        public void Dispose()
        {
            if (!_ownsRoot)
            {
                return;   // 真机样本目录是用户/命令行脚本造出来的，测试不许删它。
            }

            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
                // 临时目录清不掉不影响结论（句柄可能还在释放中）。
            }
        }

        // ---------------------------------------------------------------- 构造

        private string SevenZipPath { get; }

        private void Build()
        {
            string[] families = { "fam1", "fam2", "fam3a", "fam3b", "fam4", "_build", "_payload" };

            foreach (string family in families)
            {
                Directory.CreateDirectory(Path.Combine(Root, family));
            }

            // 公共内容物：一个文本 + 一个二进制（带一层子目录，产物结构才验得动）。
            Directory.CreateDirectory(Path.Combine(PayloadDirectory, "素材"));

            File.WriteAllText(
                Path.Combine(PayloadDirectory, "说明.txt"),
                string.Concat(Enumerable.Repeat("这是一份用于验证伪装样本的内容物说明。" + Environment.NewLine, 40)),
                new UTF8Encoding(false));

            File.WriteAllBytes(
                Path.Combine(PayloadDirectory, "素材", "data.bin"),
                DeterministicBytes(204800, 44));

            // 基准 ZIP。
            Run7z(PayloadDirectory, "a", "-tzip", "-mx1", PlainZipPath, "说明.txt", "素材");

            byte[] zipBytes = File.ReadAllBytes(PlainZipPath);

            // 族 1：图片尾部挂包（JPEG / PNG 头 + 追加完整 ZIP）。
            WriteDisguised(Fam1("照片.jpg"), BuildJpegHeader(4096), zipBytes);
            WriteDisguised(Fam1("图片.png"), BuildPngHeader(4096), zipBytes);

            // 族 2：视频尾部挂包（MP4 头 + 追加完整 ZIP）；另加一个"前缀在 7z 容忍范围内"的边界样本。
            WriteDisguised(Fam2("视频.mp4"), BuildMp4Header(32768), zipBytes);
            WriteDisguised(Fam2("小前缀.mp4"), BuildMp4Header(1024), zipBytes);

            // 族 3a：真 ZIP 分卷组 → 纯改名成 资料.mp4.001/.002…
            BuildFam3aVolumes();

            // 族 3b：真 RAR5 分卷组 → 纯改名成 资料2.mp4.partN；另加"整包改后缀"的 整包.mp4。
            BuildFam3bVolumes();
            File.Copy(PlainZipPath, Fam3bWholeArchiveMp4, overwrite: true);

            // 族 4：12 MiB 前缀（含 MP4 ftyp 头，其余是填充）+ 追加完整 ZIP。
            byte[] bigPrefix = DeterministicBytes(12 * 1024 * 1024, 55);
            byte[] ftyp = BuildMp4Header(64);
            Array.Copy(ftyp, 0, bigPrefix, 0, ftyp.Length);

            WriteDisguised(Fam4BigPrefix, bigPrefix, zipBytes);
        }

        private void BuildFam3aVolumes()
        {
            string stage = Path.Combine(Root, "_build", "fam3a");
            Directory.CreateDirectory(stage);

            Run7z(
                PayloadDirectory,
                "a", "-tzip", "-mx1", "-v64k",
                Path.Combine(stage, "资料.zip"),
                "说明.txt", "素材");

            foreach (string volume in Directory.GetFiles(stage, "资料.zip.*").OrderBy(x => x, StringComparer.Ordinal))
            {
                string name = Path.GetFileName(volume);

                // 纯改名：".zip.NNN" 里的 ".zip" 换成 ".mp4"，字节一个都不动。
                string newName = name.Replace(".zip.", ".mp4.", StringComparison.Ordinal);

                File.Move(volume, Path.Combine(Root, "fam3a", newName));
            }
        }

        private void BuildFam3bVolumes()
        {
            string? rarExe = LocateRarExe();

            if (rarExe == null)
            {
                HasRarVolumeSamples = false;
                return;
            }

            string stage = Path.Combine(Root, "_build", "fam3b");
            Directory.CreateDirectory(stage);

            RunProcess(
                rarExe,
                PayloadDirectory,
                "a", "-m1", "-r", "-v64k", "-idq",
                Path.Combine(stage, "资料2.rar"),
                "说明.txt", "素材");

            string[] volumes = Directory.GetFiles(stage, "资料2.part*.rar").OrderBy(x => x, StringComparer.Ordinal).ToArray();

            if (volumes.Length < 2)
            {
                HasRarVolumeSamples = false;
                return;
            }

            foreach (string volume in volumes)
            {
                // 纯改名：资料2.part1.rar → 资料2.mp4.part1（去掉 .rar、插入 .mp4）。
                string name = Path.GetFileName(volume);
                string newName = name
                    .Replace("资料2.", "资料2.mp4.", StringComparison.Ordinal)
                    .Replace(".rar", string.Empty, StringComparison.Ordinal);

                File.Move(volume, Path.Combine(Root, "fam3b", newName));
            }

            HasRarVolumeSamples = true;
        }

        private static string[] SortedVolumes(string directory, string pattern)
        {
            if (!Directory.Exists(directory))
            {
                return Array.Empty<string>();
            }

            return Directory.GetFiles(directory, pattern)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static void WriteDisguised(string target, byte[] prefix, byte[] zipBytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);

            output.Write(prefix);
            output.Write(zipBytes);
        }

        private static byte[] DeterministicBytes(int length, int seed)
        {
            byte[] buffer = new byte[length];
            new Random(seed).NextBytes(buffer);

            return buffer;
        }

        /// <summary>JPEG：SOI + APP0/JFIF + 填充 + EOI（形态真实，不追求可解码）。</summary>
        private static byte[] BuildJpegHeader(int length)
        {
            byte[] head =
            {
                0xFF, 0xD8,
                0xFF, 0xE0, 0x00, 0x10,
                0x4A, 0x46, 0x49, 0x46, 0x00,
                0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00
            };

            byte[] body = DeterministicBytes(length - head.Length - 2, 11);
            byte[] tail = { 0xFF, 0xD9 };

            return Concat(head, body, tail);
        }

        /// <summary>PNG：8 字节签名 + IHDR（CRC 正确）+ 填充。</summary>
        private static byte[] BuildPngHeader(int length)
        {
            byte[] signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

            byte[] ihdrData = { 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x08, 0x02, 0x00, 0x00, 0x00 };
            byte[] ihdrLength = { 0x00, 0x00, 0x00, 0x0D };
            byte[] ihdrType = Encoding.ASCII.GetBytes("IHDR");

            byte[] crcInput = Concat(ihdrType, ihdrData);
            byte[] crc = BitConverter.GetBytes(Crc32(crcInput));

            byte[] ihdr = Concat(ihdrLength, crcInput, crc);
            byte[] filler = DeterministicBytes(length - signature.Length - ihdr.Length, 22);

            return Concat(signature, ihdr, filler);
        }

        /// <summary>MP4：ftyp box（isom/iso2/avc1/mp41）+ free 头 + 填充。</summary>
        private static byte[] BuildMp4Header(int length)
        {
            byte[] ftyp =
            {
                0x00, 0x00, 0x00, 0x20,
                0x66, 0x74, 0x79, 0x70,
                0x69, 0x73, 0x6F, 0x6D, 0x00, 0x00, 0x02, 0x00,
                0x69, 0x73, 0x6F, 0x6D, 0x69, 0x73, 0x6F, 0x32,
                0x61, 0x76, 0x63, 0x31, 0x6D, 0x70, 0x34, 0x31
            };

            int fillerLength = length - ftyp.Length;
            byte[] free = Concat(
                BitConverter.GetBytes((uint)(fillerLength - 8)),
                Encoding.ASCII.GetBytes("free"));

            byte[] filler = DeterministicBytes(fillerLength - free.Length, 33);

            return Concat(ftyp, free, filler);
        }

        private static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;

            foreach (byte value in data)
            {
                crc ^= value;

                for (int bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
                }
            }

            return crc ^ 0xFFFFFFFF;
        }

        private static byte[] Concat(params byte[][] parts)
        {
            byte[] result = new byte[parts.Sum(p => p.Length)];
            int offset = 0;

            foreach (byte[] part in parts)
            {
                Array.Copy(part, 0, result, offset, part.Length);
                offset += part.Length;
            }

            return result;
        }

        private static string LocateSevenZip()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    string candidate = Path.Combine(directory.FullName, "src", "ArchiveFixer", "tools", "7zip", "7z.exe");

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                directory = directory.Parent;
            }

            string local = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe");

            if (File.Exists(local))
            {
                return local;
            }

            throw new InvalidOperationException("找不到内置 7z.exe。");
        }

        /// <summary>
        /// 找本机已装的 <c>Rar.exe</c>（与 <see cref="RarSampleSet"/> 同一套探测目录）。
        /// **只读取、只在本机临时目录里造样本**：共享软件的建包器绝不复制、绝不入库。
        /// </summary>
        private static string? LocateRarExe()
        {
            foreach (string directory in new ToolLocator().WinRarInstallationCandidates())
            {
                string candidate = Path.Combine(directory, "Rar.exe");

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private void Run7z(string workingDirectory, params string[] args)
        {
            RunProcess(SevenZipPath, workingDirectory, args);
        }

        private static void RunProcess(string exe, string workingDirectory, params string[] args)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };

            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(psi)!;

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(120_000))
            {
                process.Kill(true);
                throw new TimeoutException($"{Path.GetFileName(exe)} 造样本超时。");
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"{Path.GetFileName(exe)} 失败（exit {process.ExitCode}）：{string.Join(' ', args)}\n{stdout}\n{stderr}");
            }
        }

        private static string HashFile(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
    }
}
