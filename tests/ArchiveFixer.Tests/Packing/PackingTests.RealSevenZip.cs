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

/// <summary>PackingTests 拆分：⑥ 真 7z 端到端 / ⑦ 进度 / ⑧ 取消 / ⑨ 产物校验。</summary>
public partial class PackingTests
{

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

    /// <summary>
    /// 真 7z 端到端（2026-09-26 第 46 条）：**源是一个文件**。
    ///
    /// <para>这是他在真机上最先会试的那一档（他原话："选一个文件也可以"），而它比"源是文件夹"多了三件事：
    /// ①在文件**旁边**建同名文件夹、把文件**硬链接**进去（零字节搬动）；②那个文件夹算**其余物**
    /// （"他不算是原包的内容"），默认档下要跟着装分卷的文件夹一起被删掉；
    /// ③装分卷的文件夹会因为"同名文件夹已经被占用"而叫 <c>单文件源(1)</c>。
    /// ⛔ 全程**原文件一个字节都不能动**（硬链接删除不影响它 —— 这一条必须真跑才算数）。</para>
    /// </summary>
    [Fact]
    public async Task 真7z端到端_源是单个文件_硬链接进同名文件夹_收尾后只剩产物与原件()
    {
        string sevenZip = LocateSevenZip();

        if (string.IsNullOrEmpty(sevenZip))
        {
            Skip("测试机上没有内置 7z.exe");
            return;
        }

        var tools = new ToolLocator();

        const string baseName = "单文件源";

        string sourcePath = Path.Combine(_root, baseName + ".bin");
        byte[] payload = new byte[1_500_000];

        Random.Shared.NextBytes(payload);
        File.WriteAllBytes(sourcePath, payload);

        string before = Sha256Of(sourcePath);

        var service = new PackingService(tools, null, _ => long.MaxValue);

        PackingResult result = await service.PackAsync(new PackingRequest
        {
            SourceFolder = sourcePath,
            VolumeSizeBytes = OneMebibyte,
            Password = SamplePassword,
            RunOptions = new PackingRunOptions
            {
                TargetMode = PackingTargetMode.Local,
                SourceHandling = PackingSourceHandling.KeepInPlace,
                RestHandling = PackingRestHandling.Delete
            }
        });

        Assert.True(result.Success, result.Describe());

        // 产物名**以源名为准**，跟着实际容器走（本机有 Rar.exe 就是 .rar，否则自动 7z）。
        string expectedOuter = Path.Combine(
            _root,
            baseName + (result.OuterContainer == PackOuterContainer.Rar ? ".rar" : ".7z"));

        Assert.Equal(expectedOuter, result.OuterPath);
        Assert.True(File.Exists(expectedOuter), "最终产物必须真的在磁盘上");

        // ① 原文件：还在原地、内容一个字节不差（硬链接被删掉不影响它）。
        Assert.True(File.Exists(sourcePath), "原文件必须还在原地");
        Assert.Equal(before, Sha256Of(sourcePath));

        // ② 默认档：临时建的同名文件夹 + **那些分卷文件**都已经删掉（落点目录里不再有目录、也没有分卷）。
        Assert.False(Directory.Exists(Path.Combine(_root, baseName)), $"临时建的同名文件夹应当已被删掉：{baseName}");
        Assert.Empty(Directory.GetDirectories(_root));
        Assert.Empty(Directory.GetFiles(_root, baseName + ".7z.*"));
        Assert.Contains("已彻底删除", result.CleanupNote, StringComparison.Ordinal);

        // ③ 日志里不许出现密码明文（这一条走的是 -hp / -p，两种形态都不能漏）。
        Assert.DoesNotContain(
            SamplePassword,
            string.Join("\n", result.LogLines),
            StringComparison.Ordinal);

        // ④ 产物里面装的是**顶层的那几个分卷**（用真引擎自己列一遍）。
        //
        // ⚠ 这里**用内置 7-Zip 列 rar**，不用 `Rar.exe lb`：Rar.exe 的输出是**控制台代码页**
        // （本机 936），而测试的 RunProcess 按 UTF-8 解码 —— 英文名看不出来，**中文名会变成乱码**
        // 而让断言假红（第一次就是这么红的）。服务自己那条 rar 校验走的是 OEM 解码（已实测通过），
        // 这里换 7-Zip + `-sccUTF-8` 就能拿到正确的中文条目名。
        (int exit, string stdout, string stderr) = RunSevenZip(
            sevenZip,
            "l", "-slt", "-sccUTF-8", "-y", "-p" + SamplePassword, expectedOuter);

        Assert.True(exit == 0, $"7-Zip 应当能列出产物条目（退出码 {exit}）：{stdout}{stderr}");

        IReadOnlyList<string> entries = PackingVerifier.ParseSevenZipSltList(stdout);

        Assert.Contains(entries, e => e.EndsWith(baseName + ".7z.001", StringComparison.OrdinalIgnoreCase));
        Assert.All(entries, e => Assert.DoesNotContain("\\", e, StringComparison.Ordinal));
    }

