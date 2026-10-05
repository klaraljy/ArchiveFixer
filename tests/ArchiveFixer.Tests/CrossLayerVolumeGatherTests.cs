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
using ArchiveFixer.Services;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// <b>跨层收卷</b>（用户 2026-10-05 真机）与**凑不齐就别试**（同一批的第二条口径）。
    ///
    /// <para><b>现场</b>（<c>ArchiveFixer-本次操作_20261005_113541.txt</c>）：源目录只有
    /// <c>HK.7z.001</c> + <c>HK.7z.002</c>；第 0 层解出 3 个 mp4（7.11 GiB）；3 个 mp4 都是**双面文件**
    /// （真视频 + 尾部一整个 7z 容器），各自解出**一片分卷** ——
    /// <c>4K (11)_2.mp4</c> → <c>HK.7z.002</c>（layer-001）、<c>4K (13)_3.mp4</c> → <c>HK.7z.003</c>（layer-002）、
    /// <c>4K (18)_1.mp4</c> → <c>HK.7z.00删除1</c> →「还原」→ <c>HK.7z.001</c>（layer-003）。
    /// 三片是**同一个 3 卷 7z**，却躺在**三个不同的层产物目录**里；引擎找兄弟卷只看入口旁边那一层 ⇒
    /// 第 2 层拿 <c>HK.7z.001</c> 单独去解 ⇒ <c>Open ERROR: Cannot open the file as [7z] archive</c>
    /// ⇒ 还被报成「这是分卷压缩包的后续卷，缺少首卷」（**说反了**）⇒ 整条链「部分完成」⇒
    /// 工作区 7 个文件 / 13.48 GiB 整份删掉、什么都没发布。</para>
    ///
    /// <para><b>这里钉五件事</b>：① 纯计划层：三片分处三个目录也能判成一组并给出"收进入口那一层"的计划；
    /// 组不齐 / 尺寸不规律 / 字节数对不上 / 目标名被占 / 跨盘 ⇒ 一律"不做"；
    /// ② 真 7z 端到端：真机同形的三层夹具跑**真递归**，内层内容真的解出来；
    /// ③ 日志点名"已把同组的 N 卷收进入口「HK.7z.001」所在的那一层"；
    /// ④ 记账不半套：产物里那一组要么整组在、要么整组不在；⑤ 源包 SHA256 一个字节没变。
    /// ⑥ 凑不齐 ⇒ **一次引擎调用都不做**（假引擎数调用次数）、报"缺哪几片"、⛔ 不含「引擎操作失败」。</para>
    ///
    /// <para>⚠ 与改写 <c>RecursiveExtractor.ConfiguredWorkspaceRoot</c>（进程级静态）的用例类**串行**跑：
    /// 约定与 <c>InnerDoubleFacedCarveTests</c> / <c>RecursiveExtractorTests</c> 顶部那段说明同一条。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class CrossLayerVolumeGatherTests : IDisposable
    {
        /// <summary>7-Zip 容忍的"前面垫的数据"上限 8 MiB（实测边界）—— 9 MiB 必然落到"必须抠出来"那一档。</summary>
        private const int PrefixBytes = 9 * 1024 * 1024;

        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        private readonly string _root;
        private readonly string? _sevenZip = SevenZipFactAttribute.LocateSevenZipPath();
        private readonly ITestOutputHelper _output;

        public CrossLayerVolumeGatherTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "af-crosslayer-" + Guid.NewGuid().ToString("N"));
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
                // 临时目录删不掉不影响结论。
            }
        }

        // ================================================================ ① 纯计划层（闸门）

        [SevenZipFact]
        public void 三片分处三个目录_能判成一组_并给出收进001那一层的计划()
        {
            RequireSevenZip();

            (string first, string second, string third, long total) = BuildRealThreeVolumeSet("三目录");
            var pieces = new List<VolumeCandidate>();

            // 真机形状：三片分别在三个层产物目录里（谁都不是入口那一层）——
            // 层产物目录是 `<layer-NNN>\output`（真机日志里两卷都印成"从 output 收来"，
            // 用户看不出是哪一层 ⇒ T7 要印出层号，见下面那条断言）。
            pieces.Add(Piece(MoveToLayer(second, Path.Combine("layer-001", "output"))));
            pieces.Add(Piece(MoveToLayer(third, Path.Combine("layer-002", "output"))));

            VolumeNameRepair.CrossLayerVolumeGather decision =
                VolumeNameRepair.ResolveCrossLayerVolumeGather(first, pieces);

            // T7（2026-10-05 真机）：收卷那一行必须说清**是从哪一层收来的** ——
            // 只印最后一段目录名时两边都是 "output"，等于没说。
            Assert.Contains("从 layer-001 收来", decision.Detail, StringComparison.Ordinal);
            Assert.Contains("从 layer-002 收来", decision.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("从 output 收来", decision.Detail, StringComparison.Ordinal);

            Assert.True(decision.Applicable, decision.Detail);
            Assert.False(decision.CompleteBesideEntry, decision.Detail);
            Assert.False(decision.ShouldSkipTrial, decision.Detail);
            Assert.NotNull(decision.Plan);
            Assert.True(decision.Plan!.CanRepair, decision.Plan.Reason);

            // 计划里"真要动"的是那两片散在别处的卷（入口那一卷原地不动）。
            List<VolumeRepairItem> moved = decision.Plan.Items
                .Where(item => !string.Equals(item.CurrentPath, item.TargetPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.Equal(2, moved.Count);
            Assert.Equal(2, decision.Plan.GatheredVolumes);
            Assert.DoesNotContain(moved, item => string.Equals(item.CurrentPath, first, StringComparison.OrdinalIgnoreCase));
            Assert.All(moved, item => Assert.Equal(
                Path.GetDirectoryName(first),
                Path.GetDirectoryName(item.TargetPath)));

            _ = total;

            // 执行体仍是既有 TryApply：三片必须落在**同一个目录**里、名字是规范卷名。
            VolumeNameRepairResult applied = VolumeNameRepair.TryApply(decision.Plan);

            Assert.True(applied.Success, applied.Message);
            Assert.Equal(
                new[] { "HK.7z.001", "HK.7z.002", "HK.7z.003" },
                Directory.GetFiles(Path.GetDirectoryName(first)!)
                    .Select(Path.GetFileName)
                    .Where(name => name!.StartsWith("HK.7z.", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray()!);
        }

        [SevenZipFact]
        public void 完整单卷包_名字自述相等_照常解_不收也不免试()
        {
            RequireSevenZip();

            // "它就自己一片且自述相等 = 本来就是个完整归档"：⛔ 不许误判成分卷。
            string payload = Path.Combine(_root, "单卷-payload");
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(payload, "a.txt"), "完整单卷", Utf8NoBom);

            string archive = Path.Combine(_root, "单卷.7z");
            Run7z("a", "-t7z", archive, Path.Combine(payload, "*"));

            // 名字改成 .7z.001（真机上被「还原」工序改回标准名的就是这种形状）。
            string first = Path.Combine(_root, "单卷.7z.001");
            File.Move(archive, first);

            VolumeNameRepair.CrossLayerVolumeGather decision =
                VolumeNameRepair.ResolveCrossLayerVolumeGather(first, Array.Empty<VolumeCandidate>());

            Assert.True(decision.Applicable, decision.Detail);
            Assert.True(decision.CompleteBesideEntry, decision.Detail);
            Assert.False(decision.ShouldSkipTrial, decision.Detail);
            Assert.Null(decision.Plan);
        }

        [SevenZipFact]
        public void 组不齐_少一片_不收也不试_并报还差多少字节()
        {
            RequireSevenZip();

            (string first, string second, string third, long total) = BuildRealThreeVolumeSet("组不齐");

            // 只有 .001 与 .003（中间那片丢了）⇒ 名字上有洞 + 字节数对不上。
            File.Delete(second);

            var pieces = new List<VolumeCandidate> { Piece(MoveToLayer(third, "layer-002")) };

            VolumeNameRepair.CrossLayerVolumeGather decision =
                VolumeNameRepair.ResolveCrossLayerVolumeGather(first, pieces);

            Assert.True(decision.Applicable, decision.Detail);
            Assert.Null(decision.Plan);
            Assert.True(decision.ShouldSkipTrial, decision.Detail);

            // 不变量 7：必须报缺哪几个 —— 名字上的洞点得出名，"末卷之后"的那部分只能报字节差额。
            Assert.Contains("HK.7z.002", decision.MissingNames);
            Assert.Contains("还差", decision.Detail, StringComparison.Ordinal);

            // ⛔ 一个字节都没动。
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(Path.Combine(_root, "层产物", "layer-002", "HK.7z.003")));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(first)!, "HK.7z.003")));

            _ = total;
        }

        [SevenZipFact]
        public void 起始头字节数对不上_不收也不试()
        {
            RequireSevenZip();

            (string first, _, string third, _) = BuildRealThreeVolumeSet("字节对不上");

            // 把中间那片换成一个"名字对、内容不对"的同名文件（长度也不同）⇒ 字节数证据必然对不上。
            string second = Path.Combine(_root, "字节对不上", "HK.7z.002");
            File.WriteAllBytes(second, new byte[256 * 1024]);

            var pieces = new List<VolumeCandidate> { Piece(MoveToLayer(third, "layer-002")) };

            VolumeNameRepair.CrossLayerVolumeGather decision =
                VolumeNameRepair.ResolveCrossLayerVolumeGather(first, pieces);

            Assert.True(decision.Applicable, decision.Detail);
            Assert.Null(decision.Plan);
            Assert.True(decision.ShouldSkipTrial, decision.Detail);
            Assert.Contains("字节", decision.Detail, StringComparison.Ordinal);
        }

        [SevenZipFact]
        public void 目标名被占_整组不动_也不试()
        {
            RequireSevenZip();

            (string first, string second, _, _) = BuildRealThreeVolumeSet("目标被占");

            // 先把它挪出入口那一层（不然一会儿写 0 字节会把真片覆盖掉），再摆一个**占着目标名**的东西。
            var pieces = new List<VolumeCandidate> { Piece(MoveToLayer(second, "layer-001")) };

            // 0 字节 ⇒ 不进候选池（零字节不可能是卷），但会占住落点。
            string occupied = Path.Combine(Path.GetDirectoryName(first)!, "HK.7z.002");
            File.WriteAllBytes(occupied, Array.Empty<byte>());

            VolumeNameRepair.CrossLayerVolumeGather decision =
                VolumeNameRepair.ResolveCrossLayerVolumeGather(first, pieces);

            Assert.True(decision.Applicable, decision.Detail);
            Assert.Null(decision.Plan);

            // ⛔ 绝不覆盖：字节数其实是齐的，但落点被占 ⇒ 不搬、也不拿单独一片去试。
            Assert.True(decision.ShouldSkipTrial, decision.Detail);
            Assert.Equal(0, new FileInfo(occupied).Length);
            Assert.True(File.Exists(Path.Combine(_root, "层产物", "layer-001", "HK.7z.002")));
        }

        [Fact]
        public void 跨盘_不收()
        {
            // 入口这一层在 C:，兄弟片用 device path 写出来（卷根不同）⇒ 既有判据 IsSameVolumeRoot 判跨盘。
            string entryDirectory = Path.Combine(_root, "跨盘-入口");
            string otherDirectory = Path.Combine(_root, "跨盘-别处");

            Directory.CreateDirectory(entryDirectory);
            Directory.CreateDirectory(otherDirectory);

            string first = Path.Combine(entryDirectory, "HK.7z.001");
            string second = Path.Combine(otherDirectory, "HK.7z.002");

            File.WriteAllBytes(first, new byte[512 * 1024]);
            File.WriteAllBytes(second, new byte[512 * 1024]);

            // 入口这一层留一个**洞**（有 .001 与 .003、缺 .002）—— 否则"只有一片"在名字这条证据上
            // 看起来就是完整的一组（命名里没有"共几卷"这个信息），根本走不到跨盘那道闸门。
            File.WriteAllBytes(Path.Combine(entryDirectory, "HK.7z.003"), new byte[512 * 1024]);

            // ⚠ 名字里得有卷标记（判据是名字），内容不是真 7z ⇒ 字节数这条证据缺席，只判"收得到一起吗"。
            var pieces = new List<VolumeCandidate>
            {
                new() { Path = @"\\?\" + second, Size = new FileInfo(second).Length }
            };

            VolumeNameRepair.CrossLayerVolumeGather decision =
                VolumeNameRepair.ResolveCrossLayerVolumeGather(first, pieces);

            Assert.True(decision.Applicable, decision.Detail);
            Assert.Null(decision.Plan);
            Assert.Contains("不在同一个盘上", decision.Detail, StringComparison.Ordinal);

            // ⛔ 一个字节都没动。
            Assert.True(File.Exists(second));
            Assert.False(File.Exists(Path.Combine(entryDirectory, "HK.7z.002")));
        }

        [Fact]
        public void 尺寸不规律_判不出_什么都不做()
        {
            // 名字带垃圾尾巴（进"名字被伪装"那一档）⇒ 既有尺子要求**除末片外等大**；这里故意不规律。
            string directory = Path.Combine(_root, "尺寸不规律");
            Directory.CreateDirectory(directory);

            string first = Path.Combine(directory, "HK.7z.001删");
            File.WriteAllBytes(first, new byte[64 * 1024]);
            File.WriteAllBytes(Path.Combine(directory, "HK.7z.002删"), new byte[128 * 1024]);
            File.WriteAllBytes(Path.Combine(directory, "HK.7z.003删"), new byte[96 * 1024]);

            VolumeNameRepair.CrossLayerVolumeGather decision =
                VolumeNameRepair.ResolveCrossLayerVolumeGather(first, Array.Empty<VolumeCandidate>());

            // 尺寸规律不过 ⇒ 连"这一组叫什么"都判不出 ⇒ **整档不适用**（什么都不做：
            // 照旧让引擎去判它自己那句"分卷缺失"，⛔ 不搬、也⛔ 不免试）。
            Assert.False(decision.Applicable);
            Assert.Null(decision.Plan);
            Assert.False(decision.ShouldSkipTrial);
            // 一个字节都没动。
            Assert.Equal(64 * 1024, new FileInfo(first).Length);
        }

        [Fact]
        public void 基名不同_不碰别的组()
        {
            string directory = Path.Combine(_root, "基名不同");
            string other = Path.Combine(directory, "layer-001");
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(other);

            // 入口这一组：.001 + .002 都在同一层（对照：这一档本来就该"什么都不用做"）。
            string first = Path.Combine(directory, "HK.7z.001");
            string second = Path.Combine(directory, "HK.7z.002");
            File.WriteAllBytes(first, new byte[512 * 1024]);
            File.WriteAllBytes(second, new byte[512 * 1024]);

            // 别的层产物目录里躺着**基名不同**的卷：不许被拖进来。
            string stranger = Path.Combine(other, "OTHER.7z.002");
            File.WriteAllBytes(stranger, new byte[512 * 1024]);

            VolumeNameRepair.CrossLayerVolumeGather decision =
                VolumeNameRepair.ResolveCrossLayerVolumeGather(
                    first,
                    new List<VolumeCandidate> { new() { Path = stranger, Size = new FileInfo(stranger).Length } });

            Assert.True(decision.Applicable, decision.Detail);
            Assert.Null(decision.Plan);
            Assert.True(File.Exists(stranger));
        }

        [Fact]
        public void 入口是后续卷_这一档不适用()
        {
            string directory = Path.Combine(_root, "入口是后续卷");
            Directory.CreateDirectory(directory);

            string second = Path.Combine(directory, "HK.7z.002");
            File.WriteAllBytes(second, new byte[512 * 1024]);
            File.WriteAllBytes(Path.Combine(directory, "HK.7z.001"), new byte[512 * 1024]);

            VolumeNameRepair.CrossLayerVolumeGather decision =
                VolumeNameRepair.ResolveCrossLayerVolumeGather(second, Array.Empty<VolumeCandidate>());

            Assert.False(decision.Applicable);
            Assert.Null(decision.Plan);
            Assert.False(decision.ShouldSkipTrial);
        }

        // ================================================================ ② 真机同形的端到端

        [SevenZipFact]
        public async Task 真机同形_三片在三个层产物目录_真递归必须把内层内容解出来()
        {
            RequireSevenZip();

            (string outer, string innerPayloadName, string[] pieces) = BuildRealMachineShape();

            string outerHashBefore = Sha256Of(outer);

            string output = Path.Combine(_root, "端到端-out");
            var log = new List<(string Level, string Message)>();
            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicArchiveProber(),
                _ => new[] { string.Empty },
                limits: null,
                log: (level, message) => log.Add((level, message)));

            RecursionResult result = await extractor.ExtractAsync(
                new ArchiveTask(outer),
                output,
                RecursionMode.AllBranches,
                null,
                CancellationToken.None);

            foreach ((string level, string message) in log)
            {
                _output.WriteLine($"[{level}] {message}");
            }

            // ① 整链跑完（撤掉收卷 ⇒ 真机同形：停在第 2 层、原因「引擎操作失败」、部分完成）。
            Assert.True(result.Completed, result.Summary);
            Assert.Equal(RecursionStopReason.Completed, result.StopReason);

            // ② 内层内容真的解出来了（这才是"修好"与"少报一个失败"的分水岭）。
            string? payload = FindFileUnder(output, innerPayloadName);
            Assert.NotNull(payload);
            Assert.Contains("三卷里的真内容", File.ReadAllText(payload!), StringComparison.Ordinal);

            // ③ 日志点名"收卷"（收了几卷、收进入口那一卷所在的那一层）。
            Assert.Contains(
                log,
                entry => entry.Message.Contains("已把同组的", StringComparison.Ordinal)
                         && entry.Message.Contains("收进入口「HK.7z.001」", StringComparison.Ordinal));

            // ④ 记账不半套：产物里那一组要么整组在、要么整组不在（⛔ 不许只留下 .002 / .003）。
            string[] leftInProduct = Directory
                .GetFiles(output, "HK.7z.*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray()!;

            _output.WriteLine("产物里剩下的分卷：" + (leftInProduct.Length == 0 ? "（整组都不在）" : string.Join("、", leftInProduct)));

            Assert.True(
                leftInProduct.Length == 0 || leftInProduct.Length == 3,
                "整组要么整组在、要么整组不在，绝不许留半套：" + string.Join("、", leftInProduct));

            // ⑤ ⛔ 源包一个字节没动。
            Assert.Equal(outerHashBefore, Sha256Of(outer));

            _ = pieces;
        }

        /// <summary>
        /// **对照：产出顺序反过来**（第 1 卷那片由**第一个**分支产出）也照样要收齐、要解开。
        ///
        /// <para>为什么单列一条：收卷的挂点选在"**解这一层之前**"而不是"产出那一层之后"，
        /// 理由就是"谁先产出由队列决定、不能赌"。这一条把那个理由钉住 ——
        /// 撤掉挂点（或挪回"产出即收"）时它会与上面那条一起红。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 对照_产出顺序反过来_第1卷那片先出来_也照样收齐并解开()
        {
            RequireSevenZip();

            (string outer, string innerPayloadName, _) = BuildRealMachineShape(firstVolumeFirst: true);

            string output = Path.Combine(_root, "顺序反过来-out");
            var log = new List<(string Level, string Message)>();
            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicArchiveProber(),
                _ => new[] { string.Empty },
                limits: null,
                log: (level, message) => log.Add((level, message)));

            RecursionResult result = await extractor.ExtractAsync(
                new ArchiveTask(outer),
                output,
                RecursionMode.AllBranches,
                null,
                CancellationToken.None);

            foreach ((string level, string message) in log)
            {
                _output.WriteLine($"[{level}] {message}");
            }

            Assert.True(result.Completed, result.Summary);
            Assert.NotNull(FindFileUnder(output, innerPayloadName));
            Assert.Contains(
                log,
                entry => entry.Message.Contains("已把同组的", StringComparison.Ordinal));
        }

        // ================================================================ ③ 凑不齐 ⇒ 一次引擎都不许调

        [SevenZipFact]
        public async Task 凑不齐_引擎一次都没被调用_报缺哪几片而不是引擎失败()
        {
            RequireSevenZip();

            (string outer, string firstVolumeName) = BuildOuterWithLoneFirstVolume();

            string output = Path.Combine(_root, "免试-out");
            var counting = new CountingEngine(new SevenZipEngine());
            var log = new List<(string Level, string Message)>();
            var extractor = new RecursiveExtractor(
                counting,
                new MagicArchiveProber(),
                _ => new[] { string.Empty },
                limits: null,
                log: (level, message) => log.Add((level, message)));

            RecursionResult result = await extractor.ExtractAsync(
                new ArchiveTask(outer),
                output,
                RecursionMode.AllBranches,
                null,
                CancellationToken.None);

            foreach ((string level, string message) in log)
            {
                _output.WriteLine($"[{level}] {message}");
            }

            // ① ⛔ 引擎一次都没被调用（数的是"拿这一片去试"的次数）。
            Assert.Equal(0, counting.CountFor(firstVolumeName));

            // ② 停因是「缺卷」，⛔ 不是「引擎操作失败」。
            Assert.Equal(RecursionStopReason.MissingVolume, result.StopReason);
            Assert.False(result.Completed);

            Assert.Contains(
                log,
                entry => entry.Message.Contains("引擎一次都没被调用", StringComparison.Ordinal));
            Assert.Contains(
                log,
                entry => entry.Message.Contains("还差", StringComparison.Ordinal));
            Assert.DoesNotContain(
                log,
                entry => entry.Message.Contains("引擎操作失败", StringComparison.Ordinal));

            // ③ 结论那一段也不许说成"引擎解不开"，而且**要点名缺哪几片**（不变量 7）。
            Assert.Contains(StatusText.VolumeMissing, result.Summary, StringComparison.Ordinal);
            Assert.Contains(firstVolumeName, result.Summary, StringComparison.Ordinal);
        }

        /// <summary>
        /// **对照：缺的是中间那一卷** ⇒ 必须**点名**（不变量 7："必须报缺哪几个"），
        /// 照样一次引擎都不调。
        /// </summary>
        [SevenZipFact]
        public async Task 凑不齐_缺的是中间那一卷_要点名并且照样不试()
        {
            RequireSevenZip();

            (string outer, string firstVolumeName) = BuildOuterWithLoneFirstVolume(withLastVolume: true);

            string output = Path.Combine(_root, "免试-点名-out");
            var counting = new CountingEngine(new SevenZipEngine());
            var log = new List<(string Level, string Message)>();
            var extractor = new RecursiveExtractor(
                counting,
                new MagicArchiveProber(),
                _ => new[] { string.Empty },
                limits: null,
                log: (level, message) => log.Add((level, message)));

            RecursionResult result = await extractor.ExtractAsync(
                new ArchiveTask(outer),
                output,
                RecursionMode.AllBranches,
                null,
                CancellationToken.None);

            foreach ((string level, string message) in log)
            {
                _output.WriteLine($"[{level}] {message}");
            }

            Assert.Equal(0, counting.CountFor(firstVolumeName));
            Assert.Equal(RecursionStopReason.MissingVolume, result.StopReason);
            Assert.Contains(
                log,
                entry => entry.Message.Contains("HK.7z.002", StringComparison.Ordinal)
                         && entry.Message.Contains("引擎一次都没被调用", StringComparison.Ordinal));
        }

        // ================================================================ 夹具与工具

        /// <summary>造一组**真 3 卷 7z**（`-v1m` 切）；返回三片的路径与整包字节数。</summary>
        private (string First, string Second, string Third, long Total) BuildRealThreeVolumeSet(string name)
        {
            string directory = Path.Combine(_root, name);
            string payload = Path.Combine(directory, "payload");

            Directory.CreateDirectory(payload);

            // 3 MiB 可复现的伪随机数据 ⇒ 恰好切成 3 片（1 MiB / 1 MiB / 余量）。
            byte[] data = new byte[5 * 1024 * 1024 / 2];
            new Random(20261005).NextBytes(data);
            File.WriteAllBytes(Path.Combine(payload, "big.bin"), data);

            Run7z("a", "-t7z", "-mx0", "-v1m", Path.Combine(directory, "HK.7z"), Path.Combine(payload, "*"));

            string first = Path.Combine(directory, "HK.7z.001");
            string second = Path.Combine(directory, "HK.7z.002");
            string third = Path.Combine(directory, "HK.7z.003");

            Assert.True(File.Exists(first) && File.Exists(second) && File.Exists(third), "7-Zip 没切出 3 卷");
            Assert.Equal(3, Directory.GetFiles(directory, "HK.7z.*").Length);

            long total = new FileInfo(first).Length + new FileInfo(second).Length + new FileInfo(third).Length;

            return (first, second, third, total);
        }

        /// <summary>把一份路径包成候选（尺寸读真实文件）。</summary>
        private static VolumeCandidate Piece(string path) =>
            new() { Path = path, Size = new FileInfo(path).Length };

        /// <summary>把一份文件**移**到 <paramref name="layerName"/> 那一层（模拟"分处不同的层产物目录"）。</summary>
        private string MoveToLayer(string source, string layerName)
        {
            string directory = Path.Combine(_root, "层产物", layerName);
            Directory.CreateDirectory(directory);

            string target = Path.Combine(directory, Path.GetFileName(source));

            if (File.Exists(target))
            {
                File.Delete(target);
            }

            File.Move(source, target);

            return target;
        }

        /// <summary>
        /// 造**真机同形**的夹具：外层 7z 里装 3 个双面 mp4（真视频前缀 + 尾部一个装着**一片分卷**的 ZIP 容器）。
        ///
        /// <para>⚠ 容器必须是 **ZIP**：<c>EmbeddedArchiveDetector</c> 只认"偏移错位之后仍能自洽校验"的 ZIP
        /// （7z / RAR 的目录结构做不了这件事）—— 真机那三个 mp4 的尾部也正是 ZIP，
        /// 解出来恰好是**一片 7z 分卷**（`.002` 起没有魔数，所以名字只能靠容器里的条目名带上）。</para>
        ///
        /// <para>名字与真机一致（<c>4K (11)_2.mp4</c> / <c>(13)_3</c> / <c>(18)_1</c>），
        /// 于是第 1 层依次产出 <c>HK.7z.002</c>、<c>HK.7z.003</c>、<c>HK.7z.00删除1</c> →「还原」→ <c>HK.7z.001</c>。</para>
        /// </summary>
        private (string Outer, string InnerPayloadName, string[] Pieces) BuildRealMachineShape(bool firstVolumeFirst = false)
        {
            string work = Path.Combine(_root, "真机同形");
            string innerPayload = Path.Combine(work, "内层内容");
            string volumes = Path.Combine(work, "分卷");

            Directory.CreateDirectory(innerPayload);
            Directory.CreateDirectory(volumes);

            File.WriteAllText(Path.Combine(innerPayload, "三卷里的真内容.txt"), "三卷里的真内容\n", Utf8NoBom);

            // 3 MiB 数据 ⇒ `-v1m` 才会真的切出 3 卷（只有那个几十字节的 txt 的话只有一卷）。
            byte[] bulk = new byte[5 * 1024 * 1024 / 2];
            new Random(20261005).NextBytes(bulk);
            File.WriteAllBytes(Path.Combine(innerPayload, "bulk.bin"), bulk);

            // 内层 7z：真三卷（`-v1m`），三片分别塞进三个 mp4。
            Run7z("a", "-t7z", Path.Combine(volumes, "HK.7z"), "-v1m", Path.Combine(innerPayload, "*"));

            /*
             * 末卷在真机里那个名字是**脏的**（`HK.7z.00删除1` —— 网盘缀了字），而 7-Zip 切出来是干净的
             * `HK.7z.003`。这里按真机改个名：于是第 1 层那条支路会真的走一遍「还原」工序
             * （`HK.7z.00删除1` → `HK.7z.001`），与真机日志第 122 行同形。
             */
            string firstVolume = Path.Combine(volumes, "HK.7z.001");
            string dirtyVolume = Path.Combine(volumes, "HK.7z.00删除1");

            Assert.True(File.Exists(firstVolume), "7-Zip 没切出第 1 卷");
            Assert.Equal(3, Directory.GetFiles(volumes, "HK.7z.*").Length);
            File.Move(firstVolume, dirtyVolume);

            /*
             * 默认 = 真机的产出顺序（`(11)` → `.002`、`(13)` → `.003`、`(18)` → 脏名第 1 卷）。
             * `firstVolumeFirst` = 对照：让**第 1 卷那片先出来**（名字排序决定层 1 的处理顺序）。
             */
            (string Name, string Piece)[] mapping = firstVolumeFirst
                ? new[]
                {
                    ("AAA (1)_1.mp4", "HK.7z.00删除1"),
                    ("BBB (2)_2.mp4", "HK.7z.002"),
                    ("CCC (3)_3.mp4", "HK.7z.003")
                }
                : new[]
                {
                    ("4K (11)_2.mp4", "HK.7z.002"),
                    ("4K (13)_3.mp4", "HK.7z.003"),
                    ("4K (18)_1.mp4", "HK.7z.00删除1")
                };

            var outerContent = Path.Combine(work, "外层内容");
            Directory.CreateDirectory(outerContent);

            foreach ((string name, string piece) in mapping)
            {
                Assert.True(File.Exists(Path.Combine(volumes, piece)), $"内层分卷没切出来：{piece}");

                // 单条目的 ZIP 容器（cwd = 分卷那一层 ⇒ 条目名就是裸文件名）。
                string container = Path.Combine(work, "container-" + Guid.NewGuid().ToString("N") + ".zip");
                Run7zIn(volumes, "a", "-tzip", container, piece);

                byte[] containerBytes = File.ReadAllBytes(container);

                // 真视频前缀（> 8 MiB ⇒ 7-Zip 直接打不开这种整体偏移的文件，必须走抠取那一档）。
                byte[] prefix = new byte[PrefixBytes];
                new Random(20261005).NextBytes(prefix);

                byte[] combined = new byte[prefix.Length + containerBytes.Length];
                Array.Copy(prefix, 0, combined, 0, prefix.Length);
                Array.Copy(containerBytes, 0, combined, prefix.Length, containerBytes.Length);

                File.WriteAllBytes(Path.Combine(outerContent, name), combined);

                File.Delete(container);
            }

            string outer = Path.Combine(work, "外层.7z");
            Run7z("a", "-t7z", "-mx0", outer, Path.Combine(outerContent, "*"));

            return (outer, "三卷里的真内容.txt", new[] { "HK.7z.002", "HK.7z.003", "HK.7z.00删除1" });
        }

        /// <summary>
        /// 造一个外层 7z，里面是一片孤零零的第 1 卷（凑不齐那一档）。
        /// <paramref name="withLastVolume"/> = 连末卷也一起给（于是**缺的是中间那一卷** ⇒ 点得出名）。
        /// </summary>
        private (string Outer, string FirstVolumeName) BuildOuterWithLoneFirstVolume(bool withLastVolume = false)
        {
            string work = Path.Combine(_root, withLastVolume ? "免试-点名" : "免试");
            string inner = Path.Combine(work, "内层");
            string payload = Path.Combine(inner, "payload");

            Directory.CreateDirectory(payload);

            byte[] data = new byte[5 * 1024 * 1024 / 2];
            new Random(20261006).NextBytes(data);
            File.WriteAllBytes(Path.Combine(payload, "big.bin"), data);

            Run7z("a", "-t7z", "-mx0", "-v1m", Path.Combine(inner, "HK.7z"), Path.Combine(payload, "*"));

            string outerContent = Path.Combine(work, "外层内容");
            Directory.CreateDirectory(outerContent);

            // 只把第 1 卷（需要时加末卷）放进外层，中间那片一个都不给它。
            File.Move(
                Path.Combine(inner, "HK.7z.001"),
                Path.Combine(outerContent, "HK.7z.001"));

            if (withLastVolume)
            {
                File.Move(
                    Path.Combine(inner, "HK.7z.003"),
                    Path.Combine(outerContent, "HK.7z.003"));
            }

            string outer = Path.Combine(work, "外层.7z");

            Run7zIn(
                outerContent,
                "a",
                "-t7z",
                "-mx0",
                outer,
                withLastVolume ? new[] { "HK.7z.001", "HK.7z.003" } : new[] { "HK.7z.001" });

            File.Delete(Path.Combine(inner, "HK.7z.002"));
            File.Delete(Path.Combine(inner, "HK.7z.003"));

            return (outer, "HK.7z.001");
        }

        private static string Sha256Of(string path)
        {
            using FileStream stream = File.OpenRead(path);

            return Convert.ToHexString(SHA256.HashData(stream));
        }

        private static string? FindFileUnder(string root, string fileName)
        {
            if (!Directory.Exists(root))
            {
                return null;
            }

            return Directory
                .GetFiles(root, fileName, SearchOption.AllDirectories)
                .FirstOrDefault();
        }

        private void RequireSevenZip()
        {
            if (string.IsNullOrEmpty(_sevenZip))
            {
                throw new InvalidOperationException(
                    "找不到内置 7z.exe，且 [SevenZipFact] 没有把它跳过：环境与特性探测结果不一致。");
            }
        }

        /// <summary>跑一次内置 7-Zip（造夹具用；与生产引擎无关）。参数里可以塞一个字符串集合（多值参数）。</summary>
        private void Run7z(params object[] args) => Run7zCore(_root, args);

        /// <summary>
        /// 同上，但**指定工作目录** —— "归档里存的名字"跟着它走：
        /// 要裸文件名（不带目录）就得把工作目录设到那一层去。
        /// </summary>
        private void Run7zIn(string workDirectory, params object[] args) => Run7zCore(workDirectory, args);

        private void Run7zCore(string workDirectory, object[] args)
        {
            var psi = new ProcessStartInfo(_sevenZip!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDirectory
            };

            foreach (object arg in args)
            {
                if (arg is IEnumerable<string> many)
                {
                    foreach (string one in many)
                    {
                        psi.ArgumentList.Add(one);
                    }

                    continue;
                }

                psi.ArgumentList.Add(Convert.ToString(arg) ?? string.Empty);
            }

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("起不来 7z.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"7z {string.Join(' ', psi.ArgumentList)} 退出码 {process.ExitCode}：{stdout}{stderr}");
            }
        }

        /// <summary>
        /// 记账用的引擎包装：把每一次 <c>ListAsync</c> / <c>ExtractAsync</c> 按**文件名**记一笔，
        /// 其余原样转调真引擎（外部行为一个字不改）。
        /// </summary>
        private sealed class CountingEngine : IArchiveEngine
        {
            private readonly IArchiveEngine _inner;
            private readonly List<string> _calls = new();

            public CountingEngine(IArchiveEngine inner) => _inner = inner;

            public int CountFor(string fileName) => _calls.Count(
                name => string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase));

            public string Id => _inner.Id;

            public string DisplayName => _inner.DisplayName;

            public string Version => _inner.Version;

            public bool IsAvailable => _inner.IsAvailable;

            public EngineCapabilities Capabilities => _inner.Capabilities;

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return _inner.ProbeAsync(request, cancellationToken);
            }

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                _calls.Add(Path.GetFileName(request.ArchivePath ?? string.Empty));

                return _inner.ListAsync(request, cancellationToken);
            }

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                _calls.Add(Path.GetFileName(request.ArchivePath ?? string.Empty));

                return _inner.TestAsync(request, cancellationToken);
            }

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                _calls.Add(Path.GetFileName(request.ArchivePath ?? string.Empty));

                return _inner.ExtractAsync(request, options, cancellationToken);
            }
        }
    }
}









