using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Packing;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
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
    /// <item><description>**外层容器三选一**（2026-09-23）：7z 外层的命令 / 产物名 / `-slt` 校验、
    /// 不做外层时一个外层文件都不产、7z 外层没有 `Rar.exe` 也照常成功；</description></item>
    /// <item><description>**自选 `Rar.exe` 路径**：合法 → 解析用它；不存在 → 保存被拦下并说明改法；留空 → 回落已装目录；</description></item>
    /// <item><description>取消语义：第二步取消 → 状态是"已取消"、没有 rar、**分卷还在**；</description></item>
    /// <item><description>产物校验：少一卷 / 混进别的东西 → **不算成功**（不变量 6）。</description></item>
    /// </list>
    ///
    /// <para><b>真机依赖</b>：需要真 7z 的用例在缺 <c>tools\7zip\7z.exe</c> 时打印原因后跳过
    /// （与 <c>UnRarRealSampleTests</c> 同一套写法）；需要真 <c>Rar.exe</c> 的那一条在没有
    /// WinRAR 的机器上同样跳过 —— 但"没有 Rar.exe 时会怎样"那条判据用的是**假的 runner**，
    /// 因此在任何机器上都跑得到。</para>
    ///
    /// <para><b>为什么整类串行跑</b>（<c>ArchiveFixerGlobalState</c>，见 <c>InnerLayerContinuationTests</c>
    /// 顶部的 CollectionDefinition）：① ⑪ 那一组要断言设置真的推到了进程级的
    /// <c>ToolLocator.Default.CustomRarExePath</c>，而别的用例构造 <c>MainViewModel</c> /
    /// <c>SettingsViewModel</c> 时会把设置重推一遍（并行跑时实测出现过"刚推完就被别人清成空串"）；
    /// ② 这一组会真起 <c>7z.exe</c> / <c>Rar.exe</c> 进程，与别的重活并行只会互相抢 CPU。
    /// </para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
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

        /// <summary>
        /// 默认落点与命名（⚠ **2026-09-26 第 46 条改了口径**）。
        ///
        /// <para>旧口径：B = <c>素材_打包</c>、rar = <c>素材_打包.rar</c>。
        /// 新口径（用户拍板）：装分卷的文件夹**用源名**（撞名才 <c>素材(1)</c>）、
        /// 最终 rar **永远用源名**（<c>素材.rar</c>）；两者都落在**本地**（源旁边）。</para>
        /// </summary>
        [Fact]
        public void 默认落点_装分卷的文件夹与rar都用源名_落在源旁边()
        {
            string source = Path.Combine(_root, "素材");
            Directory.CreateDirectory(source);
            File.WriteAllBytes(Path.Combine(source, "a.bin"), new byte[64]);

            // 源文件夹还在 → 装分卷的文件夹让位成 `素材(1)`（用户原话："那就是 1111(1) 文件夹"）。
            string expectedOutput = Path.Combine(_root, "素材(1)");
            string expectedRar = Path.Combine(_root, "素材.rar");

            Assert.True(PackingPlan.TryCreate(
                new PackingRequest { SourceFolder = source, Password = SamplePassword },
                out PackingPlan? plan,
                out string error), error);

            Assert.NotNull(plan);
            Assert.Equal(expectedOutput, plan!.OutputFolder);
            Assert.Equal(expectedRar, plan.RarPath);
            Assert.Equal("素材.7z", plan.VolumeBaseName);
            Assert.Equal(_root, plan.TargetDirectory);

            // 64 字节的内容：<1 GiB → 目标 2 卷（每卷上限 1 MiB 的下限）。
            Assert.Equal(2, plan.VolumeTargetCount);
            Assert.Equal(1L * 1024 * 1024, plan.VolumeSizeBytes);
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

        /// <summary>
        /// ⚠ **2026-09-26 第 46 条改了口径**（用户原话："如果用户没有装 WinRAR，那就弄 7z 吧"）：
        /// 这句话以前是"这一步需要本机已安装 WinRAR" + 三条出路（让他自己去⑤页换容器 / 选不做外层）。
        /// 现在⑤页上**没有**"外层容器"那个控件了，程序也会**自动**改用 7z ——
        /// 所以那句话必须①说清"会自动改用 7z、产物是 .7z"；②指路**指对界面**（想拿 .rar 只有一个入口 =
        /// ②「解压方式」页 →「引擎」里的自选路径）；③**不再**让用户去点一个不存在的控件。
        /// </summary>
        [Fact]
        public void 文案_没有Rar时_说清会自动改用7z外层_并指对填自选路径的地方()
        {
            var tools = new ToolLocator { UseWinRarInstallation = false };

            Assert.False(tools.RarExists, "两档都关掉后必须判定为'没有 Rar.exe'");

            string text = tools.DescribeNoRarAvailable();

            Assert.Contains("没有 Rar.exe", text, StringComparison.Ordinal);
            Assert.Contains("自动改用 7z 外层", text, StringComparison.Ordinal);
            Assert.Contains(".7z", text, StringComparison.Ordinal);
            Assert.Contains("绝不随包分发", text, StringComparison.Ordinal);
            Assert.Contains(tools.RarExpectedPath, text, StringComparison.Ordinal);

            // 指路必须指对：想拿 .rar 只有"装 WinRAR"与"②页填自选路径"两条。
            Assert.Contains("②「解压方式」页", text, StringComparison.Ordinal);
            Assert.Contains("Rar.exe 的路径", text, StringComparison.Ordinal);

            // ⛔ 不许再出现退役的说法（那两样界面上都已经没有了）。
            Assert.DoesNotContain("三条出路", text, StringComparison.Ordinal);
            Assert.DoesNotContain("需要本机已安装 WinRAR", text, StringComparison.Ordinal);
            Assert.DoesNotContain("不做外层容器", text, StringComparison.Ordinal);
        }

        /// <summary>
        /// ⚠ **2026-09-26 第 46 条改了口径**（用户原话："如果用户没有装 WinRAR，那就弄 7z 吧"）：
        /// 没有 <c>Rar.exe</c> 时**不再报错停下**，而是**自动改用 7z 外层** ——
        /// 产物是 <c>.7z</c> 而不是 <c>.rar</c>，而且必须**如实说出来**（不许假装生成了 rar）。
        ///
        /// <para>旧口径（"明确报错 + 三条出路 + 一步都不跑"）已被这条要求取代，本用例改写为钉新行为。</para>
        /// </summary>
        [Fact]
        public async Task 没有RarExe_自动改用7z外层_并如实说明产物是7z()
        {
            string source = CreateSourceFolder("A-norar", 2, 64 * 1024);
            string output = Path.Combine(_root, "B-norar");

            var tools = new ToolLocator { UseWinRarInstallation = false };
            var runner = new FakePackRunner(sevenZipResult: arguments =>
            {
                if (FakePackRunner.IsVolumeCall(arguments))
                {
                    Directory.CreateDirectory(output);
                    File.WriteAllBytes(Path.Combine(output, "A-norar.7z.001"), new byte[2048]);

                    return FakePackRunner.Ok();
                }

                File.WriteAllBytes(Path.Combine(_root, "A-norar.7z"), new byte[4096]);

                return FakePackRunner.Ok();
            }, sevenZipListResult: _ => FakePackRunner.Ok(
                "Listing archive: " + Path.Combine(_root, "A-norar.7z") + Environment.NewLine
                + Environment.NewLine
                + "--" + Environment.NewLine
                + "Path = " + Path.Combine(_root, "A-norar.7z") + Environment.NewLine
                + "Type = 7z" + Environment.NewLine
                + Environment.NewLine
                + "----------" + Environment.NewLine
                + "Path = B-norar" + Environment.NewLine
                + Environment.NewLine
                + @"Path = B-norar\A-norar.7z.001" + Environment.NewLine));

            var service = new PackingService(tools, runner, _ => long.MaxValue);

            // 默认容器就是 rar（不显式指定），这样测的正是"默认档 + 没装 WinRAR"这条最常见的组合。
            PackingResult result = await service.PackAsync(new PackingRequest
            {
                SourceFolder = source,
                OutputFolder = output,
                VolumeSizeBytes = OneMebibyte,
                Password = SamplePassword
            });

            Assert.True(result.Success, result.Describe());
            Assert.Equal(PackOuterContainer.SevenZip, result.OuterContainer);
            Assert.EndsWith(".7z", result.OuterPath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".rar", result.OuterPath!, StringComparison.OrdinalIgnoreCase);

            // 日志里必须说清"为什么是 7z"（用户自己没选过 7z）。
            Assert.Contains(
                result.LogLines,
                line => line.Contains("没有 Rar.exe", StringComparison.Ordinal) &&
                        line.Contains("7z 外层", StringComparison.Ordinal));

            // 一层都没少跑：分卷 + 外层 + 校验。
            Assert.True(runner.TotalCalls >= 3, $"分卷 / 外层 / 校验都该跑过，实际 {runner.TotalCalls} 次");
            Assert.Empty(Directory.GetFiles(_root, "*.rar", SearchOption.AllDirectories));
        }

        [Fact]
        public async Task 没有RarExe_不做外层_照样能出结果()
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
                OuterContainer = PackOuterContainer.None
            });

            Assert.True(result.Success, "选了「不做外层」时应当能完成：" + result.Describe());
            Assert.False(result.OuterContainer.HasOuterArtifact());
            Assert.Null(result.OuterPath);
            Assert.True(File.Exists(Path.Combine(output, "A-volumesonly.7z.001")));
            Assert.False(File.Exists(PackingPlan.DefaultRarPath(output)), "不做外层就不该有 rar");
            Assert.False(File.Exists(PackingPlan.DefaultSevenZipOuterPath(output)), "不做外层也不该有 7z 外层");
            Assert.Equal(StatusText.PackPartialVolumesOnly, result.Message);
            Assert.Contains("没有外层容器文件", result.Describe(), StringComparison.Ordinal);
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
                OuterContainer = PackOuterContainer.None
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
                OuterContainer = PackOuterContainer.None
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
                    OuterContainer = PackOuterContainer.None
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

            // ⚠ 第 46 条：外层产物用**源名**（`A-both.rar`），落在落点目录（源的父目录）。
            string rarPath = Path.Combine(_root, "A-both.rar");

            var service = new PackingService(tools, null, _ => long.MaxValue);

            PackingResult result = await service.PackAsync(new PackingRequest
            {
                SourceFolder = source,
                OutputFolder = output,
                VolumeSizeBytes = OneMebibyte,
                Password = SamplePassword
            });

            Assert.True(result.Success, result.Describe());
            Assert.Equal(PackOuterContainer.Rar, result.OuterContainer);
            Assert.Equal(rarPath, result.OuterPath);
            Assert.True(File.Exists(rarPath), "两层都做时应当有结果 rar");
            Assert.True(result.OuterBytes > 0);
            Assert.Contains(StatusText.PackSuccess, result.Describe(), StringComparison.Ordinal);
            Assert.Contains(StatusText.PackOuterRarShort, result.Describe(), StringComparison.Ordinal);

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

            // 默认收尾：其余物（装分卷的文件夹）已彻底删除，原包仍在原地。
            Assert.False(Directory.Exists(output), "默认档下其余物应当已被删除（只剩外层 rar）");
            Assert.True(Directory.Exists(source), "原包默认不动");
            Assert.Contains("已彻底删除", result.CleanupNote, StringComparison.Ordinal);
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
                    OuterContainer = PackOuterContainer.None
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

        // ══════════════════════════ ⑩ 外层容器三选一（用户 2026-09-23 决定） ══════════════════════════

        [Fact]
        public void 七压技术列目录解析_切掉归档自己那一块()
        {
            /*
             * `7z l -slt` 的第一块描述**归档自己**（本机 7-Zip 26.03 实测），分隔线之后才是条目。
             * 不切掉它，归档路径就会被当成"计划外条目"，产物校验会永远不通过 ——
             * 所以这里用一段**真机形态**的输出钉住解析。
             */
            string slt = "Listing archive: C:\\x\\B.7z\r\n\r\n--\r\nPath = C:\\x\\B.7z\r\nType = 7z\r\n"
                         + "Physical Size = 270\r\nHeaders Size = 238\r\n\r\n----------\r\n"
                         + "Path = B\r\nSize = 0\r\nAttributes = D\r\n\r\n"
                         + "Path = B\\A.7z.001\r\nSize = 2000\r\nEncrypted = +\r\n\r\n"
                         + "Path = B\\A.7z.002\r\nSize = 1500\r\nEncrypted = +\r\n";

            IReadOnlyList<string> entries = PackingVerifier.ParseSevenZipSltList(slt);

            Assert.Equal(3, entries.Count);
            Assert.Equal(new[] { "B", @"B\A.7z.001", @"B\A.7z.002" }, entries);
            Assert.DoesNotContain(entries, e => e.EndsWith(".7z", StringComparison.OrdinalIgnoreCase));

            Assert.Empty(PackingVerifier.ParseSevenZipSltList(null));
            Assert.Empty(PackingVerifier.ParseSevenZipSltList(string.Empty));

            // 解析出来的条目喂给校验：与 B 里的分卷对得上 → 通过。
            PackingVerification verification = PackingVerifier.Verify(
                entries,
                "B",
                new List<PackingVolume>
                {
                    new() { FileName = "A.7z.001", Index = 1, Bytes = 2000 },
                    new() { FileName = "A.7z.002", Index = 2, Bytes = 1500 }
                });

            Assert.True(verification.Ok, verification.Detail);
        }

        [Fact]
        public async Task 外层容器7z_命令与产物名正确_校验通过()
        {
            string source = CreateSourceFolder("A-7zouter", 2, 64 * 1024);
            string output = Path.Combine(_root, "B-7zouter");

            // ⚠ 2026-09-26 第 46 条：外层产物的**名字跟着源名**（`A-7zouter.7z`），落在落点目录里
            //（这里是源的父目录 `_root`）—— 不再是 `B-7zouter.7z`。
            string outerPath = Path.Combine(_root, "A-7zouter.7z");

            var runner = new FakePackRunner(
                sevenZipResult: arguments =>
                {
                    if (FakePackRunner.IsVolumeCall(arguments))
                    {
                        Directory.CreateDirectory(output);
                        File.WriteAllBytes(Path.Combine(output, "A-7zouter.7z.001"), new byte[1024]);
                        File.WriteAllBytes(Path.Combine(output, "A-7zouter.7z.002"), new byte[1024]);

                        return FakePackRunner.Ok();
                    }

                    // 外层容器那一步：造出外层 7z（假 runner 不真的起 7z）。
                    File.WriteAllBytes(outerPath, new byte[2048]);

                    return FakePackRunner.Ok();
                },
                sevenZipListResult: _ => FakePackRunner.Ok(
                    "Listing archive: " + outerPath + Environment.NewLine
                    + Environment.NewLine
                    + "--" + Environment.NewLine
                    + "Path = " + outerPath + Environment.NewLine
                    + "Type = 7z" + Environment.NewLine
                    + Environment.NewLine
                    + "----------" + Environment.NewLine
                    + "Path = B-7zouter" + Environment.NewLine
                    + Environment.NewLine
                    + @"Path = B-7zouter\A-7zouter.7z.001" + Environment.NewLine
                    + Environment.NewLine
                    + @"Path = B-7zouter\A-7zouter.7z.002" + Environment.NewLine));

            // 这台机器**没有** Rar.exe（两档都关掉）—— 7z 外层这条路本来就与它无关。
            var tools = new ToolLocator { UseWinRarInstallation = false };
            var service = new PackingService(tools, runner, _ => long.MaxValue);

            PackingResult result = await service.PackAsync(new PackingRequest
            {
                SourceFolder = source,
                OutputFolder = output,
                VolumeSizeBytes = OneMebibyte,
                Password = SamplePassword,
                OuterContainer = PackOuterContainer.SevenZip
            });

            Assert.True(result.Success, result.Describe());
            Assert.Equal(PackOuterContainer.SevenZip, result.OuterContainer);
            Assert.Equal(outerPath, result.OuterPath);
            Assert.True(File.Exists(outerPath), "7z 外层容器应当落在 B 的同级（B.7z）");
            Assert.Equal(0, runner.RarCalls); // 一次都没碰 Rar.exe

            // ① 外层命令：字面模板（用户 2026-09-23 给的：7z a -t7z -mhe=on -mx=5 -p<密码> "<B>.7z" "<B>"）。
            IReadOnlyList<string> outerCall = runner.Arguments.Single(a => !FakePackRunner.IsVolumeCall(a) && a[0] == "a");

            Assert.Equal(
                new[]
                {
                    "a", "-t7z", "-mx=5", "-mhe=on", "-bsp1", "-sccUTF-8", "-y",
                    "-p" + SamplePassword, outerPath, output
                },
                outerCall);

            // ② 列条目走的是 7z 的 -slt（机器格式），不是给人看的表格。
            IReadOnlyList<string> listCall = runner.Arguments.Single(a => a[0] == "l");

            Assert.Equal(
                new[] { "l", "-slt", "-sccUTF-8", "-y", "-p" + SamplePassword, outerPath },
                listCall);

            // ③ 产物名与校验：外层 7z 里装的是 B 这一层下的两个分卷。
            Assert.Equal("A-7zouter.7z", Path.GetFileName(result.OuterPath));
            Assert.Equal(2, result.Volumes.Count);
            Assert.Contains("2 个分卷", result.VerificationDetail, StringComparison.Ordinal);
            Assert.Contains(StatusText.PackOuterSevenZipShort, result.Describe(), StringComparison.Ordinal);

            // ④ 日志与结论里没有密码明文（7z 外层走的是 -p，与 rar 的 -hp 形态不同）。
            string joined = string.Join("\n", result.LogLines) + "\n" + result.Describe();

            Assert.DoesNotContain(SamplePassword, joined, StringComparison.Ordinal);
            Assert.Contains(PackingPasswordPolicy.SetLogLine, joined, StringComparison.Ordinal);
        }

        [Fact]
        public async Task 外层容器7z_产物对不上_不显示成功()
        {
            string source = CreateSourceFolder("A-7zbad", 1, 4096);
            string output = Path.Combine(_root, "B-7zbad");
            string outerPath = PackingPlan.DefaultSevenZipOuterPath(output);

            // 7z 建容器"成功"、列条目也"成功"，但条目里少了一卷 —— 必须判失败（不变量 6）。
            var runner = new FakePackRunner(
                sevenZipResult: arguments =>
                {
                    if (FakePackRunner.IsVolumeCall(arguments))
                    {
                        Directory.CreateDirectory(output);
                        File.WriteAllBytes(Path.Combine(output, "A-7zbad.7z.001"), new byte[1024]);

                        return FakePackRunner.Ok();
                    }

                    File.WriteAllBytes(outerPath, new byte[1024]);

                    return FakePackRunner.Ok();
                },
                sevenZipListResult: _ => FakePackRunner.Ok(
                    "----------" + Environment.NewLine + "Path = B-7zbad" + Environment.NewLine));

            var service = new PackingService(new ToolLocator { UseWinRarInstallation = false }, runner, _ => long.MaxValue);

            PackingResult result = await service.PackAsync(new PackingRequest
            {
                SourceFolder = source,
                OutputFolder = output,
                VolumeSizeBytes = OneMebibyte,
                Password = SamplePassword,
                OuterContainer = PackOuterContainer.SevenZip
            });

            Assert.Equal(PackingState.Failed, result.State);
            Assert.False(result.Success);
            Assert.Equal(StatusText.PackVerifyFailed, result.Message);
            Assert.Contains("少了", result.Describe(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task 外层容器不做_只出分卷_不产任何外层文件()
        {
            string source = CreateSourceFolder("A-none", 2, 64 * 1024);
            string output = Path.Combine(_root, "B-none");

            var runner = new FakePackRunner(
                sevenZipResult: arguments =>
                {
                    Directory.CreateDirectory(output);
                    File.WriteAllBytes(Path.Combine(output, "A-none.7z.001"), new byte[1024]);
                    File.WriteAllBytes(Path.Combine(output, "A-none.7z.002"), new byte[1024]);

                    return FakePackRunner.Ok();
                });

            var service = new PackingService(new ToolLocator { UseWinRarInstallation = false }, runner, _ => long.MaxValue);

            PackingResult result = await service.PackAsync(new PackingRequest
            {
                SourceFolder = source,
                OutputFolder = output,
                VolumeSizeBytes = OneMebibyte,
                Password = SamplePassword,
                OuterContainer = PackOuterContainer.None
            });

            Assert.True(result.Success, result.Describe());
            Assert.Equal(PackOuterContainer.None, result.OuterContainer);
            Assert.Null(result.OuterPath);
            Assert.False(result.HasOuterArtifact);

            // 判据：只跑了一次（切分卷），**没有**外层那一步、也没有列条目那一步。
            Assert.Equal(1, runner.TotalCalls);
            Assert.Equal(1, runner.SevenZipCalls);
            Assert.Equal(0, runner.SevenZipListCalls);
            Assert.Equal(0, runner.RarCalls);

            Assert.Equal(2, result.Volumes.Count);
            Assert.Equal(StatusText.PackPartialVolumesOnly, result.Message);
            Assert.Contains("没有外层容器文件", result.Describe(), StringComparison.Ordinal);
            Assert.Contains(StatusText.PackOuterNoneShort, result.Describe(), StringComparison.Ordinal);

            // 一个外层文件都不许有（.rar / .7z 都不行）。
            Assert.Empty(Directory.GetFiles(_root, "*.rar", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(_root, "*.7z", SearchOption.AllDirectories));

            // 空间口径跟着容器走：不做外层只要分卷那一份，比"外层再存一份"少一半。
            string content = "content";
            Assert.True(
                PackingPlan.ComputeRequiredSpace(content.Length, includeOuterCopy: false)
                < PackingPlan.ComputeRequiredSpace(content.Length, includeOuterCopy: true));

            Assert.True(
                PackingPlan.TryCreate(
                    new PackingRequest
                    {
                        SourceFolder = source,
                        OutputFolder = Path.Combine(_root, "B-none-plan"),
                        Password = SamplePassword,
                        OuterContainer = PackOuterContainer.None
                    },
                    out PackingPlan? plan,
                    out string error), error);

            Assert.NotNull(plan);
            Assert.Equal(
                PackingPlan.ComputeRequiredSpace(plan!.ContentBytes, includeOuterCopy: false),
                plan.RequiredSpaceBytes);
        }

        [Fact]
        public async Task 外层容器7z_没有RarExe_照常成功()
        {
            /*
             * 这正是"外层 7z"这条路存在的意义（用户 2026-09-23 决定）：
             * RARLAB 的 EULA 不允许随包分发 Rar.exe，所以"本机没装 WinRAR"是常态 ——
             * 那时用户不该被卡住。判据是**一次都没调用 Rar** 而且任务成功。
             */
            string source = CreateSourceFolder("A-7znorar", 1, 32 * 1024);
            string output = Path.Combine(_root, "B-7znorar");
            string outerPath = PackingPlan.DefaultSevenZipOuterPath(output);

            var runner = new FakePackRunner(
                sevenZipResult: arguments =>
                {
                    if (FakePackRunner.IsVolumeCall(arguments))
                    {
                        Directory.CreateDirectory(output);
                        File.WriteAllBytes(Path.Combine(output, "A-7znorar.7z.001"), new byte[512]);

                        return FakePackRunner.Ok();
                    }

                    File.WriteAllBytes(outerPath, new byte[1024]);

                    return FakePackRunner.Ok();
                },
                sevenZipListResult: _ => FakePackRunner.Ok(
                    "----------" + Environment.NewLine
                    + "Path = B-7znorar" + Environment.NewLine
                    + @"Path = B-7znorar\A-7znorar.7z.001" + Environment.NewLine));

            var tools = new ToolLocator { UseWinRarInstallation = false };

            Assert.False(tools.RarExists, "前提：这台机器上判定为'没有 Rar.exe'");

            var service = new PackingService(tools, runner, _ => long.MaxValue);

            PackingResult result = await service.PackAsync(new PackingRequest
            {
                SourceFolder = source,
                OutputFolder = output,
                VolumeSizeBytes = OneMebibyte,
                Password = SamplePassword,
                OuterContainer = PackOuterContainer.SevenZip
            });

            Assert.True(result.Success, "没有 Rar.exe 也必须能出 7z 外层：" + result.Describe());
            Assert.Equal(0, runner.RarCalls);
            Assert.True(File.Exists(outerPath));

            // 日志里要说清用的是哪个容器、哪个 7-Zip（不变量 14）。
            Assert.Contains(
                result.LogLines,
                line => line.Contains("外层容器", StringComparison.Ordinal)
                        && line.Contains("7z", StringComparison.Ordinal));
        }

        [Fact]
        public async Task 真7z端到端_7z外层容器_密码对能列出_错密码列不出()
        {
            string sevenZip = LocateSevenZip();

            if (string.IsNullOrEmpty(sevenZip))
            {
                Skip("测试机上没有内置 7z.exe（ArchiveFixer/tools/7zip/7z.exe）");
                return;
            }

            string source = CreateSourceFolder("A-7ze2e", 2, 700 * 1024);
            string output = Path.Combine(_root, "B-7ze2e");

            // ⚠ 第 46 条：外层 7z 用**源名**（`A-7ze2e.7z`），落在落点目录（源的父目录）。
            string outerPath = Path.Combine(_root, "A-7ze2e.7z");

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
                    OuterContainer = PackOuterContainer.SevenZip
                },
                logLines.Add);

            Assert.True(result.Success, result.Describe());
            Assert.Equal(PackOuterContainer.SevenZip, result.OuterContainer);
            Assert.Equal(outerPath, result.OuterPath);
            Assert.True(File.Exists(outerPath), "7z 外层容器应当存在：" + outerPath);
            Assert.True(new FileInfo(outerPath).Length > 0);

            /*
             * ⚠ 第 46 条的默认收尾（用户："其余物……默认删掉，因为这个对用户来说一点用没有"）：
             * 成功之后**装分卷的文件夹 B 已经被彻底删除**，只剩外层容器。
             */
            Assert.False(Directory.Exists(output), "默认档下其余物（装分卷的文件夹）应当已被删除");
            Assert.Contains("其余物", result.CleanupNote, StringComparison.Ordinal);
            Assert.Contains("已彻底删除", result.CleanupNote, StringComparison.Ordinal);
            Assert.Contains("原包：不动", result.CleanupNote, StringComparison.Ordinal);
            Assert.True(Directory.Exists(source), "原包默认不动");

            // 分卷清单在删之前就已经收好了（结果里照样能报出切了几卷）。
            Assert.Equal(2, result.Volumes.Count);

            // 真 7z 自己列一遍外层容器：对密码能列出条目（-mhe=on 的文件名也解得开）。
            (int okExit, string okOut, string okErr) = RunSevenZip(sevenZip, "l", "-slt", "-p" + SamplePassword, outerPath);

            Assert.True(okExit == 0, $"对密码应当能列出 7z 外层容器（退出码 {okExit}）：{okOut}{okErr}");

            IReadOnlyList<string> entries = PackingVerifier.ParseSevenZipSltList(okOut);

            Assert.Contains(entries, e => e.EndsWith("A-7ze2e.7z.001", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(entries, e => e.EndsWith("A-7ze2e.7z.002", StringComparison.OrdinalIgnoreCase));

            // 错密码：列不出来（不许当成"空包"或"坏包"绕过去）。
            (int badExit, string badOut, string badErr) = RunSevenZip(sevenZip, "l", "-slt", "-p" + "wrong-password", outerPath);

            Assert.NotEqual(0, badExit);
            Assert.DoesNotContain("A-7ze2e.7z.001", badOut, StringComparison.Ordinal);

            // 日志里搜不到密码明文。
            string joined = string.Join("\n", result.LogLines) + "\n" + result.Describe()
                            + "\n" + result.Message + "\n" + result.FailureReason + "\n" + string.Join("\n", logLines);

            Assert.DoesNotContain(SamplePassword, joined, StringComparison.Ordinal);
            Assert.Contains(PackingPasswordPolicy.SetLogLine, joined, StringComparison.Ordinal);
        }

        // ══════════════════════════ ⑪ 自选 Rar.exe 路径（用户 2026-09-23 决定） ══════════════════════════

        [Fact]
        public void 自选RarExe路径_合法就用它_留空回落已装目录()
        {
            string fakeRar = Path.Combine(_root, "我自己的-Rar.exe");
            File.WriteAllText(fakeRar, "占位：只验证'自选路径被解析链采纳'，本用例不会执行它");

            // ① 填了一个真实存在的文件 → 就用它（即使"已装 WinRAR 目录"这一档被关掉）。
            var custom = new ToolLocator { UseWinRarInstallation = false, CustomRarExePath = fakeRar };

            Assert.True(custom.RarExists);
            Assert.True(custom.IsUsingCustomRarPath);
            Assert.Equal(fakeRar, custom.RarExePath);
            Assert.Contains("自选", custom.DescribeRarResolution(), StringComparison.Ordinal);
            Assert.Contains(fakeRar, custom.DescribeRarResolution(), StringComparison.Ordinal);

            // ② 填了一个不存在的 → **不吃掉后面的档位**（与 UnRAR 同一口径：回落到下一档并如实说明）。
            var missing = new ToolLocator { CustomRarExePath = Path.Combine(_root, "没有这个-Rar.exe") };

            Assert.False(missing.IsUsingCustomRarPath);
            Assert.True(missing.RarExists, "回落到'已装 WinRAR 目录'那一档（本机确实装了 WinRAR）");
            Assert.Contains("已装 WinRAR 目录", missing.DescribeRarResolution(), StringComparison.Ordinal);

            // ③ 留空 = 自动：用本机已装 WinRAR 目录里的那一份（本机实测有 C:\Program Files\WinRAR\Rar.exe）。
            var auto = new ToolLocator();

            if (!auto.RarExists)
            {
                Skip("测试机上没有 Rar.exe（没装 WinRAR）—— 第 ③ 段跳过，前两段照样跑到了");
                return;
            }

            Assert.False(auto.IsUsingCustomRarPath);
            Assert.Contains("已装 WinRAR 目录", auto.DescribeRarResolution(), StringComparison.Ordinal);
            Assert.EndsWith("Rar.exe", auto.RarExePath, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void 设置里的RarExe路径_不存在时保存被拦下并说明改法()
        {
            string previous = ToolLocator.Default.CustomRarExePath;

            try
            {
                SettingsViewModel viewModel = CreateSettingsViewModel("data-rar");
                string typed = Path.Combine(_root, "并不存在的-Rar.exe");

                viewModel.CustomRarExePath = typed;
                viewModel.SaveCommand.Execute(null);

                // 没有关窗（DialogResult 仍为 null）= 保存没有发生。
                Assert.Null(viewModel.DialogResult);
                Assert.Contains("Rar.exe 路径", viewModel.Message, StringComparison.Ordinal);
                Assert.Contains("不存在", viewModel.Message, StringComparison.Ordinal);
                Assert.Contains("清空这一格", viewModel.Message, StringComparison.Ordinal);

                // 说清"留空是什么行为"（= 自动用本机已装 WinRAR 目录里的那一份）。
                Assert.Contains(StatusText.SettingsRarExePathEmptyHint, viewModel.Message, StringComparison.Ordinal);

                // 最要紧的一条：用户填的值**还在**（不是"被清空 + 告诉你保存成功"）。
                Assert.Equal(typed, viewModel.CustomRarExePath);

                // 走正常的 Normalize 一定会被清空 —— 那正是必须拦在前面的原因。
                var probe = new AppSettings { CustomRarExePath = typed };
                probe.Normalize();

                Assert.Equal(string.Empty, probe.CustomRarExePath);

                // 合法文件 → 保存通过，而且值真的推给了外部工具路径的唯一来源。
                string real = Path.Combine(_root, "Rar-占位.exe");
                File.WriteAllText(real, "占位：只验证路径被接受");

                SettingsViewModel ok = CreateSettingsViewModel("data-rar-ok");

                ok.CustomRarExePath = real;

                // 工具状态那一行要能看出"用的是自选的那份"。
                Assert.Contains("自选", ok.RarToolStatusText, StringComparison.Ordinal);

                ok.SaveCommand.Execute(null);

                Assert.True(ok.DialogResult);
                Assert.Equal(real, ok.CustomRarExePath);
                Assert.Equal(real, ToolLocator.Default.CustomRarExePath);
            }
            finally
            {
                ToolLocator.Default.CustomRarExePath = previous;
            }
        }

        [Fact]
        public void 解压方式页里真的有RarExe那一格_并且写明许可边界()
        {
            /*
             * 2026-09-24 第 11 条之后外部工具路径归到②解压方式页的「引擎」分组
             * （原来是设置窗口的「高级设置」里）。
             */
            string xaml = File.ReadAllText(
                Path.Combine(XamlBindingScan.RepositoryRoot, "ArchiveFixer", "Views", "Tabs", "ExtractionTab.xaml"));

            Assert.Contains("CustomRarExePath", xaml, StringComparison.Ordinal);
            Assert.Contains("SelectRarExeCommand", xaml, StringComparison.Ordinal);
            Assert.Contains("RarExePathHint", xaml, StringComparison.Ordinal);
            Assert.Contains("RarToolStatusText", xaml, StringComparison.Ordinal);

            // 文案必须写明许可边界，而不是只写一个"路径"。
            Assert.Contains("绝不随包分发", StatusText.SettingsRarExePathHint, StringComparison.Ordinal);
            Assert.Contains("共享软件", StatusText.SettingsRarExePathHint, StringComparison.Ordinal);
            Assert.Contains("你自己安装", StatusText.SettingsRarExePathHint, StringComparison.Ordinal);
            Assert.Contains("留空", StatusText.SettingsRarExePathHint, StringComparison.Ordinal);
        }

        private SettingsViewModel CreateSettingsViewModel(string dataFolder)
        {
            return new SettingsViewModel(
                AppSettings.CreateDefault(),
                new SettingsService(new PathService
                {
                    DataRootDirectory = Path.Combine(_root, dataFolder)
                }));
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
        /// 可控的假 runner：验"没有 Rar.exe 时一步都不跑""第二步取消""产物对不上""7z 外层的命令与校验"
        /// 这类判据，不需要真的装 WinRAR / 起进程（验收判据点名要求用假 runner 断言没被调用）。
        /// </summary>
        private sealed class FakePackRunner : IPackProcessRunner
        {
            private readonly IPackProcessRunner? _sevenZipDelegate;
            private readonly Func<IReadOnlyList<string>, PackStepResult>? _sevenZipResult;
            private readonly Func<IReadOnlyList<string>, PackStepResult>? _sevenZipListResult;
            private readonly Func<IReadOnlyList<string>, PackStepResult>? _rarCreateResult;
            private readonly Func<IReadOnlyList<string>, PackStepResult>? _rarListResult;

            public FakePackRunner(
                IPackProcessRunner? sevenZipDelegate = null,
                Func<IReadOnlyList<string>, PackStepResult>? sevenZipResult = null,
                Func<IReadOnlyList<string>, PackStepResult>? rarCreateResult = null,
                Func<IReadOnlyList<string>, PackStepResult>? rarListResult = null,
                Func<IReadOnlyList<string>, PackStepResult>? sevenZipListResult = null)
            {
                _sevenZipDelegate = sevenZipDelegate;
                _sevenZipResult = sevenZipResult;
                _rarCreateResult = rarCreateResult;
                _rarListResult = rarListResult;
                _sevenZipListResult = sevenZipListResult;
            }

            public int TotalCalls { get; private set; }

            public int SevenZipCalls { get; private set; }

            /// <summary>7z 的列条目调用（外层容器 = 7z 时的产物校验）。</summary>
            public int SevenZipListCalls { get; private set; }

            public int RarCreateCalls { get; private set; }

            public int RarListCalls { get; private set; }

            /// <summary>走了 Rar.exe 这条路的调用总数（"没有 Rar.exe 也照常成功"这条判据靠它）。</summary>
            public int RarCalls => RarCreateCalls + RarListCalls;

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

                    /*
                     * 7z 也有"列条目"这一步（外层容器 = 7z 时的产物校验）：命令的第一个参数是 l。
                     * 内层分卷与外层容器都是 a（靠有没有 -v 参数区分），所以只能按子命令认。
                     */
                    bool isSevenZipListing = arguments.Count > 0 &&
                                             string.Equals(arguments[0], "l", StringComparison.OrdinalIgnoreCase);

                    if (isSevenZipListing)
                    {
                        SevenZipListCalls++;

                        if (_sevenZipListResult != null)
                        {
                            return _sevenZipListResult(arguments);
                        }

                        if (_sevenZipDelegate != null)
                        {
                            return await _sevenZipDelegate
                                .RunAsync(tool, arguments, usedPassword, progress, cancellationToken)
                                .ConfigureAwait(false);
                        }

                        return Ok();
                    }

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

            /// <summary>一次 7z 调用是不是"切分卷"（只有它带 -v）。</summary>
            public static bool IsVolumeCall(IReadOnlyList<string> arguments)
            {
                return arguments.Any(a => a.StartsWith("-v", StringComparison.Ordinal));
            }
        }
    }
}
