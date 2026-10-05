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
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// <b>2026-10-05 真机日志审计（`ArchiveFixer-本次操作_20261005_134118.txt`，一次成功的一键处理跑了 21:34）之后
    /// 的第一批修复</b>。用户最不满的是**浪费时间**，其次是"假 ERROR"与"说话不对"。
    ///
    /// <list type="number">
    /// <item><description><b>T1/T4/T9 双面文件</b>：尾部归档里只有**一个原样存**的条目时，
    /// 直接按偏移把那一段取出来（一次写、一次读、**一次引擎都不调**）；判不出来原样走"抠副本 + 引擎"，
    /// 但那一步之前不再写假 ERROR，并且补一行"正在按偏移取出归档副本"的进度。</description></item>
    /// <item><description><b>T2 密码预检</b>：探针按**归档顺序靠前**挑，不再挑全局最小的那个
    /// （真机为了一个 173 字节的条目把 6.84 GB 的 solid 包整解了一遍 = 56 秒）。</description></item>
    /// <item><description><b>T5 预检文案</b>：清单里看不出内层包时不许说"没有内层包"。</description></item>
    /// <item><description><b>T6 结果校验</b>：实际多于预期时要点名多出来的是谁。</description></item>
    /// <item><description><b>T8 收尾扫描</b>：不扫「其余物」。</description></item>
    /// </list>
    ///
    /// <para>⚠ 本类跑真 7z、读 <c>RecursiveExtractor.ConfiguredWorkspaceRoot</c>（进程级静态），
    /// 必须与改写它的用例类**串行**：约定与 <c>InnerDoubleFacedCarveTests</c> 顶部那段说明同一条。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class RealMachine20261005FixesTests : IDisposable
    {
        /// <summary>7-Zip 容忍的前置数据上限 8 MiB（实测边界）—— 9 MiB 必然落到"必须抠出来"那一档。</summary>
        private const int PrefixBytes = 9 * 1024 * 1024;

        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        private readonly string _root;
        private readonly string? _sevenZip = SevenZipFactAttribute.LocateSevenZipPath();
        private readonly ITestOutputHelper _output;

        public RealMachine20261005FixesTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "af-rm1005-" + Guid.NewGuid().ToString("N"));
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

        // ================================================================ T1 + T4：一次写、零引擎调用

        /// <summary>
        /// <b>T1 + T4</b>：尾部归档里**只有一个原样存的条目** ⇒ 直接按偏移把那一段取出来。
        ///
        /// <para>三条断言各钉一件事：① **引擎一次都没被调去解这个 mp4**（数调用次数，⛔ 不用计时）；
        /// ② 产物**逐字节相同**（SHA256 对夹具里那份原始内容）；③ 日志里那三行假 ERROR 的形状**不许出现**，
        /// 取而代之的是"只有一个条目……直接取出来"那行 INFO。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 双面内层包_尾部只有一个原样存的条目_直接取出来_一次也不解_且产物逐字节相同()
        {
            RequireSevenZip();

            (string doubleFaced, byte[] payload) = BuildDoubleFacedInnerPackage(stored: true);
            string outer = BuildOuterArchive(doubleFaced);

            string payloadHash = Convert.ToHexString(SHA256.HashData(payload));
            string sourceHashBefore = Sha256Of(doubleFaced);

            (RecursionResult result, List<(string Level, string Message)> log, CountingEngine engine) =
                await RunRecursionAsync(outer);

            foreach ((string level, string message) in log)
            {
                _output.WriteLine($"[{level}] {message}");
            }

            Assert.True(result.Completed, result.Summary);

            // ① 引擎**没有被调用去解那一次**（那一次会写盘、会产生三条假 ERROR 里的第二条）。
            //    ⚠ 只读的 list 照旧有一次（它是"引擎读不读得动这一份"的唯一判据，也是这一层清单的来源）——
            //    近路的挂点正在它之后，⛔ 不许抢在引擎前面。
            //    ⚠ 比的是**文件名**：递归层解的是工作区里那一份（`layer-000\output\…`），不是原路径。
            string mp4Name = Path.GetFileName(doubleFaced);

            Assert.DoesNotContain(
                engine.ExtractCalls,
                path => string.Equals(Path.GetFileName(path), mp4Name, StringComparison.OrdinalIgnoreCase));
            Assert.Single(
                engine.ListCalls,
                path => string.Equals(Path.GetFileName(path), mp4Name, StringComparison.OrdinalIgnoreCase));

            // ② 产物逐字节相同（与夹具里那份原始内容比 SHA256）。
            string? produced = FindFileUnder(result.FinalOutputPath, "内层片.bin");
            Assert.NotNull(produced);
            Assert.Equal(payloadHash, Sha256Of(produced!));

            // ③ 说话对不对：新那行 INFO 在；旧那行"解压失败：7-Zip 无法识别或不支持该格式"不在。
            Assert.Contains(
                log,
                entry => entry.Message.Contains("只有一个条目", StringComparison.Ordinal));
            Assert.DoesNotContain(
                log,
                entry => entry.Level == "ERROR"
                         && entry.Message.Contains("无法识别或不支持该格式", StringComparison.Ordinal));
            Assert.DoesNotContain(
                log,
                entry => entry.Message.Contains("已按偏移把归档那一段取出成副本", StringComparison.Ordinal));

            // ④ 源文件一个字节都没动。
            Assert.Equal(sourceHashBefore, Sha256Of(doubleFaced));
        }

        /// <summary>
        /// <b>对照 + T9</b>：同形状，但尾部归档里那个条目是**压缩存**的（不是原样存）
        /// ⇒ 近路**不许**走（解出来还得解压），照旧"抠出副本 + 引擎"，而且抠取之前要有一行进度。
        ///
        /// <para>它同时钉住两件事：① 近路的判据没有放宽（压缩存的一律交回老路）；
        /// ② 真机那 48–83 秒的抠取不再是一片空白。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 对照_尾部条目是压缩存的_照旧抠副本交给引擎_而且抠取前有一行进度()
        {
            RequireSevenZip();

            (string doubleFaced, byte[] payload) = BuildDoubleFacedInnerPackage(stored: false);
            string outer = BuildOuterArchive(doubleFaced);

            string payloadHash = Convert.ToHexString(SHA256.HashData(payload));

            (RecursionResult result, List<(string Level, string Message)> log, CountingEngine engine) =
                await RunRecursionAsync(outer);

            foreach ((string level, string message) in log)
            {
                _output.WriteLine($"[{level}] {message}");
            }

            Assert.True(result.Completed, result.Summary);

            // 老路：抠出副本 + 引擎解（引擎至少被调过一次解压）。
            string mp4Name = Path.GetFileName(doubleFaced);

            Assert.Contains(
                engine.ExtractCalls,
                path => string.Equals(Path.GetFileName(path), mp4Name, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                log,
                entry => entry.Message.Contains("已按偏移把归档那一段取出成副本", StringComparison.Ordinal));
            Assert.DoesNotContain(
                log,
                entry => entry.Message.Contains("只有一个条目", StringComparison.Ordinal));

            // T9：抠取之前那行进度（⛔ 不报百分比，只说清在做什么、大概多少量）。
            Assert.Contains(
                log,
                entry => entry.Message.Contains("正在按偏移取出归档副本", StringComparison.Ordinal));

            // T4：**我们那句结论**不许再报 ERROR（真机那三行假 ERROR 就是它）——
            // 引擎自己的原话（`7-Zip 原话：… Cannot open the file as archive`）照旧留一行，
            // 那是"失败必须留痕"的既有口径，不在这次要修的范围里。
            Assert.DoesNotContain(
                log,
                entry => entry.Level == "ERROR"
                         && entry.Message.Contains("解压失败：7-Zip 无法识别或不支持该格式", StringComparison.Ordinal));

            // 内容照旧正确。
            string? produced = FindFileUnder(result.FinalOutputPath, "内层片.bin");
            Assert.NotNull(produced);
            Assert.Equal(payloadHash, Sha256Of(produced!));
        }

        // ================================================================ T2：探针按归档顺序挑

        /// <summary>
        /// <b>T2</b>：真机那次"为一个 173 字节的条目把 6.84 GB 的 solid 包整解一遍（56 秒）"。
        /// 代价只与"它前面还有多少数据"有关 ⇒ 探针必须**优先挑归档顺序里靠前的条目**，
        /// 而不是全局最小的那一个。
        /// </summary>
        [Fact]
        public void 探针_最小的条目排在很后面时_改挑靠前的那个()
        {
            // 真机那 173 字节的说明文件深在 6.84 GB 的 solid 包后面 —— 这里用"排在第 4 位"代表"在窗口之外"。
            ArchiveListResult list = Listing(
                ("第一个.bin", 2L * 1024 * 1024),
                ("第二个.bin", 3L * 1024 * 1024),
                ("第三个.bin", 4L * 1024 * 1024),
                ("深在最后的小说明.txt", 173L));

            // 旧口径会挑最后那个 173 字节的（真机 56 秒的来源）；新口径只在前 3 个合格条目里挑最小的。
            Assert.Equal("第一个.bin", PasswordProbe.ChooseProbeEntry(list));
        }

        /// <summary>窗口内的**最小**那一个照旧优先（"第一个，或前几个里最小的"）。</summary>
        [Fact]
        public void 探针_前几个里有更小的_挑其中最小的那一个()
        {
            ArchiveListResult list = Listing(
                ("第一个.bin", 4L * 1024 * 1024),
                ("第二个.bin", 1024L),
                ("第三个.bin", 2048L),
                ("第四个.bin", 512L));

            Assert.Equal("第二个.bin", PasswordProbe.ChooseProbeEntry(list));
        }

        /// <summary>对照：一个合格的都没有 ⇒ 照旧什么都不挑（调用方退回整包试解）。</summary>
        [Fact]
        public void 探针_没有合格条目时照旧不硬凑()
        {
            ArchiveListResult list = ListingWithDirectories(
                ("目录", 0L, true),
                ("太大.bin", 64L * 1024 * 1024, false),
                ("通配符[1].txt", 512L, false));

            Assert.Null(PasswordProbe.ChooseProbeEntry(list));
        }

        // ================================================================ T5：预检文案

        /// <summary>
        /// <b>T5</b>：第 0 层预检那句"没有内层包"是假话（真机那个包里有 3 层内层包，
        /// 只是第 0 层清单里那三个 <c>.mp4</c> 的名字看不出是归档）。
        /// ⛔ 只改措辞，预算口径一个字不动（这一档本来就是 0 净增量）。
        /// </summary>
        [Fact]
        public void 空间预检_按名字看不出内层包时不许说没有内层包()
        {
            ArchiveListResult list = Listing(("4K (11)_2.mp4", 2_863_006_596L), ("说明.txt", 173L));

            TaskSpaceEstimate estimate = SpaceEstimator.RefineWithListing(
                new TaskSpaceEstimate { TaskPath = "HK.7z.001", SourceBytes = 100 },
                list);

            Assert.Contains("按名字看不出内层包", estimate.Basis, StringComparison.Ordinal);
            Assert.DoesNotContain("没有内层包", estimate.Basis, StringComparison.Ordinal);

            // ⛔ 预算口径一个字没改：名字看不出内层包 ⇒ 那一份净增量还是 0。
            Assert.Equal(0, estimate.ProcessArtifactBytes);
        }

        // ================================================================ T6：结果校验多说一句

        /// <summary>
        /// <b>T6</b>：真机那行 `校验通过：预期 851 个文件 / …，实际 855 个 / …` 判了通过却没说多出来的是什么。
        /// 现在要点名（多出来的是别的层留下、仍在产物树里的内容物），并说清"实际多于预期也算通过"。
        /// </summary>
        [Fact]
        public void 结果校验_实际多于预期时要点名多出来的是谁()
        {
            string output = Path.Combine(_root, "surplus");
            Directory.CreateDirectory(Path.Combine(output, "别的层留下的"));
            File.WriteAllText(Path.Combine(output, "这一层的.txt"), "本层的", Utf8NoBom);
            File.WriteAllText(Path.Combine(output, "别的层留下的", "上一层.txt"), "别的层的", Utf8NoBom);
            File.WriteAllText(Path.Combine(output, "别的层留下的", "又一层.txt"), "别的层的", Utf8NoBom);

            ArchiveListResult expected = Listing(("这一层的.txt", Encoding.UTF8.GetByteCount("本层的")));

            OutputVerificationResult result = OutputVerifier.Verify(output, expected);

            Assert.True(result.Verified, result.Message);
            Assert.Contains("实际比预期多 2 个文件", result.Message, StringComparison.Ordinal);
            Assert.Contains("别的层解出来", result.Message, StringComparison.Ordinal);
            Assert.Contains("上一层.txt", result.Message, StringComparison.Ordinal);
            Assert.Contains("多于预期也算通过", result.Message, StringComparison.Ordinal);

            // 对照：不多的时候一个字都不许多说（不然每一条成功日志都被撑长）。
            string exact = Path.Combine(_root, "exact");
            Directory.CreateDirectory(exact);
            File.WriteAllText(Path.Combine(exact, "只有一个.txt"), "只有这个", Utf8NoBom);

            OutputVerificationResult clean = OutputVerifier.Verify(
                exact,
                Listing(("只有一个.txt", Encoding.UTF8.GetByteCount("只有这个"))));

            Assert.True(clean.Verified, clean.Message);
            Assert.DoesNotContain("实际比预期多", clean.Message, StringComparison.Ordinal);
        }

        // ================================================================ T8：收尾扫描不看其余物

        /// <summary>
        /// <b>T8</b>：真机收尾那次"第 1 层产物扫描"把 <c>其余物\HK.7z.001/.002</c>（**本批的源包**）
        /// 也数进了"新文件 857 个"、还各写一行"跳过" —— 它们永远不可能是内层包候选。
        ///
        /// <para>⛔ 对照那半边同样重要：**其余物里没登记过的包照旧要扫**（默认档续解链的输入，
        /// 上一层产出的内层包就是收进其余物之后才被认出来当下一个任务的）。整棵排除「其余物」
        /// 会让 <c>InnerLayerContinuationTests</c> 那 18 条当场红 —— 这一条把两个方向一起钉住。</para>
        /// </summary>
        [Fact]
        public void 收尾扫描_已知的源包与已登记任务不计数_但其余物里没登记过的包照旧要扫()
        {
            string rest = Path.Combine("H:\\out\\HK", "其余物");

            var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(rest, "HK.7z.001")
            };

            var knownTaskPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(rest, "HK.7z.002")
            };

            // ① 本批的源包 / 已经登记过的路径：不进统计、不写"跳过"（真机那两行）。
            Assert.False(OneClickCoordinator.ShouldScanProducedFile(
                Path.Combine(rest, "HK.7z.001"), sourcePaths, knownTaskPaths));
            Assert.False(OneClickCoordinator.ShouldScanProducedFile(
                Path.Combine(rest, "HK.7z.002"), sourcePaths, knownTaskPaths));

            // ② ⛔ 其余物里**没登记过**的包照旧要扫（否则默认档续解链断掉）。
            Assert.True(OneClickCoordinator.ShouldScanProducedFile(
                Path.Combine(rest, "上一层产出的内层包.7z.001"), sourcePaths, knownTaskPaths));

            // ③ 内容物当然照旧要扫。
            Assert.True(OneClickCoordinator.ShouldScanProducedFile(
                Path.Combine("H:\\out\\HK", "内容", "真内容.txt"), sourcePaths, knownTaskPaths));
        }

        // ================================================================ 夹具与工具

        /// <summary>
        /// 造"双面文件"：<c>[9 MiB 假视频][一个完整 ZIP]</c>，ZIP 里**恰好一个条目**
        /// <c>内层片.bin</c>（<paramref name="stored"/> = true 时用 <c>-mx0</c> 原样存）。
        /// </summary>
        private (string Path, byte[] Payload) BuildDoubleFacedInnerPackage(bool stored)
        {
            string payloadDirectory = Path.Combine(_root, stored ? "payload-stored" : "payload-deflate");
            Directory.CreateDirectory(payloadDirectory);

            byte[] payload = new byte[2 * 1024 * 1024];

            if (stored)
            {
                new Random(20261005).NextBytes(payload);
            }
            else
            {
                /*
                 * ⚠ 对照那一档的数据必须**压得动**：7-Zip 对"压完反而更大"的内容会自动改回原样存，
                 * 那样这一档就退化成"原样存"、近路照样命中，对照就失去意义了（第一版就是这么红的）。
                 */
                for (int i = 0; i < payload.Length; i++)
                {
                    payload[i] = (byte)('A' + (i % 7));
                }
            }

            File.WriteAllBytes(Path.Combine(payloadDirectory, "内层片.bin"), payload);

            string zip = Path.Combine(_root, stored ? "尾部-stored.zip" : "尾部-deflate.zip");

            if (stored)
            {
                Run7z(payloadDirectory, "a", "-tzip", "-mx0", zip, "内层片.bin");
            }
            else
            {
                Run7z(payloadDirectory, "a", "-tzip", zip, "内层片.bin");
            }

            byte[] zipBytes = File.ReadAllBytes(zip);
            byte[] prefix = new byte[PrefixBytes];
            new Random(20261004).NextBytes(prefix);

            byte[] combined = new byte[prefix.Length + zipBytes.Length];
            Array.Copy(prefix, 0, combined, 0, prefix.Length);
            Array.Copy(zipBytes, 0, combined, prefix.Length, zipBytes.Length);

            string path = Path.Combine(_root, stored ? "4K (11)_2.mp4" : "4K (13)_3.mp4");
            File.WriteAllBytes(path, combined);

            return (path, payload);
        }

        /// <summary>把双面文件装进一个真 7z（递归解压要从这里出发去探内层包）。</summary>
        private string BuildOuterArchive(string doubleFaced)
        {
            string stage = Path.Combine(_root, "外层内容-" + Path.GetFileNameWithoutExtension(doubleFaced));
            Directory.CreateDirectory(stage);
            File.Copy(doubleFaced, Path.Combine(stage, Path.GetFileName(doubleFaced)), overwrite: true);

            string outer = Path.Combine(_root, Path.GetFileNameWithoutExtension(doubleFaced) + "-外层.7z");
            Run7z(stage, "a", "-t7z", outer, Path.GetFileName(doubleFaced));

            return outer;
        }

        /// <summary>跑真实生产路径：递归核心 + 真 7-Zip（外面套一层数调用次数的装饰器）。</summary>
        private async Task<(RecursionResult Result, List<(string Level, string Message)> Log, CountingEngine Engine)>
            RunRecursionAsync(string outer)
        {
            string output = Path.Combine(_root, "out-" + Guid.NewGuid().ToString("N"));
            var log = new List<(string Level, string Message)>();
            var engine = new CountingEngine(new SevenZipEngine());

            var extractor = new RecursiveExtractor(
                engine,
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

            return (result, log, engine);
        }

        private static string? FindFileUnder(string root, string fileName)
        {
            return !Directory.Exists(root)
                ? null
                : Directory.GetFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
        }

        private static string Sha256Of(string path)
        {
            using FileStream stream = File.OpenRead(path);

            return Convert.ToHexString(SHA256.HashData(stream));
        }

        private static ArchiveListResult Listing(params (string Path, long Size)[] entries)
        {
            return ListingWithDirectories(
                entries.Select(entry => (entry.Path, entry.Size, false)).ToArray());
        }

        private static ArchiveListResult ListingWithDirectories(params (string Path, long Size, bool IsDirectory)[] entries)
        {
            var list = new List<ArchiveEntry>();

            foreach ((string path, long size, bool isDirectory) in entries)
            {
                list.Add(new ArchiveEntry { Path = path, Size = size, IsDirectory = isDirectory });
            }

            return new ArchiveListResult
            {
                Success = true,
                FileCount = list.Count(entry => !entry.IsDirectory),
                TotalUncompressedSize = list.Sum(entry => entry.Size),
                Entries = list
            };
        }

        private void RequireSevenZip()
        {
            if (string.IsNullOrEmpty(_sevenZip))
            {
                throw new InvalidOperationException(
                    "找不到内置 7z.exe，且 [SevenZipFact] 没有把它跳过：环境与特性探测结果不一致。");
            }
        }

        /// <summary>跑一次内置 7-Zip（造夹具用；与生产引擎无关）。</summary>
        private void Run7z(string workDirectory, params string[] args)
        {
            var psi = new ProcessStartInfo(_sevenZip!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDirectory
            };

            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
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

        /// <summary>数调用次数的引擎装饰器（⛔ 判"引擎有没有被调去解那一次"只数次数，不靠计时）。</summary>
        private sealed class CountingEngine : IArchiveEngine
        {
            private readonly IArchiveEngine _inner;

            public CountingEngine(IArchiveEngine inner)
            {
                _inner = inner;
            }

            public List<string> ListCalls { get; } = new();

            public List<string> ExtractCalls { get; } = new();

            public string Id => _inner.Id;

            public string DisplayName => _inner.DisplayName;

            public string Version => _inner.Version;

            public bool IsAvailable => _inner.IsAvailable;

            public EngineCapabilities Capabilities => _inner.Capabilities;

            public Task<ArchiveProbeResult> ProbeAsync(
                ArchiveRequest request,
                CancellationToken cancellationToken = default)
            {
                return _inner.ProbeAsync(request, cancellationToken);
            }

            public Task<ArchiveListResult> ListAsync(
                ArchiveRequest request,
                CancellationToken cancellationToken = default)
            {
                ListCalls.Add(request.ArchivePath);

                return _inner.ListAsync(request, cancellationToken);
            }

            public Task<ArchiveOperationResult> TestAsync(
                ArchiveRequest request,
                CancellationToken cancellationToken = default)
            {
                return _inner.TestAsync(request, cancellationToken);
            }

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                ExtractCalls.Add(request.ArchivePath);

                return _inner.ExtractAsync(request, options, cancellationToken);
            }
        }
    }
}
