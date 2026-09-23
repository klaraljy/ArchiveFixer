using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Packing;
using ArchiveFixer.Password;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 打包功能（用户 2026-09-22 需求第 10 条；设计 docs/打包功能.md）。
    ///
    /// <para>这一组钉住的是**判据**，不是实现细节：</para>
    /// <list type="bullet">
    /// <item><description>分卷怎么算（用户的两个例子 1 GB → 2 卷、1.5 GB → 3 卷）；</description></item>
    /// <item><description>密码必填、两次要一致、**明文不许出现在日志里**；</description></item>
    /// <item><description>没有 <c>Rar.exe</c> 时明确报错、**一个字节都不产出**、绝不用 7z 假装生成 .rar；</description></item>
    /// <item><description>落点非法（B 在 A 里、rar 在 B 里、rar 在 A 里、A 在 B 里）一律拒绝；</description></item>
    /// <item><description>空间不够**不开始**，并报出需要 / 可用 / 差多少；</description></item>
    /// <item><description>**真 7z 端到端**：1 MiB 分卷 → <c>.7z.001</c>/<c>.002</c>，对密码列得出、错密码列不出；</description></item>
    /// <item><description>取消语义：第二步取消 → 状态是"已取消"、没有 rar、**分卷还在**；</description></item>
    /// <item><description>产物校验：少一卷 / 混进别的东西 → **不算成功**（不变量 6）。</description></item>
    /// </list>
    ///
    /// <para><b>真机依赖</b>：需要真 7z 的用例在缺 <c>tools\7zip\7z.exe</c> 时打印原因后跳过
    /// （与 <c>UnRarRealSampleTests</c> 同一套写法）；需要真 <c>Rar.exe</c> 的那一条在没有
    /// WinRAR 的机器上同样跳过 —— 但"没有 Rar.exe 时会怎样"那条判据用的是**假的 runner**，
    /// 因此在任何机器上都跑得到。</para>
    /// </summary>
    public class PackingTests : IDisposable
    {
        /// <summary>测试用密码。**占位符**，不是任何真实密码（AGENTS.md §8）。</summary>
        private const string SamplePassword = "<sample-password>";

        /// <summary>另一个占位符（用来验"外层用另一个密码"）。</summary>
        private const string OuterSamplePassword = "<sample-outer-password>";

        private const long OneMebibyte = 1024L * 1024L;

        private readonly string _root;
        private readonly ITestOutputHelper _output;

        public PackingTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerPacking", Guid.NewGuid().ToString("N"));
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
                // 临时目录删不掉不影响结论，下次跑换一个 GUID 目录。
            }
        }

        // ══════════════════════════ ① 分卷规划 ══════════════════════════

        [Fact]
        public void 分卷规划_用户给的两个例子都对得上()
        {
            long gib = 1024L * 1024L * 1024L;

            // 用户原话："≤1 GB → .001+.002；>1 GB → .001/.002/.003"。
            // 唯一自洽解 = 每个分卷上限 512 MiB（docs/打包功能.md §2）。
            Assert.Equal(2, PackingPlan.ComputeVolumeCount(1 * gib, PackingPlan.DefaultVolumeSizeBytes));
            Assert.Equal(3, PackingPlan.ComputeVolumeCount(gib + gib / 2, PackingPlan.DefaultVolumeSizeBytes));
            Assert.Equal(8, PackingPlan.ComputeVolumeCount(4 * gib, PackingPlan.DefaultVolumeSizeBytes));

            Assert.Equal(512L * 1024 * 1024, PackingPlan.DefaultVolumeSizeBytes);
        }

        [Fact]
        public void 分卷规划_边界与溢出()
        {
            // 正好等于分卷大小 → 1 卷（不多切一卷出来）。
            Assert.Equal(1, PackingPlan.ComputeVolumeCount(PackingPlan.DefaultVolumeSizeBytes, PackingPlan.DefaultVolumeSizeBytes));

            // 多 1 个字节 → 2 卷。
            Assert.Equal(2, PackingPlan.ComputeVolumeCount(PackingPlan.DefaultVolumeSizeBytes + 1, PackingPlan.DefaultVolumeSizeBytes));

            // 内容为 0 → 0 卷（这种请求会被 TryCreate 拒掉，见下一条）。
            Assert.Equal(0, PackingPlan.ComputeVolumeCount(0, PackingPlan.DefaultVolumeSizeBytes));

            // 超过 4 GiB：不溢出、不变成负数（老写法 content + size - 1 在这里会回绕）。
            Assert.Equal(9, PackingPlan.ComputeVolumeCount(4 * 1024L * 1024 * 1024 + 1, PackingPlan.DefaultVolumeSizeBytes));
            Assert.True(PackingPlan.ComputeVolumeCount(long.MaxValue, OneMebibyte) > 0);

            // 空间需求：内容 × 2.02 + 32 MiB，饱和加法绝不回绕成负数。
            long fourGib = 4 * 1024L * 1024 * 1024;
            long required = PackingPlan.ComputeRequiredSpace(fourGib);

            // 两份内容（分卷一份 + 外层 rar 一份）+ 2% 压缩后估算 + 32 MiB 余量。
            Assert.True(required >= fourGib * 2, $"4 GiB 内容的需求不该少于两份（实际 {required}）");
            Assert.True(required <= fourGib * 2 + 200L * 1024 * 1024, $"余量不该大得离谱（实际 {required}）");
            Assert.True(PackingPlan.ComputeRequiredSpace(long.MaxValue) > 0, "超大内容算出来不能是负数");
        }

        [Fact]
        public void 分卷大小_界面范围与整数MiB()
        {
            Assert.Equal(string.Empty, PackingPlan.ValidateVolumeSize(PackingPlan.DefaultVolumeSizeBytes));
            Assert.Equal(string.Empty, PackingPlan.ValidateVolumeSize(PackingPlan.MinVolumeSizeBytes));
            Assert.Equal(string.Empty, PackingPlan.ValidateVolumeSize(PackingPlan.MaxVolumeSizeBytes));

            Assert.NotEqual(string.Empty, PackingPlan.ValidateVolumeSize(PackingPlan.MinVolumeSizeBytes - 1));
            Assert.NotEqual(string.Empty, PackingPlan.ValidateVolumeSize(PackingPlan.MaxVolumeSizeBytes + 1));
            Assert.NotEqual(string.Empty, PackingPlan.ValidateVolumeSize(0));
            Assert.NotEqual(string.Empty, PackingPlan.ValidateVolumeSize(1536 * 1024));
        }

        [Fact]
        public void 源文件夹为空_拒绝开始并说清原因()
        {
            string source = Path.Combine(_root, "A-empty");
            Directory.CreateDirectory(source);

            bool ok = PackingPlan.TryCreate(
                new PackingRequest
                {
                    SourceFolder = source,
                    VolumeSizeBytes = PackingPlan.DefaultVolumeSizeBytes,
                    Password = SamplePassword
                },
                out PackingPlan? plan,
                out string error);

            Assert.False(ok);
            Assert.Null(plan);
            Assert.Contains("没有可打包的内容", error, StringComparison.Ordinal);
        }

        [Fact]
        public void 默认落点_B与A同级_rar与B同级()
        {
            string source = Path.Combine(_root, "素材");
            Directory.CreateDirectory(source);
            File.WriteAllBytes(Path.Combine(source, "a.bin"), new byte[64]);

            string expectedOutput = Path.Combine(_root, "素材_打包");
            string expectedRar = Path.Combine(_root, "素材_打包.rar");

            Assert.Equal(expectedOutput, PackingPlan.DefaultOutputFolder(source));
            Assert.Equal(expectedRar, PackingPlan.DefaultRarPath(expectedOutput));

            Assert.True(PackingPlan.TryCreate(
                new PackingRequest { SourceFolder = source, Password = SamplePassword },
                out PackingPlan? plan,
                out string error), error);

            Assert.NotNull(plan);
            Assert.Equal(expectedOutput, plan!.OutputFolder);
            Assert.Equal(expectedRar, plan.RarPath);
            Assert.Equal("素材.7z", plan.VolumeBaseName);
            Assert.Equal(1, plan.PlannedVolumeCount);
        }

        // ══════════════════════════ ② 密码 ══════════════════════════

        [Fact]
        public void 密码_空与纯空格都拒绝_两次不一致也拒绝()
        {
            Assert.False(PackingPasswordPolicy.IsUsable(string.Empty));
            Assert.False(PackingPasswordPolicy.IsUsable(null));
            Assert.False(PackingPasswordPolicy.IsUsable("    "));
            Assert.False(PackingPasswordPolicy.IsUsable("\t"));

            Assert.Equal(PackingPasswordPolicy.RequiredMessage, PackingPasswordPolicy.Validate(string.Empty, string.Empty));
            Assert.Equal(PackingPasswordPolicy.RequiredMessage, PackingPasswordPolicy.Validate("   ", "   "));

            Assert.Equal(PackingPasswordPolicy.MismatchMessage, PackingPasswordPolicy.Validate("aaa", "bbb"));

            Assert.Equal(string.Empty, PackingPasswordPolicy.Validate(SamplePassword, SamplePassword));

            // 不 Trim：带首尾空格的密码照样算数，而且必须**逐字符**相等。
            Assert.True(PackingPasswordPolicy.IsUsable(" p "));
            Assert.Equal(string.Empty, PackingPasswordPolicy.Validate(" p ", " p "));
            Assert.Equal(PackingPasswordPolicy.MismatchMessage, PackingPasswordPolicy.Validate(" p ", "p"));

            // 外层勾了"用另一个密码"就必须填、且要对得上。
            Assert.Equal(PackingPasswordPolicy.OuterRequiredMessage, PackingPasswordPolicy.ValidateOuter(true, "  ", "  "));
            Assert.Equal(PackingPasswordPolicy.MismatchMessage, PackingPasswordPolicy.ValidateOuter(true, "a", "b"));
            Assert.Equal(string.Empty, PackingPasswordPolicy.ValidateOuter(true, "a", "a"));
            Assert.Equal(string.Empty, PackingPasswordPolicy.ValidateOuter(false, string.Empty, string.Empty));

            // 没填外层 → 与内层同一个（默认就是两层一个密码）。
            var request = new PackingRequest { Password = SamplePassword };
            Assert.Equal(SamplePassword, request.EffectiveOuterPassword);
            Assert.False(request.UsesSeparateOuterPassword);

            var separate = new PackingRequest { Password = SamplePassword, OuterPassword = OuterSamplePassword };
            Assert.Equal(OuterSamplePassword, separate.EffectiveOuterPassword);
            Assert.True(separate.UsesSeparateOuterPassword);
        }

        [Fact]
        public async Task 密码为空_一步都不跑_也不建输出目录()
        {
            string source = CreateSourceFolder("A-nopassword", 1, 4096);
            string output = Path.Combine(_root, "B-nopassword");

            var runner = new FakePackRunner();
            var service = new PackingService(new ToolLocator { UseWinRarInstallation = false }, runner, _ => long.MaxValue);

            PackingResult result = await service.PackAsync(new PackingRequest
            {
                SourceFolder = source,
                OutputFolder = output,
                VolumeSizeBytes = OneMebibyte,
                Password = "   "
            });

            Assert.Equal(PackingState.Failed, result.State);
            Assert.Contains("必须设置密码", result.Describe(), StringComparison.Ordinal);
            Assert.Equal(0, runner.TotalCalls);
            Assert.False(Directory.Exists(output));
        }

        [Fact]
        public void 密码脱敏_命令行与异常文本都不会漏出明文()
        {
            string commandLine = "Rar.exe a -hp" + SamplePassword + " -r -ep1 -y out.rar folder";

            string masked = PasswordMasker.Sanitize(commandLine);

            Assert.DoesNotContain(SamplePassword, masked, StringComparison.Ordinal);
            Assert.Contains("-hp******", masked, StringComparison.Ordinal);

            string sevenZipCommandLine = "7z.exe a -t7z -mhe=on -p" + SamplePassword + " out.7z *";
            Assert.DoesNotContain(SamplePassword, PasswordMasker.Sanitize(sevenZipCommandLine), StringComparison.Ordinal);

            // 兜底那一层：按字面替换，无论密码以什么形态漏出来。
            Assert.DoesNotContain(
                SamplePassword,
                PackPasswordGuard.Sanitize("工具说：密码 " + SamplePassword + " 不对", SamplePassword),
                StringComparison.Ordinal);
        }

        // ══════════════════════════ ③ 没有 Rar.exe ══════════════════════════

        [Fact]
        public void 文案_没有Rar时那句话必须点名要装WinRAR()
        {
            Assert.Contains("需要本机已安装 WinRAR", StatusText.PackNeedRar, StringComparison.Ordinal);
            Assert.Contains("只做 7z 分卷", StatusText.PackSkipRarOption, StringComparison.Ordinal);

            var tools = new ToolLocator { UseWinRarInstallation = false };

            Assert.False(tools.RarExists, "两档都关掉后必须判定为'没有 Rar.exe'");
            Assert.Contains("需要本机已安装 WinRAR", tools.DescribeNoRarAvailable(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task 没有RarExe_明确报错_一步都不跑_且不产生rar()
        {
            string source = CreateSourceFolder("A-norar", 2, 64 * 1024);
            string output = Path.Combine(_root, "B-norar");
            string rarPath = PackingPlan.DefaultRarPath(output);

            var tools = new ToolLocator { UseWinRarInstallation = false };
            var runner = new FakePackRunner();
            var service = new PackingService(tools, runner, _ => long.MaxValue);

            PackingResult result = await service.PackAsync(new PackingRequest
            {
                SourceFolder = source,
                OutputFolder = output,
                VolumeSizeBytes = OneMebibyte,
                Password = SamplePassword
            });

            Assert.Equal(PackingState.Failed, result.State);
            Assert.False(result.Success);

            string described = result.Describe();

            Assert.Contains("需要本机已安装 WinRAR", described, StringComparison.Ordinal);
            Assert.Contains(StatusText.PackSkipRarOption, described, StringComparison.Ordinal);

            // 判据：**一步都没跑**（不是"跑了但失败了"），而且没有留下任何 .rar。
            Assert.Equal(0, runner.TotalCalls);
            Assert.False(File.Exists(rarPath), "没有 Rar.exe 时绝不能产生 .rar（更不能用 7z 假装生成）");
            Assert.False(Directory.Exists(output), "在切分卷之前就该停下：连输出文件夹都不该建");
            Assert.Empty(Directory.GetFiles(_root, "*.rar", SearchOption.AllDirectories));

            // 两条出路都要在失败正文里（"只做 7z 分卷"必须是可点的，不是让用户自己去敲命令）。
            Assert.Contains("装好 WinRAR", described, StringComparison.Ordinal);
            Assert.Contains(StatusText.PackSkipRarOption, described, StringComparison.Ordinal);
            Assert.DoesNotContain(StatusText.PackSuccess, described, StringComparison.Ordinal);
        }

        [Fact]
        public async Task 没有RarExe_勾了只做7z分卷_照样能出结果()
        {
            string sevenZip = LocateSevenZip();

            if (string.IsNullOrEmpty(sevenZip))
            {
                Skip("测试机上没有内置 7z.exe（ArchiveFixer/tools/7zip/7z.exe）");
                return;
            }

            string source = CreateSourceFolder("A-volumesonly", 2, 300 * 1024);
            string output = Path.Combine(_root, "B-volumesonly");

            var tools = new ToolLocator { UseWinRarInstallation = false };
            var service = new PackingService(tools, new FakePackRunner(new PackProcessRunner(tools)), _ => long.MaxValue);

            PackingResult result = await service.PackAsync(new PackingRequest
            {
                SourceFolder = source,
                OutputFolder = output,
                VolumeSizeBytes = OneMebibyte,
                Password = SamplePassword,
                SkipOuterRar = true
            });

            Assert.True(result.Success, "勾了「只做 7z 分卷」时应当能完成：" + result.Describe());
            Assert.True(result.SkippedOuterRar);
            Assert.Null(result.RarPath);
            Assert.True(File.Exists(Path.Combine(output, "A-volumesonly.7z.001")));
            Assert.False(File.Exists(PackingPlan.DefaultRarPath(output)), "勾了跳过外层就不该有 rar");
            Assert.Equal(StatusText.PackPartialVolumesOnly, result.Message);
            Assert.Contains("没有做外层 rar", result.Describe(), StringComparison.Ordinal);
        }

        // ══════════════════════════ ④ 落点 ══════════════════════════

        [Fact]
        public void 输出落在A里面_拒绝()
        {
            string source = CreateSourceFolder("A-inside", 1, 1024);
            string output = Path.Combine(source, "B");
            string rar = Path.Combine(_root, "结果.rar");

            string? rejected = PackingPlan.ValidatePlacement(source, output, rar);

            Assert.NotNull(rejected);
            Assert.Contains("不能放在源文件夹 A 里面", rejected!, StringComparison.Ordinal);

            Assert.False(PackingPlan.TryCreate(
                new PackingRequest { SourceFolder = source, OutputFolder = output, RarPath = rar, Password = SamplePassword },
                out _,
                out string error));
            Assert.Contains("不能放在源文件夹 A 里面", error, StringComparison.Ordinal);
        }

        [Fact]
        public void 结果rar落在B里面_拒绝()
        {
            string source = CreateSourceFolder("A-rarinB", 1, 1024);
            string output = Path.Combine(_root, "B-rarinB");
            string rar = Path.Combine(output, "结果.rar");

            string? rejected = PackingPlan.ValidatePlacement(source, output, rar);

            Assert.NotNull(rejected);
            Assert.Contains("不能放在输出文件夹 B 里面", rejected!, StringComparison.Ordinal);
        }

        [Fact]
        public void rar落在A里面或A落在B里面_同样拒绝()
        {
            string source = CreateSourceFolder("A-cross", 1, 1024);

            // rar 写进源目录 → 不变量 12（源目录里一个字节都不许写）。
            string? rarInSource = PackingPlan.ValidatePlacement(
                source,
                Path.Combine(_root, "B-cross"),
                Path.Combine(source, "结果.rar"));

            Assert.NotNull(rarInSource);
            Assert.Contains("不能放在源文件夹 A 里面", rarInSource!, StringComparison.Ordinal);

            // A 在 B 里面 → 外层 rar 装的是整个 B，会把源文件原样再存一份。
            // ⚠ rar 必须放在 B **外面**，否则会先撞上"rar 不能放在 B 里面"那一条，测的就不是这一条判据了。
            string? sourceInOutput = PackingPlan.ValidatePlacement(
                source,
                _root,
                Path.Combine(Path.GetDirectoryName(_root) ?? _root, "B-cross.rar"));

            Assert.NotNull(sourceInOutput);
            Assert.Contains("源文件夹 A 在输出文件夹 B 里面", sourceInOutput!, StringComparison.Ordinal);
        }

        [Fact]
        public void 输出文件夹非空或rar已存在_都不默认覆盖()
        {
            string source = CreateSourceFolder("A-conflict", 1, 1024);
            string output = Path.Combine(_root, "B-conflict");

            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "别人放的东西.txt"), "x");

            string? notEmpty = PackingPlan.ValidatePlacement(source, output, Path.Combine(_root, "B-conflict.rar"));
            Assert.NotNull(notEmpty);
            Assert.Contains("不为空", notEmpty!, StringComparison.Ordinal);

            // 空的 B 是可以用的。
            Directory.Delete(output, recursive: true);
            Directory.CreateDirectory(output);
            Assert.Null(PackingPlan.ValidatePlacement(source, output, Path.Combine(_root, "B-conflict.rar")));

            File.WriteAllText(Path.Combine(_root, "B-conflict.rar"), "old");
            string? occupied = PackingPlan.ValidatePlacement(source, output, Path.Combine(_root, "B-conflict.rar"));
            Assert.NotNull(occupied);
            Assert.Contains("不会默认覆盖", occupied!, StringComparison.Ordinal);
        }

        // ══════════════════════════ ⑤ 空间 ══════════════════════════

        [Fact]
        public async Task 空间不够_不开始_并报需要可用差()
        {
            string source = CreateSourceFolder("A-nospace", 1, 256 * 1024);
            string output = Path.Combine(_root, "B-nospace");

            var runner = new FakePackRunner();
            var service = new PackingService(
                new ToolLocator { UseWinRarInstallation = false },
                runner,
                _ => 1024L); // 目标盘"只剩 1 KiB"

            PackingResult result = await service.PackAsync(new PackingRequest
            {
                SourceFolder = source,
                OutputFolder = output,
                VolumeSizeBytes = OneMebibyte,
                Password = SamplePassword,
                SkipOuterRar = true
            });

            Assert.Equal(PackingState.Failed, result.State);
            Assert.Equal(StatusText.DiskSpaceInsufficient, result.Message);

            string described = result.Describe();

            Assert.Contains("需要", described, StringComparison.Ordinal);
            Assert.Contains("可用", described, StringComparison.Ordinal);
            Assert.Contains("差", described, StringComparison.Ordinal);

            Assert.Equal(0, runner.TotalCalls);
            Assert.False(Directory.Exists(output), "空间不够就不该开始（连输出目录都不建）");
        }

        [Fact]
        public async Task 取不到可用空间_不拦但明说()
        {
            string sevenZip = LocateSevenZip();

            if (string.IsNullOrEmpty(sevenZip))
            {
                Skip("测试机上没有内置 7z.exe");
                return;
            }

            string source = CreateSourceFolder("A-unknownspace", 1, 128 * 1024);
            string output = Path.Combine(_root, "B-unknownspace");

            var service = new PackingService(
                new ToolLocator { UseWinRarInstallation = false },
                null,
                _ => null); // 取不到可用空间

            PackingResult result = await service.PackAsync(new PackingRequest
            {
                SourceFolder = source,
                OutputFolder = output,
                VolumeSizeBytes = OneMebibyte,
                Password = SamplePassword,
                SkipOuterRar = true
            });

            Assert.True(result.Success, result.Describe());
            Assert.Contains(
                result.LogLines,
                line => line.Contains("取不到", StringComparison.Ordinal)
                        || line.Contains("没有做空间门判断", StringComparison.Ordinal));
        }

        // ══════════════════════════ ⑥ 真 7z 端到端 ══════════════════════════

        [Fact]
        public async Task 真7z端到端_1MiB分卷_密码对能列出_错密码列不出()
        {
            string sevenZip = LocateSevenZip();

            if (string.IsNullOrEmpty(sevenZip))
            {
                Skip("测试机上没有内置 7z.exe（ArchiveFixer/tools/7zip/7z.exe）");
                return;
            }

            // 不可压缩的随机内容：可压缩的内容会被压成一卷，"分卷测试"就名不副实了。
            string source = CreateSourceFolder("A-e2e", 2, 700 * 1024);
            string output = Path.Combine(_root, "B-e2e");

            var tools = new ToolLocator { UseWinRarInstallation = false };
            var logLines = new List<string>();
            var service = new PackingService(tools, null, _ => long.MaxValue);

            PackingResult result = await service.PackAsync(
                new PackingRequest
                {
                    SourceFolder = source,
                    OutputFolder = output,
                    VolumeSizeBytes = OneMebibyte,
                    Password = SamplePassword,
                    SkipOuterRar = true
                },
                logLines.Add);

            Assert.True(result.Success, result.Describe());

            string first = Path.Combine(output, "A-e2e.7z.001");
            string second = Path.Combine(output, "A-e2e.7z.002");

            Assert.True(File.Exists(first), "1 MiB 分卷下应当有 .7z.001");
            Assert.True(File.Exists(second), "1 MiB 分卷下应当有 .7z.002（内容是随机数据，压不小）");
            Assert.Equal(2, result.Volumes.Count);
            Assert.True(new FileInfo(first).Length > 0);

            // 真 7z 自己列一遍：对密码能列出条目（证明 -mhe=on 的分卷是好的、密码是对的）。
            (int okExit, string okOut, string okErr) = RunSevenZip(sevenZip, "l", "-p" + SamplePassword, first);

            Assert.True(okExit == 0, $"对密码应当能列出分卷内容（退出码 {okExit}）：{okOut}{okErr}");
            Assert.Contains("rand1.bin", okOut, StringComparison.Ordinal);

            // 错密码：列不出来（而且不许把它当成"空包"或"坏包"绕过去）。
            (int badExit, string badOut, string badErr) = RunSevenZip(sevenZip, "l", "-p" + "wrong-password", first);

            Assert.NotEqual(0, badExit);
            Assert.Contains("Wrong password", badOut + badErr, StringComparison.OrdinalIgnoreCase);

            // 日志里搜不到密码明文（日志行 + 结论 + 失败原因一起搜）。
            string joined = string.Join("\n", result.LogLines) + "\n" + result.Describe() + "\n" + result.Message
                            + "\n" + result.FailureReason + "\n" + string.Join("\n", logLines);

            Assert.DoesNotContain(SamplePassword, joined, StringComparison.Ordinal);
            Assert.Contains(PackingPasswordPolicy.SetLogLine, joined, StringComparison.Ordinal);

            // 可追溯（不变量 14）：日志里要能看见用的是哪个引擎、哪个版本。
            Assert.Contains(
                result.LogLines,
                line => line.Contains("7-Zip", StringComparison.Ordinal) && line.Contains("版本", StringComparison.Ordinal));
        }

        [Fact]
        public async Task 真7z端到端_两层都做_rar里装的是B这一层()
        {
            string sevenZip = LocateSevenZip();

            if (string.IsNullOrEmpty(sevenZip))
            {
                Skip("测试机上没有内置 7z.exe");
                return;
            }

            var tools = new ToolLocator();

            if (!tools.RarExists)
            {
                Skip("测试机上没有 Rar.exe（没装 WinRAR）—— 这一条留给真机人工项");
                return;
            }

            string source = CreateSourceFolder("A-both", 2, 600 * 1024);
            string output = Path.Combine(_root, "B-both");
            string rarPath = PackingPlan.DefaultRarPath(output);

            var service = new PackingService(tools, null, _ => long.MaxValue);

            PackingResult result = await service.PackAsync(new PackingRequest
            {
                SourceFolder = source,
                OutputFolder = output,
                VolumeSizeBytes = OneMebibyte,
                Password = SamplePassword
            });

            Assert.True(result.Success, result.Describe());
            Assert.Equal(rarPath, result.RarPath);
            Assert.True(File.Exists(rarPath), "两层都做时应当有结果 rar");
            Assert.True(result.RarBytes > 0);
            Assert.Contains(StatusText.PackSuccess, result.Describe(), StringComparison.Ordinal);

            // 产物校验那句话要说清"数出几个、都在哪一层"。
            Assert.Contains("分卷", result.VerificationDetail, StringComparison.Ordinal);

            // 自己再列一遍 rar：条目必须带 B 这一层（用户解出来要看到文件夹）。
            string rarExe = tools.RarExePath;
            (int exit, string stdout, string stderr) = RunProcess(rarExe, new[] { "lb", "-p" + SamplePassword, rarPath });

            Assert.True(exit == 0, $"Rar.exe lb 应当能列出条目（退出码 {exit}）：{stdout}{stderr}");

            IReadOnlyList<string> entries = PackingVerifier.ParseBareList(stdout);

            Assert.Contains(entries, e => e.EndsWith("A-both.7z.001", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(entries, e => e.EndsWith("A-both.7z.002", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(entries, e => PackingVerifier.IsWithinFolder(e, Path.GetFileName(output)));

            // 日志里同样不许出现密码明文（这一条走的是 -hp，与 7z 的 -p 形态不同）。
            string joined = string.Join("\n", result.LogLines) + "\n" + result.Describe();
            Assert.DoesNotContain(SamplePassword, joined, StringComparison.Ordinal);
        }

        // ══════════════════════════ ⑦ 进度 ══════════════════════════

        [Fact]
        public void 进度解析_七压与RAR两种形态都认_认不出就不编()
        {
            // 7-Zip：\r 分隔的"百分比 [序号] 命令 名字"。
            Assert.Equal(0, PackProgressParser.TryParse("  0%"));
            Assert.Equal(67, PackProgressParser.TryParse(" 67% 8 + payload.bin"));
            Assert.Equal(12, PackProgressParser.TryParse("\r 12%"));

            // RAR：同一行里用退格回写的"… 50% 确定"。
            Assert.Equal(50, PackProgressParser.TryParse("正在添加    D:\\x\\B\\a.7z.001      50%  确定"));
            Assert.Equal(100, PackProgressParser.TryParse("正在添加    D:\\x\\B\\a.7z.002     \b\b\b100%  确定"));

            // 认不出来 → null（绝不编一个百分比出来）。
            Assert.Null(PackProgressParser.TryParse("Everything is Ok"));
            Assert.Null(PackProgressParser.TryParse(string.Empty));
            Assert.Null(PackProgressParser.TryParse(null));
            Assert.Null(PackProgressParser.TryParse("1000% 不是进度"));

            Assert.Equal("payload.bin", PackProgressParser.ExtractDetail(" 67% 8 + payload.bin"));
            Assert.Equal(string.Empty, PackProgressParser.ExtractDetail("Everything is Ok"));
        }

        [Fact]
        public async Task 真7z_进度能被看见并回报()
        {
            string sevenZip = LocateSevenZip();

            if (string.IsNullOrEmpty(sevenZip))
            {
                Skip("测试机上没有内置 7z.exe");
                return;
            }

            string source = CreateSourceFolder("A-progress", 4, 1024 * 1024);
            string output = Path.Combine(_root, "B-progress");

            var tools = new ToolLocator { UseWinRarInstallation = false };
            var plan = new PackingPlan
            {
                SourceFolder = source,
                SourceFolderName = "A-progress",
                OutputFolder = output,
                RarPath = PackingPlan.DefaultRarPath(output),
                VolumeSizeBytes = OneMebibyte
            };

            Directory.CreateDirectory(output);

            var reports = new List<PackStepProgress>();

            PackStepResult result = await new PackProcessRunner(tools).RunAsync(
                PackToolKind.SevenZip,
                PackingService.BuildVolumeArguments(plan, SamplePassword),
                SamplePassword,
                new SynchronousProgress(reports),
                CancellationToken.None);

            Assert.True(result.Success, result.Message + result.CombinedOutput);

            // "长任务必须能看进度"：真 7z 跑起来时至少上报过一次百分比，
            // 而且报出来的百分比都在合法区间里。
            Assert.NotEmpty(reports);
            Assert.Contains(reports, r => r.Percent >= 0 && r.Percent <= 100);
        }

        // ══════════════════════════ ⑧ 取消 ══════════════════════════

        [Fact]
        public async Task 取消_第二步被取消_已取消_没有rar_分卷还在()
        {
            string sevenZip = LocateSevenZip();

            if (string.IsNullOrEmpty(sevenZip))
            {
                Skip("测试机上没有内置 7z.exe");
                return;
            }

            string source = CreateSourceFolder("A-cancel", 2, 300 * 1024);
            string output = Path.Combine(_root, "B-cancel");
            string rarPath = PackingPlan.DefaultRarPath(output);

            /*
             * 第一步走**真 7z**（这样"分卷还在"才是真判据，不是假 runner 编出来的），
             * 第二步的 rar 用假 runner 直接抛取消。
             *
             * Rar.exe 的存在性用"自选路径指向一个刚建出来的假文件"来满足 —— 这样在没有装 WinRAR
             * 的机器上这条用例照样跑得到（那个假 exe 永远不会被执行：rar 那一步被假 runner 拦住了）。
             */
            string fakeRar = Path.Combine(_root, "Rar.exe");
            File.WriteAllText(fakeRar, "这不是真的 Rar.exe：本用例里那一步由假 runner 拦住，绝不会被执行。");

            var tools = new ToolLocator { UseWinRarInstallation = false, CustomRarExePath = fakeRar };
            Assert.True(tools.RarExists);

            var runner = new FakePackRunner(
                new PackProcessRunner(tools),
                rarCreateResult: _ => throw new OperationCanceledException("用户在第二步点了取消"));

            var service = new PackingService(tools, runner, _ => long.MaxValue);

            PackingResult result = await service.PackAsync(new PackingRequest
            {
                SourceFolder = source,
                OutputFolder = output,
                VolumeSizeBytes = OneMebibyte,
                Password = SamplePassword
            });

            Assert.Equal(PackingState.Cancelled, result.State);
            Assert.True(result.Cancelled);
            Assert.False(result.Success, "取消绝不能显示成成功（不变量 6）");
            Assert.Equal(StatusText.PackCancelled, result.Message);

            Assert.False(File.Exists(rarPath), "取消时不许留半个 rar");
            Assert.True(File.Exists(Path.Combine(output, "A-cancel.7z.001")), "已经切出来的分卷必须保留");
            Assert.True(result.Volumes.Count >= 1);

            string joined = string.Join("\n", result.LogLines);

            Assert.Contains("分卷", joined, StringComparison.Ordinal);
            Assert.Contains(PackingPasswordPolicy.SetLogLine, joined, StringComparison.Ordinal);
            Assert.DoesNotContain(SamplePassword, joined, StringComparison.Ordinal);
        }

        [Fact]
        public async Task 取消_第一步之前就取消_立刻返回且什么都不做()
        {
            string source = CreateSourceFolder("A-precancel", 1, 4096);
            string output = Path.Combine(_root, "B-precancel");

            var runner = new FakePackRunner();
            var service = new PackingService(new ToolLocator { UseWinRarInstallation = false }, runner, _ => long.MaxValue);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            PackingResult result = await service.PackAsync(
                new PackingRequest
                {
                    SourceFolder = source,
                    OutputFolder = output,
                    VolumeSizeBytes = OneMebibyte,
                    Password = SamplePassword,
                    SkipOuterRar = true
                },
                cancellationToken: cancellation.Token);

            Assert.Equal(PackingState.Cancelled, result.State);
            Assert.Equal(0, runner.TotalCalls);
        }

        // ══════════════════════════ ⑨ 产物校验 ══════════════════════════

        [Fact]
        public void 产物校验_少一卷或混进别的东西都不通过()
        {
            var volumes = new List<PackingVolume>
            {
                new() { FileName = "A.7z.001", Index = 1, Bytes = 100 },
                new() { FileName = "A.7z.002", Index = 2, Bytes = 100 }
            };

            PackingVerification good = PackingVerifier.Verify(
                new[] { @"B\A.7z.001", @"B\A.7z.002", "B" },
                "B",
                volumes);

            Assert.True(good.Ok);
            Assert.Contains("2 个分卷", good.Detail, StringComparison.Ordinal);

            PackingVerification missing = PackingVerifier.Verify(
                new[] { @"B\A.7z.001", "B" },
                "B",
                volumes);

            Assert.False(missing.Ok);
            Assert.Contains("少了", missing.Detail, StringComparison.Ordinal);
            Assert.Contains("A.7z.002", missing.Detail, StringComparison.Ordinal);

            PackingVerification stray = PackingVerifier.Verify(
                new[] { @"B\A.7z.001", @"B\A.7z.002", "B", @"别处\多余.txt" },
                "B",
                volumes);

            Assert.False(stray.Ok);
            Assert.Contains("B 这一层之外", stray.Detail, StringComparison.Ordinal);

            Assert.False(PackingVerifier.Verify(Array.Empty<string>(), "B", volumes).Ok);
            Assert.False(PackingVerifier.Verify(new[] { "B" }, "B", Array.Empty<PackingVolume>()).Ok);

            // bare listing 的解析：一行一个名字，空行丢掉。
            Assert.Equal(3, PackingVerifier.ParseBareList("B\\A.7z.001\r\nB\\A.7z.002\r\n\r\nB\r\n").Count);
            Assert.Empty(PackingVerifier.ParseBareList(null));
        }

        [Fact]
        public async Task 产物对不上_不显示成功()
        {
            string source = CreateSourceFolder("A-verifyfail", 1, 4096);
            string output = Path.Combine(_root, "B-verifyfail");

            // 7z 那一步"成功"（假 runner 里顺手把两个分卷造出来），rar 那一步也"成功"，
            // 但列条目时少了一卷 —— 结果必须是失败，绝不能显示成功（不变量 6）。
            var runner = new FakePackRunner(
                sevenZipResult: _ =>
                {
                    Directory.CreateDirectory(output);
                    File.WriteAllBytes(Path.Combine(output, "A-verifyfail.7z.001"), new byte[1024]);
                    File.WriteAllBytes(Path.Combine(output, "A-verifyfail.7z.002"), new byte[1024]);

                    return FakePackRunner.Ok();
                },
                rarListResult: _ => FakePackRunner.Ok(@"B-verifyfail\A-verifyfail.7z.001" + Environment.NewLine + "B-verifyfail"));

            var service = new PackingService(new ToolLocator(), runner, _ => long.MaxValue);

            PackingResult result = await service.PackAsync(new PackingRequest
            {
                SourceFolder = source,
                OutputFolder = output,
                VolumeSizeBytes = OneMebibyte,
                Password = SamplePassword
            });

            Assert.Equal(PackingState.Failed, result.State);
            Assert.False(result.Success);
            Assert.Equal(StatusText.PackVerifyFailed, result.Message);
            Assert.Contains("少了", result.Describe(), StringComparison.Ordinal);
        }

        // ══════════════════════════ 辅助 ══════════════════════════

        private void Skip(string reason)
        {
            _output.WriteLine("跳过：" + reason);
        }

        /// <summary>造一个源文件夹：<paramref name="fileCount"/> 个**不可压缩**的随机文件。</summary>
        private string CreateSourceFolder(string name, int fileCount, int bytesPerFile)
        {
            string directory = Path.Combine(_root, name);
            Directory.CreateDirectory(directory);

            var random = new Random(20260922);

            for (int i = 1; i <= fileCount; i++)
            {
                var bytes = new byte[bytesPerFile];
                random.NextBytes(bytes);

                File.WriteAllBytes(Path.Combine(directory, "rand" + i + ".bin"), bytes);
            }

            return directory;
        }

        /// <summary>内置 7z.exe（优先程序输出目录，其次源码树）。</summary>
        private static string LocateSevenZip()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    string candidate = Path.Combine(directory.FullName, "ArchiveFixer", "tools", "7zip", "7z.exe");

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                directory = directory.Parent;
            }

            string local = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe");

            return File.Exists(local) ? local : string.Empty;
        }

        /// <summary>
        /// 起一次 7z 并收输出。
        ///
        /// ⚠ <b>必须重定向 stdin</b>：加密头（<c>-mhe=on</c>）的包在**没给密码**时 7-Zip 会
        /// 交互式地问密码（实测会一直等），测试进程会挂在那里。所以这里永远传 <c>-p</c>，
        /// 并且把 stdin 立刻关掉。
        /// </summary>
        private static (int ExitCode, string StdOut, string StdErr) RunSevenZip(string sevenZip, params string[] args)
        {
            return RunProcess(sevenZip, args);
        }

        private static (int ExitCode, string StdOut, string StdErr) RunProcess(string exe, IReadOnlyList<string> args)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动：" + exe);

            try
            {
                process.StandardInput.Close();
            }
            catch
            {
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(120_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                throw new TimeoutException($"{Path.GetFileName(exe)} 超时：{string.Join(' ', args)}");
            }

            return (process.ExitCode, stdout, stderr);
        }

        /// <summary>
        /// 同步收集进度。<b>刻意不用 <see cref="Progress{T}"/></b>：那个会把回调投到捕获的
        /// 同步上下文上（xUnit 有自己的），断言可能跑在回调之前，测试就变成了看运气。
        /// </summary>
        private sealed class SynchronousProgress : IProgress<PackStepProgress>
        {
            private readonly List<PackStepProgress> _sink;
            private readonly object _lock = new();

            public SynchronousProgress(List<PackStepProgress> sink)
            {
                _sink = sink;
            }

            public void Report(PackStepProgress value)
            {
                lock (_lock)
                {
                    _sink.Add(value);
                }
            }
        }

        /// <summary>
        /// 可控的假 runner：验"没有 Rar.exe 时一步都不跑""第二步取消""产物对不上"这类判据，
        /// 不需要真的装 WinRAR（验收判据点名要求用假 runner 断言没被调用）。
        /// </summary>
        private sealed class FakePackRunner : IPackProcessRunner
        {
            private readonly IPackProcessRunner? _sevenZipDelegate;
            private readonly Func<IReadOnlyList<string>, PackStepResult>? _sevenZipResult;
            private readonly Func<IReadOnlyList<string>, PackStepResult>? _rarCreateResult;
            private readonly Func<IReadOnlyList<string>, PackStepResult>? _rarListResult;

            public FakePackRunner(
                IPackProcessRunner? sevenZipDelegate = null,
                Func<IReadOnlyList<string>, PackStepResult>? sevenZipResult = null,
                Func<IReadOnlyList<string>, PackStepResult>? rarCreateResult = null,
                Func<IReadOnlyList<string>, PackStepResult>? rarListResult = null)
            {
                _sevenZipDelegate = sevenZipDelegate;
                _sevenZipResult = sevenZipResult;
                _rarCreateResult = rarCreateResult;
                _rarListResult = rarListResult;
            }

            public int TotalCalls { get; private set; }

            public int SevenZipCalls { get; private set; }

            public int RarCreateCalls { get; private set; }

            public int RarListCalls { get; private set; }

            public List<IReadOnlyList<string>> Arguments { get; } = new();

            public async Task<PackStepResult> RunAsync(
                PackToolKind tool,
                IReadOnlyList<string> arguments,
                string usedPassword,
                IProgress<PackStepProgress>? progress,
                CancellationToken cancellationToken)
            {
                TotalCalls++;
                Arguments.Add(arguments);

                if (tool == PackToolKind.SevenZip)
                {
                    SevenZipCalls++;

                    if (_sevenZipDelegate != null)
                    {
                        return await _sevenZipDelegate
                            .RunAsync(tool, arguments, usedPassword, progress, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    return _sevenZipResult?.Invoke(arguments) ?? Ok();
                }

                bool isListing = arguments.Count > 0 &&
                                 string.Equals(arguments[0], "lb", StringComparison.OrdinalIgnoreCase);

                if (isListing)
                {
                    RarListCalls++;

                    return _rarListResult?.Invoke(arguments) ?? Ok();
                }

                RarCreateCalls++;

                return _rarCreateResult?.Invoke(arguments) ?? Ok();
            }

            public static PackStepResult Ok(string output = "")
            {
                return new PackStepResult
                {
                    Success = true,
                    ExitCode = 0,
                    StandardOutput = output
                };
            }
        }
    }
}