    /// <summary>
    /// 真 7z 端到端（2026-09-26 第 46 条 + 追加改口径）：**原包操作 = 移入其余物 + 其余物保留**。
    ///
    /// <para>⚠ 追加改口径之后，分卷不再装在中间文件夹里，所以这一档由程序**现建**一个以源名命名的
    /// 文件夹（源文件夹本来就叫这个名字 → 让位成 <c>A-move(1)</c>）把源搬进去；"其余物保留"
    /// 留下的是**那个文件夹 + 那些分卷文件**。要钉三件事：
    /// ①源**真的**从原地消失了；②它在那个文件夹里、内容一个字节没少；③分卷也还在。</para>
    /// </summary>
    [Fact]
    public async Task 真7z端到端_原包移入其余物_保留档下源被搬进新建的文件夹且内容不丢()
    {
        string sevenZip = LocateSevenZip();

        if (string.IsNullOrEmpty(sevenZip))
        {
            Skip("测试机上没有内置 7z.exe");
            return;
        }

        var tools = new ToolLocator();

        const string baseName = "A-move";

        string source = CreateSourceFolder(baseName, 2, 600 * 1024);
        string sourceHash = HashDirectory(source);

        var service = new PackingService(tools, null, _ => long.MaxValue);

        PackingResult result = await service.PackAsync(new PackingRequest
        {
            SourceFolder = source,
            VolumeSizeBytes = OneMebibyte,
            Password = SamplePassword,
            RunOptions = new PackingRunOptions
            {
                TargetMode = PackingTargetMode.Local,
                SourceHandling = PackingSourceHandling.MoveToRest,
                RestHandling = PackingRestHandling.Keep
            }
        });

        Assert.True(result.Success, result.Describe());

        string outer = Path.Combine(
            _root,
            baseName + (result.OuterContainer == PackOuterContainer.Rar ? ".rar" : ".7z"));

        Assert.True(File.Exists(outer), "最终产物必须真的在磁盘上");

        // 装源包的那个文件夹（这一档才建）：源文件夹本身占着 `A-move`，于是让位成 `A-move(1)`。
        string holder = Path.Combine(_root, baseName + "(1)");

        Assert.False(Directory.Exists(source), "移入其余物之后，源不该还在原地");
        Assert.True(Directory.Exists(holder), $"装原包的那个文件夹应当保留：{holder}");

        string moved = Path.Combine(holder, baseName);

        Assert.True(Directory.Exists(moved), "源文件夹应当被搬进那个文件夹里");
        Assert.Equal(sourceHash, HashDirectory(moved));

        // 其余物那一档是"保留"，所以分卷也还在（就在落点目录里）。
        Assert.NotEmpty(Directory.GetFiles(_root, baseName + ".7z.0*"));

        Assert.Contains("已移入其余物", result.CleanupNote, StringComparison.Ordinal);
        Assert.Contains("保留", result.CleanupNote, StringComparison.Ordinal);
    }

