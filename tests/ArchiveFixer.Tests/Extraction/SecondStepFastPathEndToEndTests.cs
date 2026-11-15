using ArchiveFixer.Tests.Batch120;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests;

/// <summary>
/// 端到端：**外层单包 + 同基名非等大 7z 分卷**（真机 2026-11-10 形状）。
///
/// <para>钉三件事：① 第二大步不再对"名字槽齐全 + 字节和相等"的组做整组 7z t
/// （日志里不许出现 `[第二大步·试开]`）；② 快通道覆盖判据在"外层包同基名、已消费"时仍生效；
/// ③ 最终内容照常落地、分卷片不残留。</para>
///
/// <para>红检撤销点：把 <c>VolumeBoundaryPipeline.IsCoveredByFastPath</c> 改成永远 false，
/// 或把 <c>SevenZipBoundaryResolver.TryResolveNonUniformAsync</c> 改回"必须试开" ⇒ 本用例必红。</para>
/// </summary>
[Trait("Category", "Extended")]
public sealed class SecondStepFastPathEndToEndTests
{
    [Fact]
    public async Task 外层同基名_内层非等大分卷_不整组试开且内容照常落地()
    {
        string? sevenZip = LocateFirst(@"E:\ArchiveFixer\tools\7zip\7z.exe", Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe"));
        string? tar = LocateFirst(@"C:\Windows\System32\tar.exe");

        if (sevenZip == null || tar == null)
        {
            FixtureGate.Skip(nameof(SecondStepFastPathEndToEndTests), "找不到 7z.exe 或 tar.exe");
            return;
        }

        const string sampleId = "999000006";
        // 917/927 备份母本只读：这一份夹具写进本次运行目录，不再写进 BackupBatchRoot。
        string fixtureBackupRoot = Path.Combine(Batch120Operation.RunRoot, "fixture-backup");
        string sampleDir = Path.Combine(fixtureBackupRoot, sampleId);
        string sourceDir = Path.Combine(sampleDir, "原包");

        if (Directory.Exists(sampleDir))
        {
            Directory.Delete(sampleDir, recursive: true);
        }

        Directory.CreateDirectory(sourceDir);

        const int contentBytes = 8 * 1024 * 1024;
        BuildNonUniformFixture(sevenZip, tar, sourceDir, contentBytes);

        var recipe = new JsonObject
        {
            ["sampleId"] = sampleId,
            ["category"] = "Normal",
            ["kind"] = "normal",
            ["scenario"] = new JsonObject
            {
                ["placement"] = "Original",
                ["sourceHandling"] = "KeepInPlace",
                ["contentHandling"] = "Keep"
            }
        };

        File.WriteAllText(
            Path.Combine(sampleDir, "recipe.json"),
            recipe.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        bool keepFixture = Environment.GetEnvironmentVariable("ARCHIVEFIXER_KEEP_FIXTURE") == "1";

        try
        {
            Batch120RunRecord record = await Batch120Operation.RunOneAsync(
                sampleId,
                fixtureBackupRoot,
                Batch120Operation.TestBatchRoot,
                Batch120Operation.SpecifiedBatchRoot,
                Batch120Operation.RecordRoot,
                keepLogs: true);

            Assert.True(string.IsNullOrEmpty(record.Error), "一键处理报错：" + record.Error);

            string? content = record.After
                .Select(item => Path.Combine(record.Workspace, item.Path))
                .FirstOrDefault(path => Path.GetFileName(path) == "content.bin" && File.Exists(path));

            Assert.True(content != null, "最终内容 content.bin 没有落地（第二大步整组被跳过）");
            Assert.Equal(contentBytes, new FileInfo(content!).Length);

            int leftoverPieces = Directory
                .GetFiles(record.Workspace, "*.7z.00?", SearchOption.AllDirectories)
                .Count(path => Path.GetFileName(path).StartsWith("aaa", StringComparison.OrdinalIgnoreCase));

            Assert.Equal(0, leftoverPieces);

            string[] workspaceShells = Directory.GetDirectories(record.Workspace, ".ArchiveFixer.work", SearchOption.AllDirectories);
            Assert.True(
                workspaceShells.Length == 0,
                "成功链结束后工作区壳没清掉：" + string.Join("；", workspaceShells));

            string logDir = Path.Combine(Batch120Operation.RecordRoot, "logs-" + sampleId);
            string? logFile = Directory.Exists(logDir)
                ? Directory.GetFiles(logDir, "*.log", SearchOption.AllDirectories).Where(path => !path.EndsWith(".user.log", StringComparison.OrdinalIgnoreCase)).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;

            Assert.True(logFile != null, "没有拿到这次运行的日志（KEEP_LOGS 没生效）");

            string log = File.ReadAllText(logFile!, Encoding.UTF8);

            Assert.Contains("[第二大步", log, StringComparison.Ordinal);
            Assert.DoesNotContain("[第二大步·试开]", log, StringComparison.Ordinal);
            Assert.DoesNotContain("上次没跑完的残留", log, StringComparison.Ordinal);
        }
        finally
        {
            if (!keepFixture)
            {
                try
                {
                    if (Directory.Exists(sampleDir))
                    {
                        Directory.Delete(sampleDir, recursive: true);
                    }
                }
                catch
                {
                }

                try
                {
                    string recordPath = Path.Combine(Batch120Operation.RecordRoot, sampleId + ".json");
                    if (File.Exists(recordPath))
                    {
                        File.Delete(recordPath);
                    }

                    string logDir = Path.Combine(Batch120Operation.RecordRoot, "logs-" + sampleId);
                    if (Directory.Exists(logDir))
                    {
                        Directory.Delete(logDir, recursive: true);
                    }
                }
                catch
                {
                }
            }
        }
    }

    /// <summary>
    /// content.bin → 单个 7z（文件名加密，与真机同档）→ 按**不等分**切 5 片 →
    /// 外面套一个**同基名**的 tar。形状与 `nidekikibabe-2026p6.tar` 一致，只是体积缩到 8 MB。
    /// </summary>
    private static void BuildNonUniformFixture(string sevenZip, string tar, string sourceDir, int contentBytes)
    {
        string work = Path.GetFullPath(Path.Combine(sourceDir, "..", "work"));

        if (Directory.Exists(work))
        {
            Directory.Delete(work, recursive: true);
        }

        Directory.CreateDirectory(work);

        string content = Path.Combine(work, "content.bin");
        var buffer = new byte[contentBytes];
        new Random(20261110).NextBytes(buffer);
        File.WriteAllBytes(content, buffer);

        const string pieceBase = "aaa";
        const string archiveName = pieceBase + ".7z";

        Run(sevenZip, work, "a", "-t7z", "-mx=0", "-y", "-pDemo#Pass1", "-mhe=on", archiveName, "content.bin");
        File.Delete(content);

        string single = Path.Combine(work, archiveName);
        long total = new FileInfo(single).Length;

        // 不等分：35% / 20% / 15% / 15% / 剩余（真机就是"中间片小于首片"的非等大形状）。
        double[] fractions = { 0.35, 0.20, 0.15, 0.15, 0.15 };
        var boundaries = new List<long>();
        long consumed = 0;

        foreach (double fraction in fractions)
        {
            long length = (long)(total * fraction);
            boundaries.Add(Math.Min(length, total - consumed));
            consumed += boundaries[^1];
        }

        boundaries.Add(total - consumed);

        var pieces = new List<string>();

        using (var reader = new FileStream(single, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            for (int index = 0; index < boundaries.Count; index++)
            {
                if (boundaries[index] <= 0)
                {
                    continue;
                }

                string pieceName = $"{pieceBase}.7z.{index + 1:000}";
                string piecePath = Path.Combine(work, pieceName);

                using (var writer = new FileStream(piecePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    long remaining = boundaries[index];
                    var chunk = new byte[1024 * 1024];

                    while (remaining > 0)
                    {
                        int read = reader.Read(chunk, 0, (int)Math.Min(chunk.Length, remaining));

                        if (read <= 0)
                        {
                            break;
                        }

                        writer.Write(chunk, 0, read);
                        remaining -= read;
                    }
                }

                pieces.Add(pieceName);
            }
        }

        Assert.InRange(pieces.Count, 4, 6);
        File.Delete(single);

        Run(tar, work, new[] { "-cf", Path.Combine(sourceDir, pieceBase + ".tar") }.Concat(pieces).ToArray());

        Directory.Delete(work, recursive: true);
    }

    private static string? LocateFirst(params string[] candidates)
        => candidates.FirstOrDefault(File.Exists);

    private static void Run(string exe, string workdir, params string[] args)
    {
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(exe)} 失败（{process.ExitCode}）：{output}");
        }
    }
}
