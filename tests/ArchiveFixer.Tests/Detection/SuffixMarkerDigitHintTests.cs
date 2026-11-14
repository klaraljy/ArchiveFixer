using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **「后缀段里的数字」的闸门**（方案《分卷族系统化识别》§1.3 第 3 层 / §4 阶段 B 的 (e)）。
    ///
    /// <para>口径三条，缺一不可：</para>
    /// <list type="number">
    /// <item><description>后缀段里的数字**只有**在同目录能配出一组**形状自洽**的兄弟卷时才允许参与
    /// "谁是首卷 / 怎么排"的判断（基名逐字相同 + 卷标记连续 + 除末片外等大，
    /// 唯一出口 <see cref="VolumeContentInference.ReadSiblingShape"/>）；</description></item>
    /// <item><description>它**只能提候选**，最终裁决只有硬链接试开（<see cref="VolumeProbeVerifier"/>）；
    /// 试不开 / 判不出 ⇒ <b>什么都不做</b>；</description></item>
    /// <item><description>⛔ 与**基名正文**里的数字无关：<c>风景01</c> 里的 <c>01</c> 是基名正文，
    /// 不构成任何证据（卷标记段永远只由 <see cref="VolumeGroupDetector.TryGetVolumeIndex"/> 认出来）。</description></item>
    /// </list>
    ///
    /// <para>⛔ 判据顺序按 §1.3 的阶梯走：第 0 层魔数 → 第 1 层族专属（7z = 起始头 + 尺寸 + 试开）→
    /// 第 2 层名字骨架 → 第 3 层才是这条弱线索；⛔ 不许把弱线索提到内容证据之前。</para>
    /// </summary>
    public class SuffixMarkerDigitHintTests : IDisposable
    {
        private readonly string _root;
        private readonly string? _sevenZip;

        public SuffixMarkerDigitHintTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerSuffixDigit", Guid.NewGuid().ToString("N"));
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

        /// <summary>
        /// ① **对照组**：名字本来就标准的一组三卷 7z（<c>风景01.7z.001/.002/.003</c>）——
        /// 卷号由**卷标记段**给出，弱线索根本不参与；而且这一组"一片名字都没丢"，
        /// 所以入口判据不放它进内容路（⛔ 不白烧一次试开：第 45 条，成功的任务只留一行）。
        ///
        /// <para>⚠ 基名正文里那个 <c>01</c> 在这里一点作用都没有 —— 它既不是卷标记，也不参与任何判断。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 对照组_标准名三卷_卷号全由卷标记段给出_弱线索不参与()
        {
            string directory = NewDirectory();

            var payload = new byte[2621440];
            new Random(20261007).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(directory, "data.bin"), payload);

            Run7z(directory, "a", "-t7z", "-mx0", "-v1m", "风景01.7z", "data.bin");

            string first = Path.Combine(directory, "风景01.7z.001");

            Assert.True(File.Exists(Path.Combine(directory, "风景01.7z.003")), "样本不是三卷");

            // 卷标记（= 后缀段里的数字）就是从这三段名字上读出来的，与基名正文里的 `01` 无关。
            Assert.Equal(1, VolumeGroupDetector.TryGetVolumeIndex("风景01.7z.001"));
            Assert.Equal(2, VolumeGroupDetector.TryGetVolumeIndex("风景01.7z.002"));
            Assert.Equal(3, VolumeGroupDetector.TryGetVolumeIndex("风景01.7z.003"));
            Assert.Null(VolumeGroupDetector.TryGetVolumeIndex("风景01"));

            SiblingVolumeShape shape = VolumeContentInference.ReadSiblingShape(
                first,
                VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(first),
                VolumeContentInference.ExtensionFor(VolumeContentFormat.SevenZip));

            Assert.True(shape.SelfConsistent);
            Assert.Equal(new[] { 1, 2, 3 }, shape.Markers);
            Assert.Empty(shape.MissingMarkers);
            Assert.Empty(shape.NamelessFillers);

            VolumeNameRepairPlan plan = await VolumeNameRepair.PlanByContentAsync(
                first,
                VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(first),
                NewEngine(),
                Path.Combine(directory, VolumeContentInference.WorkDirectoryName));

            // 名字齐全 ⇒ 内容路什么都不做（⛔ 不去白试开一次）。
            Assert.False(plan.CanRepair);
            Assert.Empty(plan.Items);
            Assert.False(plan.TrialAttempted);
            Assert.Equal(StatusText.VolumeRepairAlreadyStandard, plan.Reason);
        }

        /// <summary>
        /// ② **后缀段里确实有 <c>1</c>、可这一组配不出自洽形状**（<c>风景01.7z.pa1rt1</c> /
        /// <c>风景02.7z.pa1rt2</c> 这种"字母数字混着塞"、而且基名还不是同一个）：
        /// 这条弱线索**只能提候选首卷**，而这里连锚点都找不到（没有一份带 7z 起始头）⇒
        /// 什么都不做，一个字节都不改。
        /// </summary>
        [Fact]
        public async Task 后缀段里有数字一_配不成自洽的一组时_判不出就什么都不做()
        {
            string directory = NewDirectory();
            string first = WriteRandom(directory, "风景01.7z.pa1rt1", 1024 * 1024);
            string second = WriteRandom(directory, "风景02.7z.pa1rt2", 512 * 1024);

            var before = new[] { first, second }
                .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

            // 两份的基名不是一个（`风景01` / `风景02`）⇒ 配不成一组 ⇒ 闸门不放行。
            SiblingVolumeShape shape = VolumeContentInference.ReadSiblingShape(
                first,
                VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(first),
                VolumeContentInference.ExtensionFor(VolumeContentFormat.SevenZip));

            Assert.False(shape.SelfConsistent);
            Assert.Empty(shape.Markers);

            VolumeNameRepairPlan plan = await VolumeNameRepair.PlanByContentAsync(
                first,
                VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(first),
                NewEngineWithoutProbeExpectation(),
                Path.Combine(directory, VolumeContentInference.WorkDirectoryName));

            Assert.False(plan.CanRepair);
            Assert.Empty(plan.Items);
            Assert.False(plan.TrialAttempted);
            Assert.Equal(StatusText.VolumeRepairContentNotAVolumeMember, plan.Reason);

            foreach ((string path, byte[] expected) in before)
            {
                Assert.True(File.Exists(path), $"源文件被动过：{path}");
                Assert.Equal(expected, File.ReadAllBytes(path));
            }
        }

        /// <summary>
        /// ③ **数字在基名里、后缀段里没有数字**（<c>风景01</c> + <c>风景01.7z.abc</c>）：
        /// ⛔ 绝不许因为基名里有 <c>01</c> 就把它认成首卷（那会把它改名成 <c>风景01.7z.001</c>）。
        /// </summary>
        [Fact]
        public async Task 数字只在基名里_绝不许认首卷()
        {
            string directory = NewDirectory();
            string plain = WriteRandom(directory, "风景01", 1024 * 1024);
            string abc = WriteRandom(directory, "风景01.7z.abc", 1024 * 1024);

            var before = new[] { plain, abc }
                .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

            Assert.Null(VolumeGroupDetector.TryGetVolumeIndex("风景01"));

            // `风景01` 连"短数字尾巴"都没有（尾巴是**后缀段**，基名正文里的 `01` 不算）：
            // 弱线索的唯一入口就在这里，⛔ 它不许去名字里找 `1`。
            Assert.Null(VolumeContentInference.TryReadShortNumberTail("风景01"));

            SiblingVolumeShape shape = VolumeContentInference.ReadSiblingShape(
                plain,
                VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(plain),
                VolumeContentInference.ExtensionFor(VolumeContentFormat.SevenZip));

            Assert.False(shape.SelfConsistent);
            Assert.Empty(shape.Markers);

            foreach (string entry in new[] { plain, abc })
            {
                VolumeNameRepairPlan plan = await VolumeNameRepair.PlanByContentAsync(
                    entry,
                    VolumeNameRepair.EnumerateVolumeCandidatesInDirectory(entry),
                    NewEngineWithoutProbeExpectation(),
                    Path.Combine(directory, VolumeContentInference.WorkDirectoryName));

                Assert.False(plan.CanRepair);
                Assert.Empty(plan.Items);
                Assert.False(plan.TrialAttempted);
            }

            foreach ((string path, byte[] expected) in before)
            {
                Assert.True(File.Exists(path), $"源文件被动过：{path}");
                Assert.Equal(expected, File.ReadAllBytes(path));
            }

            Assert.False(File.Exists(Path.Combine(directory, "风景01.7z.001")), "基名里的数字不许换来一次改名");
        }

        private string NewDirectory()
        {
            string directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            return directory;
        }

        /// <summary>写一份"认不出格式"的文件（= 7z 的续卷那种裸字节流）。</summary>
        private static string WriteRandom(string directory, string name, int bytes)
        {
            string path = Path.Combine(directory, name);
            var content = new byte[bytes];
            new Random(20261008).NextBytes(content);
            File.WriteAllBytes(path, content);

            return path;
        }

        private Engines.IArchiveEngine NewEngine()
        {
            var engine = new Engines.SevenZip.SevenZipEngine();

            Assert.True(engine.IsAvailable, "内置 7z.exe 不可用，这一组用例跑不起来");

            return engine;
        }

        /// <summary>②③ 两档预期**一次引擎都不调**（结论在"找不到锚点"这一步就下了），所以不要求内置 7z 在位。</summary>
        private static Engines.IArchiveEngine NewEngineWithoutProbeExpectation() =>
            new Engines.SevenZip.SevenZipEngine();

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
