using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **7z 起始头自述的"整包应当有多少字节"**（方案《分卷族系统化识别》§4 阶段 B 的 (d)）。
    ///
    /// <para>7z 的起始头 32 字节是**明文**（<c>-mhe</c> 也不加密），里面写着
    /// <c>NextHeaderOffset</c> / <c>NextHeaderSize</c>，而 7z 的布局是"起始头 + 打包流 + next header"
    /// 首尾相接 ⇒ <c>32 + offset + size</c> = **整包应当有的字节数**（与卷序无关）。</para>
    ///
    /// <para>三档各有各的用处（⛔ 全都只读，⛔ 不拿它猜"哪一卷是第几卷"）：</para>
    /// <list type="number">
    /// <item><description><b>正好相等</b> ⇒ 字节数这一条证据成立（"整组齐了"），顺序仍由名字 / 试开回答；</description></item>
    /// <item><description><b>少于</b> ⇒ 缺卷，如实报"还差多少字节"，而且**一次引擎都不用调**；</description></item>
    /// <item><description><b>多于</b> ⇒ 候选池里混进了不属于这一组的文件 ⇒ **缩池重来**
    /// （只接受唯一说得通的那种缩法；缩不出唯一答案就如实报"多出多少字节"、一个字节不动）。</description></item>
    /// </list>
    /// </summary>
    public class SevenZipStartHeaderCompletenessTests : IDisposable
    {
        /// <summary>2.5 MiB：<c>-v1m</c> 正好三卷（1 MiB + 1 MiB + 0.5 MiB）。</summary>
        private const int PayloadBytes = 2621440;

        private readonly string _root;
        private readonly string? _sevenZip;
        private readonly ITestOutputHelper _output;

        public SevenZipStartHeaderCompletenessTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerStartHeader", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _sevenZip = SevenZipFactAttribute.LocateSevenZipPath();
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

        /// <summary>三档的纯读数：正好 / 少了末卷 / 多了一份不相干的文件。</summary>
        [SevenZipFact]
        public void 起始头三档_正好_少于_多于()
        {
            string directory = NewThreeVolumeSample(out string first, out string nameless, out string third);

            long volumesTotal = new FileInfo(first).Length
                                + new FileInfo(nameless).Length
                                + new FileInfo(third).Length;

            Assert.True(SevenZipStartHeader.TryReadExpectedTotalBytes(first, out long expected));
            Assert.Equal(volumesTotal, expected);

            // ① 三卷都在 ⇒ 正好（与顺序无关：Σ 就是 Σ）。
            SevenZipByteBudget exact = SevenZipStartHeader.Measure(first, VolumesOf(first));

            _output.WriteLine($"[正好] {exact.Verdict}｜{exact.ExpectedBytes} / {exact.ActualBytes}");

            Assert.True(exact.Known);
            Assert.Equal(SevenZipByteBudgetVerdict.Exact, exact.Verdict);
            Assert.Equal(0, exact.DifferenceBytes);

            // ② 拿掉末卷 ⇒ 少于，而且差的就是末卷那几字节。
            // ⚠ 必须移出**这个目录**：同目录里留一份等长的副本，候选池会照旧把它算进来（Σ 不变）。
            long thirdLength = new FileInfo(third).Length;
            string parked = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".parked");

            File.Move(third, parked);

            SevenZipByteBudget shortOne = SevenZipStartHeader.Measure(first, VolumesOf(first));

            _output.WriteLine($"[少于] {shortOne.Verdict}｜差 {shortOne.DifferenceBytes}");

            Assert.Equal(SevenZipByteBudgetVerdict.Short, shortOne.Verdict);
            Assert.Equal(thirdLength, shortOne.DifferenceBytes);

            File.Move(parked, third);

            // ③ 多一份等长的不相干文件 ⇒ 多于，多出来的正是它。
            string extra = Path.Combine(directory, "111.extra");

            File.WriteAllBytes(extra, new byte[thirdLength]);

            SevenZipByteBudget excess = SevenZipStartHeader.Measure(first, VolumesOf(first));

            _output.WriteLine($"[多于] {excess.Verdict}｜多 {excess.DifferenceBytes}");

            Assert.Equal(SevenZipByteBudgetVerdict.Excess, excess.Verdict);
            Assert.Equal(thirdLength, excess.DifferenceBytes);

            // ⛔ 读不出起始头时什么都不许断言（这一档是默认档）。
            Assert.False(SevenZipStartHeader.TryReadExpectedTotalBytes(extra, out long none));
            Assert.Equal(0, none);
            Assert.False(SevenZipStartHeader.Measure(extra, new[] { extra }).Known);
        }

        /// <summary>
        /// **多于 ⇒ 缩池重来**（端到端）：一组两卷的 7z，名字丢了的末卷叫 <c>111</c>，
        /// 同目录里还躺着一份**同尺寸但不相干**的满片 <c>333</c>。
        ///
        /// <para>不缩池的话，候选池里"最像末片"的是那份更短的 <c>111</c>、而满片是 <c>333</c> ⇒
        /// 假设顺序把 <c>333</c> 当第 2 卷 ⇒ 引擎列不出东西 ⇒ 什么都不做（老行为，白烧一次引擎）。
        /// 起始头一算：少了/多了摆在那儿 —— 去掉 <c>333</c> 之后**正好**等于整包字节数，
        /// 而且是唯一说得通的那种缩法 ⇒ 用它重来 ⇒ 试开成立 ⇒ 改名计划正确。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 多于_缩池重来_去掉不相干的满片之后计划正确()
        {
            string directory = NewTwoVolumeSample(out string first, out string second);

            // 末卷的名字整个丢了。
            string nameless = Path.Combine(directory, "111");

            File.Move(second, nameless);

            // 一份同尺寸（= 满片大小）、**认不出归档**的不相干文件 —— 它会被 BuildCandidates 收进候选池。
            string extra = Path.Combine(directory, "333");
            var filler = new byte[new FileInfo(first).Length];
            new Random(20261005).NextBytes(filler);
            File.WriteAllBytes(extra, filler);

            Assert.Equal(
                VolumeContentFormat.Unknown,
                VolumeContentInference.SniffFormat(extra));

            long total = new FileInfo(first).Length + new FileInfo(nameless).Length;

            Assert.True(SevenZipStartHeader.TryReadExpectedTotalBytes(first, out long expected));
            Assert.Equal(total, expected);

            VolumeNameRepairPlan plan = await VolumeNameRepair.PlanByContentAsync(
                first,
                VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(first),
                NewEngine(),
                Path.Combine(directory, VolumeContentInference.WorkDirectoryName));

            _output.WriteLine($"[缩池重来] CanRepair={plan.CanRepair}｜Suggested=[{plan.SuggestedFileName}]｜Reason=[{plan.Reason}]");
            _output.WriteLine($"[缩池重来] Items=[{string.Join(" | ", plan.Items.Select(i => i.CurrentFileName + " → " + i.SuggestedFileName))}]");

            Assert.True(plan.CanRepair, $"这一档缩池之后应当能出计划：{plan.Reason}");
            Assert.Equal(2, plan.Items.Count);
            Assert.Equal(
                new[] { "111.7z.001→111.7z.001", "111→111.7z.002" },
                plan.Items.Select(i => i.CurrentFileName + "→" + i.SuggestedFileName));

            // 不相干的那一份留在原地、一个字节没动（⛔ 它不进组、也不改名）。
            Assert.True(File.Exists(extra));
            Assert.Equal(filler, File.ReadAllBytes(extra));

            VolumeNameRepairResult result = VolumeNameRepair.TryApply(plan);

            Assert.True(result.Success, result.Message);
            Assert.True(File.Exists(Path.Combine(directory, "111.7z.002")));
            Assert.False(File.Exists(nameless));
            Assert.True(File.Exists(extra));
            Assert.Equal(filler, File.ReadAllBytes(extra));
        }

        // ── 样本 ──

        /// <summary>
        /// 这一组"手上这几卷"的清单：锚点 + 同目录里够格当续卷的那些（候选池的唯一出口
        /// <see cref="VolumeContentInference.BuildCandidates"/>，它已经把锚点自己与"认得出归档魔数的另一包"
        /// 排除在外）—— 与生产代码量的是同一份东西。
        /// </summary>
        private static IReadOnlyList<string?> VolumesOf(string anchorPath) =>
            new[] { anchorPath }
                .Concat(VolumeContentInference
                    .BuildCandidates(anchorPath, VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(anchorPath))
                    .Select(c => c.Path))
                .ToList();

        private string NewThreeVolumeSample(out string first, out string nameless, out string third)
        {
            string directory = NewDirectory();

            WritePayload(directory, PayloadBytes, 20261003);
            Run7z(directory, "a", "-t7z", "-mx0", "-v1m", "111.7z", "data.bin");

            first = Path.Combine(directory, "111.7z.001");
            nameless = Path.Combine(directory, "111");
            third = Path.Combine(directory, "111.7z.003");

            Assert.True(File.Exists(third), "样本不是三卷");

            File.Move(Path.Combine(directory, "111.7z.002"), nameless);

            return directory;
        }

        /// <summary>1.5 MiB：<c>-v1m</c> 正好两卷（1 MiB + 0.5 MiB）。</summary>
        private string NewTwoVolumeSample(out string first, out string second)
        {
            string directory = NewDirectory();

            WritePayload(directory, 1572864, 20261006);
            Run7z(directory, "a", "-t7z", "-mx0", "-v1m", "111.7z", "data.bin");

            first = Path.Combine(directory, "111.7z.001");
            second = Path.Combine(directory, "111.7z.002");

            Assert.True(File.Exists(second), "样本不是两卷");
            Assert.Equal(1024 * 1024, new FileInfo(first).Length);
            Assert.True(new FileInfo(second).Length < new FileInfo(first).Length);

            return directory;
        }

        private string NewDirectory()
        {
            string directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            return directory;
        }

        private static void WritePayload(string directory, int bytes, int seed)
        {
            var payload = new byte[bytes];
            new Random(seed).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(directory, "data.bin"), payload);
        }

        private Engines.IArchiveEngine NewEngine()
        {
            var engine = new Engines.SevenZip.SevenZipEngine();

            Assert.True(engine.IsAvailable, "内置 7z.exe 不可用，这一组用例跑不起来");

            return engine;
        }

        private void Run7z(string workingDirectory, params string[] args)
        {
            if (string.IsNullOrEmpty(_sevenZip))
            {
                throw new InvalidOperationException("测试机上没有 7z.exe");
            }

            var psi = new ProcessStartInfo(_sevenZip)
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

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 7z.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            Assert.True(process.WaitForExit(120_000), "7z 超时");
            Assert.True(process.ExitCode == 0, $"7z 失败：{stdout}{stderr}");
        }
    }
}
