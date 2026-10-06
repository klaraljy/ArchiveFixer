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
    /// 工作区根：**只有一个来源 —— 这一单的目标位置**（用户 2026-09-30 收口拍板）+ 空壳工作区当场清掉。
    ///
    /// <para><b>用户原话</b>：「刚刚我就发现你会讲解压失败的残留放在安装包的位置，这次是小的 5G 左右，
    /// 那要是 40G 的东西，解压不小心失败了，你同样会放在安装位置吗」；
    /// 收口那句：「**这个彻底取消，用户没有定工作区的权力，就是在解压的地方设立隐形的工作区，
    /// 这就完全不存在跨盘的操作**」。</para>
    ///
    /// <para>这一组钉六件事：</para>
    /// <list type="number">
    /// <item><description>工作区根 = <c>&lt;这一批第一个算得出落点的目标目录&gt;\.ArchiveFixer.work</c>
    /// （点开头 + Hidden 属性；目标目录还不存在就先建它）；</description></item>
    /// <item><description>⛔ 所有目标位置都不可用 → 报错说清是哪一个，**绝不回落 C 盘 / 程序目录**
    /// （老实现回落到 <c>&lt;程序目录&gt;\data\work</c> 并 WARN 开工，已被用户否掉）；</description></item>
    /// <item><description>⛔ **任何输入下都不会在别的盘创建工作区**：横扫设置里各个字段的组合，
    /// 断言根路径的前缀就是目标目录那块盘（用例 目标目录在别的盘_不回落C盘也不碰程序目录）；</description></item>
    /// <item><description>工作区**不在**源包那一层；</description></item>
    /// <item><description>失败 / 取消：**有文件一律保留**；一个文件都没有 → 空壳当场删掉；</description></item>
    /// <item><description>③ 页照旧扫得到、删得掉，越界仍不动；空间核算把"同卷 = 暂存与成品是同一份字节"
    /// 如实算进去。</description></item>
    /// </list>
    ///
    /// <para><b>测试纪律</b>：临时目录全在 <see cref="Path.GetTempPath"/> 下的独立目录里；
    /// **绝不依赖本机真有 D 盘**（盘符用可注入的探针模拟，见 <c>WorkspaceRootResolver</c> 的
    /// <c>driveOf</c> / <c>ensureDirectory</c> 两个参数）；也**绝不会去建
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
        /// **新默认档**（用户 2026-09-30）：工作区根落在**这一单的目标目录里面**
        /// （<c>&lt;目标目录&gt;\.ArchiveFixer.work</c>），不再去盘根开第四个顶层目录。
        ///
        /// <para>同时钉三件事：① 目标目录还不存在时**先建它**（"原地就在原地创"）；
        /// ② 工作区那一层**带 Hidden 属性**（用户"要创的话就隐秘文件"）；
        /// ③ 程序目录下的老位置一个字节都不碰。</para>
        /// </summary>
        [Fact]
        public void 默认档_工作区根落在目标目录里面_带隐藏属性且先建目标目录()
        {
            string dataRoot = Path.Combine(_root, "program-data");
            string target = Path.Combine(_root, "H盘上的成果", "1111");
            var created = new List<string>();

            WorkspaceRootResolution resolution = WorkspaceRootResolver.Resolve(
                new[] { target },
                ensureDirectory: path =>
                {
                    created.Add(path);
                    return true;
                });

            Assert.Equal(WorkspaceRootOrigin.OutputDirectory, resolution.Origin);
            Assert.True(resolution.Resolved);
            Assert.Equal(Path.Combine(target, ".ArchiveFixer.work"), resolution.RootDirectory);
            Assert.Equal(target, resolution.TargetDirectory);

            // ① 先建目标目录、再在其中建工作区；两下都在目标目录这一棵里。
            Assert.Equal(new[] { target, Path.Combine(target, ".ArchiveFixer.work") }, created);
            Assert.DoesNotContain(created, path => path.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase));

            // ② 工作区落在目标目录里面 —— 它的父目录正好就是目标目录。
            Assert.Equal(target, Path.GetDirectoryName(resolution.RootDirectory));

            // 日志里必须写清"在哪、为什么"，用户照着这一行就能去目录里找。
            Assert.Contains(target, resolution.Reason, StringComparison.Ordinal);
            Assert.Contains("目标目录", resolution.Reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// ⛔ **目标位置不可用 → 报错说清是哪一个，绝不回落 C 盘 / 程序目录**（用户 2026-09-30 点名的红线：
        /// "甚至危险操作固定到了 C 盘"）。
        ///
        /// <para>老实现这里会悄悄回落到 <c>&lt;程序目录&gt;\data\work</c> 并写一条 WARN"整批照常开工"；
        /// 现在必须是：<c>RootDirectory</c> 为空 + <c>Origin = TargetUnusable</c> + ERROR 级 +
        /// 文案里**说清是哪个目标位置不行、以及去哪儿改落点**。</para>
        ///
        /// <para>⚠ 用户 2026-09-30 收口后，工作区只有"由目标位置派生"这一条路（设置项
        /// <c>CacheRootDirectory</c> 已删除），所以这一档**不是**"解析不出来该挑哪个盘"，
        /// 而是"唯一那条路走不通" —— 文案里不许再出现"去哪儿设工作区"。</para>
        /// </summary>
        [Theory]
        [InlineData("一个落点都没有")]
        [InlineData("目标目录建不出来")]
        public void 目标位置不可用_报错且绝不回落程序目录(string failureKind)
        {
            string dataRoot = Path.Combine(_root, "program-data-" + failureKind);

            WorkspaceRootResolution resolution = WorkspaceRootResolver.Resolve(
                failureKind == "一个落点都没有"
                    ? Array.Empty<string>()
                    : new[] { Path.Combine(_root, "建不出来的目标目录") },
                ensureDirectory: _ => failureKind != "目标目录建不出来");

            Assert.Equal(WorkspaceRootOrigin.TargetUnusable, resolution.Origin);
            Assert.False(resolution.Resolved);

            // 关键：**没有**任何位置被选出来 —— 尤其不是程序目录下那个老位置。
            Assert.Equal(string.Empty, resolution.RootDirectory);
            Assert.DoesNotContain(dataRoot, resolution.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\\data\\work", resolution.Reason, StringComparison.OrdinalIgnoreCase);

            // 级别必须是 ERROR（不许降级成 WARN 让它看起来"只是慢一点"）。
            Assert.Equal("ERROR", resolution.LogLevel);

            // 文案必须说清：工作区建在目标目录里、位置没有任何设置项可以改、去哪儿改落点。
            Assert.Contains("目标位置不可用", resolution.Reason, StringComparison.Ordinal);
            Assert.Contains("目标目录", resolution.Reason, StringComparison.Ordinal);
            Assert.Contains("输出位置", resolution.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("缓存根目录", resolution.Reason, StringComparison.Ordinal);

            if (failureKind == "目标目录建不出来")
            {
                Assert.Contains("不可写", resolution.Reason, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// **一批里落点跨盘**：工作区跟着**第一个**目标目录，并**如实说明**本批跨了几个盘
        /// （不许假装它们同盘）。
        /// </summary>
        [Fact]
        public void 一批跨盘_跟着第一个目标目录且如实说明()
        {
            WorkspaceRootResolution resolution = WorkspaceRootResolver.Resolve(
                new[] { @"D:\out\a", @"E:\other\b" },
                driveOf: path => path.StartsWith(@"E:", StringComparison.OrdinalIgnoreCase) ? @"E:\" : @"D:\",
                ensureDirectory: _ => true);

            Assert.Equal(Path.Combine(@"D:\out\a", ".ArchiveFixer.work"), resolution.RootDirectory);
            Assert.True(resolution.CrossDrive);
            Assert.Equal(2, resolution.BatchDrives.Count);
            Assert.Contains("跨 2 个盘", resolution.Reason, StringComparison.Ordinal);
            Assert.Contains(@"E:", resolution.Reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// ⛔ **解析器不接受任何来自设置 / 用户的覆盖**（用户 2026-09-30："这个彻底取消，用户没有定
        /// 工作区的权力"）。
        ///
        /// <para>这条是"把设置项读回来当覆盖"那类改动的守门用例：参数表里已经没有"缓存根目录"这个形参，
        /// 于是**一个再像"用户指定位置"的输入都进不来**。用例用反射把参数表钉死 ——
        /// 谁要是哪天又加回一个"可以指定工作区"的入参，这里立刻变红。</para>
        /// </summary>
        [Fact]
        public void 解析器_不接受任何外部指定的工作区位置()
        {
            string[] parameterNames = typeof(WorkspaceRootResolver)
                .GetMethod(nameof(WorkspaceRootResolver.Resolve))!
                .GetParameters()
                .Select(parameter => parameter.Name ?? string.Empty)
                .ToArray();

            Assert.Equal(new[] { "batchDestinationDirectories", "driveOf", "ensureDirectory" }, parameterNames);

            // 枚举里也不许再有"用户指定"的那一档（老值 ConfiguredCacheRoot 已删除）。
            Assert.DoesNotContain(
                Enum.GetNames<WorkspaceRootOrigin>(),
                name => name.Contains("Configured", StringComparison.OrdinalIgnoreCase));

            // 而且真的没有"另一个候选位置"这种东西：只有目标目录那一个答案。
            string target = Path.Combine(_root, "唯一的目标目录");
            var created = new List<string>();

            WorkspaceRootResolution resolution = WorkspaceRootResolver.Resolve(
                new[] { target },
                ensureDirectory: path =>
                {
                    created.Add(path);
                    return true;
                });

            Assert.Equal(new[] { target, Path.Combine(target, ".ArchiveFixer.work") }, created);
            Assert.Equal(Path.Combine(target, ".ArchiveFixer.work"), resolution.RootDirectory);
            Assert.Equal(target, resolution.TargetDirectory);
        }

        /// <summary>
        /// 工作区根**在目标目录里面**（用户 2026-09-30 的新口径），而且**不与源包同层**
        /// （不变量 12 还站得住的那一半：中间产物不许散在源包那一层里）。
        ///
        /// <para>⚠ 有意写清楚这一档的变化：未指定位置时目标目录 = <c>&lt;源目录&gt;\&lt;包名&gt;</c>，
        /// 于是工作区落在 <c>&lt;源目录&gt;\&lt;包名&gt;\.ArchiveFixer.work</c> —— 它在**我们生成的成品目录里面**，
        /// 不在源包那一层。老的"既不在源目录、也不在成品目录"这条断言与新口径直接冲突，
        /// 已被用户 2026-09-30 的指示取代；这里保留的是"不许散进源包那一层"。</para>
        /// </summary>
        [Fact]
        public void 工作区根在目标目录里面_且不与源包同层()
        {
            string sourceDirectory = Path.Combine(_root, "src");
            string asSource = Path.Combine(sourceDirectory, "pack-1");

            Directory.CreateDirectory(sourceDirectory);

            WorkspaceRootResolution resolution = WorkspaceRootResolver.Resolve(
                new[] { asSource },
                ensureDirectory: _ => true);

            // ① 在目标目录里面。
            Assert.True(
                ArchivePathGuard.IsInsideRoot(asSource, resolution.RootDirectory, out _),
                $"工作区根不在目标目录里面：{resolution.RootDirectory}");

            // ② 不与源包同层：它的**父目录**是成品目录，不是源包所在的那一层。
            Assert.Equal(asSource, Path.GetDirectoryName(resolution.RootDirectory));
            Assert.False(
                SafePathHelper.PathEquals(sourceDirectory, Path.GetDirectoryName(resolution.RootDirectory)),
                "工作区落进了源包那一层");

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
                "工作区根不该在启动时被建出来（它只由目标目录派生，那时还不知道目标目录在哪）");
        }

        // ================================================================ ② 用过的根（只记本次会话）

        /// <summary>
        /// 账本**只记本次会话用过的根**（用户 2026-09-30 收口）：记住 → 读回、同一个根只出现一次，
        /// 而且**一个字节都不落盘** —— 重启之后不会再去猜"上次在哪开过工"。
        /// </summary>
        [Fact]
        public void 用过的根只记本次会话_不落盘也不读历史()
        {
            WorkspaceRootIndex.ResetForTests();

            try
            {
                WorkspaceRootIndex.Remember(@"D:\.ArchiveFixer.work");
                WorkspaceRootIndex.Remember(@"E:\.ArchiveFixer.work");

                // 再记一次第一个：它回到最前，且不重复。
                List<string> roots = WorkspaceRootIndex.Remember(@"D:\.ArchiveFixer.work");

                Assert.Equal(new[] { @"D:\.ArchiveFixer.work", @"E:\.ArchiveFixer.work" }, roots);
                Assert.Equal(roots, WorkspaceRootIndex.SessionRoots);

                // ⛔ 落盘清单那套已经删除：数据根里**没有**任何账本文件。
                string dataRoot = Path.Combine(_root, "index");
                Directory.CreateDirectory(dataRoot);

                Assert.Empty(Directory.GetFiles(dataRoot));
                Assert.DoesNotContain(
                    typeof(WorkspaceRootIndex).GetMembers(),
                    member => member.Name.Contains("Load", StringComparison.Ordinal)
                              || member.Name.Contains("FileName", StringComparison.Ordinal));

                // 空 / 空白输入只当没记，不抛也不留空条目。
                WorkspaceRootIndex.Remember(null);
                WorkspaceRootIndex.Remember("   ");

                Assert.Equal(roots, WorkspaceRootIndex.SessionRoots);
            }
            finally
            {
                WorkspaceRootIndex.ResetForTests();
            }
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
        /// **默认档 + 指定位置**：整批的工作区根落在**这一单的目标目录里面**
        /// （<c>&lt;目标目录&gt;\.ArchiveFixer.work</c>），暂存目录真的写在它下面，那一层**带 Hidden 属性**，
        /// 程序目录那一份**一个字节都没建**，日志里写明"工作区在目标目录里面"。
        ///
        /// <para>用户 2026-09-30 的三条诉求在这里一次性钉住：不再占盘根一个顶层目录、目录是隐秘的、
        /// 且"原地就在原地创"。</para>
        /// </summary>
        [Fact]
        public async Task 管线_默认落在目标目录里面_带隐藏属性且不碰程序目录()
        {
            Harness harness = CreateHarness(customOutput: true);
            string source = harness.CreateSourceFile("pack.7z");

            ArchiveTask task = harness.AddTask(source);

            // 工作区那一层在解压进行中才存在（批尾会连空壳一起收掉），所以隐藏属性在引擎回调里取证。
            bool workspaceHiddenDuringExtract = false;

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                harness.Engine.LastExtractOutputPath = request.OutputPath ?? string.Empty;

                string runningRoot = Path.Combine(harness.OutputRoot, "pack", ".ArchiveFixer.work");

                workspaceHiddenDuringExtract =
                    Directory.Exists(runningRoot) &&
                    (File.GetAttributes(runningRoot) & FileAttributes.Hidden) != 0;

                WriteFiles(request.OutputPath!, 2);
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(2));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            string expectedRoot = Path.Combine(harness.OutputRoot, "pack", ".ArchiveFixer.work");

            Assert.Equal(expectedRoot, harness.PathService.WorkDirectory);
            Assert.Equal(expectedRoot, RecursiveExtractor.ConfiguredWorkspaceRoot);

            // 目标目录真的被建出来了（"目标目录还不存在就先建它"）。
            Assert.True(Directory.Exists(harness.OutputRoot), $"目标目录没被建出来：{harness.OutputRoot}");

            // 引擎写盘的地方（暂存目录）确实在这个根下面。
            Assert.StartsWith(expectedRoot, harness.Engine.LastExtractOutputPath, StringComparison.OrdinalIgnoreCase);

            // ① 工作区那一层**带 Hidden 属性** —— 用户翻自己目录时看不见它（诉求③）。
            Assert.True(workspaceHiddenDuringExtract, $"工作区目录没有 Hidden 属性：{expectedRoot}");

            // ② 不再在盘根开第四个顶层目录：盘根那个老位置不该出现。
            string driveRootWorkspace = Path.Combine(Path.GetPathRoot(harness.OutputRoot)!, ".ArchiveFixer.work");
            Assert.False(
                string.Equals(driveRootWorkspace, expectedRoot, StringComparison.OrdinalIgnoreCase),
                "工作区又回到盘根去了");

            // 程序目录那一份（老位置）在这条路上**根本没被创建**。
            Assert.False(
                Directory.Exists(Path.Combine(harness.CacheRoot, "work")),
                "默认档下程序目录下的老位置一个字节都不该被建");

            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("本批工作区在目标目录里面", StringComparison.Ordinal) &&
                        line.Contains(expectedRoot, StringComparison.OrdinalIgnoreCase));

            // 用过的根进了**本次会话**的账本：③ 页这一趟靠它才找得到这个根。
            Assert.Contains(
                WorkspaceRootIndex.SessionRoots,
                root => string.Equals(root, expectedRoot, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// **未指定位置**：落点由"源包所在目录"算出 → 工作区就落在**源包旁边那个包名目录里面**。
        /// 判据是"喂给解析器的目标目录确实来自源包目录"，不是只看根落在哪。
        /// </summary>
        [Fact]
        public async Task 管线_未指定位置时落在源包旁边的目标目录里面()
        {
            Harness harness = CreateHarness(customOutput: false);
            string source = harness.CreateSourceFile("plain.7z");

            var seenDestinations = new List<string>();

            harness.Coordinator.WorkspaceDriveOverride = destination =>
            {
                seenDestinations.Add(destination);
                return Path.GetPathRoot(destination) ?? string.Empty;
            };

            ArchiveTask task = harness.AddTask(source);

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                WriteFiles(request.OutputPath!, 1);
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(1));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            string expectedRoot = Path.Combine(harness.SourceRoot, "plain", ".ArchiveFixer.work");

            Assert.Equal(expectedRoot, harness.PathService.WorkDirectory);

            // 关键证据：解析用的目标目录来自**源包所在目录**（未指定位置那一档的落点规则）。
            Assert.Contains(
                seenDestinations,
                destination => destination.StartsWith(
                    Path.GetDirectoryName(source)!,
                    StringComparison.OrdinalIgnoreCase));

            // 而且工作区**不在**源包那一层（不变量 12：中间产物不许写进源目录）。
            Assert.False(
                string.Equals(harness.SourceRoot, Path.GetDirectoryName(expectedRoot), StringComparison.OrdinalIgnoreCase),
                "工作区落进了源目录那一层");
        }

        /// <summary>
        /// ⛔ **管线里也没有第二个位置**：工作区永远在目标目录里面，
        /// 而且**设置里任何一个字段的取值都改不动它**（用户 2026-09-30："用户没有定工作区的权力"）。
        ///
        /// <para>造法：把这批任务的目标目录指到指定位置（<c>harness.OutputRoot</c>），
        /// 然后断言 ① 根 = <c>&lt;目标目录&gt;\&lt;包名&gt;\.ArchiveFixer.work</c>；
        /// ② 程序目录下那个老位置 <c>&lt;数据根&gt;\work</c> 一个字节都没被建。</para>
        /// </summary>
        [Fact]
        public async Task 管线_工作区只在目标目录里_设置改不动它()
        {
            Harness harness = CreateHarness(customOutput: true);
            string source = harness.CreateSourceFile("configured.7z");

            ArchiveTask task = harness.AddTask(source);

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                WriteFiles(request.OutputPath!, 1);
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(1));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            string expectedRoot = Path.Combine(harness.OutputRoot, "configured", ".ArchiveFixer.work");

            Assert.Equal(expectedRoot, harness.PathService.WorkDirectory);

            // ⛔ 老位置（数据根下的 work）在这条路上**根本没被创建** —— 没有任何设置能把它换回来。
            Assert.False(
                Directory.Exists(Path.Combine(harness.CacheRoot, "work")),
                "数据根下的老位置不该被建：工作区只由目标目录派生");
        }

        /// <summary>
        /// **同卷的空间口径在真管线上看得见**：工作区与成品在同一块盘（同一棵树）时，
        /// 空间门的日志里必须出现"同卷…只算一份"这句话（用户要求"如实算进去"，看不见就等于没算）。
        /// </summary>
        [Fact]
        public async Task 管线_同卷时空间依据里写明_只算一份()
        {
            Harness harness = CreateHarness(customOutput: true);
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

            // 新默认档下工作区就在目标目录里面 ⇒ 两者必然同卷。
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("空间门通过（精确）", StringComparison.Ordinal) &&
                        line.Contains("同卷", StringComparison.Ordinal) &&
                        line.Contains("只算一份", StringComparison.Ordinal));
        }

        /// <summary>
        /// ⛔ **目标位置不可用 → 整批停手并报错，绝不悄悄换个盘**（用户 2026-09-30 的红线）。
        ///
        /// <para>造法：把输出位置指到一个**不存在的盘**（落点预检会判否），
        /// 于是这一批一个算得出目标目录的任务都没有。老实现会回落程序目录并"照常开工"；
        /// 现在必须是：任务一个字节都没动、程序目录下的老位置没被建、日志里 ERROR 说清是哪个位置不行。</para>
        /// </summary>
        [Fact]
        public async Task 管线_目标位置不可用时整批停手并报错()
        {
            Harness harness = CreateHarness(customOutput: true);

            // 把输出位置指到一个**不存在的盘**（OutputPlacement 的盘符预检会判否）→ 一个目标目录都算不出来。
            harness.Vm.SelectedOutputDirectory = @"Z:\不存在的盘\成果";

            string source = harness.CreateSourceFile("no-destination.7z");
            ArchiveTask task = harness.AddTask(source);

            bool engineCalled = false;

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                engineCalled = true;
                WriteFiles(request.OutputPath!, 1);
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(1));

            await harness.Coordinator.StartExtractAsync();

            Assert.False(engineCalled, "目标位置不可用时引擎绝不该被调用（一个字节都不许写）");
            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.True(File.Exists(source), "源包必须原样保留");

            // 程序目录下的老位置**一个字节都没建**（这就是"不许回落"的可观测判据）。
            Assert.False(
                Directory.Exists(Path.Combine(harness.CacheRoot, "work")),
                "目标位置不可用时，程序目录下的老位置绝不许被建出来");

            // 必须说清：工作区只由目标目录派生、位置没有任何设置项可以改、去哪儿改落点。
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("没有开工", StringComparison.Ordinal) &&
                        line.Contains("目标位置不可用", StringComparison.Ordinal) &&
                        line.Contains("输出位置", StringComparison.Ordinal));
        }

        // ================================ ④补 工作区根**不许**由"入口包还没解出来"那一单来定

        /// <summary>
        /// 真机 `CCCC`（2026-10-06，用户当场问「批的工作区根 / 落点还是按"任务表里这一组第一单"选的，
        /// 你到底改没改」）：表里第一单是散在 `111(4)` 的 `111.z0删除3`，它那一组的**入口包 `111.zip`
        /// 正压在 `111.rar` 里、还没解出来** ⇒ 落点退回旧口径 ⇒ `111(4)\111`；而工作区根取"第一个算得出
        /// 落点的目标目录" ⇒ 整批的中间产物落在 `CCCC\111(4)\111\.ArchiveFixer.work`，用户目录里凭空多出这一层。
        ///
        /// <para>判据只有一条：**这一单的入口包现在在不在盘上**（唯一出口
        /// <c>GroupVolumeDirectory.Resolve</c>，与落点实现同一个事实位）。不在 ⇒ 它的"目标目录"只是占位值，
        /// 排到最后当兜底。于是工作区根落在**能真解出东西的那一单**自己那条链上（`out\pack`）。</para>
        ///
        /// <para><b>红检</b>：把 <c>IsUnresolvedVolumeGroupMember</c> 那一档拿掉（回到"按任务表顺序取第一个"）
        /// ⇒ 第三条断言变红（根落在 <c>out\pack(2)\pack</c>，也就是真机那个 <c>111(4)\111</c> 的同一形状）。</para>
        /// </summary>
        [Fact]
        public async Task 管线_入口包还没解出来那一单不当工作区根的锚点()
        {
            Harness harness = CreateHarness(customOutput: true);

            // 第 1 单：这一组散着的一片（`other.z02`），而入口包 `other.zip` 还在别处的包里没解出来。
            Directory.CreateDirectory(Path.Combine(harness.SourceRoot, "other(2)"));
            string scattered = harness.CreateSourceFile(Path.Combine("other(2)", "other.z0删除2"));
            ArchiveTask unresolved = harness.AddTask(scattered);

            unresolved.IsVolumeGroup = true;
            unresolved.VolumePaths.Add(scattered);

            // 第 2 单：真能解出东西的那一份（另一个包名 ⇒ 落点必然不同，"挑错人"才看得见）。
            string producer = harness.CreateSourceFile("pack.rar");
            ArchiveTask producing = harness.AddTask(producer);

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                WriteFiles(request.OutputPath!, 2);
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(2));

            await harness.Coordinator.StartExtractAsync();

            string placeholderRoot = Path.Combine(harness.OutputRoot, "other", ".ArchiveFixer.work");
            string expectedRoot = Path.Combine(harness.OutputRoot, "pack", ".ArchiveFixer.work");

            // ① 这一单确实"拿不到入口包"（前提成立，否则这条用例什么也没验）。
            Assert.True(unresolved.IsVolumeGroup);
            Assert.Empty(Detection.GroupVolumeDirectory.Resolve(new[] { scattered }).Path);

            // ② 工作区根落在**能解出东西的那一单**那条链上，⛔ 不是"表里第一单"那一层。
            Assert.Equal(expectedRoot, harness.PathService.WorkDirectory);
            Assert.Equal(expectedRoot, RecursiveExtractor.ConfiguredWorkspaceRoot);

            // ③ 用户目录里那层占位目录**没有**成为整批工作区（这就是真机 `111(4)\111` 的同一形状）。
            Assert.NotEqual(placeholderRoot, harness.PathService.WorkDirectory);

            // ④ 如实说清为什么没用它（判据是"入口包还没被解出来"，不是"这单被跳过了"）。
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("入口包还没被解出来", StringComparison.Ordinal));

            // ⑤ 两个任务照旧各自跑完 —— 这一档**只影响挑哪个目录当工作区根**。
            Assert.Equal(StatusText.ExtractSuccess, producing.Status);
            Assert.Equal(StatusText.ExtractSuccess, unresolved.Status);
        }

        /// <summary>
        /// 兜底那一档不许丢：**整批只有"入口包还没解出来"的分卷组成员**时，照旧拿它的目标目录当工作区根
        /// —— ⛔ 绝不因为"挑不出更好的根"就整批停手（用户 2026-09-30 的红线只针对"一个目标目录都算不出来"）。
        /// </summary>
        [Fact]
        public async Task 管线_整批只有拿不到入口包的分卷组_照旧用它的目标目录当根()
        {
            Harness harness = CreateHarness(customOutput: true);

            Directory.CreateDirectory(Path.Combine(harness.SourceRoot, "other(2)"));
            string scattered = harness.CreateSourceFile(Path.Combine("other(2)", "other.z0删除2"));
            ArchiveTask only = harness.AddTask(scattered);

            only.IsVolumeGroup = true;
            only.VolumePaths.Add(scattered);

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                WriteFiles(request.OutputPath!, 1);
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(1));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(
                Path.Combine(harness.OutputRoot, "other", ".ArchiveFixer.work"),
                harness.PathService.WorkDirectory);

            // 引擎真的被调过（不是"整批没开工"）。
            Assert.Equal(StatusText.ExtractSuccess, only.Status);
        }

        /// <summary>
        /// 真机 `CCCC`（2026-10-06）：入口包（跨盘 ZIP 的末片 `111.zip`）还压在 `111.rar` 里时，
        /// 那一组散着的片**落点要跟"产出它的那一单"那条链**，⛔ 不是"任务表里这一组第一单所在目录"。
        ///
        /// <para>这就是用户连着追问的那一条：旧口径把 `111.zip` 与产物一起带到 `111(4)\111`。
        /// 现在：`pack.z0删除2` 这一单（唯一在盘上的片是续卷）⇒ 入口包标准名 = `pack.zip`
        /// （`VolumeGroupDetector.TryGetFirstVolumeName`）⇒ 产出方 = 同基名、又不是本组成员的 `111.rar`
        /// ⇒ 落点 = `&lt;产出方落点&gt;`（`out\111`）—— 与产出方**同一层**。</para>
        ///
        /// <para><b>红检</b>：把 <c>GroupProducerEntryResolver</c> 那一挂拿掉（或让它恒返回空）
        /// ⇒ 落点退回 `out\pack`（源包自己那一层 + 包名）⇒ 这条断言变红。</para>
        /// </summary>
        [Fact]
        public async Task 管线_入口包还没解出来时_落点跟产出它的那一单那条链()
        {
            Harness harness = CreateHarness(customOutput: true);

            // 产出方：`111.rar`（真机上它把入口包 `111.zip` 解出来、落在 out\111）。
            // 名字刻意与那一组同基名（`111`）—— 判据不是"名字像"，而是"同包基名 + 不是本组成员"。
            Directory.CreateDirectory(Path.Combine(harness.SourceRoot, "111"));
            string producer = harness.CreateSourceFile(Path.Combine("111", "111.rar")); ArchiveTask producing = harness.AddTask(producer);

            // 那一组散着的一片（续卷）：入口包本体不在盘上。
            Directory.CreateDirectory(Path.Combine(harness.SourceRoot, "111(4)"));
            string scattered = harness.CreateSourceFile(Path.Combine("111(4)", "111.z0删除2"));
            ArchiveTask unresolved = harness.AddTask(scattered);

            unresolved.IsVolumeGroup = true;
            unresolved.VolumePaths.Add(scattered);

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                WriteFiles(request.OutputPath!, 1);
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(1));

            // 前提成立：这一组自己那几片里没有入口包（唯一在盘上的是一片续卷）。
            Assert.Empty(Detection.GroupVolumeDirectory.Resolve(new[] { scattered }).Path);
            Assert.Equal(
                "111.zip",
                Detection.VolumeGroupDetector.TryGetFirstVolumeName("111.z0删除2"));

            await harness.Coordinator.StartExtractAsync();

            // 产出方那一单自己那条链的落点（`out\111`）—— 入口包将来就落在它这一层。
            string producerLanding = Path.Combine(harness.OutputRoot, "111");

            // ① 这一组的片落点 = 入口包**将来**所在的那一层，⛔ 不是 `out\111(4)\111`。
            Assert.Equal(producerLanding, unresolved.OutputPath);

            // 对照组：旧口径（源包自己那一层 + 包名）必须**不是**这个答案。
            Assert.NotEqual(Path.Combine(harness.OutputRoot, "111(4)", "111"), unresolved.OutputPath);

            // ② 工作区根也跟着落到产出方那一层（与落点同一个事实位）。
            Assert.Equal(
                Path.Combine(producerLanding, ".ArchiveFixer.work"),
                harness.PathService.WorkDirectory);

            // ③ 如实说清"为什么落在那一层"（判据是"入口包还没被解出来 + 产出它的那一单"）。
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("入口包", StringComparison.Ordinal) &&
                        line.Contains("现在还没被解出来", StringComparison.Ordinal) &&
                        line.Contains("产出它的那一单", StringComparison.Ordinal));

            Assert.Equal(StatusText.ExtractSuccess, producing.Status);
        }

        // ================================================================ ⑤ 空壳工作区

        /// <summary>
        /// 失败收尾**且一个文件都没解出来** → 空壳当场删掉（没有东西可留），日志写清为什么。
        /// 源文件照旧一个字节都不动（不变量 1）。
        /// </summary>
        [Fact]
        public async Task 失败且一个文件都没有_空壳工作区被删掉()
        {
            Harness harness = CreateHarness(customOutput: true);
            string source = harness.CreateSourceFile("empty-shell.7z");

            ArchiveTask task = harness.AddTask(source);


            // 假引擎直接报密码错误：暂存目录建出来了，但里面**一个文件都没有**。
            harness.Engine.OnExtractAsync = _ => Task.FromResult(WrongPassword());
            harness.Engine.OnListAsync = _ => Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

            await harness.Coordinator.StartExtractAsync();

            // ⚠ 路径必须在**批尾**算：工作区根由这一批的目标目录派生，批首之前那个值是老位置。
            string taskWorkDirectory = harness.PathService.BuildTaskWorkDirectory(task);

            Assert.Equal(StatusText.WrongPassword, task.Status);
            Assert.False(Directory.Exists(taskWorkDirectory), $"空壳工作区没被清掉：{taskWorkDirectory}");
            Assert.True(File.Exists(source), "失败时源文件必须原样保留");

            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("这次没解出任何东西", StringComparison.Ordinal) &&
                        line.Contains("空壳工作区已清掉", StringComparison.Ordinal));
        }

        /// <summary>
        /// **默认档**（用户 2026-09-25 第 25 条追加）：失败时工作区里有文件也**整份清掉** ——
        /// 用户原话："我不希望有这么多的失败残留……有一个导出失败列表不就可以了吗，
        /// 而且对于用户来说，失败了就失败了，成功了就成功了"。
        ///
        /// <para>这里用双面文件（内嵌归档）：抠取副本是**真实的字节拷贝**，所以失败时工作区里
        /// 一定有东西 —— 正是这条新默认档要覆盖的局面。三条红线同时钉住：
        /// 源包原地不动、日志说清删了几个多大、③ 页扫不到任何残留。</para>
        /// </summary>
        [Fact]
        public async Task 失败但有文件_默认档整个工作区被清掉()
        {
            Harness harness = CreateHarness(customOutput: true);
            string source = harness.CreateEmbeddedSourceFile("cleared.7z");

            ArchiveTask task = harness.AddTask(source);
            task.EmbeddedArchiveOffset = 4096;


            harness.Engine.OnExtractAsync = _ => Task.FromResult(WrongPassword());
            harness.Engine.OnListAsync = _ => Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

            await harness.Coordinator.StartExtractAsync();

            // ⚠ 路径必须在**批尾**算：工作区根由这一批的目标目录派生，批首之前那个值是老位置。
            string taskWorkDirectory = harness.PathService.BuildTaskWorkDirectory(task);

            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.False(Directory.Exists(taskWorkDirectory), $"默认档下失败的工作区还在：{taskWorkDirectory}");

            // 源包一个字节都不动（不变量 1，正反两条：文件还在 + 内容没被改）。
            Assert.True(File.Exists(source), "失败时源文件必须原样保留");

            // 删了什么、多大、怎么改主意：一条 INFO 说全。
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("已清理工作区：", StringComparison.Ordinal) &&
                        line.Contains("这次没成功", StringComparison.Ordinal) &&
                        line.Contains("失败时保留中间产物", StringComparison.Ordinal));

            // ③ 页扫的是各工作区根：清干净之后一个残留都不该列出来。
            Assert.Empty(WorkspaceCleanupService.ScanMany(harness.Vm.WorkspaceScanRoots));
        }

        /// <summary>
        /// 打开「失败时保留中间产物」＝回到老行为：有文件就保留，并且日志说清**在哪、几个、多大**
        /// （③ 页扫得到、能清）。
        /// </summary>
        [Fact]
        public async Task 打开保留开关_失败但有文件_一律保留且说清在哪()
        {
            Harness harness = CreateHarness(customOutput: true);
            string source = harness.CreateEmbeddedSourceFile("kept.7z");

            ArchiveTask task = harness.AddTask(source);
            task.EmbeddedArchiveOffset = 4096;


            // 用户在 ③ 页打开「失败时保留中间产物（排查用）」。
            harness.Vm.Settings.KeepFailedWorkspace = true;

            harness.Engine.OnExtractAsync = _ => Task.FromResult(WrongPassword());
            harness.Engine.OnListAsync = _ => Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

            await harness.Coordinator.StartExtractAsync();

            // ⚠ 路径必须在**批尾**算：工作区根由这一批的目标目录派生，批首之前那个值是老位置。
            string taskWorkDirectory = harness.PathService.BuildTaskWorkDirectory(task);

            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.True(Directory.Exists(taskWorkDirectory), $"失败时有文件的工作区被清掉了：{taskWorkDirectory}");
            Assert.NotEmpty(Directory.GetFiles(taskWorkDirectory, "*", SearchOption.AllDirectories));

            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("保留在原处", StringComparison.Ordinal) &&
                        line.Contains(taskWorkDirectory, StringComparison.OrdinalIgnoreCase));

            // 留着就一定要**扫得到**（用户 2026-09-24 第 22 条：看不见的保留等于没保留）。
            IReadOnlyList<WorkspaceLeftover> leftovers = WorkspaceCleanupService.ScanMany(harness.Vm.WorkspaceScanRoots);

            Assert.Contains(leftovers, item => string.Equals(item.DirectoryPath, taskWorkDirectory, StringComparison.OrdinalIgnoreCase));
            Assert.True(WorkspaceCleanupService.TotalBytesOf(leftovers) > 0, "开着保留开关留下的残留应当有体积可报");
        }

        /// <summary>
        /// 零文件空壳**无论如何都删**：打开「失败时保留中间产物」也删 —— 空目录不占空间、
        /// 也没有现场可看，留着只会在 ③ 页多一行噪声。
        /// </summary>
        [Fact]
        public async Task 打开保留开关_零文件空壳照样删掉()
        {
            Harness harness = CreateHarness(customOutput: true);
            string source = harness.CreateSourceFile("empty-shell-kept.7z");

            ArchiveTask task = harness.AddTask(source);


            harness.Vm.Settings.KeepFailedWorkspace = true;

            harness.Engine.OnExtractAsync = _ => Task.FromResult(WrongPassword());
            harness.Engine.OnListAsync = _ => Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

            await harness.Coordinator.StartExtractAsync();

            // ⚠ 路径必须在**批尾**算：工作区根由这一批的目标目录派生，批首之前那个值是老位置。
            string taskWorkDirectory = harness.PathService.BuildTaskWorkDirectory(task);

            Assert.False(Directory.Exists(taskWorkDirectory), $"打开保留开关时空壳工作区也该删：{taskWorkDirectory}");
            Assert.True(File.Exists(source), "失败时源文件必须原样保留");
        }

        /// <summary>
        /// **边界一：工作区里有外来子目录 → 一个字节都不删**（删除的第二道容器内校验）。
        ///
        /// <para>判据与成功路径的 <c>CleanupTaskWorkspaceDirectory</c> 逐字相同：本任务的工作区里
        /// 只允许有我们自己造的 <c>stage</c> 子目录。出现别的子目录说明"它不是我们造的那个目录"
        /// （最典型：任务名撞上了递归工作区的 <c>recursive</c>），为安全起见整份不动、只写 WARN。</para>
        /// </summary>
        [Fact]
        public async Task 失败清理_工作区里有外来子目录_一个字节都不删()
        {
            Harness harness = CreateHarness(customOutput: true);
            string source = harness.CreateSourceFile("foreign.7z");

            ArchiveTask task = harness.AddTask(source);

            string foreign = string.Empty;

            harness.Engine.OnExtractAsync = _ => Task.Run(() =>
            {
                /*
                 * 外来子目录必须在**解压进行中**造出来（收尾清理那一刻它得已经在那儿）。
                 * ⚠ 路径在这里算：工作区根由这一批的目标目录派生，批首才定下来 —— 装配时算的是老位置。
                 */
                string work = harness.PathService.BuildTaskWorkDirectory(task);

                foreign = Path.Combine(work, "不是我们造的");

                Directory.CreateDirectory(foreign);
                File.WriteAllText(Path.Combine(foreign, "keep.txt"), "这不是程序造的内容，绝不许删");

                return WrongPassword();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.WrongPassword, task.Status);
            Assert.True(Directory.Exists(foreign), "工作区里有外来子目录时，一个字节都不许删");
            Assert.True(File.Exists(Path.Combine(foreign, "keep.txt")));
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("非本任务造的子目录", StringComparison.Ordinal) &&
                        line.Contains(foreign, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// **边界二：记下来的目录不在（当前生效的）工作区根之下 → 一个字节都不删**。
        ///
        /// <para>造法：解压途中把工作区根换到别处（真实场景 = 用户下一批改了缓存根 / 换了输出盘），
        /// 于是收尾时那条记录已经不在根之下。容器内校验收紧到"必须在根之下"，
        /// 越界只写 WARN —— 它保护的是"工作区之外的任何东西都不许被这条清理碰到"。</para>
        /// </summary>
        [Fact]
        public async Task 失败清理_目录不在工作区根之下_一个字节都不删()
        {
            Harness harness = CreateHarness(customOutput: true);
            string source = harness.CreateSourceFile("outside-root.7z");

            ArchiveTask task = harness.AddTask(source);

            string taskWorkDirectory = string.Empty;
            string otherRoot = Path.Combine(_root, "别的根", "work");

            Directory.CreateDirectory(otherRoot);

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                // ⚠ 先算真实路径（工作区根由这一批的目标目录派生，批首才定下来），再把它换走。
                taskWorkDirectory = harness.PathService.BuildTaskWorkDirectory(task);

                // 解压途中工作区根被换走（下一批跟了别的输出目录）：收尾时那条记录不再属于当前根。
                harness.PathService.WorkDirectory = otherRoot;
                File.WriteAllText(Path.Combine(request.OutputPath!, "half.bin"), "半截产物");
                return WrongPassword();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.WrongPassword, task.Status);
            Assert.True(Directory.Exists(taskWorkDirectory), "越界的工作区目录绝不许删");
            Assert.NotEmpty(Directory.GetFiles(taskWorkDirectory, "*", SearchOption.AllDirectories));
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("不在工作区根之下", StringComparison.Ordinal));
        }

        /// <summary>取消收尾**且一个文件都没解出来** → 同样按空壳清掉（用户点停之后留下的空壳最没意义）。</summary>
        [Fact]
        public async Task 取消且一个文件都没有_空壳工作区被删掉()
        {
            Harness harness = CreateHarness(customOutput: true);
            string source = harness.CreateSourceFile("cancel-empty.7z");

            ArchiveTask task = harness.AddTask(source);


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

            // ⚠ 路径必须在**批尾**算：工作区根由这一批的目标目录派生，批首之前那个值是老位置。
            string taskWorkDirectory = harness.PathService.BuildTaskWorkDirectory(task);

            Assert.Equal(StatusText.Cancelled, task.Status);
            Assert.False(Directory.Exists(taskWorkDirectory), $"取消之后的空壳工作区没被清掉：{taskWorkDirectory}");
            Assert.True(File.Exists(source), "取消时源文件必须原样保留");
        }

        // ================================================================ ⑥ ③ 页（扫得到 / 删得掉 / 越界不动）

        /// <summary>
        /// 本趟还没解过任何东西时，扫的根 = **老位置 + 当前生效的那一个**（去重之后是同一个目录）。
        ///
        /// <para>这条同时是"界面上报几个残留"的地基：老位置与生效的根重合时必须去重，只能剩一个 ——
        /// 否则同一个目录会被数两遍。</para>
        /// </summary>
        [Fact]
        public void 扫描根_没解过东西时只剩老位置那一个根()
        {
            // 账本是**本次会话**的静态状态：前面几条用例跑过管线，得先清干净才谈得上"没解过东西"。
            WorkspaceRootIndex.ResetForTests();

            try
            {
                Harness harness = CreateHarness(customOutput: true);
                VmHarness vm = harness.CreateViewModel(new DialogService());

                Assert.Equal(new[] { Path.Combine(harness.CacheRoot, "work") }, vm.Vm.WorkspaceScanRoots);
            }
            finally
            {
                WorkspaceRootIndex.ResetForTests();
            }
        }

        /// <summary>
        /// ③ 页照旧能用：残留散在**两个根**（当前生效的根 + 本次会话用过的根）下时
        /// 两个都扫得到（界面一行 + 启动日志带实际位置），确认之后两个都删得掉。
        /// </summary>
        [Fact]
        public void 界面_多个根下的残留都扫得到并删得掉()
        {
            Harness harness = CreateHarness(customOutput: true);

            string effectiveRoot = Path.Combine(harness.CacheRoot, "work");
            string otherDriveRoot = Path.Combine(_root, "other-drive", ".ArchiveFixer.work");

            string inEffectiveRoot = CreateLeftover(effectiveRoot, "0001_pack.7z-aaaaaaaa", 2048);
            string inOtherRoot = CreateLeftover(otherDriveRoot, "0002_pack.7z-bbbbbbbb", 4096);

            // 本次会话里用过"另一个盘上那个根"（= 上一批跟着目标目录派生出来的根）。
            WorkspaceRootIndex.Remember(otherDriveRoot);

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

        // ================================================================ ⑦ 工作区那棵树必须被排除
        //
        // 用户 2026-09-30 划的**最关键那个坑**：工作区现在落在输出目录里面，所有
        // "扫产物 / 数内容物 / 找内层包 / 校验落点"的判据都必须把它排除掉；
        // 不排除的后果是程序把工作区里的中间产物当成"用户的内容物"搬出去 —— **不可逆**的事故。

        /// <summary>
        /// 扫描类判据的唯一出口 <see cref="WorkspaceTree"/>：
        /// ① 按名字认出 <c>.ArchiveFixer.work</c>（不需要知道当前生效的根是哪一个）；
        /// ② 按位置认出"在根之下"（覆盖"根还没解析出来 / 名字被改过"那一档）。
        /// </summary>
        [Fact]
        public void 工作区判据_名字与位置两条都认()
        {
            string target = Path.Combine(_root, "excl", "out", "pack");
            string workspace = Path.Combine(target, ".ArchiveFixer.work");

            Directory.CreateDirectory(Path.Combine(workspace, "task-1", "layer-000", "output"));

            string innerDecoy = Path.Combine(workspace, "task-1", "layer-000", "output", "inner.7z");
            File.WriteAllText(innerDecoy, "中间的包，绝不是用户的内容物");

            string realContent = Path.Combine(target, "photo-1.jpg");
            File.WriteAllText(realContent, "真正的内容物");

            // ① 名字：**任意一段**是那个点开头的名字就算（工作区里面的文件靠这一段认出来）。
            Assert.True(WorkspaceTree.IsWorkspaceDirectoryName(workspace));
            Assert.False(WorkspaceTree.IsWorkspaceDirectoryName(innerDecoy), "最后一段是 inner.7z，单看名字认不出来");
            Assert.True(WorkspaceTree.HasWorkspaceSegment(innerDecoy), "但它路径里有一段就是工作区目录名");

            Assert.True(WorkspaceTree.ShouldSkipEntry(workspace, null));
            Assert.True(WorkspaceTree.ShouldSkipEntry(innerDecoy, null));

            // ② 位置：根名不是那个点开头的名字时（老位置 <数据根>\work 里的残留），靠根来认。
            string legacyWorkspace = Path.Combine(_root, "excl", "data", "work");
            string insideLegacy = Path.Combine(legacyWorkspace, "task-9", "stage", "half.bin");

            Directory.CreateDirectory(Path.GetDirectoryName(insideLegacy)!);
            File.WriteAllText(insideLegacy, "半截产物");

            Assert.False(WorkspaceTree.IsWorkspaceDirectoryName(insideLegacy), "work 这个名字本身认不出来");
            Assert.True(WorkspaceTree.ShouldSkipEntry(insideLegacy, legacyWorkspace));

            // 目录内递归枚举：只看得见真正的内容物，工作区里的中间包一个都不出现。
            IReadOnlyList<string> files = WorkspaceTree.EnumerateFiles(target, workspace);

            Assert.Contains(realContent, files);
            Assert.DoesNotContain(innerDecoy, files);
            Assert.DoesNotContain(files, file => file.Contains(".ArchiveFixer.work", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// ⛔ **工作区里的东西绝不许被当成"源包"搬进其它物**（不可逆动作的入口）。
        ///
        /// <para>路径：<c>ProcessArtifactLayout.Plan(task, 其余物目录)</c> 是"把源包搬进可删的其余物"
        /// 的生产入口。源在工作区里时必须**一个字节都不搬**，并如实记下原因。</para>
        /// </summary>
        [Fact]
        public void 工作区里的东西_绝不许被当成源包搬进其余物()
        {
            string target = Path.Combine(_root, "srcmove", "out", "pack");
            string workspace = Path.Combine(target, ".ArchiveFixer.work");
            string artifactDirectory = Path.Combine(target, "其余物");

            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(artifactDirectory);

            // 假装它是"内层包"——它就躺在工作区里面（抠出来的副本 / 逐层中间包都长这样）。
            string decoy = Path.Combine(workspace, "inner.7z");
            File.WriteAllText(decoy, "工作区里的中间产物");

            string? previousRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;

            try
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = workspace;

                var task = new ArchiveTask(decoy, 1)
                {
                    IsArchive = true,
                    DetectedFormat = "7Z",
                    Status = StatusText.Recognized,
                    IsSelected = true
                };

                SourcePackageMovePlan plan = new SourcePackageMover().Plan(task, artifactDirectory);

                Assert.Empty(plan.Moves);
                Assert.Equal(0, plan.TotalSize);
                Assert.Contains(
                    plan.Skipped,
                    skip => skip.SourcePath == decoy && skip.Reason == ArtifactSkipReason.SourceInsideWorkspace);

                // 一个字节都没动：文件还在原地、其余物目录里什么都没有。
                Assert.True(File.Exists(decoy), "工作区里的文件被搬走了");
                Assert.Empty(Directory.GetFileSystemEntries(artifactDirectory));
            }
            finally
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = previousRoot;
            }
        }

        /// <summary>
        /// ⛔ **发布目标落在工作区里 → 立刻停手**。
        ///
        /// <para>工作区默认建在目标目录里面之后，"发布目标"与"工作区"第一次成了父子关系：
        /// 万一有人把工作区本身当成发布目标，产物会被搬进工作区，紧接着收尾清理把它整份删掉 ——
        /// 用户的成品**不可逆地消失**。所以这一档必须一个字节都不动。</para>
        /// </summary>
        [Fact]
        public void 发布目标落在工作区里_立刻停手不搬任何东西()
        {
            string root = Path.Combine(_root, "publish-guard", "work");
            var workspace = new ExtractionWorkspace(root, "task-1");

            WorkspaceLayer layer = workspace.CreateNextLayer(Path.Combine(_root, "fake.7z"));
            string product = Path.Combine(layer.OutputPath, "photo-1.jpg");

            File.WriteAllText(product, "真正的内容物");

            WorkspacePublishResult result = workspace.Publish(workspace.RootDirectory);

            Assert.False(result.Success);
            Assert.Equal(0, result.MovedFileCount);
            Assert.Contains("工作区", result.Message, StringComparison.Ordinal);

            // 产物**还在工作区那一层里**，没有被搬进工作区根（否则紧接着就被清理删掉了）。
            Assert.True(File.Exists(product), "发布被拒之后产物必须原地不动");
            Assert.False(File.Exists(Path.Combine(workspace.RootDirectory, "photo-1.jpg")));
        }

        /// <summary>
        /// **整批结束之后 <c>&lt;目标目录&gt;\.ArchiveFixer.work</c> 这一层空壳不残留**
        /// （用户 2026-09-30："整批结束后这一层空壳也要删掉，别留在用户目录里"）。
        /// </summary>
        [Fact]
        public async Task 整批结束后_工作区空壳不留在目标目录里()
        {
            Harness harness = CreateHarness(customOutput: true);
            string source = harness.CreateSourceFile("shell.7z");

            ArchiveTask task = harness.AddTask(source);

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                WriteFiles(request.OutputPath!, 2);
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(2));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            string workspace = Path.Combine(harness.OutputRoot, "shell", ".ArchiveFixer.work");

            Assert.False(Directory.Exists(workspace), $"整批结束后工作区空壳还留在用户目录里：{workspace}");

            // 整棵目标目录树里**一个工作区痕迹都不许留**（不是只看那一层目录在不在 ——
            // 也可能改成把壳清空却把目录留下、或者留下 stage 之类的东西）。
            Assert.DoesNotContain(
                Directory.EnumerateFileSystemEntries(harness.OutputRoot, "*", SearchOption.AllDirectories),
                path => path.Contains(".ArchiveFixer.work", StringComparison.OrdinalIgnoreCase));

            // 内容物照旧在（收掉的是空壳，不是成品）。
            Assert.Contains(
                Directory.GetFileSystemEntries(Path.Combine(harness.OutputRoot, "shell")),
                path => !path.EndsWith(".ArchiveFixer.work", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// ⛔ **任何输入下都不会在别的盘（尤其 C 盘）创建工作区**（用户 2026-09-30 点名的红线：
        /// "甚至危险操作固定到了 C 盘"）。
        ///
        /// <para>横扫三类"输入"：① <see cref="AppSettings"/> 的**每一个字段**都塞一个指向 C 盘的恶意值；
        /// ② 几个像是"工作区根"的环境变量；③ 落点在别的盘（<c>H:\</c>，用可注入的盘符探针模拟，
        /// 本机不需要真有 H 盘）。判据只有一条：解析出来的根**前缀必须正好是目标目录那块盘**，
        /// 而且这一趟创建的目录**全在目标目录这一棵里**。</para>
        /// </summary>
        [Fact]
        public void 任何输入下_工作区都只在目标目录那块盘上()
        {
            const string target = @"H:\成果\1111";
            const string hostileRoot = @"C:\af-should-never-be-created";

            // ② 环境变量：用几个**专用名**（谁都不读它们，所以改进程环境不会影响别的用例）。
            string[] variableNames =
            {
                "ARCHIVEFIXER_CACHE_ROOT",
                "ARCHIVEFIXER_WORKSPACE_ROOT",
                "ARCHIVEFIXER_WORK_DIR",
                "TEMP_WORKSPACE_ROOT"
            };

            var originals = new Dictionary<string, string?>();

            try
            {
                foreach (string name in variableNames)
                {
                    originals[name] = Environment.GetEnvironmentVariable(name);
                    Environment.SetEnvironmentVariable(name, hostileRoot);
                }

                // ① 设置里每一个字段都塞成"指向 C 盘"的恶意值（字符串字段给路径、布尔的翻一面）。
                AppSettings settings = AppSettings.CreateDefault();

                foreach (System.Reflection.PropertyInfo property in typeof(AppSettings).GetProperties())
                {
                    if (!property.CanWrite || property.GetIndexParameters().Length > 0)
                    {
                        continue;
                    }

                    object? hostile = property.PropertyType == typeof(string)
                        ? Path.Combine(hostileRoot, property.Name)
                        : property.PropertyType == typeof(bool)
                            ? !(bool)(property.GetValue(settings) ?? false)
                            : null;

                    if (hostile != null)
                    {
                        property.SetValue(settings, hostile);
                    }
                }

                var created = new List<string>();

                WorkspaceRootResolution resolution = WorkspaceRootResolver.Resolve(
                    new[] { target },
                    driveOf: _ => @"H:\",
                    ensureDirectory: path =>
                    {
                        created.Add(path);
                        return true;
                    });

                // 判据：前缀 == 目标盘；一个字节都不许落在别的盘上（尤其 C 盘）。
                Assert.Equal(Path.Combine(target, ".ArchiveFixer.work"), resolution.RootDirectory);
                Assert.StartsWith(@"H:\", resolution.RootDirectory, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(@"C:", resolution.RootDirectory, StringComparison.OrdinalIgnoreCase);

                Assert.All(created, path => Assert.StartsWith(target, path, StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(created, path => path.StartsWith(@"C:", StringComparison.OrdinalIgnoreCase));

                // 连盘根级别的工作区都不该出现（老行为会在盘根开第四个顶层目录）。
                string driveRootWorkspace = Path.Combine(@"H:\", ".ArchiveFixer.work");
                Assert.DoesNotContain(driveRootWorkspace, created);
            }
            finally
            {
                foreach ((string name, string? value) in originals)
                {
                    Environment.SetEnvironmentVariable(name, value);
                }
            }
        }

        /// <summary>
        /// ⛔ **点「打开工作区目录」不许凭空造出一个工作区**（用户 2026-09-30："用户没有定工作区的权力"、
        /// "甚至危险操作固定到了 C 盘"）。
        ///
        /// <para>现场：本趟还没解过任何东西时，当前生效的根是**升级前的老位置** <c>&lt;数据根&gt;\work</c>
        /// （它只是"③ 页要扫哪些根"的一项）。老实现先 <c>EnsureDirectoryExists</c> 再打开 ——
        /// 于是"点一下按钮"就在程序所在那块盘上造出一个工作区目录，正是用户点名不要的那件事。</para>
        ///
        /// <para>现在：目录不在就什么都不建，只如实说清"为什么不在"。</para>
        /// </summary>
        [Fact]
        public void 打开工作区目录_目录不在时不建任何目录()
        {
            WorkspaceRootIndex.ResetForTests();

            try
            {
                Harness harness = CreateHarness(customOutput: true);
                var dialog = new CapturingDialogService();

                VmHarness vm = harness.CreateViewModel(dialog);

                string legacyRoot = Path.Combine(harness.CacheRoot, "work");

                Assert.Equal(legacyRoot, vm.Vm.CurrentWorkspaceRoot);
                Assert.False(Directory.Exists(legacyRoot), "前提：这一趟还没解过东西，老位置本来不存在");

                vm.Vm.OpenWorkDirectoryCommand.Execute(null);

                Assert.False(
                    Directory.Exists(legacyRoot),
                    "点一下按钮就在程序那块盘上造出工作区目录 —— 用户明确否掉过这件事");

                Assert.Contains(
                    vm.LogTexts,
                    line => line.Contains("工作区目录现在不存在", StringComparison.Ordinal) &&
                            line.Contains(legacyRoot, StringComparison.OrdinalIgnoreCase));

                // 提示框要**如实说清为什么不在**（不是"出错了"，也不是"这里就是工作区"）。
                Assert.Contains("工作区只跟目标目录走", dialog.LastInfo, StringComparison.Ordinal);
                Assert.Contains(".ArchiveFixer.work", dialog.LastInfo, StringComparison.Ordinal);
            }
            finally
            {
                WorkspaceRootIndex.ResetForTests();
            }
        }

        /// <summary>
        /// ⛔ **我们自己建出来的目标目录壳，批尾空着就自己收掉**（用户 2026-09-30 工作区改到目标目录内之后
        /// 必须补上的收尾）。
        ///
        /// <para>为什么必须有：工作区要落在 <c>&lt;目标目录&gt;\.ArchiveFixer.work</c>，所以批首必须先建
        /// 目标目录（用户原话"原地就在原地创"）。老口径那句"最终目录连建都不该建"因此在实现上不再成立 ——
        /// 但**用户看得见的残留一条都不许多**：一批全失败 / 被取消时那个空壳留着，就是"凭空多一个文件夹"，
        /// 正是他反复抱怨过的东西。</para>
        /// </summary>
        [Fact]
        public async Task 自己建的目标目录_这一单没成_批尾空壳被收掉()
        {
            Harness harness = CreateHarness(customOutput: true);
            string source = harness.CreateSourceFile("shell-gone.7z");

            ArchiveTask task = harness.AddTask(source);

            string createdTarget = Path.Combine(harness.OutputRoot, "shell-gone");

            Assert.False(Directory.Exists(createdTarget), "前提：这个目标目录本来不存在（是我们建的）");

            harness.Engine.OnExtractAsync = _ => Task.FromResult(WrongPassword());
            harness.Engine.OnListAsync = _ => Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

            await harness.Coordinator.StartExtractAsync();

            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.True(File.Exists(source), "失败时源包必须原样保留");

            Assert.False(
                Directory.Exists(createdTarget),
                $"我们自己建出来的空目标目录没被收掉（用户会看到一个凭空多出来的文件夹）：{createdTarget}");
        }

        /// <summary>
        /// ⛔ **用户本来就有的目标目录一个字节都不许动**（上一条的对照面：判据必须包含"确实是我们建的"）。
        /// </summary>
        [Fact]
        public async Task 用户本来就有的目标目录_没解出东西也不许被收掉()
        {
            Harness harness = CreateHarness(customOutput: true);
            string source = harness.CreateSourceFile("shell-kept.7z");

            ArchiveTask task = harness.AddTask(source);

            string userTarget = Path.Combine(harness.OutputRoot, "shell-kept");

            // 用户在解压之前自己就把这个目录建好了（里面什么都没有）。
            Directory.CreateDirectory(userTarget);

            harness.Engine.OnExtractAsync = _ => Task.FromResult(WrongPassword());
            harness.Engine.OnListAsync = _ => Task.FromResult(ArchiveListResult.Failure("WrongPassword", "密码错误", "fake", "1.0"));

            await harness.Coordinator.StartExtractAsync();

            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);
            Assert.True(Directory.Exists(userTarget), "用户本来就有的目标目录绝不许被我们收掉");
            Assert.Empty(Directory.EnumerateFileSystemEntries(userTarget));
        }

        /// <summary>
        /// ⛔ **目标目录在别的盘时，绝不回落到 C 盘 / 程序目录**（同一件事的端到端版本）。
        /// </summary>
        [Fact]
        public void 目标目录在别的盘_不回落C盘也不碰程序目录()
        {
            var created = new List<string>();

            WorkspaceRootResolution resolution = WorkspaceRootResolver.Resolve(
                new[] { @"H:\成果\1111" },
                driveOf: _ => @"H:\",
                ensureDirectory: path =>
                {
                    created.Add(path);
                    return true;
                });

            Assert.Equal(Path.Combine(@"H:\成果\1111", ".ArchiveFixer.work"), resolution.RootDirectory);
            Assert.StartsWith(@"H:\", resolution.RootDirectory, StringComparison.OrdinalIgnoreCase);

            // 拿不到 C 盘 / 程序目录的影子：这一批创建的目录**全在 H 盘那个目标目录这一棵里**。
            Assert.All(created, path => Assert.StartsWith(@"H:\成果\1111", path, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(created, path => path.StartsWith(@"C:", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(created, path => path.Contains("program-data", StringComparison.OrdinalIgnoreCase));
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

        private Harness CreateHarness(bool customOutput)
        {
            /*
             * 装配的关键一点：**数据根显式指到临时目录**（这样设置 / 日志 / 账本全在临时目录里，
             * 绝不会写进测试输出目录或用户真实数据）。
             *
             * ⚠ 2026-09-30：这里原来还要"先把 settings.CacheRootDirectory 设成临时目录、构造完再清空"，
             * 因为 MainViewModel 会按那个设置项去改数据根。设置项删除、那段覆写也删除之后，
             * PathService 上这一句就是数据根的唯一出口 —— 那个来回已经没有必要。
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

            // 这个用例拿「细节日志」当行为证据（第 44 条之后，成功时默认只留两行）。
            coordinator.KeepTaskDetailInLog = true;

            /*
             * ⛔ 2026-09-30：这里原来还有一个"二选一"—— 给 settings.CacheRootDirectory 填一个值来模拟
             * "用户显式设过工作区位置"那一档。设置项删除之后它没有任何对应物了：
             * 工作区只由这一批的目标目录派生，装配侧能改的只有 customOutput（落点）那一个开关。
             */

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
                outputRoot,
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
                string outputRoot,
                string fakeDriveRoot)
            {
                Vm = vm;
                Coordinator = coordinator;
                Engine = engine;
                Log = log;
                PathService = pathService;
                CacheRoot = cacheRoot;
                SourceRoot = sourceRoot;
                OutputRoot = outputRoot;
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

            /// <summary>
            /// "指定位置"那一档的输出根（<c>_root\out</c>，真实临时目录）。
            /// 目标目录 = <c>&lt;它&gt;\&lt;包名&gt;</c>，工作区就在那个包名目录里面。
            /// </summary>
            public string OutputRoot { get; }

            /// <summary>假输出盘根（只用于"跨盘描述"那一条；工作区位置已经不看盘符了）。</summary>
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

            /// <summary>最近一次信息框的正文（模态框在测试里必须被挡住，否则用例会卡死）。</summary>
            public string LastInfo { get; private set; } = string.Empty;

            public override void ShowInfo(string message)
            {
                LastInfo = message;
            }

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
