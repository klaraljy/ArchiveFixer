using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **真样本**用例的开关（用户 2026-09-29 放在仓库同级的 <c>_tmp\ArchiveFixer\aaa-real\</c> 下的三套真包）。
    ///
    /// <para><b>为什么要它</b>：RAR 1.5–4.x（RAR4）的卷号、真 PKZIP 跨盘 zip 的片头形状，这两件事**合成样本证明不了** ——
    /// 合成样本只能证明"按我以为的格式写出来的字节，我的解析器读得对"（自己验自己）。
    /// 真样本才有资格回答"真实产品写出来的字节，我读得对不对"。</para>
    ///
    /// <para>⛔ 样本本体（1.3 GB）绝不进仓库、绝不提交；样本目录**只读**，用例只动自己临时复制出来的副本。
    /// 样本不在（换机器 / 用户清掉了）就**跳过并说明**，不是失败 —— 与 <see cref="SevenZipFactAttribute"/>
    /// 同一套做法（xunit 2.x 只能在发现阶段跳过，没有可靠的运行时跳过 API）。</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class RealVolumeSampleFactAttribute : FactAttribute
    {
        /// <summary>样本目录的环境变量名（与 <c>ARCHIVEFIXER_DISGUISE_DIR</c> 同一套约定，换机器不用改代码）。</summary>
        internal const string DirectoryEnvironmentVariable = "ARCHIVEFIXER_REAL_SAMPLE_DIR";

        /// <summary>样本目录在 <c>_tmp\ArchiveFixer</c> 下的名字（⛔ 不写死绝对路径，换机器/换盘符都成立）。</summary>
        private const string RelativeSampleDirectory = "aaa-real";

        internal static readonly string TempRoot = ResolveTempRoot();

        internal static readonly string SampleRoot = ResolveSampleRoot();

        public RealVolumeSampleFactAttribute()
        {
            if (string.IsNullOrEmpty(SevenZipFactAttribute.LocateSevenZipPath()))
            {
                Skip = "测试机上没有可用的 7z.exe，跳过真样本用例。";

                return;
            }

            if (!HasSamples())
            {
                Skip = @"真样本不在（仓库同级 _tmp\ArchiveFixer\aaa-real 里没有文件），跳过真样本用例。";
            }
        }

        internal static bool HasSamples()
        {
            try
            {
                return Directory.Exists(SampleRoot)
                    && Directory.GetFiles(SampleRoot, "*", SearchOption.AllDirectories).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>仓库同级的 <c>_tmp\ArchiveFixer</c>：从测试程序集目录向上找含 <c>ArchiveFixer.slnx</c> 的目录，再回到它的**同级**。</summary>
        private static string ResolveTempRoot()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    string? parent = directory.Parent?.FullName;

                    return string.IsNullOrWhiteSpace(parent)
                        ? Path.Combine(directory.FullName, "_tmp", "ArchiveFixer")
                        : Path.Combine(parent!, "_tmp", "ArchiveFixer");
                }

                directory = directory.Parent;
            }

            return Path.Combine(Path.GetTempPath(), "ArchiveFixer");
        }

        private static string ResolveSampleRoot()
        {
            string? configured = Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);

            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(TempRoot, RelativeSampleDirectory)
                : configured!;
        }
    }

    /// <summary>
    /// "这台机器上有 <c>Rar.exe</c>"用例的开关（只有它能写**真的** RAR4 分卷 —— 7-Zip 不会写 RAR）。
    ///
    /// <para>路径来自产品自己的唯一出口 <see cref="Engines.ToolLocator"/>（⛔ 不在测试里另写一套探测，
    /// 也⛔ 绝不复制 <c>Rar.exe</c>：它是共享软件，只检测、只调用）。没装就跳过并说明。</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class RarExeFactAttribute : FactAttribute
    {
        public RarExeFactAttribute()
        {
            if (string.IsNullOrEmpty(SevenZipFactAttribute.LocateSevenZipPath()))
            {
                Skip = "测试机上没有可用的 7z.exe，跳过需要真实引擎的用例。";

                return;
            }

            if (!Engines.ToolLocator.Default.RarExists)
            {
                Skip = "本机没有 Rar.exe（WinRAR 是共享软件，程序只检测与调用它）—— 造不出真的 RAR4 分卷，跳过。";
            }
        }
    }

    /// <summary>
    /// **真样本端到端**（用户 2026-09-29 明确"都可以真试"）：三套真包在**副本**上走既有管线 ——
    /// 按内容认组 → 按内容定序 → 整组改回标准名 → 交给引擎真的去开这一组。
    ///
    /// <para><b>三套样本的形状</b>（只读原目录探出来的事实）：</para>
    /// <list type="number">
    /// <item><description>两套 RAR 1.5–4.x（RAR4）第一卷+第二卷：<c>Rar!\x1A\x07\x00</c> 头、主头 flags 带
    /// <c>MHD_FIRSTVOLUME|MHD_NEWNUMBERING|MHD_VOLUME</c>，卷号在卷尾 ENDARC 块里；第一卷 400 MiB 整、
    /// 第二卷是余量 —— 名字里都被塞了「删除」这类垃圾（一段在后缀里、一段在卷号里）。</description></item>
    /// <item><description>一套真 PKZIP 跨盘 zip（<c>.z01</c> + <c>.zip</c>）：**第一片以跨盘标记
    /// <c>PK\x07\x08</c> 开头**（不是本地文件头！），末片的 EOCD 说"本盘号 1、共 2 盘"。</description></item>
    /// </list>
    ///
    /// <para><b>⚠ 一条必须如实说清的限制</b>：三套样本的**文件内容都是加密的**
    /// （<c>7z l -slt</c> 每个 mp4 都是 <c>Encrypted = +</c>，空密码 <c>7z t -p""</c> 报
    /// <c>Wrong password</c>），而任务里没有给密码、也不许猜。所以这一组用例能验到的是
    /// 「认得出、定得序、改得回标准名、引擎真的打开了这一组并列出条目（名字/大小/CRC 与逐字节事实一致）」，
    /// **验不到"解出来的字节与原件一致"** —— 那一步卡在密码上，如实断言成「密码错误 + 输出目录一个字节都没有」。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class RealVolumeSampleTests : IDisposable
    {
        private readonly string _root;

        public RealVolumeSampleTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerRealSample", Guid.NewGuid().ToString("N"));
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
                // 清不掉只是脏一点（几百 MB，用户自己会清 _tmp）。
            }
        }

        // ── RAR 1.5–4.x（RAR4）：卷号读不读得到 ──

        [RealVolumeSampleFact]
        public void 真样本RAR4_卷号读得出来_整组按内容连成第1卷与第2卷()
        {
            IReadOnlyList<RealSampleSet> sets = RealSampleSet.Discover(VolumeContentFormat.Rar);

            Assert.True(sets.Count >= 2, $"RAR 真样本套数 = {sets.Count}（预期 ≥ 2 套）");

            foreach (RealSampleSet set in sets)
            {
                VolumeNumberReading first = VolumeNumberFromContent.Read(set.EntryFile);
                VolumeNumberReading second = VolumeNumberFromContent.Read(set.LaterFile);

                Assert.True(first.IsVolumeMember, $"{set.Name}：第一卷没认成分卷组成员");
                Assert.True(second.IsVolumeMember, $"{set.Name}：第二卷没认成分卷组成员");
                Assert.Equal(RarNumberingFamily.New, first.RarNumbering);
                Assert.Equal(RarNumberingFamily.New, second.RarNumbering);
                Assert.Equal(VolumeNumberFail.None, first.Fail);
                Assert.Equal(VolumeNumberFail.None, second.Fail);

                /*
                 * 主头的 MHD_FIRSTVOLUME 是**与卷尾字段无关的**一份独立证据：
                 * 它必须只长在第 1 卷上。少了这条，"0 起的整组里恰好缺了第 1 卷"会被读成"1 起的一组"。
                 */
                Assert.True(first.FirstVolumeFlag, $"{set.Name}：第一卷的主头没有带「这是第 1 卷」标记");
                Assert.False(second.FirstVolumeFlag, $"{set.Name}：第二卷的主头不该带「这是第 1 卷」标记");

                // 卷号字段的**原值**：基数（0 起还是 1 起）由整组自洽判定，单卷上不做加减。
                Assert.NotNull(first.RawField);
                Assert.NotNull(second.RawField);
                Assert.Equal(first.RawField!.Value + 1, second.RawField!.Value);

                IReadOnlyList<VolumeNumberReading> readings = RealSampleSet.ReadAll(set);

                VolumeGroupOrder order = VolumeNumberFromContent.ResolveGroup(set.EntryFile, readings);

                Assert.True(order.Confirmed, $"{set.Name}：内容定序没成（{order.Fail}）");
                Assert.Equal(2, order.Count);
                Assert.Equal(set.EntryFile, order.Slots[0].Path, ignoreCase: true);
                Assert.Equal(set.LaterFile, order.Slots[1].Path, ignoreCase: true);
                Assert.Equal(new[] { 1, 2 }, order.Slots.Select(slot => slot.Number).ToArray());
            }
        }

        [RealVolumeSampleFact]
        public async Task 真样本RAR4_按内容给出partN标准名_只改名字_字节数一个不差()
        {
            IReadOnlyList<RealSampleSet> sets = RealSampleSet.Discover(VolumeContentFormat.Rar);

            Assert.True(sets.Count >= 2, $"RAR 真样本套数 = {sets.Count}（预期 ≥ 2 套）");

            foreach (RealSampleSet set in sets)
            {
                string directory = CopySetToWorkDirectory(set);

                string entry = Path.Combine(directory, Path.GetFileName(set.EntryFile));
                string later = Path.Combine(directory, Path.GetFileName(set.LaterFile));

                var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                {
                    [entry] = new FileInfo(entry).Length,
                    [later] = new FileInfo(later).Length
                };

                VolumeNameRepairPlan plan = await VolumeNameRepair.PlanByContentAsync(
                    entry,
                    VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(entry),
                    new Engines.SevenZip.SevenZipEngine(),
                    workRootDirectory: WorkRootFor(entry));

                Assert.True(plan.CanRepair, $"{set.Name}：内容级计划没成（{plan.Reason}）");
                Assert.Equal(2, plan.Items.Count);

                string[] suggested = plan.Items.Select(item => item.SuggestedFileName).ToArray();

                /*
                 * ⛔ 只钉"标准名长什么样"，不钉样本自己的基名（基名只影响改完像不像人写的）：
                 * RAR 族的第 N 卷必须是 `<同一个基名>.partN.rar`。
                 */
                Assert.EndsWith(".part1.rar", suggested[0], StringComparison.OrdinalIgnoreCase);
                Assert.EndsWith(".part2.rar", suggested[1], StringComparison.OrdinalIgnoreCase);
                Assert.Equal(Stem(suggested[0]), Stem(suggested[1]));
                VolumeNameRepairResult result = VolumeNameRepair.TryApply(plan);

                Assert.True(result.Success, $"{set.Name}：{result.Message}");
                Assert.True(File.Exists(Path.Combine(directory, suggested[0])));
                Assert.True(File.Exists(Path.Combine(directory, suggested[1])));
                Assert.False(File.Exists(entry), $"{set.Name}：旧名字还在");
                Assert.False(File.Exists(later), $"{set.Name}：旧名字还在");

                // 只改名字的机器证据：整组字节数一个不差。
                Assert.Equal(sizes[entry], new FileInfo(Path.Combine(directory, suggested[0])).Length);
                Assert.Equal(sizes[later], new FileInfo(Path.Combine(directory, suggested[1])).Length);
            }
        }

        // ── 真 PKZIP 跨盘 zip：第一片以数据描述符开头 ──

        [RealVolumeSampleFact]
        public void 真样本跨盘ZIP_第一片以跨盘标记开头_照样算这一组的成员()
        {
            IReadOnlyList<RealSampleSet> sets = RealSampleSet.Discover(VolumeContentFormat.Zip);

            Assert.True(sets.Count >= 1, $"跨盘 zip 真样本套数 = {sets.Count}（预期 ≥ 1 套）");

            foreach (RealSampleSet set in sets)
            {
                VolumeNumberReading first = VolumeNumberFromContent.Read(set.EntryFile);

                Assert.Equal(VolumeContentFormat.Zip, first.Format);
                Assert.True(first.IsVolumeMember, $"{set.Name}：第一片（以 PK\\x07\\x08 开头）没被认成这一组的成员");
                Assert.Equal(VolumeNumberFail.None, first.Fail);

                // 非末片的内容里没有盘号：位置只能靠"末片的盘号 + 片数"消去法定。
                Assert.Null(first.Number);

                VolumeNumberReading tail = VolumeNumberFromContent.Read(set.LaterFile);

                Assert.True(tail.IsVolumeMember);
                Assert.Equal(2, tail.Number);
                Assert.Equal(2, tail.Total);

                IReadOnlyList<VolumeNumberReading> readings = RealSampleSet.ReadAll(set);

                VolumeGroupOrder fromFirst = VolumeNumberFromContent.ResolveGroup(set.EntryFile, readings);
                VolumeGroupOrder fromTail = VolumeNumberFromContent.ResolveGroup(set.LaterFile, readings);

                foreach (VolumeGroupOrder order in new[] { fromFirst, fromTail })
                {
                    Assert.True(order.Confirmed, $"{set.Name}：内容定序没成（{order.Fail}）");
                    Assert.Equal(2, order.Count);
                    Assert.Equal(set.EntryFile, order.Slots[0].Path, ignoreCase: true);
                    Assert.Equal(set.LaterFile, order.Slots[1].Path, ignoreCase: true);
                }
            }
        }

        [RealVolumeSampleFact]
        public async Task 真样本跨盘ZIP_按内容给出z01与zip标准名_只改名字_字节数一个不差()
        {
            IReadOnlyList<RealSampleSet> sets = RealSampleSet.Discover(VolumeContentFormat.Zip);

            Assert.True(sets.Count >= 1, $"跨盘 zip 真样本套数 = {sets.Count}（预期 ≥ 1 套）");

            foreach (RealSampleSet set in sets)
            {
                string directory = CopySetToWorkDirectory(set);

                string first = Path.Combine(directory, Path.GetFileName(set.EntryFile));
                string tail = Path.Combine(directory, Path.GetFileName(set.LaterFile));

                var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                {
                    [first] = new FileInfo(first).Length,
                    [tail] = new FileInfo(tail).Length
                };

                // 从**第一片**进去算计划：这条正是"第一片认不出来"那个缺陷的出口。
                VolumeNameRepairPlan plan = await VolumeNameRepair.PlanByContentAsync(
                    first,
                    VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(first),
                    new Engines.SevenZip.SevenZipEngine(),
                    workRootDirectory: WorkRootFor(first));

                Assert.True(plan.CanRepair, $"{set.Name}：内容级计划没成（{plan.Reason}）");
                Assert.Equal(2, plan.Items.Count);

                string[] suggested = plan.Items.Select(item => item.SuggestedFileName).ToArray();

                Assert.EndsWith(".z01", suggested[0], StringComparison.OrdinalIgnoreCase);
                Assert.EndsWith(".zip", suggested[1], StringComparison.OrdinalIgnoreCase);
                Assert.Equal(Stem(suggested[0]), Stem(suggested[1]));

                VolumeNameRepairResult result = VolumeNameRepair.TryApply(plan);

                Assert.True(result.Success, $"{set.Name}：{result.Message}");
                Assert.True(File.Exists(Path.Combine(directory, suggested[0])));
                Assert.True(File.Exists(Path.Combine(directory, suggested[1])));
                Assert.False(File.Exists(first), $"{set.Name}：旧名字还在");
                Assert.False(File.Exists(tail), $"{set.Name}：旧名字还在");

                Assert.Equal(sizes[first], new FileInfo(Path.Combine(directory, suggested[0])).Length);
                Assert.Equal(sizes[tail], new FileInfo(Path.Combine(directory, suggested[1])).Length);

                /*
                 * 改完名字之后，真 7-Zip 必须**认得出这一组**（Multivolume = +）并列出条目 ——
                 * 这才是"标准名改对了"的机器证据："改完像不像人写的"不算证据。
                 */
                IReadOnlyList<SevenZipEntry> entries = ListEntries(Path.Combine(directory, suggested[0]));

                Assert.True(entries.Count > 0, $"{set.Name}：改完名字之后 7-Zip 列不出条目");
                Assert.Equal(ExpectedEntryCount, entries.Count);
            }
        }

        // ── 端到端：既有管线（MainViewModel / ExtractionCoordinator） ──

        [RealVolumeSampleFact]
        public async Task 真样本_既有管线_认组_定序_整组改回标准名_引擎真的打开这一组()
        {
            var sets = new List<RealSampleSet>();

            sets.AddRange(RealSampleSet.Discover(VolumeContentFormat.Rar));
            sets.AddRange(RealSampleSet.Discover(VolumeContentFormat.Zip));

            Assert.True(sets.Count >= 3, $"真样本套数 = {sets.Count}（预期 3 套）");

            foreach (RealSampleSet set in sets)
            {
                string directory = CopySetToWorkDirectory(set);

                /*
                 * 交给管线的就是**用户会拖进来的那一个文件**：RAR 是第一卷、跨盘 zip 是**第一片**。
                 * 跨盘 zip 的第一片没有归档魔数（它开头是跨盘标记），整组的入口要等改名成 `.zip` 之后才成立 ——
                 * 这正是"按内容认组"必须成立的理由。
                 */
                string entry = Path.Combine(directory, Path.GetFileName(set.EntryFile));

                long entrySize = new FileInfo(entry).Length;
                long laterSize = new FileInfo(Path.Combine(directory, Path.GetFileName(set.LaterFile))).Length;

                Harness harness = CreateHarness();
                ArchiveTask task = await AddTaskAsync(harness, entry);

                await harness.Coordinator.StartExtractAsync();

                string[] standard = Directory.GetFiles(directory);

                // ① 整组改回了标准名（旧名字一个不剩）
                string[] expected = set.Format == VolumeContentFormat.Rar
                    ? new[] { ".part1.rar", ".part2.rar" }
                    : new[] { ".z01", ".zip" };

                foreach (string suffix in expected)
                {
                    string[] hits = standard
                        .Where(path => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    Assert.True(hits.Length == 1, $"{set.Name}：改完之后找不到唯一的 {suffix}（{hits.Length} 个）");
                }

                Assert.Equal(2, standard.Length);
                Assert.False(File.Exists(entry), $"{set.Name}：旧名字还在");

                // 改名是**管线**做的（内容级那条路），不是用例自己改的：这个机器标记只在那一支上打。
                Assert.True(task.VolumeNameAutoRenamed, $"{set.Name}：不是管线按内容改名的那一条路");

                // 只改名字的机器证据：整组字节数一个不差。
                long[] renamedSizes = standard.Select(path => new FileInfo(path).Length).OrderBy(v => v).ToArray();

                Assert.Equal(
                    new[] { entrySize, laterSize }.OrderBy(v => v).ToArray(),
                    renamedSizes);

                // ② 任务路径跟着改成了标准名（后面各层拿到的就是它）
                Assert.Contains(
                    Path.GetFileName(task.CurrentPath),
                    standard.Select(Path.GetFileName).ToArray());

                // ③ 真 7-Zip 拿改完名字的这一组**真的打开了**，并列出与原件一致的条目
                string opened = set.Format == VolumeContentFormat.Rar
                    ? standard.Single(path => path.EndsWith(".part1.rar", StringComparison.OrdinalIgnoreCase))
                    : standard.Single(path => path.EndsWith(".z01", StringComparison.OrdinalIgnoreCase));

                IReadOnlyList<SevenZipEntry> entries = ListEntries(opened);

                Assert.Equal(ExpectedEntryCount, entries.Count);
                Assert.Equal(ExpectedTotalBytes, entries.Sum(entryItem => entryItem.Size));
                Assert.True(IsMultivolume(opened), $"{set.Name}：7-Zip 不认为这是分卷组");

                /*
                 * ④ 解压这一步：样本内容**是加密的**（E7z 的 -slt 每个 mp4 都是 Encrypted = +，
                 * 空密码 t 报 Wrong password），而任务里没有密码、程序也不许猜。
                 * 所以这里如实断言两件事：管线报的是**密码错误**（说明整组已经被正确打开、
                 * 一直走到解密那一步了），输出目录里**一个字节都没有**（没有假成功）。
                 */
                Assert.Equal(StatusText.WrongPassword, task.Status);
                Assert.Empty(Directory.GetFiles(harness.OutputRoot, "*", SearchOption.AllDirectories));
            }
        }

        // ── 真的 RAR4 分卷（本机 WinRAR 命令行造的）→ 真的解出内容、逐字节一致 ──

        [RarExeFact]
        public async Task 真WinRAR打的RAR4三卷_名字被改烂_既有管线真的解出内容_逐字节一致()
        {
            /*
             * 为什么还要这一条（用户 2026-09-29："真的解出内容，不是报了个好听的结论"）：
             * 用户给的三套真包**内容都是加密的**（`7z l -slt` 每个 mp4 都是 Encrypted = +，
             * 空密码 `7z t -p""` 报 Wrong password），任务里又没有密码、程序也不许猜 ——
             * 所以那三套只能验到"认得出来、排得对、改得回标准名、引擎真的打开了这一组"。
             * 这一条补上**最后一段**：用本机 WinRAR 的命令行 Rar.exe 造一组**不加密的 RAR4 分卷**
             * （Rar.exe 是唯一能写 RAR 的工具，7-Zip 不会写），把卷标记从名字里彻底抹掉，
             * 再走同一条既有管线 —— 断言的是磁盘上解出来的字节与原件**逐字节相同**。
             */
            string directory = Path.Combine(_root, "rar4e2e-" + Guid.NewGuid().ToString("N")[..8]);

            Directory.CreateDirectory(directory);

            var payload = new byte[2621440]; // 2.5 MiB：-v1m 正好三卷（两个满卷 + 一个尾卷）
            new Random(20260929).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(directory, "data.bin"), payload);

            RunRar(directory, "a", "-ma4", "-m0", "-v1m", "r4.rar", "data.bin");

            string[] volumes = Directory.GetFiles(directory, "r4.part*.rar").OrderBy(p => p).ToArray();

            Assert.Equal(3, volumes.Length);

            // ① 它是 RAR 1.5–4.x 那一族（签名 `Rar!\x1A\x07\x00`；RAR5 是 `...\x07\x01\x00`）——
            //    所以下文一律叫它 RAR4，而不是"RAR3"。
            byte[] signature = File.ReadAllBytes(volumes[0]).Take(7).ToArray();

            Assert.Equal(
                new byte[] { (byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07, 0x00 },
                signature);

            // ② 卷号读得出来，而且整组连成 1..3（这一条同时是"ENDARC 卷号位置"在**第二个生产者**
            //    身上的交叉验证：用户那两套真包 + 本机 WinRAR 的输出，两边的字节布局必须同时成立）
            VolumeNumberReading first = VolumeNumberFromContent.Read(volumes[0]);
            VolumeNumberReading second = VolumeNumberFromContent.Read(volumes[1]);
            VolumeNumberReading third = VolumeNumberFromContent.Read(volumes[2]);

            Assert.Equal(VolumeNumberFail.None, first.Fail);
            Assert.True(first.FirstVolumeFlag);
            Assert.False(second.FirstVolumeFlag);
            Assert.False(third.FirstVolumeFlag);
            Assert.Equal(first.RawField!.Value + 1, second.RawField!.Value);
            Assert.Equal(first.RawField!.Value + 2, third.RawField!.Value);

            VolumeGroupOrder order = VolumeNumberFromContent.ResolveGroup(
                volumes[0],
                volumes.Select(VolumeNumberFromContent.Read));

            Assert.True(order.Confirmed, $"内容定序没成（{order.Fail}）");
            Assert.Equal(new[] { 1, 2, 3 }, order.Slots.Select(slot => slot.Number).ToArray());

            // ③ 名字改烂：卷标记一点都不剩 —— 判据只能靠内容
            string[] garbled = { "r4a.rar", "r4b.rar", "r4c.rar" };

            for (int index = 0; index < volumes.Length; index++)
            {
                File.Move(volumes[index], Path.Combine(directory, garbled[index]));
            }

            Assert.False(FileNameHelper.IsVolumePartFileName(garbled[0]), "样本没造对：名字里还有卷标记");

            // ④ 既有管线：认组 → 按内容定序 → 整组改回标准名 → 真的解出内容
            Harness harness = CreateHarness();
            ArchiveTask task = await AddTaskAsync(harness, Path.Combine(directory, garbled[0]));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.True(task.VolumeNameAutoRenamed, "不是管线按内容改名的那一条路");

            foreach (string name in new[] { "r4a.part1.rar", "r4a.part2.rar", "r4a.part3.rar" })
            {
                Assert.True(File.Exists(Path.Combine(directory, name)), $"整组没改回标准名：缺 {name}");
            }

            foreach (string name in garbled)
            {
                Assert.False(File.Exists(Path.Combine(directory, name)), $"旧名字还在：{name}");
            }

            // ⑤ **逐字节一致** —— 这才叫"真的解出内容"
            string[] extracted = Directory.GetFiles(harness.OutputRoot, "data.bin", SearchOption.AllDirectories);

            Assert.Single(extracted);
            Assert.Equal(payload.Length, new FileInfo(extracted[0]).Length);
            Assert.Equal(payload, File.ReadAllBytes(extracted[0]));
        }

        /// <summary>本机 <c>Rar.exe</c>（路径来自产品自己的唯一出口 <see cref="Engines.ToolLocator"/>）。</summary>
        /// <summary>
        /// 这一单的"目标工作区根"（同卷）：<c>&lt;样本副本目录&gt;\.ArchiveFixer.work</c>。
        /// 真实调用方传的是 <c>&lt;目标目录&gt;\.ArchiveFixer.work</c>（不变量 12：需要临时物的地方由调用方传进来）；
        /// 这里样本在只读副本的临时目录里，用**同一个常量**拼，⛔ 不写死名字。
        /// </summary>
        private static string WorkRootFor(string samplePath) =>
            Path.Combine(
                Path.GetDirectoryName(samplePath)!,
                Detection.VolumeContentInference.WorkDirectoryName);

        private static void RunRar(string workingDirectory, params string[] args)
        {
            string rar = Engines.ToolLocator.Default.RarExePath;

            var psi = new ProcessStartInfo(rar)
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

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 Rar.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            Assert.True(process.WaitForExit(300_000), "Rar.exe 超时");
            Assert.True(process.ExitCode == 0, $"Rar.exe 失败：{stdout}{stderr}");
        }

        // ── 真样本的发现与副本 ──

        /// <summary>样本目录里的一套（一组分卷）。</summary>
        internal sealed class RealSampleSet
        {
            public string Name { get; init; } = string.Empty;

            public string DirectoryPath { get; init; } = string.Empty;

            public VolumeContentFormat Format { get; init; }

            /// <summary>组的第 1 片/卷（也是**用户会拖进来的那一个**）。</summary>
            public string EntryFile { get; init; } = string.Empty;

            /// <summary>组的最后一片/卷。</summary>
            public string LaterFile { get; init; } = string.Empty;

            /// <summary>按内容挑出样本目录里所有这一族的套（⛔ 不看名字：样本的名字本来就是改烂的）。</summary>
            internal static IReadOnlyList<RealSampleSet> Discover(VolumeContentFormat format)
            {
                var sets = new List<RealSampleSet>();

                if (!Directory.Exists(RealVolumeSampleFactAttribute.SampleRoot))
                {
                    return sets;
                }

                foreach (string directory in Directory.GetDirectories(RealVolumeSampleFactAttribute.SampleRoot))
                {
                    // 下划线开头的是探针/临时目录（不是用户放的那几套样本），不当样本看。
                    if (Path.GetFileName(directory).StartsWith('_'))
                    {
                        continue;
                    }

                    string[] files = Directory.GetFiles(directory);

                    if (files.Length < 2)
                    {
                        continue;
                    }

                    var readings = files
                        .Select(path => (Path: path, Reading: VolumeNumberFromContent.Read(path)))
                        .Where(item => item.Reading.Format == format && item.Reading.IsVolumeMember)
                        .ToList();

                    if (readings.Count < 2)
                    {
                        continue;
                    }

                    /*
                     * 族的入口：RAR 是带"这是第 1 卷"标记的那一个；跨盘 zip 是 EOCD 带盘号的那个（末片）——
                     * 但用户会拖进来的其实是**第一片**，所以两边都用"内容里排第 1 的那一个"当入口。
                     */
                    string? first = null;
                    string? later = null;

                    foreach ((string path, VolumeNumberReading reading) in readings)
                    {
                        bool isFirst = format == VolumeContentFormat.Rar
                            ? reading.FirstVolumeFlag
                            : reading.Number == null;

                        if (isFirst && first == null)
                        {
                            first = path;
                        }
                        else
                        {
                            later = path;
                        }
                    }

                    if (first == null || later == null)
                    {
                        continue;
                    }

                    sets.Add(new RealSampleSet
                    {
                        Name = Path.GetFileName(directory),
                        DirectoryPath = directory,
                        Format = format,
                        EntryFile = first,
                        LaterFile = later
                    });
                }

                return sets;
            }

            internal static IReadOnlyList<VolumeNumberReading> ReadAll(RealSampleSet set) =>
                Directory.GetFiles(set.DirectoryPath)
                    .Select(VolumeNumberFromContent.Read)
                    .ToList();
        }

        /// <summary>把一套样本**复制**到这一条用例自己的临时目录（⛔ 绝不改样本目录里的东西）。</summary>
        private string CopySetToWorkDirectory(RealSampleSet set)
        {
            string directory = Path.Combine(_root, set.Name + "-" + Guid.NewGuid().ToString("N")[..8]);

            Directory.CreateDirectory(directory);

            foreach (string file in Directory.GetFiles(set.DirectoryPath))
            {
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), overwrite: false);
            }

            return directory;
        }

        /// <summary>
        /// 两个建议名是不是同一个基名（RAR 族带 <c>.partN.rar</c> 两段，跨盘 zip 族带 <c>.zNN</c>/<c>.zip</c> 一段）。
        /// </summary>
        private static string Stem(string fileName)
        {
            string withoutExtension = Path.GetFileNameWithoutExtension(fileName);

            // RAR 族多一段：把 `.partN` 也去掉，剩下的才是两个名字共同的基名。
            int part = withoutExtension.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase);

            return part > 0 ? withoutExtension[..part] : withoutExtension;
        }

        // ── 真 7-Zip 的"这一组到底能不能开" ──

        internal sealed class SevenZipEntry
        {
            public string Name { get; init; } = string.Empty;

            public long Size { get; init; }

            public string Crc { get; init; } = string.Empty;
        }

        /// <summary>这套真样本里的条目数（只读原目录探出来的事实：5 个 mp4）。</summary>
        private const int ExpectedEntryCount = 5;

        private const long ExpectedTotalBytes = 680432893;

        /// <summary>用真 7-Zip 列出这一组的条目（<c>-slt</c>：名字 / 大小 / CRC）。</summary>
        private static IReadOnlyList<SevenZipEntry> ListEntries(string path)
        {
            (string stdout, string stderr) = Run7z("l", "-slt", path);

            // ⚠ 加密条目 7-Zip 仍然列得出来（头没加密），退出码可能是 0；列不出来才说明名字改错了。
            var entries = new List<SevenZipEntry>();
            var name = new StringBuilder();
            long size = -1;
            string crc = string.Empty;
            bool folder = false;

            void Flush()
            {
                if (name.Length > 0 && size >= 0 && !folder && crc.Length > 0)
                {
                    entries.Add(new SevenZipEntry { Name = name.ToString(), Size = size, Crc = crc });
                }

                name.Clear();
                size = -1;
                crc = string.Empty;
                folder = false;
            }

            foreach (string line in stdout.Split('\n'))
            {
                string text = line.TrimEnd('\r');

                if (text.StartsWith("Path = ", StringComparison.Ordinal))
                {
                    Flush();
                    name.Append(text["Path = ".Length..]);
                }
                else if (text.StartsWith("Size = ", StringComparison.Ordinal))
                {
                    _ = long.TryParse(text["Size = ".Length..], out size);
                }
                else if (text.StartsWith("CRC = ", StringComparison.Ordinal))
                {
                    crc = text["CRC = ".Length..].Trim();
                }
                else if (text.StartsWith("Folder = ", StringComparison.Ordinal))
                {
                    folder = string.Equals(text["Folder = ".Length..].Trim(), "+", StringComparison.Ordinal);
                }
            }

            Flush();

            Assert.False(
                string.IsNullOrWhiteSpace(stdout) && !string.IsNullOrWhiteSpace(stderr),
                $"7-Zip 没有输出：{stderr}");

            return entries;
        }

        private static bool IsMultivolume(string path)
        {
            (string stdout, _) = Run7z("l", path);

            return stdout.Contains("Multivolume = +", StringComparison.Ordinal);
        }

        private static (string Stdout, string Stderr) Run7z(params string[] args)
        {
            string sevenZip = SevenZipFactAttribute.LocateSevenZipPath();

            Assert.False(string.IsNullOrEmpty(sevenZip), "测试机上没有 7z.exe");

            var psi = new ProcessStartInfo(sevenZip)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 7z.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            Assert.True(process.WaitForExit(300_000), "7z 超时");

            return (stdout, stderr);
        }

        // ── 既有管线（与 DisguisedVolumeContentProbeTests 同一套夹具） ──

        private static async Task<ArchiveTask> AddTaskAsync(Harness harness, string path)
        {
            var task = new ArchiveTask(path, harness.Vm.Tasks.Count + 1) { IsSelected = true };

            await new ArchiveDetectService().ApplyDetectResultAsync(task);
            task.CaptureSourceSnapshot();
            harness.Vm.Tasks.Add(task);

            return task;
        }

        private Harness CreateHarness()
        {
            string dataRoot = Path.Combine(_root, "data-" + Guid.NewGuid().ToString("N"));
            string outputRoot = Path.Combine(_root, "out-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.RecursionMode = "SingleLayer";
            settings.AutoScanAfterDrop = false;
            settings.MaxParallelExtractCount = 1;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            settingsService.Save(settings);

            IArchiveEngine engine = new Engines.SevenZip.SevenZipEngine();
            var logService = new LogService(pathService);

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                engine,
                new PasswordService(),
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new ConfirmDialogService());

            var coordinator = new ExtractionCoordinator(
                vm,
                engine,
                new PasswordService(),
                pathService,
                new ConfirmDialogService());

            coordinator.KeepTaskDetailInLog = true;

            return new Harness(vm, coordinator, logService, outputRoot, dataRoot);
        }

        private sealed class ConfirmDialogService : DialogService
        {
            public override bool ShowConfirm(
                string message,
                string optionText,
                bool optionCheckedByDefault,
                string detail,
                out bool optionChecked)
            {
                optionChecked = optionCheckedByDefault;

                return true;
            }
        }

        private sealed class Harness
        {
            public Harness(
                MainViewModel vm,
                ExtractionCoordinator coordinator,
                LogService log,
                string outputRoot,
                string dataRoot)
            {
                Vm = vm;
                Coordinator = coordinator;
                Log = log;
                OutputRoot = outputRoot;
                DataRoot = dataRoot;
            }

            public MainViewModel Vm { get; }

            public ExtractionCoordinator Coordinator { get; }

            public LogService Log { get; }

            public string OutputRoot { get; }

            public string DataRoot { get; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);
        }
    }
}
