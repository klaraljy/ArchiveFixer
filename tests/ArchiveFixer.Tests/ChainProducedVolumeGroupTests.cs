using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ArchiveFixer.Tests.Batch120;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 形状回归：**上游单包解出的一整套分卷**（ChainProduced）必须被第二大步凑齐并解开。
    ///
    /// <para>
    /// 形状取自 2026-11-10 真机样本：`nidekikibabe-2026p6.tar` 里是 5 片 `xxx.7z.001~005`，
    /// 而这 5 片是**单卷 7z 按字节切出来的**（7z 把这种读成 `Type = Split`、`Volumes = 5`），
    /// 不是 `7z -v` 原生分卷 —— 这一档在 7z 语义里是"先拼回一整份 7z、再解那份 7z"。
    /// </para>
    ///
    /// <para>
    /// ⚠ 本用例**不承担红检**：运行时事实（日志 `第二大步·组分析 找到 5 片` / `第 1 轮结束：分卷 1 组`）
    /// 证明"定稿后再补登记账本"并不是这一档成立的前提 —— 把补登记摘掉它照样绿。
    /// 留着它是钉住这个真实形状（Split 形态 + 文件头加密 + 归集），别让以后改坏。
    /// </para>
    /// </summary>
    public class ChainProducedVolumeGroupTests
    {
        private const string SampleId = "999000001";

        [Fact]
        public async Task 外层单包解出的整套分卷_必须被第二大步解开且不留片()
        {
            string? sevenZip = LocateSevenZip();
            if (sevenZip == null)
            {
                FixtureGate.Skip("ChainProducedVolumeGroupTests", "找不到 7z.exe");
                return;
            }

            string sampleDir = Path.Combine(Batch120Operation.BackupBatchRoot, SampleId);
            string sourceDir = Path.Combine(sampleDir, "原包");
            if (Directory.Exists(sampleDir))
            {
                Directory.Delete(sampleDir, recursive: true);
            }

            Directory.CreateDirectory(sourceDir);

            const int contentBytes = 4 * 1024 * 1024;
            string tarPath = BuildFixture(sevenZip, sourceDir, contentBytes);

            var recipe = new JsonObject
            {
                ["sampleId"] = SampleId,
                ["category"] = "Normal",
                ["kind"] = "normal",
                ["scenario"] = new JsonObject
                {
                    ["placement"] = "Original",
                    ["sourceHandling"] = "DeleteInPlace",
                    ["contentHandling"] = "Delete",
                    ["collect"] = true
                }
            };

            File.WriteAllText(
                Path.Combine(sampleDir, "recipe.json"),
                recipe.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            Batch120RunRecord record = await Batch120Operation.RunOneAsync(SampleId);

            // 产物必须落地：分卷里的 content.bin 应该出现在落点
            string? content = record.After
                .Select(item => Path.Combine(record.Workspace, item.Path))
                .FirstOrDefault(path => Path.GetFileName(path) == "content.bin" && File.Exists(path));

            Assert.True(content != null, "最终内容 content.bin 没有落到工作区（整组被跳过）");
            Assert.Equal(contentBytes, new FileInfo(content!).Length);

            // 分卷片解完必须回收，不许留在成品目录里
            int leftoverPieces = Directory
                .GetFiles(record.Workspace, "*.7z.00?", SearchOption.AllDirectories)
                .Count(path => Path.GetFileName(path).StartsWith("aaa-B1", StringComparison.OrdinalIgnoreCase));

            Assert.True(leftoverPieces == 0, $"分卷片残留 {leftoverPieces} 个");

            Directory.Delete(sampleDir, recursive: true);
            _ = tarPath;
        }

        /// <summary>
        /// content.bin → 单个 7z → **按字节切成 5 片**（`.7z.001~005`，与真机同一形态：
        /// 7z 把这种叫 `Type = Split`，不是 7z 原生的 `-v` 分卷）→ 外面套一层 tar；返回 tar 路径。
        /// </summary>
        private static string BuildFixture(string sevenZip, string sourceDir, int contentBytes)
        {
            string work = Path.Combine(sourceDir, "..", "work");
            work = Path.GetFullPath(work);
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }

            Directory.CreateDirectory(work);

            string content = Path.Combine(work, "content.bin");
            var buffer = new byte[contentBytes];
            new Random(20261110).NextBytes(buffer);
            File.WriteAllBytes(content, buffer);

            Run(sevenZip, work, "a", "-t7z", "-mx=0", "-y", "-pDemo#Pass1", "-mhe=on", "aaa-B1.7z", "content.bin");
            File.Delete(content);

            string single = Path.Combine(work, "aaa-B1.7z");
            long total = new FileInfo(single).Length;
            long partSize = (total + 4) / 5;

            var pieces = new List<string>();
            using (var reader = new FileStream(single, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                for (int index = 0; index < 5; index++)
                {
                    string piece = Path.Combine(work, $"aaa-B1.7z.{index + 1:000}");
                    using (var writer = new FileStream(piece, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        long remaining = Math.Min(partSize, total - reader.Position);
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

                    pieces.Add($"aaa-B1.7z.{index + 1:000}");
                }
            }

            Assert.Equal(5, pieces.Count);
            File.Delete(single);

            string tar = Path.Combine(sourceDir, "aaa.tar");
            Run("tar", work, new[] { "-cf", tar }.Concat(pieces).ToArray());

            Directory.Delete(work, recursive: true);
            return tar;
        }

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

        private static string? LocateSevenZip()
        {
            foreach (string candidate in new[]
                     {
                         @"E:\ArchiveFixer\tools\7zip\7z.exe",
                         Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe")
                     })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}
