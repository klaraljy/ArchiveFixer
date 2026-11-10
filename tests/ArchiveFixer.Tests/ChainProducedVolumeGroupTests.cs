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
    /// 缺陷复现：**上游单包解出的一整套分卷**（ChainProduced）在定稿后进不了第二大步账本。
    ///
    /// <para>
    /// 真机现场（2026-11-10）：`nidekikibabe-2026p6.tar` 解出 5 片 `xxx.7z.001~005`，
    /// 第二大步只看到 1 片 ⇒ 整组被跳过 ⇒ 最终内容一个字节没落地。
    /// 根因：解压结束时产物还在 `.ArchiveFixer.work\...\stage`，登记器按工作区规则整批跳过；
    /// 定稿搬到落点后没人补登记。
    /// </para>
    ///
    /// <para>
    /// 这个用例就是那个形状：外层单一 tar → 里层 5 片 7z 分卷 → 分卷里是最终内容。
    /// ⛔ 它必须能红：把「定稿后补登记」摘掉，本用例应当失败。
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
                    ["contentHandling"] = "Delete"
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

        /// <summary>content.bin → 7z 切 5 片 → 外面套一层 tar；返回 tar 路径。</summary>
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

            Run(sevenZip, work, "a", "-t7z", "-mx=0", "-y", "-v1048576b", "aaa-B1.7z", "content.bin");
            File.Delete(content);

            string[] pieces = Directory
                .GetFiles(work, "aaa-B1.7z.*")
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(Path.GetFileName)
                .ToArray()!;

            Assert.Equal(5, pieces.Length);

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