    /// <summary>
    /// 真 7z 端到端（2026-09-26 第 46 条）：**危险组合** —— 原包移入其余物 **+** 其余物彻底删除。
    ///
    /// <para>弹窗里对这一步有红字警告（"这等于连你的原文件夹一起彻底删除"）。这条用例把那个语义
    /// **真跑一遍**：干完之后落点目录里**只剩那一个 <c>.rar</c>**，源与分卷都不在了 ——
    /// 换句话说警告说的就是事实，不是吓唬人。⚠ 只在测试的临时目录里跑，绝不碰任何真实素材。</para>
    /// </summary>
    [Fact]
    public async Task 真7z端到端_原包移入其余物_其余物彻底删除_最后只剩那一个压缩包()
    {
        string sevenZip = LocateSevenZip();

        if (string.IsNullOrEmpty(sevenZip))
        {
            Skip("测试机上没有内置 7z.exe");
            return;
        }

        var tools = new ToolLocator();

        const string baseName = "A-both-delete";

        string source = CreateSourceFolder(baseName, 2, 600 * 1024);

        var service = new PackingService(tools, null, _ => long.MaxValue);

        PackingResult result = await service.PackAsync(new PackingRequest
        {
            SourceFolder = source,
            VolumeSizeBytes = OneMebibyte,
            Password = SamplePassword,
            RunOptions = new PackingRunOptions
            {
                TargetMode = PackingTargetMode.Local,
                SourceHandling = PackingSourceHandling.MoveToRest,
                RestHandling = PackingRestHandling.Delete
            }
        });

        Assert.True(result.Success, result.Describe());

        string outer = Path.Combine(
            _root,
            baseName + (result.OuterContainer == PackOuterContainer.Rar ? ".rar" : ".7z"));

        Assert.True(File.Exists(outer), "最终产物必须真的在磁盘上");

        Assert.False(Directory.Exists(source), "这一档下源文件夹会被连根删掉（弹窗里的红字就是这个意思）");
        Assert.Empty(Directory.GetDirectories(_root));
        Assert.Contains("已彻底删除", result.CleanupNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 真7z端到端_两层都做_rar里装的就是那几个分卷_顶层()
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

        // ⚠ 第 46 条：外层产物用**源名**（`A-both.rar`），落在落点目录（源的父目录）。
        string rarPath = Path.Combine(_root, "A-both.rar");

        var service = new PackingService(tools, null, _ => long.MaxValue);

        PackingResult result = await service.PackAsync(new PackingRequest
        {
            SourceFolder = source,
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
        Assert.Contains("顶层", result.VerificationDetail, StringComparison.Ordinal);

        // 自己再列一遍 rar：追加改口径之后，条目就是**顶层的那几个分卷**（没有文件夹层）。
        string rarExe = tools.RarExePath;
        (int exit, string stdout, string stderr) = RunProcess(rarExe, new[] { "lb", "-p" + SamplePassword, rarPath });

        Assert.True(exit == 0, $"Rar.exe lb 应当能列出条目（退出码 {exit}）：{stdout}{stderr}");

        IReadOnlyList<string> entries = PackingVerifier.ParseBareList(stdout);

        Assert.Equal(2, entries.Count);
        Assert.Contains("A-both.7z.001", entries);
        Assert.Contains("A-both.7z.002", entries);

        // 日志里同样不许出现密码明文（这一条走的是 -hp，与 7z 的 -p 形态不同）。
        string joined = string.Join("\n", result.LogLines) + "\n" + result.Describe();
        Assert.DoesNotContain(SamplePassword, joined, StringComparison.Ordinal);

        // 默认收尾：分卷（其余物）已彻底删除，原包仍在原地，落点目录里只剩 rar。
        Assert.False(File.Exists(Path.Combine(_root, "A-both.7z.001")), "默认档下分卷应当已被删除");
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

            // ⚠ 追加改口径：分卷基路径是算好的（`<落点>\A-progress.7z`），不再由 OutputFolder + 基名拼。
            VolumeBasePath = Path.Combine(output, "A-progress.7z"),
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

        /*
         * ⚠ 2026-09-26 追加改口径：**不再要求目录层**（folderName 传空 = 顶层）。
         * 历史那一版（"归档里有个 B 文件夹"）仍然保留支持 —— 传了 folderName 就照旧判。
         */
        PackingVerification good = PackingVerifier.Verify(
            new[] { "A.7z.001", "A.7z.002" },
            string.Empty,
            volumes);

        Assert.True(good.Ok);
        Assert.Contains("2 个分卷", good.Detail, StringComparison.Ordinal);
        Assert.Contains("顶层", good.Detail, StringComparison.Ordinal);

        PackingVerification missing = PackingVerifier.Verify(
            new[] { "A.7z.001" },
            string.Empty,
            volumes);

        Assert.False(missing.Ok);
        Assert.Contains("少了", missing.Detail, StringComparison.Ordinal);
        Assert.Contains("A.7z.002", missing.Detail, StringComparison.Ordinal);

        PackingVerification stray = PackingVerifier.Verify(
            new[] { "A.7z.001", "A.7z.002", "多余.txt" },
            string.Empty,
            volumes);

        Assert.False(stray.Ok);
        Assert.Contains("多出这些东西", stray.Detail, StringComparison.Ordinal);

        // 传了目录名就按"有目录层"判（历史口径仍然能用）。
        PackingVerification layered = PackingVerifier.Verify(
            new[] { @"B\A.7z.001", @"B\A.7z.002", "B" },
            "B",
            volumes);

        Assert.True(layered.Ok);

        Assert.False(PackingVerifier.Verify(Array.Empty<string>(), string.Empty, volumes).Ok);
        Assert.False(PackingVerifier.Verify(new[] { "A.7z.001" }, string.Empty, Array.Empty<PackingVolume>()).Ok);

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
}
