using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **分卷组的"不许删 / 不许列成残留"闸门 —— 接手代理补的那几条守门用例**（2026-09-30 第二轮）。
    ///
    /// <para>前任（<see cref="VolumeGroupResolverTests"/>）钉的是**判定器本身**；这一份钉的是
    /// **判定器的下游**：可删其余物（<c>PlanFinalLayout</c>）与导入期提醒（<c>SourceJunkScanner</c>）。
    /// 每一条都对应一个**真会丢数据**的形状：</para>
    /// <list type="number">
    /// <item><description>判定器**判不出**（同一份数据被硬链接成两卷）⇒ 整份计划作废，一个字节都不搬；</description></item>
    /// <item><description>**孤儿卷**：带卷标记，但归组被"尺寸规律"挡掉（<c>x.7z.001</c> + 更大的
    /// <c>x.7z.002删除</c>）⇒ 老写法只看扩展名就把它收进可删的其余物 ⇒ 这一条钉它必须整份作废；</description></item>
    /// <item><description>判不出但**组已经认出来**（<c>GroupFilePaths</c> 非空）⇒ 导入期提醒里仍然一个成员都不许出现
    /// （老写法在"判不出"那一档直接放手，恰恰放走的是最该保护的那一批）。</description></item>
    /// </list>
    /// </summary>
    public sealed class VolumeGroupAssemblyGuardTests : IDisposable
    {
        private readonly string _root;

        public VolumeGroupAssemblyGuardTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixer-volume-guard-" + Guid.NewGuid().ToString("N"));
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

        // ================================================================ ① 判不出 ⇒ 不许删源

        /// <summary>
        /// **判不出 ⇒ 整份计划作废**：<c>x.7z.002</c> 是 <c>x.7z.001</c> 的硬链接
        /// （同一个 FileId = 磁盘上只有一份数据，它的"卷号"是假的）。
        ///
        /// <para>判定器这一档给 <see cref="VolumeGroupVerdict.Undetermined"/>；
        /// 定稿闸门必须因此**什么都不做** —— 不能"名字看着像分卷"就把它们当可删的其余物。</para>
        /// </summary>
        [Fact]
        public void 判不出_同一份硬链接成两卷_整份计划作废()
        {
            string stage = NewDirectory("guard-undetermined");

            string first = WriteFile(stage, "x.7z.001", 4096);
            string second = Path.Combine(stage, "x.7z.002");

            Assert.True(TryCreateHardLink(second, first), "本机文件系统不支持硬链接，这条用例失去意义");

            VolumeGroupResolution resolution = Resolve(first);

            Assert.Equal(VolumeGroupVerdict.Undetermined, resolution.Verdict);
            Assert.False(resolution.CanEnterDeletableRestItems);

            ExtractionCoordinator.FinalLayoutPlan plan = PlanFinalLayout(stage, "out-undetermined", "x");

            Assert.True(plan.Failed, "判不出必须让整份计划作废（兜底落在「什么都不做」那一档）");
            Assert.Empty(plan.ProcessArtifactSources);
            Assert.Empty(plan.Moves);
        }

        // ================================================================ ② 孤儿卷（归组被挡掉）

        /// <summary>
        /// **孤儿卷的兜底闸门**：<c>x.7z.001</c>（1 KB）+ <c>x.7z.002删除</c>（2 KB）。
        ///
        /// <para><c>x.7z.002删除</c> 是"名字被伪装过"的卷（百度网盘那种尾巴），
        /// <see cref="VolumeGroupDetector"/> 对它要再过一道**尺寸规律** —— 末卷比首卷还大 ⇒ 整桶被丢掉。
        /// 于是 <c>x.7z.001</c> 落不进任何组，老写法就只剩"扩展名 <c>.001</c> ⇒ 可删的其余物"这一条路：
        /// **它旁边那个文件很可能就是它的兄弟，删掉这一卷整组再也解不开**。</para>
        ///
        /// <para>现在：这一档回头逐文件问判定器（判不出）⇒ 整份计划作废。</para>
        /// </summary>
        [Fact]
        public void 孤儿卷_归组被尺寸规律挡掉_也不许进可删的其余物()
        {
            string stage = NewDirectory("guard-orphan");

            string first = WriteFile(stage, "x.7z.001", 1024);
            WriteFile(stage, "x.7z.002删除", 2048);
            WriteFile(stage, "说明.txt", 32);

            // 前提：这一对**真的**落不进任何组（否则这条用例什么都没证明）。
            Assert.Empty(VolumeGroupDetector.Group(new[]
            {
                new VolumeCandidate { Path = first, Size = 1024 },
                new VolumeCandidate { Path = Path.Combine(stage, "x.7z.002删除"), Size = 2048 }
            }));

            // 前提：老判据单看名字**确实**会把它当"其余物"（危险就危险在这里）。
            Assert.True(ExtractionCoordinator.IsProcessArtifactFile(first));

            VolumeGroupResolution resolution = Resolve(first);

            Assert.Equal(VolumeGroupVerdict.Undetermined, resolution.Verdict);
            Assert.False(resolution.CanEnterDeletableRestItems);

            ExtractionCoordinator.FinalLayoutPlan plan = PlanFinalLayout(stage, "out-orphan", "x");

            Assert.True(plan.Failed, "带卷标记却证不出整组完整的文件，一个都不许进可删的其余物");
            Assert.Empty(plan.ProcessArtifactSources);
            Assert.Empty(plan.Moves);
        }

        /// <summary>
        /// 反面（不许误伤）：名字标准的完整数字组照旧进其余物 —— 兜底那一刀只砍"证不出来的"。
        /// </summary>
        [Fact]
        public void 标准完整组_照旧进可删的其余物()
        {
            string stage = NewDirectory("guard-ok");

            WriteFile(stage, "y.7z.001", 4096);
            WriteFile(stage, "y.7z.002", 4096);
            WriteFile(stage, "y.7z.003", 512);

            ExtractionCoordinator.FinalLayoutPlan plan = PlanFinalLayout(stage, "out-ok", "y");

            Assert.False(plan.Failed);
            Assert.Equal(3, plan.ProcessArtifactSources.Count);
        }

        // ================================================================ ③ 导入期提醒：判不出也要保护

        /// <summary>
        /// **判不出（<c>Undetermined</c>）但组已经认出来了** ⇒ 组成员照样不许进"疑似无用物"提醒。
        ///
        /// <para>现场形状：<c>x.7z.001</c> 与 <c>x.7z.002</c> 是同一份数据的两个名字（硬链接），
        /// 旁边那个 <c>x.jpg</c> 基名段相同、体积也对得上 —— 判定器把它当成这一组的候选收进
        /// <c>GroupFilePaths</c>（结论仍是"判不出"，因为同 FileId 那一份数据不能算两卷）。</para>
        ///
        /// <para>老写法在"判不出"那一档**直接放手**（`Verdict == Undetermined ⇒ return`），
        /// 于是 <c>x.jpg</c> 会被列成无用物 —— 而它恰恰可能是这一组缺的那一片。
        /// 这一条用例就是那个缺口的守门。</para>
        /// </summary>
        [Fact]
        public async Task 判不出但组已认出来_导入提醒里一个成员都不许出现()
        {
            string directory = NewDirectory("guard-junk");

            string first = WriteFile(directory, "x.7z.001", 8192);
            string second = Path.Combine(directory, "x.7z.002");

            Assert.True(TryCreateHardLink(second, first), "本机文件系统不支持硬链接，这条用例失去意义");

            string disguised = WriteFile(directory, "x.jpg", 4096);
            WriteFile(directory, "说明.txt", 64);

            // 前提：名字判据确实会把它当候选（否则这条用例什么都没证明）。
            Assert.True(SourceJunkScanner.LooksLikeJunkCandidate("x.jpg"));

            VolumeGroupResolution resolution = Resolve(first);

            Assert.Equal(VolumeGroupVerdict.Undetermined, resolution.Verdict);
            Assert.NotEmpty(resolution.GroupFilePaths);
            Assert.True(resolution.IsGroupMember(disguised), "同基名段 + 体积对得上的候选必须被认成同一组");

            var prober = new MagicArchiveProber();

            SourceJunkScanResult scanned = await SourceJunkScanner.ScanAsync(
                new[] { new ArchiveTask(first, 1) { IsArchive = true, DetectedFormat = "7Z" } },
                prober,
                CancellationToken.None);

            Assert.DoesNotContain(
                scanned.Items,
                item => string.Equals(item.FileName, "x.jpg", StringComparison.OrdinalIgnoreCase));

            // 真的无用物照旧要报出来（保护不许把整段提醒弄没）。
            Assert.Contains(
                scanned.Items,
                item => string.Equals(item.FileName, "说明.txt", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// **真案 ② 的提醒形状**：跨盘族的末卷被改名成 <c>一只顶美.jpg</c>
        /// （<c>.jpg</c> 在"疑似无用物"后缀表里，而且裸切续卷**没有魔数** —— 只看名字、只看魔数都保护不到它）。
        ///
        /// <para>老判据只保护"名字里带卷标记、同族同基名"的兄弟，<c>.jpg</c> 一个都保护不到 ⇒
        /// 提醒框一出现，用户就会把这一卷从列表里删掉，剩下 <c>.z01…z05</c> 再也解不开。</para>
        /// </summary>
        [Fact]
        public async Task 真案二形状_末卷被改名成jpg_导入提醒里不许列出来()
        {
            string directory = NewDirectory("guard-junk-case2");

            for (int i = 1; i <= 5; i++)
            {
                WriteFile(directory, $"一只顶美.z{i:D2}", 4096);
            }

            string renamed = WriteFile(directory, "一只顶美.jpg", 1024);
            WriteFile(directory, "说明.txt", 64);

            Assert.True(SourceJunkScanner.LooksLikeJunkCandidate("一只顶美.jpg"));

            var prober = new MagicArchiveProber();
            Assert.False(await prober.IsArchiveAsync(renamed), "裸切续卷不该被魔数认成归档");

            Assert.True(Resolve(Path.Combine(directory, "一只顶美.z01")).IsGroupMember(renamed));

            SourceJunkScanResult scanned = await SourceJunkScanner.ScanAsync(
                new[] { new ArchiveTask(Path.Combine(directory, "一只顶美.z01"), 1) { IsArchive = true, DetectedFormat = "ZIP" } },
                prober,
                CancellationToken.None);

            Assert.DoesNotContain(
                scanned.Items,
                item => string.Equals(item.FileName, "一只顶美.jpg", StringComparison.OrdinalIgnoreCase));

            Assert.Contains(
                scanned.Items,
                item => string.Equals(item.FileName, "说明.txt", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// **伪装成 <c>.mp4</c> 的续卷**（用户点名的第三个真案）：<c>.mp4</c> 既不在"无用物后缀表"里、
        /// 也不在归档后缀表里 —— 两条老判据都**看不见**它。
        ///
        /// <para>所以钉两件事：① 残留分类不会把它当其余物（名字这一层它"不是归档"）；
        /// ② 但它**是这一组的成员**（判定器按基名段 + 体积认出来）⇒ 定稿闸门因此整份作废，
        /// 它既不会被搬成内容物、也不会连累那一卷进可删的其余物。</para>
        /// </summary>
        [Fact]
        public void 真案三_伪装成mp4的续卷_残留分类看不见它_但组成员身份拦得住()
        {
            string stage = NewDirectory("guard-mp4");

            string first = WriteFile(stage, "电影.7z.001", 8192);
            string disguised = WriteFile(stage, "电影.mp4", 8192);

            Assert.False(ExtractionCoordinator.IsProcessArtifactFile(disguised));
            Assert.False(SourceJunkScanner.LooksLikeJunkCandidate("电影.mp4"));

            VolumeGroupResolution resolution = Resolve(first);

            Assert.True(resolution.IsGroupMember(disguised));
            Assert.False(resolution.CanEnterDeletableRestItems);

            ExtractionCoordinator.FinalLayoutPlan plan = PlanFinalLayout(stage, "out-mp4", "电影");

            Assert.True(plan.Failed);
            Assert.Empty(plan.ProcessArtifactSources);
            Assert.Empty(plan.Moves);
        }

        // ================================================================ ④ 跨盘 = 判不出（⛔ 不许在源盘开工作区）

        /// <summary>
        /// **跨盘 ⇒ 判不出**（用户 2026-09-30 决定：工作区就设在解压的地方，"这就完全不存在跨盘的操作"）。
        ///
        /// <para>硬链接不能跨卷 ⇒ 源文件与工作区根不在同一个卷上时，试开**没有合法落点**。
        /// 本分支第一版会退到"源卷根下的 <c>.ArchiveFixer.work</c>"去试 —— 那正是被否决的形态
        /// （在用户盘上开目录 = 不变量 12 禁止的"中间产物写进源目录"，收工删掉也一样：中途崩掉就留垃圾）。</para>
        ///
        /// <para>这一条钉四件事：① 跨盘**一次都不试**（引擎一次都没被调用 ⇒ 不可能在源盘新建任何东西）；
        /// ② 结论如实降成"判不出"、不许删源；③ 组成员身份照旧带出去（不删源的安全结论一个字不变）；
        /// ④ 源目录树与源卷根**前后完全一致**（⛔ 跨盘这一档在源盘上什么都不会新建）。</para>
        ///
        /// <para>跨盘现场怎么造：源目录放在**测试程序集所在的卷**上，工作区根故意放在**系统临时目录那个卷**
        /// 上（两个不同盘符）。单卷机器上这条用例没有意义，发现阶段跳过（见
        /// <see cref="CrossVolumeFactAttribute"/>）。</para>
        /// </summary>
        [CrossVolumeFact]
        public async Task 跨盘_不做试开_判不出_源盘上什么都不会新建()
        {
            // 源：测试程序集所在卷（不碰用户目录）；工作区：另一个卷上一个**不存在**的目录。
            string sourceParent = Path.Combine(AppContext.BaseDirectory, "crossvol-src-" + Guid.NewGuid().ToString("N"));
            string workRoot = Path.Combine(Path.GetTempPath(), "crossvol-work-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(sourceParent);

            try
            {
                for (int i = 1; i <= 5; i++)
                {
                    WriteFile(sourceParent, $"一只顶美.z{i:D2}", 4096);
                }

                string renamed = WriteFile(sourceParent, "一只顶美.z删除ip", 1024);
                string first = Path.Combine(sourceParent, "一只顶美.z01");

                // 前提：这一对真的跨卷（否则这条用例什么都没证明）。
                Assert.False(
                    VolumeContentInference.IsSameVolumeRoot(first, workRoot),
                    "源目录与工作区根落在了同一个卷上，这条用例测不到跨盘那一档");

                /*
                 * 源卷一侧的基准快照（跑之前拍）：
                 *   ① 源目录树（相对路径 + 字节数）；
                 *   ② 源卷根下那个 `.ArchiveFixer.work` 壳的**内容** —— 老写法正是在那里
                 *      `<壳>\volprobe-<guid>\…` 建硬链接的（收工只删 probe 目录、壳会留下）。
                 *
                 * ⚠ 本机实测：`C:\.ArchiveFixer.work` 与 `E:\.ArchiveFixer.work` **本来就存在**
                 * （早前跑 `VolumeNameRepair` 那条**老路**留下的空壳，不是这条线新建的，本用例也不动它）。
                 * 所以"有没有多出壳"这一条在那种机器上是弱断言；跨盘不写盘的**硬证据**是下面
                 * ③ 的"引擎零调用 + Attempted=false"（跨盘检查排在 `Directory.CreateDirectory` **之前**）
                 * 与 ④ 的"源目录树一字不差"。
                 */
                string sourceVolumeRoot = Path.GetPathRoot(first) ?? string.Empty;
                string shell = Path.Combine(sourceVolumeRoot, VolumeContentInference.WorkDirectoryName);

                string[] treeBefore = SnapshotTree(sourceParent);
                string[] shellBefore = SnapshotTree(shell);

                var engine = new RecordingEngine();
                var verifier = new VolumeProbeVerifier(engine);
                var resolver = new VolumeGroupResolver(verifier.TryOpenAsync);

                VolumeGroupResolution resolution = await resolver.ResolveAsync(new VolumeGroupQuery
                {
                    AnchorPath = first,
                    WorkRootDirectory = workRoot,
                    AllowTrialOpen = true
                });

                // ① 结论如实降级，而且没有删源资格。
                Assert.Equal(VolumeGroupVerdict.Undetermined, resolution.Verdict);
                Assert.False(resolution.CanEnterDeletableRestItems);
                Assert.Contains("跨盘无法试开，整卷是否齐全无法确认", resolution.Reason, StringComparison.Ordinal);
                Assert.DoesNotContain("完整（试开确认）", resolution.Reason, StringComparison.Ordinal);

                // ② 组还是认出来了 ⇒ 组成员一个都不许被当"可删残留"（安全结论不变）。
                Assert.True(resolution.IsGroupMember(renamed));
                Assert.NotEmpty(resolution.GroupFilePaths);

                // ③ 一次都没试：引擎一次没被调用，工作区根也没被建出来。
                Assert.Empty(engine.ListCalls);
                Assert.False(Directory.Exists(workRoot), "跨盘这一档连工作区根都不许碰");

                // ④ 源盘一侧零新建：源目录树一字不差，源卷根那个壳里也没多出任何东西。
                Assert.Equal(treeBefore, SnapshotTree(sourceParent));
                Assert.Equal(shellBefore, SnapshotTree(shell));
            }
            finally
            {
                TryDeleteDirectory(sourceParent);
                TryDeleteDirectory(workRoot);
            }
        }

        // ================================================================ ⑤ 没有工作区根 = 一次都不试（改名那条路）

        /// <summary>
        /// **没有工作区根 ⇒ 一次都不试开**（用户 2026-09-30 红线：工作区只准设在解压的地方）。
        ///
        /// <para>改名那条路（<c>VolumeNameRepair</c>）**没有目标目录** ⇒ 没有工作区根。老实现会退到
        /// <c>&lt;第一卷卷根&gt;\.ArchiveFixer.work\volprobe-&lt;guid&gt;</c> —— 本机 C:\ 与 E:\ 根上
        /// 那两个空壳就是这么来的。现在这一档：**引擎零调用、任何地方都不新建、结论判不出、不许删源**。</para>
        /// </summary>
        [Fact]
        public async Task 没有工作区根_一次都不试开_判不出_源盘零新建()
        {
            string sourceParent = NewDirectory("no-work-root");

            for (int i = 1; i <= 5; i++)
            {
                WriteFile(sourceParent, $"一只顶美.z{i:D2}", 4096);
            }

            string renamed = WriteFile(sourceParent, "一只顶美.z删除ip", 1024);
            string first = Path.Combine(sourceParent, "一只顶美.z01");

            string sourceVolumeRoot = Path.GetPathRoot(first) ?? string.Empty;
            string shell = Path.Combine(sourceVolumeRoot, VolumeContentInference.WorkDirectoryName);

            string[] treeBefore = SnapshotTree(sourceParent);
            string[] shellBefore = SnapshotTree(shell);

            var engine = new RecordingEngine();
            var verifier = new VolumeProbeVerifier(engine);
            var resolver = new VolumeGroupResolver(verifier.TryOpenAsync);

            // WorkRootDirectory 留空 = 改名那条路的形态。
            VolumeGroupResolution resolution = await resolver.ResolveAsync(new VolumeGroupQuery
            {
                AnchorPath = first,
                AllowTrialOpen = true
            });

            Assert.Equal(VolumeGroupVerdict.Undetermined, resolution.Verdict);
            Assert.False(resolution.CanEnterDeletableRestItems);
            Assert.Contains("整卷是否齐全无法确认", resolution.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("完整（试开确认）", resolution.Reason, StringComparison.Ordinal);

            // 组还是认出来了 ⇒ 不删源的安全结论不变。
            Assert.True(resolution.IsGroupMember(renamed));

            // 一次都不试 + 哪儿都没新建。
            Assert.Empty(engine.ListCalls);
            Assert.Equal(treeBefore, SnapshotTree(sourceParent));
            Assert.Equal(shellBefore, SnapshotTree(shell));
        }

        // ================================================================ 辅助

        /// <summary>
        /// 目录树快照（相对路径 + 字节数，排序后比较）：用来钉"跨盘这一档一个字节都不动"。
        /// 目录不存在时返回空数组（**不是**抛异常）—— "本来就没有"和"跑完还是没有"要能比得起来。
        /// </summary>
        private static string[] SnapshotTree(string directory) =>
            !Directory.Exists(directory)
                ? Array.Empty<string>()
                : Directory
                    .EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(directory, path)
                        + "|" + (Directory.Exists(path)
                            ? "dir"
                            : new FileInfo(path).Length.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                    .OrderBy(text => text, StringComparer.Ordinal)
                    .ToArray();

        private static void TryDeleteDirectory(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch
            {
                // 临时目录清不掉不影响结论。
            }
        }

        /// <summary>
        /// **跨盘用例的标记**：只有"测试程序集所在卷"与"系统临时目录所在卷"是两个不同卷时才有意义。
        ///
        /// <para>做法与 <c>SevenZipFactAttribute</c> 一致：**发现阶段**探测，不满足就设
        /// <see cref="FactAttribute.Skip"/> —— ⛔ 不用"运行时 return"假装跑过（那样报表里算"通过"，
        /// 会让人读成"跨盘验过了"）。</para>
        /// </summary>
        [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
        public sealed class CrossVolumeFactAttribute : FactAttribute
        {
            public CrossVolumeFactAttribute()
            {
                string assemblyRoot = Path.GetPathRoot(AppContext.BaseDirectory) ?? string.Empty;
                string tempRoot = Path.GetPathRoot(Path.GetTempPath()) ?? string.Empty;

                if (assemblyRoot.Length == 0 ||
                    string.Equals(assemblyRoot, tempRoot, StringComparison.OrdinalIgnoreCase))
                {
                    Skip = "这台机器上测试程序集与系统临时目录在同一个卷，造不出跨盘现场（跳过）。";
                }
            }
        }

        /// <summary>
        /// **跨盘用的试开替身**：只数"引擎被调用了几次"—— 跨盘这一档必须**一次都没有**。
        /// ⛔ 它自己不写盘：真写盘的只有 <c>VolumeProbeVerifier</c> 建硬链接那一步。
        /// </summary>
        private sealed class RecordingEngine : ArchiveFixer.Engines.IArchiveEngine
        {
            public List<string> ListCalls { get; } = new();

            public string Id => "recording";

            public string DisplayName => "计数替身";

            public string Version => "0";

            public bool IsAvailable => true;

            public ArchiveFixer.Engines.EngineCapabilities Capabilities { get; } = new()
            {
                CanProbe = true,
                CanList = true,
                CanTest = true,
                CanExtract = true,
                SupportsPassword = true
            };

            public Task<ArchiveFixer.Engines.ArchiveProbeResult> ProbeAsync(
                ArchiveFixer.Engines.ArchiveRequest request,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveFixer.Engines.ArchiveProbeResult { IsArchive = false });

            public Task<ArchiveFixer.Engines.ArchiveListResult> ListAsync(
                ArchiveFixer.Engines.ArchiveRequest request,
                CancellationToken cancellationToken = default)
            {
                lock (ListCalls)
                {
                    ListCalls.Add(request.ArchivePath);
                }

                return Task.FromResult(new ArchiveFixer.Engines.ArchiveListResult
                {
                    Success = true,
                    FileCount = 1,
                    Entries = new List<ArchiveFixer.Engines.ArchiveEntry> { new() { Path = "x", Size = 1 } }
                });
            }

            public Task<ArchiveFixer.Engines.ArchiveOperationResult> TestAsync(
                ArchiveFixer.Engines.ArchiveRequest request,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveFixer.Engines.ArchiveOperationResult { Success = true });

            public Task<ArchiveFixer.Engines.ArchiveOperationResult> ExtractAsync(
                ArchiveFixer.Engines.ArchiveRequest request,
                ArchiveFixer.Models.ExtractOptions options,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new ArchiveFixer.Engines.ArchiveOperationResult { Success = true });
        }

        private static VolumeGroupResolution Resolve(string anchor) =>
            new VolumeGroupResolver().Resolve(new VolumeGroupQuery
            {
                AnchorPath = anchor,
                AllowTrialOpen = false
            });

        private ExtractionCoordinator.FinalLayoutPlan PlanFinalLayout(
            string stage,
            string outputName,
            string archiveBaseName) =>
            ExtractionCoordinator.PlanFinalLayout(
                stage,
                Path.Combine(_root, outputName),
                sharedOutputRoot: false,
                archiveBaseName);

        private string NewDirectory(string name)
        {
            string path = Path.Combine(_root, name);
            Directory.CreateDirectory(path);

            return path;
        }

        private static string WriteFile(string directory, string name, long size)
        {
            string path = Path.Combine(directory, name);

            File.WriteAllBytes(path, MakeBytes((int)Math.Min(size, 64 * 1024)));

            if (size > 64 * 1024)
            {
                using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Write);
                stream.SetLength(size);
            }

            return path;
        }

        private static byte[] MakeBytes(int count)
        {
            var bytes = new byte[count];

            for (int i = 0; i < count; i++)
            {
                bytes[i] = (byte)(i % 251);
            }

            return bytes;
        }

        private static bool TryCreateHardLink(string linkPath, string existingPath)
        {
            try
            {
                return NativeHardLink.CreateHardLink(linkPath, existingPath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>硬链接（用例自己造的，用来钉"同一 FileId ⇒ 同一份"）。</summary>
        private static class NativeHardLink
        {
            [System.Runtime.InteropServices.DllImport(
                "kernel32.dll",
                CharSet = System.Runtime.InteropServices.CharSet.Unicode,
                SetLastError = true)]
            [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
            private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

            public static bool CreateHardLink(string linkPath, string existingPath) =>
                CreateHardLinkW(linkPath, existingPath, IntPtr.Zero);
        }
    }
}
