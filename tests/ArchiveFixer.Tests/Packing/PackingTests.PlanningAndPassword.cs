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

namespace ArchiveFixer.Tests;

/// <summary>PackingTests 拆分：① 分卷规划 / ② 密码 / ③ 没有 Rar.exe / ④ 落点 / ⑤ 空间。</summary>
public partial class PackingTests
{

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
    public void 默认落点_分卷直接落在源旁边_rar也用源名()
    {
        string source = Path.Combine(_root, "素材");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "a.bin"), new byte[64]);

        /*
         * ⚠ 2026-09-26 追加改口径（用户原话："7z 分卷文件外面好像不用再套一件文件夹了，
         * 有点太多余了，这样也不用去考虑文件名重复的问题了"）：
         * 分卷**直接落在落点目录里**（默认 = 源旁边），不再有 `素材(1)` 那一层中间文件夹。
         */
        string expectedRar = Path.Combine(_root, "素材.rar");

        Assert.True(PackingPlan.TryCreate(
            new PackingRequest { SourceFolder = source, Password = SamplePassword },
            out PackingPlan? plan,
            out string error), error);

        Assert.NotNull(plan);
        Assert.Equal(_root, plan!.OutputFolder);
        Assert.Equal(Path.Combine(_root, "素材.7z"), plan.VolumeBasePath);
        Assert.Equal("素材.7z", plan.VolumeBaseName);
        Assert.Equal(expectedRar, plan.RarPath);
        Assert.Equal(_root, plan.TargetDirectory);

        // 64 字节的内容：<1 GiB → 目标 2 卷（每卷上限 1 MiB 的下限）。
        Assert.Equal(2, plan.VolumeTargetCount);
        Assert.Equal(1L * 1024 * 1024, plan.VolumeSizeBytes);
        Assert.Equal(1, plan.PlannedVolumeCount);
    }

    /// <summary>
    /// ⛔ 分卷撞名时的判据必须看**真正的第一卷**（`素材.7z.001`）：只看 `素材.7z` 是看不出来的
    /// （那个文件根本不存在），而 7z 带 <c>-y</c> 会把已有的分卷直接覆盖掉。
    /// </summary>
    [Fact]
    public void 分卷撞名_看的是真正的第一卷_而不是基名()
    {
        string source = Path.Combine(_root, "素材");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "a.bin"), new byte[64]);

        // 已经存在 `素材.7z.001`（上一次留下的）→ 这一次必须让位成 `素材(1).7z`。
        File.WriteAllBytes(Path.Combine(_root, "素材.7z.001"), new byte[16]);

        Assert.True(PackingPlan.TryCreate(
            new PackingRequest { SourceFolder = source, Password = SamplePassword },
            out PackingPlan? plan,
            out string error), error);

        Assert.Equal(Path.Combine(_root, "素材(1).7z"), plan!.VolumeBasePath);
        Assert.Equal(Path.Combine(_root, "素材.rar"), plan.RarPath);
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
            // ⚠ 追加改口径：外层 7z 里就是**顶层的那一个分卷**。
            + "Path = A-norar.7z.001" + Environment.NewLine));

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
        Assert.Contains("不能落在源文件夹里面", rejected!, StringComparison.Ordinal);

        Assert.False(PackingPlan.TryCreate(
            new PackingRequest { SourceFolder = source, OutputFolder = output, RarPath = rar, Password = SamplePassword },
            out _,
            out string error));
        Assert.Contains("不能落在源文件夹里面", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠ 追加改口径：**"外层容器不能落在落点目录里面"那条判据退役**（旧模型里落点是一个中间文件夹、
    /// 外层容器会把它整个装进去；现在分卷直接落在落点目录里、外层容器也只装点名的分卷文件，
    /// 自己也住在同一个目录里）。这条反向钉住新口径：rar 与落点同目录必须放行。
    /// </summary>
    [Fact]
    public void 结果rar与落点同目录_放行()
    {
        string source = CreateSourceFolder("A-rarinB", 1, 1024);
        string output = Path.Combine(_root, "B-rarinB");
        string rar = Path.Combine(output, "结果.rar");

        Assert.Null(PackingPlan.ValidatePlacement(source, output, rar));
    }

    [Fact]
    public void rar落在A里面_同样拒绝()
    {
        string source = CreateSourceFolder("A-cross", 1, 1024);

        // rar 写进源目录 → 不变量 12（源目录里一个字节都不许写）。
        string? rarInSource = PackingPlan.ValidatePlacement(
            source,
            Path.Combine(_root, "B-cross"),
            Path.Combine(source, "结果.rar"));

        Assert.NotNull(rarInSource);
        Assert.Contains("不能放在源文件夹里面", rarInSource!, StringComparison.Ordinal);

        // A 在 B 里面 → 外层容器装的是整个落点目录，会把源文件原样再存一份。
        /*
         * ⚠ 2026-09-26 追加改口径：原来这里还拒"源文件夹落在落点目录里面"——那条是
         * "B（中间文件夹）会被整个装进外层容器"那个旧模型的前提。现在分卷直接落在落点目录里、
         * 外层容器装的是**逐个点名的分卷文件**，而"默认落点 = 源旁边"本身就让源位于落点目录里，
         * 所以那条判据整块退役 —— 这里反向钉住"默认形状必须放行"。
         */
        string? sourceInOutput = PackingPlan.ValidatePlacement(
            source,
            _root,
            Path.Combine(Path.GetDirectoryName(_root) ?? _root, "B-cross.rar"));

        Assert.Null(sourceInOutput);
    }

    [Fact]
    public void rar已存在_不默认覆盖()
    {
        string source = CreateSourceFolder("A-conflict", 1, 1024);
        string output = Path.Combine(_root, "B-conflict");

        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "别人放的东西.txt"), "x");

        // ⚠ 追加改口径：落点目录**可以是非空的**（默认就是源所在的那个目录），不再拒。
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

        Assert.False(result.Success, result.Describe());
        Assert.Contains(
            result.LogLines,
            line => line.Contains("空间未知不算够", StringComparison.Ordinal)
                    || line.Contains("取不到", StringComparison.Ordinal));
    }
}
