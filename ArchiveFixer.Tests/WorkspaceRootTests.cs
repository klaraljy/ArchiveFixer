using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 第 23 条：**工作区默认跟着输出盘走**（用户 2026-09-24 拍板）+ 空壳工作区当场清掉。
    ///
    /// <para><b>用户原话</b>：「刚刚我就发现你会讲解压失败的残留放在安装包的位置，这次是小的 5G 左右，
    /// 那要是 40G 的东西，解压不小心失败了，你同样会放在安装位置吗」。</para>
    ///
    /// <para>这一组钉六件事：</para>
    /// <list type="number">
    /// <item><description>默认根 = <c>&lt;输出盘&gt;\.ArchiveFixer.work</c>：指定位置时跟着那个盘，
    /// 未指定位置时跟着源包所在盘（断言**落点真的来自源包目录**，不是另拼的一条路）；</description></item>
    /// <item><description>拿不到盘 → 回落程序目录 + WARN 说明原因（整批照常开工）；</description></item>
    /// <item><description>用户显式设过 <c>CacheRootDirectory</c> → 永远以它为准（一个字都不改他的选择）；</description></item>
    /// <item><description>工作区**不在**源目录里、**不在**成品目录里（反向断言，两处都要）；</description></item>
    /// <item><description>失败 / 取消：**有文件一律保留**；一个文件都没有 → 空壳当场删掉；</description></item>
    /// <item><description>③ 页在"默认跟输出盘"形态下照旧扫得到、删得掉，越界仍不动；
    /// 空间核算把"同卷 = 暂存与成品是同一份字节"如实算进去。</description></item>
    /// </list>
    ///
    /// <para><b>测试纪律</b>：临时目录全在 <see cref="Path.GetTempPath"/> 下的独立目录里；
    /// **绝不依赖本机真有 D 盘**（盘符用可注入的探针模拟，见 <c>WorkspaceRootResolver</c> 的
    /// <c>driveOf</c> / <c>driveExists</c> / <c>ensureDirectory</c> 三个参数）；也**绝不会去建
    /// <c>C:\.ArchiveFixer.work</c>**（凡是用真实盘根映射的用例一律把"建目录"换成记账替身）。
    /// 构造 <c>MainViewModel</c> 会写进程级静态（递归工作区根），所以整类进
    /// <c>ArchiveFixerGlobalState</c> 集合、与其它同类用例串行，并在装配后立刻还原。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class WorkspaceRootTests : IDisposable
    {
        private readonly string _root;

        public WorkspaceRootTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerWorkspaceRoot", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论（句柄可能还在释放中）。
            }
        }

        // ================================================================ ① 解析规则（纯逻辑）

        /// <summary>
        /// 指定位置在 D:\ → 工作区根就在 D:\（`D:\.ArchiveFixer.work`），而且**不去建程序目录下那个老位置**。
        ///
        /// <para>盘符是**假盘**（探针说 D:\ 在、建目录只记账），所以这条用例不碰本机任何真实盘 ——
        /// 更不会在测试里造出 <c>D:\.ArchiveFixer.work</c> 或 <c>C:\.ArchiveFixer.work</c>。</para>
        /// </summary>
        [Fact]
        public void 指定位置在D盘_工作区根就在D盘_不碰程序目录()
        {
            string dataRoot = Path.Combine(_root, "program-data");
            var created = new List<string>();

            WorkspaceRootResolution resolution = WorkspaceRootResolver.Resolve(
                configuredCacheRoot: null,
                dataRootDirectory: dataRoot,
                batchDestinationDirectories: new[] { @"D:\out\pack-1" },
                driveExists: drive => string.Equals(drive, @"D:\", StringComparison.OrdinalIgnoreCase),
                ensureDirectory: path =>
                {
                    created.Add(path);
                    return true;
                });

            Assert.Equal(WorkspaceRootOrigin.OutputDrive, resolution.Origin);
            Assert.Equal(Path.Combine(@"D:\", ".ArchiveFixer.work"), resolution.RootDirectory);
            Assert.False(resolution.IsFallback);

            // 只建了输出盘上那一个；程序目录（老位置）一个目录都没碰。
            Assert.Equal(new[] { Path.Combine(@"D:\", ".ArchiveFixer.work") }, created);
            Assert.DoesNotContain(created, path => path.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase));

            // 日志里必须写清"在哪块盘"：用户照着这一行就能去盘上找。
            Assert.Contains("D", resolution.Reason, StringComparison.Ordinal);
            Assert.Contains(resolution.RootDirectory, resolution.Reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// 未指定位置时落点 = 源包所在目录 → 工作区跟着**源包那块盘**。
        /// 判据不只是"根在哪块盘"（那可能只是碰巧），而是**喂给它的落点真的来自源包目录**。
        /// </summary>
        [Fact]
        public void 未指定位置_工作区跟着源包所在盘()
        {
            string sourceDirectory = Path.Combine(_root, "src");
            string dataRoot = Path.Combine(_root, "program-data");

            Directory.CreateDirectory(sourceDirectory);

            // 落点由调用方走唯一实现（PathService.ResolveOutputPlacement）算出来 —— 这里就是它的结果形态：
            // 未指定位置 = 源包所在目录 + 包名。
            string destination = Path.Combine(sourceDirectory, "pack-1");

            WorkspaceRootResolution resolution = WorkspaceRootResolver.Resolve(
                configuredCacheRoot: null,
                dataRootDirectory: dataRoot,
                batchDestinationDirectories: new[] { destination },
                ensureDirectory: _ => true);

            Assert.Equal(WorkspaceRootOrigin.OutputDrive, resolution.Origin);

            // 真实盘根映射：临时目录与源目录在同一块盘上，所以根就在那块盘的根下。
            Assert.Equal(
                Path.Combine(Path.GetPathRoot(sourceDirectory)!, ".ArchiveFixer.work"),
                resolution.RootDirectory);
        }

        /// <summary>一批里落点跨盘：取**第一个**能算出盘的，并且**如实说明跨了几个盘**（不许假装同盘）。</summary>
        [Fact]
        public void 一批跨盘_取第一个盘且如实说明()
        {
            WorkspaceRootResolution resolution = WorkspaceRootResolver.Resolve(
                configuredCacheRoot: null,
                dataRootDirectory: Path.Combine(_root, "program-data"),
                batchDestinationDirectories: new[] { @"D:\out\a", @"E:\other\b" },
                driveExists: _ => true,
                ensureDirectory: _ => true);

            Assert.Equal(Path.Combine(@"D:\", ".ArchiveFixer.work"), resolution.RootDirectory);
            Assert.True(resolution.CrossDrive);
            Assert.Equal(2, resolution.BatchDrives.Count);
            Assert.Contains("跨 2 个盘", resolution.Reason, StringComparison.Ordinal);
            Assert.Contains(@"E:", resolution.Reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// 拿不到盘（盘不存在 / 建不出目录）→ **回落老行为** <c>&lt;程序目录&gt;\data\work</c>，
        /// 写 WARN 说明原因，而且**绝不因此让整批开不了工**。
        /// </summary>
        [Theory]
        [InlineData("盘不存在")]
        [InlineData("建不出目录")]
        public void 拿不到盘_回落程序目录并写WARN(string failureKind)
        {
            string dataRoot = Path.Combine(_root, failureKind);

            WorkspaceRootResolution resolution = WorkspaceRootResolver.Resolve(
                configuredCacheRoot: null,
                dataRootDirectory: dataRoot,
                batchDestinationDirectories: new[] { @"Z:\out\pack-1" },
                driveExists: _ => failureKind != "盘不存在",
                ensureDirectory: _ => failureKind != "建不出目录");

            Assert.Equal(WorkspaceRootOrigin.ProgramDirectoryFallback, resolution.Origin);
            Assert.True(resolution.IsFallback);
            Assert.Equal("WARN", resolution.LogLevel);
            Assert.Equal(Path.Combine(dataRoot, "work"), resolution.RootDirectory);

            // 原因必须写具体：是"盘不存在"还是"建不出来"，用户的改法完全不同。
            Assert.Contains(failureKind == "盘不存在" ? "不存在" : "不可写", resolution.Reason, StringComparison.Ordinal);
        }

        /// <summary>一个算得出落点的任务都没有（例如落点全算不出来）→ 同样回落，不抛、不空转。</summary>
        [Fact]
        public void 没有可用落点_回落程序目录()
        {
            string dataRoot = Path.Combine(_root, "no-destination");

            WorkspaceRootResolution resolution = WorkspaceRootResolver.Resolve(
                configuredCacheRoot: null,
                dataRootDirectory: dataRoot,
                batchDestinationDirectories: Array.Empty<string>(),
                ensureDirectory: _ => true);

            Assert.Equal(WorkspaceRootOrigin.ProgramDirectoryFallback, resolution.Origin);
            Assert.Equal(Path.Combine(dataRoot, "work"), resolution.RootDirectory);
        }

        /// <summary>
        /// 用户**显式设过** <c>CacheRootDirectory</c> → 永远以它为准（<c>&lt;它&gt;\work</c>），
        /// 哪怕这一批的落点在别的盘上。用户的选择一个字都不改。
        /// </summary>
        [Fact]
        public void 用户设过缓存根目录_永远以它为准()
        {
            string configured = Path.Combine(_root, "user-cache");
            var created = new List<string>();

            WorkspaceRootResolution resolution = WorkspaceRootResolver.Resolve(
                configuredCacheRoot: configured,
                dataRootDirectory: Path.Combine(_root, "program-data"),
                batchDestinationDirectories: new[] { @"D:\out\pack-1" },
                driveExists: _ => true,
                ensureDirectory: path =>
                {
                    created.Add(path);
                    return true;
                });

            Assert.Equal(WorkspaceRootOrigin.ConfiguredCacheRoot, resolution.Origin);
            Assert.Equal(Path.Combine(configured, "work"), resolution.RootDirectory);

            // 与改这一条之前的行为逐字相同：老位置就在缓存根下面。
            Assert.Equal(new[] { Path.Combine(configured, "work") }, created);
            Assert.Contains("以它为准", resolution.Reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// **反向断言**：工作区根不在源目录里、也不在成品目录里（不变量 12）。
        ///
        /// <para>这里用的是**真实盘根映射**（临时目录在 C 盘 → 根会算成 <c>C:\.ArchiveFixer.work</c>），
        /// 但建目录换成了记账替身 —— 所以断言的是真实路径形状，而磁盘上什么都没被创建。</para>
        /// </summary>
        [Fact]
        public void 工作区根不在源目录也不在成品目录()
        {
            string sourceDirectory = Path.Combine(_root, "src");
            string asSource = Path.Combine(sourceDirectory, "pack-1");

            Directory.CreateDirectory(sourceDirectory);

            WorkspaceRootResolution resolution = WorkspaceRootResolver.Resolve(
                configuredCacheRoot: null,
                dataRootDirectory: Path.Combine(_root, "program-data"),
                batchDestinationDirectories: new[] { asSource },
                ensureDirectory: _ => true);

            Assert.False(
                ArchivePathGuard.IsInsideRoot(sourceDirectory, resolution.RootDirectory, out _),
                $"工作区根落进了源目录：{resolution.RootDirectory}");

            Assert.False(
                ArchivePathGuard.IsInsideRoot(asSource, resolution.RootDirectory, out _),
                $"工作区根落进了成品目录：{resolution.RootDirectory}");

            // 它也不等于源目录 / 成品目录本身。
            Assert.False(SafePathHelper.PathEquals(sourceDirectory, resolution.RootDirectory));
            Assert.False(SafePathHelper.PathEquals(asSource, resolution.RootDirectory));
        }

        /// <summary>目录名的形状是刻意的：点开头（Windows 默认隐藏）、且只有这一个来源。</summary>
        [Fact]
        public void 默认工作区目录名是点开头的隐藏目录()
        {
            Assert.Equal(".ArchiveFixer.work", WorkspaceRootResolver.DefaultWorkspaceDirectoryName);
            Assert.Equal(WorkspaceRootResolver.DefaultWorkspaceDirectoryName, PathService.DefaultWorkspaceDirectoryName);
            Assert.StartsWith(".", WorkspaceRootResolver.DefaultWorkspaceDirectoryName, StringComparison.Ordinal);
        }

        /// <summary>工作区根**按需创建**：启动时那一轮基础目录创建不许碰它（否则会建在错的盘上）。</summary>
        [Fact]
        public void 确保基础目录_不再无脑建工作区()
        {
            string dataRoot = Path.Combine(_root, "lazy");
            var pathService = new PathService { DataRootDirectory = dataRoot };

            pathService.EnsureBaseDirectories();

            Assert.True(Directory.Exists(pathService.LogsDirectory), "日志目录本来就该建");
            Assert.True(Directory.Exists(pathService.TempDirectory), "临时目录本来就该建");
            Assert.False(
                Directory.Exists(Path.Combine(dataRoot, "work")),
                "工作区根不该在启动时被建出来（默认跟输出盘，那时还不知道是哪块盘）");
        }

        // ================================================================ ② 用过的根的小账本

        /// <summary>账本：记住 → 读回；同一个根只出现一次；读取时忽略注释行；写不进去也不抛。</summary>
        [Fact]
        public void 用过的根记在账本里_重启后照样读得回()
        {
            string dataRoot = Path.Combine(_root, "index");
            Directory.CreateDirectory(dataRoot);

            WorkspaceRootIndex.Remember(dataRoot, @"D:\.ArchiveFixer.work");
            WorkspaceRootIndex.Remember(dataRoot, @"E:\.ArchiveFixer.work");

            // 再记一次第一个：它回到最前，且不重复。
            List<string> roots = WorkspaceRootIndex.Remember(dataRoot, @"D:\.ArchiveFixer.work");

            Assert.Equal(new[] { @"D:\.ArchiveFixer.work", @"E:\.ArchiveFixer.work" }, roots);
            Assert.Equal(roots, WorkspaceRootIndex.Load(dataRoot));

            // 数据根为空 / 不存在：只返回空，不抛。
            Assert.Empty(WorkspaceRootIndex.Load(null));
            Assert.Empty(WorkspaceRootIndex.Load(Path.Combine(_root, "不存在的数据根")));
        }

        // ================================================================ ③ 空间口径（同卷 / 跨卷）

        /// <summary>
        /// **同卷 = 暂存与成品是同一份字节**：峰值只算一份内容物（定稿走同卷改名），
        /// 而且这句话必须能出现在依据里 —— 用户要求"如实算进去"，那就得看得见。
        /// </summary>
        [Fact]
        public void 空间口径_同卷时内容物只算一份()
        {
            var estimate = new TaskSpaceEstimate
            {
                SourceBytes = 3L * 1024 * 1024 * 1024,
                ContentBytes = 2L * 1024 * 1024 * 1024,
                ProcessArtifactBytes = 512L * 1024 * 1024
            }.WithVolumeLayout(workspaceSharesTargetVolume: true);

            Assert.True(estimate.WorkspaceSharesTargetVolume);

            // 峰值 = 源包 + 过程物 + 内容物 **各一份**（不是 2×内容物：同卷定稿是改名不是复制）。
            Assert.Equal(3L * 1024 * 1024 * 1024 + 2L * 1024 * 1024 * 1024 + 512L * 1024 * 1024, estimate.PeakBytes);

            // 工作区那一份（内容物 + 过程物）单独可查 —— 跨卷时它就是工作区盘的下限。
            Assert.Equal(2L * 1024 * 1024 * 1024 + 512L * 1024 * 1024, estimate.StagingBytes);

            Assert.Contains("同卷", estimate.Basis, StringComparison.Ordinal);
            Assert.Contains("只算一份", estimate.Basis, StringComparison.Ordinal);
            Assert.Contains("同卷", TaskSpaceEstimate.DescribeVolumeLayout(true), StringComparison.Ordinal);
        }

        /// <summary>跨卷：如实说清"定稿是复制、工作区盘还要另留一份"，不许默认成同卷。</summary>
        [Fact]
        public void 空间口径_跨卷与未知时如实说明()
        {
            var crossVolume = new TaskSpaceEstimate
            {
                SourceBytes = 1000,
                ContentBytes = 2000,
                ProcessArtifactBytes = 500
            }.WithVolumeLayout(workspaceSharesTargetVolume: false);

            Assert.Contains("跨卷", crossVolume.Basis, StringComparison.Ordinal);
            Assert.Contains("已知限制", crossVolume.Basis, StringComparison.Ordinal);

            // 峰值本身不变（口径改的是"这块盘还够不够"的解释，不是凭空加需求）。
            Assert.Equal(3500, crossVolume.PeakBytes);

            // 一句话说法里要带出"工作区那块盘另需多少"，否则日志里看不出这一档的代价。
            Assert.Contains("工作区那块盘另需", crossVolume.Describe(), StringComparison.Ordinal);

            var unknown = new TaskSpaceEstimate().WithVolumeLayout(workspaceSharesTargetVolume: null);

            Assert.Null(unknown.WorkspaceSharesTargetVolume);
            Assert.Contains("未知", unknown.Basis, StringComparison.Ordinal);

            // 未知那一档**不许**声称"只算一份"（那是同卷才成立的口径）。
            Assert.DoesNotContain("只算一份", TaskSpaceEstimate.DescribeVolumeLayout(null), StringComparison.Ordinal);
            Assert.Contains("不做同卷假设", TaskSpaceEstimate.DescribeVolumeLayout(null), StringComparison.Ordinal);
        }

        // ================================================================ ④ 批首解析（真管线）

        /// <summary>
        /// **默认档 + 指定位置**：整批的工作区根落在（假）输出盘上，暂存目录真的写在它下面，
        /// 程序目录那一份**一个字节都没建**，日志里写明"本批工作区在 X 盘"。
        /// </summary>
        [Fact]
        public async Task 管线_默认跟在输出盘上_且不碰程序目录()
        {
            Harness harness = CreateHarness(configureCacheRoot: false, customOutput: true);
            string source = harness.CreateSourceFile("pack.7z");

            harness.Coordinator.WorkspaceDriveOverride = _ => harness.FakeDriveRoot;
            harness.Coordinator.WorkspaceDriveExistsOverride = drive => Directory.Exists(drive);

            ArchiveTask task = harness.AddTask(source);

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                harness.Engine.LastExtractOutputPath = request.OutputPath ?? string.Empty;
                WriteFiles(request.OutputPath!, 2);
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(2));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            string expectedRoot = Path.Combine(harness.FakeDriveRoot, ".ArchiveFixer.work");

            Assert.Equal(expectedRoot, harness.PathService.WorkDirectory);
            Assert.Equal(expectedRoot, RecursiveExtractor.ConfiguredWorkspaceRoot);

            // 引擎写盘的地方（暂存目录）确实在这个根下面。
            Assert.StartsWith(expectedRoot, harness.Engine.LastExtractOutputPath, StringComparison.OrdinalIgnoreCase);

            // 程序目录那一份（老位置）在这条路上**根本没被创建**。
            Assert.False(
                Directory.Exists(Path.Combine(harness.CacheRoot, "work")),
                "默认跟输出盘时，程序目录下的老位置一个字节都不该被建");

            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("本批工作区在", StringComparison.Ordinal) &&
                        line.Contains(harness.FakeDriveRoot, StringComparison.OrdinalIgnoreCase));

            // 用过的根进了账本：③ 页与下次启动靠它才找得到这个根。
            Assert.Contains(
                WorkspaceRootIndex.Load(harness.CacheRoot),
                root => string.Equals(root, expectedRoot, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// **未指定位置**：落点由"源包所在目录"算出 → 工作区跟着源包那块盘。
        /// 判据是"喂给盘映射的那个落点确实来自源包目录"，不是只看根落在哪。
        /// </summary>
        [Fact]
        public async Task 管线_未指定位置时跟着源包所在盘()
        {
            Harness harness = CreateHarness(configureCacheRoot: false, customOutput: false);
            string source = harness.CreateSourceFile("plain.7z");

            var seenDestinations = new List<string>();

            harness.Coordinator.WorkspaceDriveOverride = destination =>
            {
                seenDestinations.Add(destination);
                return harness.FakeDriveRoot;
            };

            harness.Coordinator.WorkspaceDriveExistsOverride = _ => true;

            ArchiveTask task = harness.AddTask(source);

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                WriteFiles(request.OutputPath!, 1);
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(1));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.Equal(Path.Combine(harness.FakeDriveRoot, ".ArchiveFixer.work"), harness.PathService.WorkDirectory);

            // 关键证据：算盘用的落点来自**源包所在目录**（未指定位置那一档的落点规则）。
            Assert.Contains(
                seenDestinations,
                destination => destination.StartsWith(
                    Path.GetDirectoryName(source)!,
                    StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>用户设过缓存根目录 → 管线照旧用 <c>&lt;它&gt;\work</c>（老行为一个字都不改）。</summary>
        [Fact]
        public async Task 管线_用户设过缓存根目录时以它为准()
        {
            Harness harness = CreateHarness(configureCacheRoot: true, customOutput: true);
            string source = harness.CreateSourceFile("configured.7z");

            // 注入了假盘也必须用不上：显式设置的那一档**不进盘符分支**。
            harness.Coordinator.WorkspaceDriveOverride = _ => harness.FakeDriveRoot;

            ArchiveTask task = harness.AddTask(source);

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                WriteFiles(request.OutputPath!, 1);
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(1));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.Equal(Path.Combine(harness.CacheRoot, "work"), harness.PathService.WorkDirectory);
            Assert.False(Directory.Exists(Path.Combine(harness.FakeDriveRoot, ".ArchiveFixer.work")));

            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("以它为准", StringComparison.Ordinal));
        }

        /// <summary>
        /// **同卷的空间口径在真管线上看得见**：工作区与成品在同一块盘时，
        /// 空间门的日志里必须出现"同卷…只算一份"这句话（用户要求"如实算进去"，看不见就等于没算）。
        /// </summary>
        [Fact]
        public async Task 管线_同卷时空间依据里写明_只算一份()
        {
            Harness harness = CreateHarness(configureCacheRoot: true, customOutput: true);
            string source = harness.CreateSourceFile("same-volume.7z");

            ArchiveTask task = harness.AddTask(source);

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                WriteFiles(request.OutputPath!, 2);
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(2));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // 工作区（<缓存根>\work）与成品（<out>\包名）都在这个临时目录下的同一块盘上。
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("空间门通过（精确）", StringComparison.Ordinal) &&
                        line.Contains("同卷", StringComparison.Ordinal) &&
                        line.Contains("只算一份", StringComparison.Ordinal));
        }

        /// <summary>
        /// **拿不到盘**：换成"盘不存在"的探针 → 回落程序目录 + WARN，而且**整批照常解压成功**
        /// （绝不因为工作区定不下来就让整批开不了工）。
        /// </summary>
        [Fact]
        public async Task 管线_拿不到盘时回落程序目录且整批照跑()
        {
            Harness harness = CreateHarness(configureCacheRoot: false, customOutput: true);
            string source = harness.CreateSourceFile("fallback.7z");

            harness.Coordinator.WorkspaceDriveOverride = _ => @"Z:\";
            harness.Coordinator.WorkspaceDriveExistsOverride = _ => false;

            ArchiveTask task = harness.AddTask(source);

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                WriteFiles(request.OutputPath!, 1);
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(1));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.Equal(Path.Combine(harness.CacheRoot, "work"), harness.PathService.WorkDirectory);

            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("工作区回落到程序目录", StringComparison.Ordinal) &&
                        line.Contains("盘不存在", StringComparison.Ordinal));
        }

        // ================================================================ ⑤ 空壳工作区

        /// <summary>
        /// 失败收尾**且一个文件都没解出来** → 空壳当场删掉（没有东西可留），日志写清为什么。
        /// 源文件照旧一个字节都不动（不变量 1）。
        /// </summary>
        [Fact]
        public async Task 失败且一个文件都没有_空壳工作区被删掉()
        {
            Harness harness = CreateHarness(configureCacheRoot: true, customOutput: true);
            string source = harness.CreateSourceFile("empty-shell.7z");

            ArchiveTask task = harness.AddTask(source);

            string taskWorkDirectory = harness.PathService.BuildTaskWorkDirectory(task);

            // 假引擎直接报密码错误：暂存目录建出来了，但里面**一个文件都没有**。
            harness.Engine.OnExtractAsync = _ => Task.FromResult(WrongPassword());
            harness.Engine.OnListAsync = _ => Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.WrongPassword, task.Status);
            Assert.False(Directory.Exists(taskWorkDirectory), $"空壳工作区没被清掉：{taskWorkDirectory}");
            Assert.True(File.Exists(source), "失败时源文件必须原样保留");

            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("这次没解出任何东西", StringComparison.Ordinal) &&
                        line.Contains("空壳工作区已清掉", StringComparison.Ordinal));
        }

        /// <summary>
        /// **红线**：失败时工作区里只要有文件就一律保留（那是那批唯一解出来的一份），
        /// 并且日志要说清它留在哪、几个文件、多大。
        ///
        /// <para>这里用双面文件（内嵌归档）：抠取副本是**真实的字节拷贝**，所以失败时工作区里
        /// 一定有东西 —— 正是这条红线要覆盖的局面。</para>
        /// </summary>
        [Fact]
        public async Task 失败但有文件_一律保留()
        {
            Harness harness = CreateHarness(configureCacheRoot: true, customOutput: true);
            string source = harness.CreateEmbeddedSourceFile("kept.7z");

            ArchiveTask task = harness.AddTask(source);
            task.EmbeddedArchiveOffset = 4096;

            string taskWorkDirectory = harness.PathService.BuildTaskWorkDirectory(task);

            harness.Engine.OnExtractAsync = _ => Task.FromResult(WrongPassword());
            harness.Engine.OnListAsync = _ => Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

            await harness.Coordinator.StartExtractAsync();

            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.True(Directory.Exists(taskWorkDirectory), $"失败时有文件的工作区被清掉了：{taskWorkDirectory}");
            Assert.NotEmpty(Directory.GetFiles(taskWorkDirectory, "*", SearchOption.AllDirectories));

            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("保留在原处", StringComparison.Ordinal));
        }

        /// <summary>取消收尾**且一个文件都没解出来** → 同样按空壳清掉（用户点停之后留下的空壳最没意义）。</summary>
        [Fact]
        public async Task 取消且一个文件都没有_空壳工作区被删掉()
        {
            Harness harness = CreateHarness(configureCacheRoot: true, customOutput: true);
            string source = harness.CreateSourceFile("cancel-empty.7z");

            ArchiveTask task = harness.AddTask(source);

            string taskWorkDirectory = harness.PathService.BuildTaskWorkDirectory(task);

            // 解压"成功"但一个文件都不写；收尾第一件事（列目录）时按下「取消当前」。
            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                harness.Engine.Extracted = true;
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ =>
            {
                if (harness.Engine.Extracted)
                {
                    harness.Coordinator.CancelCurrentTask();
                }

                return Task.FromResult(ListResult(5));
            };

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.Cancelled, task.Status);
            Assert.False(Directory.Exists(taskWorkDirectory), $"取消之后的空壳工作区没被清掉：{taskWorkDirectory}");
            Assert.True(File.Exists(source), "取消时源文件必须原样保留");
        }

        // ================================================================ ⑥ ③ 页（扫得到 / 删得掉 / 越界不动）

        /// <summary>
        /// 扫的根必须**正好是当前生效的那一个**（多一个根就多一批会被列出来、被删掉的目录）。
        ///
        /// <para>这条同时是"界面上报几个残留"的地基：用户设过缓存根时，老位置与生效的根是同一个，
        /// 去重之后只能剩一个 —— 否则同一个目录会被数两遍。</para>
        /// </summary>
        [Fact]
        public void 扫描根_用户设过缓存根时只有那一个根()
        {
            Harness harness = CreateHarness(configureCacheRoot: true, customOutput: true);
            VmHarness vm = harness.CreateViewModel(new DialogService());

            Assert.Equal(new[] { Path.Combine(harness.CacheRoot, "work") }, vm.Vm.WorkspaceScanRoots);
        }

        /// <summary>
        /// **默认跟输出盘**形态下 ③ 页照旧能用：残留散在**两个根**（当前生效的根 + 账本里用过的根）下时
        /// 两个都扫得到（界面一行 + 启动日志带实际位置），确认之后两个都删得掉。
        /// </summary>
        [Fact]
        public void 界面_多个根下的残留都扫得到并删得掉()
        {
            Harness harness = CreateHarness(configureCacheRoot: true, customOutput: true);

            string effectiveRoot = Path.Combine(harness.CacheRoot, "work");
            string otherDriveRoot = Path.Combine(_root, "other-drive", ".ArchiveFixer.work");

            string inEffectiveRoot = CreateLeftover(effectiveRoot, "0001_pack.7z-aaaaaaaa", 2048);
            string inOtherRoot = CreateLeftover(otherDriveRoot, "0002_pack.7z-bbbbbbbb", 4096);

            // 账本里记住"另一个盘上那个根"（= 上一批跟着输出盘定下来的根）。
            WorkspaceRootIndex.Remember(harness.CacheRoot, otherDriveRoot);

            var dialog = new CapturingDialogService { Answer = true };
            VmHarness vm = harness.CreateViewModel(dialog);

            Assert.True(vm.Vm.HasWorkspaceLeftovers);
            Assert.Contains("2 个目录", vm.Vm.WorkspaceLeftoverBanner, StringComparison.Ordinal);
            Assert.Equal(6144, vm.Vm.WorkspaceLeftoverTotalBytes);
            Assert.True(vm.Vm.ClearWorkspaceLeftoversCommand.CanExecute(null));

            vm.Vm.ClearWorkspaceLeftoversCommand.Execute(null);

            Assert.False(Directory.Exists(inEffectiveRoot), "当前生效的根下那个残留没删掉");
            Assert.False(Directory.Exists(inOtherRoot), "账本里那个根下的残留没删掉");
            Assert.False(vm.Vm.HasWorkspaceLeftovers);

            // 确认框明细里逐条带上"它在哪个根下"，用户才知道自己要删的是哪块盘上的东西。
            Assert.Contains(effectiveRoot, dialog.LastDetail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(otherDriveRoot, dialog.LastDetail, StringComparison.OrdinalIgnoreCase);

            // 启动那条日志报的是**实际位置**（两个根都要出现）。
            Assert.Contains(
                vm.LogTexts,
                line => line.Contains("工作区残留", StringComparison.Ordinal) &&
                        line.Contains(effectiveRoot, StringComparison.OrdinalIgnoreCase) &&
                        line.Contains(otherDriveRoot, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 越界仍不动：跨根清理时，工作区**外面**的目录、根目录**本身**、更深的层级，一个字节都不许删
        /// （不变量 13 —— 这条在"多根"形态下同样成立）。
        /// </summary>
        [Fact]
        public void 多根清理_越界仍一个字节都不动()
        {
            string rootA = Path.Combine(_root, "multi", "a");
            string rootB = Path.Combine(_root, "multi", "b");

            string leftoverA = CreateLeftover(rootA, "0001_a.7z-aaaaaaaa", 16);
            string leftoverB = CreateLeftover(rootB, "0002_b.7z-bbbbbbbb", 16);

            string outside = Path.Combine(_root, "multi", "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");

            // "更深的层级"放在**另一个根**的残留下面：它既存在、又不属于 rootB（父目录不是 rootB）。
            string deeper = Path.Combine(leftoverA, "stage", "nested");
            Directory.CreateDirectory(deeper);

            IReadOnlyList<WorkspaceLeftover> scanned = WorkspaceCleanupService.ScanMany(new[] { rootA, rootB, rootA });

            Assert.Equal(2, scanned.Count);
            Assert.Contains(scanned, item => item.RootDirectory == rootA);
            Assert.Contains(scanned, item => item.RootDirectory == rootB);

            IReadOnlyList<WorkspaceCleanupOutcome> outcomes = WorkspaceCleanupService.Cleanup(
                rootB,
                new[] { outside, rootB, deeper, leftoverA, leftoverB });

            Assert.True(Directory.Exists(outside), "工作区外面的目录绝不许删");
            Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
            Assert.True(Directory.Exists(rootB), "工作区根目录本身绝不许删");
            Assert.True(Directory.Exists(leftoverA), "拿 B 根去删 A 根下面的目录必须被拒（越界）");
            Assert.True(Directory.Exists(deeper), "更深的层级不是我们造的目录，绝不许删");

            // 只有"本根直属子目录"那一个真的被删了。
            Assert.False(Directory.Exists(leftoverB));
            Assert.Equal(1, outcomes.Count(item => item.Deleted));
        }

        // ================================================================ 装配

        private static string CreateLeftover(string workRoot, string name, int bytes)
        {
            string directory = Path.Combine(workRoot, name);

            Directory.CreateDirectory(Path.Combine(directory, "stage"));
            File.WriteAllBytes(Path.Combine(directory, "stage", name + ".bin"), new byte[bytes]);

            return directory;
        }

        private static void WriteFiles(string directory, int count)
        {
            Directory.CreateDirectory(directory);

            for (int i = 0; i < count; i++)
            {
                File.WriteAllText(Path.Combine(directory, $"payload-{i:D5}.bin"), "x");
            }
        }

        private static ArchiveOperationResult Succeeded() => new()
        {
            Success = true,
            Status = StatusText.ExtractSuccess,
            Message = "解压成功",
            DetectedErrorType = "None"
        };

        private static ArchiveOperationResult WrongPassword() => new()
        {
            Success = false,
            Status = StatusText.WrongPassword,
            Message = "密码错误",
            DetectedErrorType = "WrongPassword"
        };

        private static ArchiveListResult ListResult(int fileCount) => new()
        {
            Success = true,
            FileCount = fileCount,
            TotalUncompressedSize = 0,
            Entries = Enumerable.Range(0, fileCount)
                .Select(i => new ArchiveEntry { Path = $"payload-{i:D5}.bin", Size = 1 })
                .ToList(),
            EngineId = "fake",
            EngineVersion = "1.0"
        };

        private Harness CreateHarness(bool configureCacheRoot, bool customOutput)
        {
            /*
             * 装配的关键一点：**缓存根目录先设成临时目录**（这样设置 / 日志 / 账本全在临时目录里，
             * 绝不会写进测试输出目录或用户真实数据），构造完再把设置里那一格清空 ——
             * 于是这一批走的是"默认跟输出盘"那一档，而数据根仍然是临时的。
             */
            string cacheRoot = Path.Combine(_root, "cache-" + Guid.NewGuid().ToString("N"));
            string sourceRoot = Path.Combine(_root, "src");
            string outputRoot = Path.Combine(_root, "out");
            string fakeDrive = Path.Combine(_root, "fakedrive");

            Directory.CreateDirectory(cacheRoot);
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(outputRoot);
            Directory.CreateDirectory(fakeDrive);

            var pathService = new PathService { DataRootDirectory = cacheRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CacheRootDirectory = cacheRoot;
            settings.AutoScanAfterDrop = false;
            settings.RecursionMode = "SingleLayer";
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);

            if (customOutput)
            {
                // 指定位置：落点 = <outputRoot>\<包名>。
                settings.ExtractToOriginalDirectory = false;
                settings.CustomOutputDirectory = outputRoot;
            }
            else
            {
                // 未指定位置：落点 = 源包所在目录\<包名>。
                settings.ExtractToOriginalDirectory = true;
                settings.CustomOutputDirectory = string.Empty;
            }

            settingsService.Save(settings);

            var engine = new FakeEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

            MainViewModel vm = CreateViewModel(
                cacheRoot,
                pathService,
                settingsService,
                engine,
                passwordService,
                logService,
                new DialogService());

            var coordinator = new ExtractionCoordinator(vm, engine, passwordService, pathService, new DialogService());

            /*
             * 二选一（清空的动作刻意放在**装配之后**，见方法开头的说明）：
             * · true  = 用户**显式设过**缓存根目录（老行为，工作区 = <cacheRoot>\work）；
             * · false = 留空（默认档：跟着输出盘）。
             *
             * Settings 对象与 VM **共享**（同一份引用），所以批首读到的就是这个值。
             */
            vm.Settings.CacheRootDirectory = configureCacheRoot ? cacheRoot : string.Empty;

            RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
            ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

            return new Harness(
                vm,
                coordinator,
                engine,
                logService,
                pathService,
                cacheRoot,
                sourceRoot,
                fakeDrive);
        }

        /// <summary>造一个 MainViewModel（构造里会扫一次工作区、写进程级静态，所以由装配方负责还原）。</summary>
        private static MainViewModel CreateViewModel(
            string dataRoot,
            PathService pathService,
            SettingsService settingsService,
            IArchiveEngine engine,
            PasswordService passwordService,
            LogService logService,
            DialogService dialogService)
        {
            _ = dataRoot;

            return new MainViewModel(
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
                dialogService);
        }

        private sealed class Harness
        {
            public Harness(
                MainViewModel vm,
                ExtractionCoordinator coordinator,
                FakeEngine engine,
                LogService log,
                PathService pathService,
                string cacheRoot,
                string sourceRoot,
                string fakeDriveRoot)
            {
                Vm = vm;
                Coordinator = coordinator;
                Engine = engine;
                Log = log;
                PathService = pathService;
                CacheRoot = cacheRoot;
                SourceRoot = sourceRoot;
                FakeDriveRoot = fakeDriveRoot;
            }

            public MainViewModel Vm { get; }

            public ExtractionCoordinator Coordinator { get; }

            public FakeEngine Engine { get; }

            public LogService Log { get; }

            public PathService PathService { get; }

            /// <summary>数据根（临时目录）：程序目录在测试里的替身。</summary>
            public string CacheRoot { get; }

            public string SourceRoot { get; }

            /// <summary>假输出盘根（测试注入的盘映射指向它，所以绝不碰真实盘）。</summary>
            public string FakeDriveRoot { get; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.Message);

            public string CreateSourceFile(string fileName)
            {
                string path = Path.Combine(SourceRoot, fileName);
                File.WriteAllText(path, "not a real archive - the engine is faked in these tests");
                return path;
            }

            /// <summary>造"双面文件"：偏移之后那一段才是归档 —— 抠取是真实字节拷贝，失败时工作区里会有东西。</summary>
            public string CreateEmbeddedSourceFile(string fileName)
            {
                string path = Path.Combine(SourceRoot, fileName);
                byte[] bytes = new byte[4096 + 512];

                for (int i = 0; i < bytes.Length; i++)
                {
                    bytes[i] = (byte)(i % 251);
                }

                File.WriteAllBytes(path, bytes);
                return path;
            }

            public ArchiveTask AddTask(string sourcePath)
            {
                var task = new ArchiveTask(sourcePath, Vm.Tasks.Count + 1)
                {
                    IsArchive = true,
                    DetectedFormat = "7Z",
                    ExtensionStatus = StatusText.ExtensionNormal,
                    Status = StatusText.Recognized,
                    IsSelected = true
                };

                Vm.Tasks.Add(task);
                return task;
            }

            /// <summary>另造一个 MainViewModel（③ 页那两条只需要界面这一层，不需要再跑解压）。</summary>
            public VmHarness CreateViewModel(DialogService dialogService)
            {
                string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;

                MainViewModel vm = WorkspaceRootTests.CreateViewModel(
                    CacheRoot,
                    PathService,
                    new SettingsService(PathService),
                    new NeverAvailableEngine(),
                    new PasswordService(),
                    Log,
                    dialogService);

                RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;

                return new VmHarness(vm, Log);
            }
        }

        private sealed class VmHarness
        {
            public VmHarness(MainViewModel vm, LogService log)
            {
                Vm = vm;
                LogService = log;
            }

            public MainViewModel Vm { get; }

            public LogService LogService { get; }

            public IEnumerable<string> LogTexts => LogService.Logs.Select(item => item.Message);
        }

        /// <summary>把用户答案写死、并把确认框明细记下来的替身（只在测试里用）。</summary>
        private sealed class CapturingDialogService : DialogService
        {
            public bool Answer { get; set; }

            public string LastDetail { get; private set; } = string.Empty;

            public override bool ShowConfirm(
                string message,
                string optionText,
                bool optionCheckedByDefault,
                string detail,
                out bool optionChecked)
            {
                LastDetail = detail;
                optionChecked = false;
                return Answer;
            }
        }

        /// <summary>本组用例不碰解压（③ 页那两条）：一个永远不可用的假引擎就够。</summary>
        private sealed class NeverAvailableEngine : IArchiveEngine
        {
            public string Id => "none";

            public string DisplayName => "假引擎";

            public string Version => "1.0";

            public bool IsAvailable => false;

            public EngineCapabilities Capabilities { get; } = new();

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveProbeResult { IsArchive = false });

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveListResult());

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveOperationResult { Success = false });

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveOperationResult { Success = false });
        }

        /// <summary>可控的假引擎（与 <c>ExtractionPipelineFixTests</c> 同一套做法）。</summary>
        private sealed class FakeEngine : IArchiveEngine
        {
            public List<string> ExtractCalls { get; } = new();

            public string LastExtractOutputPath { get; set; } = string.Empty;

            public bool Extracted { get; set; }

            public Func<ArchiveRequest, Task<ArchiveOperationResult>>? OnExtractAsync { get; set; }

            public Func<ArchiveRequest, Task<ArchiveListResult>>? OnListAsync { get; set; }

            public string Id => "fake";

            public string DisplayName => "假引擎";

            public string Version => "1.0";

            public bool IsAvailable => true;

            public EngineCapabilities Capabilities { get; } = new()
            {
                CanProbe = true,
                CanList = true,
                CanTest = true,
                CanExtract = true,
                SupportsPassword = true
            };

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                OnListAsync != null ? OnListAsync(request) : Task.FromResult(ListResult(0));

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default) =>
                Task.FromResult(Succeeded());

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                ExtractCalls.Add(request.ArchivePath);

                if (OnExtractAsync != null)
                {
                    return OnExtractAsync(request);
                }

                LastExtractOutputPath = request.OutputPath ?? string.Empty;
                return Task.FromResult(Succeeded());
            }
        }
    }
}
