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

/// <summary>PackingTests 拆分：⑩ 外层容器三选一。</summary>
public partial class PackingTests
{

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
                // ⚠ 追加改口径：归档里就是**顶层的那几个分卷**（没有中间文件夹那一层）。
                + "Path = A-7zouter.7z.001" + Environment.NewLine
                + Environment.NewLine
                + "Path = A-7zouter.7z.002" + Environment.NewLine));

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

        // ① 外层命令：字面模板（追加改口径后给的是**逐个点名的分卷文件名**，进程在落点目录里跑）。
        IReadOnlyList<string> outerCall = runner.Arguments.Single(a => !FakePackRunner.IsVolumeCall(a) && a[0] == "a");

        Assert.Equal(
            new[]
            {
                "a", "-t7z", "-mx=5", "-mhe=on", "-bsp1", "-sccUTF-8", "-y",
                "-p" + SamplePassword, outerPath, "A-7zouter.7z.001", "A-7zouter.7z.002"
            },
            outerCall);

        // ⛔ 那一步必须在**落点目录**里执行（给绝对路径会把路径也存进归档，多出一层目录）。
        int outerIndex = runner.Arguments.IndexOf(outerCall);

        Assert.Equal(output, runner.WorkingDirectories[outerIndex]);

        // ② 列条目走的是 7z 的 -slt（机器格式），不是给人看的表格。
        IReadOnlyList<string> listCall = runner.Arguments.Single(a => a[0] == "l");

        Assert.Equal(
            new[] { "l", "-slt", "-sccUTF-8", "-y", "-p" + SamplePassword, outerPath },
            listCall);

        // ③ 产物名与校验：外层 7z 里装的是**顶层**那两个分卷。
        Assert.Equal("A-7zouter.7z", Path.GetFileName(result.OuterPath));
        Assert.Equal(2, result.Volumes.Count);
        Assert.Contains("2 个分卷", result.VerificationDetail, StringComparison.Ordinal);
        Assert.Contains("顶层", result.VerificationDetail, StringComparison.Ordinal);
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
                + "Path = A-7znorar.7z.001" + Environment.NewLine));

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
         * ⚠ 2026-09-26 追加改口径（用户："7z 分卷文件外面好像不用再套一件文件夹了"）：
         * 分卷**直接落在落点目录里**，成功之后默认把它们彻底删掉，只剩外层容器 ——
         * 落点目录本身还在（它是源的父目录）。
         */
        Assert.False(File.Exists(Path.Combine(_root, "A-7ze2e.7z.001")), "默认档下分卷应当已被删除");
        Assert.False(File.Exists(Path.Combine(_root, "A-7ze2e.7z.002")), "默认档下分卷应当已被删除");
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
}
