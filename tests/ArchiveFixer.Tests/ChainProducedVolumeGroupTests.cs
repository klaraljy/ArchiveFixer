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
    /// 形状回归 + 红检：**外层单一包解出的整套分卷**（ChainProduced）必须被第二大步凑齐并解开。
    ///
    /// <para>
    /// 形状取自 2026-11-10 真机样本 `nidekikibabe-2026p6.tar`：外层包解出 5 片 `xxx.7z.001~005`
    /// （单卷 7z 按字节切出来的，7z 读成 `Type = Split`）。
    /// 现场（`D:\ArchiveFixer\logs\ArchiveFixer_20261110_161401.log` 719/794/801/852/853 行）：
    /// 外层包 16:42:06 已经「定稿完成」，16:43:44 第二大步仍把它算进同基名的 7z 分卷组
    /// （`实际 42247939299` = 5 片 + 外层包）⇒ `SizeAnomaly` ⇒ 组建组被拒 ⇒ 5 片永远不解。
    /// </para>
    ///
    /// <para>
    /// ⛔ 判据只认**路径事实**：这个源包已经解成功并定稿，就不许再当分卷候选。
    /// 所以下面的用例故意把外层包换成 `.tar` / `.7z` / `.rar` 三种后缀各跑一遍
    /// —— 不依赖后缀、也不依赖外层与内层片是否同基名。
    /// </para>
    ///
    /// <para>
    /// **红检撤销点**：把 <c>ExtractionCoordinator.PostProcessSuccessStep</c> 里
    /// `MarkSourceConsumed(task)` 那一次调用去掉（或让 `FirstStepFileLedger.SecondStepCandidates`
    /// 忽略 `IsConsumedSource`）⇒ 本类用例必须失败。
    /// </para>
    /// </summary>
    public class ChainProducedVolumeGroupTests
    {
        [Fact]
        public async Task 外层单包解出的整套分卷_不同基名_必须成组解出且不留片()
        {
            string? sevenZip = LocateSevenZip();
            if (sevenZip == null)
            {
                FixtureGate.Skip("ChainProducedVolumeGroupTests", "找不到 7z.exe");
                return;
            }

            await RunFixture(sevenZip, "999000001", "tar", "aaa", "aaa-B1", collect: true);
        }

        [Fact]
        public async Task 外层单包与内层分卷同基名_三种后缀都不得把外层包算进分卷组()
        {
            string? sevenZip = LocateSevenZip();
            if (sevenZip == null)
            {
                FixtureGate.Skip("ChainProducedVolumeGroupTests", "找不到 7z.exe");
                return;
            }

            string? rar = LocateRar();

            // 后缀不同、基名相同：一份是 tar、一份是 7z、有 WinRAR 时再跑 rar。
            await RunFixture(sevenZip, "999000003", "tar", "aaa", "aaa", collect: false);
            await RunFixture(sevenZip, "999000004", "7z", "aaa", "aaa", collect: false);

            if (rar != null)
            {
                await RunFixture(sevenZip, "999000005", "rar", "aaa", "aaa", collect: false, rar: rar);
            }
        }

        private static async Task RunFixture(
            string sevenZip,
            string sampleId,
            string outerFormat,
            string tarBaseName,
            string pieceBaseName,
            bool collect,
            string? rar = null)
        {
            string sampleDir = Path.Combine(Batch120Operation.BackupBatchRoot, sampleId);
            string sourceDir = Path.Combine(sampleDir, "原包");
            if (Directory.Exists(sampleDir))
            {
                Directory.Delete(sampleDir, recursive: true);
            }

            Directory.CreateDirectory(sourceDir);

            const int contentBytes = 4 * 1024 * 1024;
            string outerPath = BuildFixture(
                sevenZip,
                sourceDir,
                contentBytes,
                outerFormat,
                tarBaseName,
                pieceBaseName,
                rar);

            var scenario = new JsonObject
            {
                ["placement"] = "Original",
                ["sourceHandling"] = "DeleteInPlace",
                ["contentHandling"] = "Delete"
            };

            if (collect)
            {
                scenario["collect"] = true;
            }

            var recipe = new JsonObject
            {
                ["sampleId"] = sampleId,
                ["category"] = "Normal",
                ["kind"] = "normal",
                ["scenario"] = scenario
            };

            File.WriteAllText(
                Path.Combine(sampleDir, "recipe.json"),
                recipe.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            Batch120RunRecord record = await Batch120Operation.RunOneAsync(sampleId);

            // ① 最终内容必须落地（整组没被跳过）
            string? content = record.After
                .Select(item => Path.Combine(record.Workspace, item.Path))
                .FirstOrDefault(path => Path.GetFileName(path) == "content.bin" && File.Exists(path));

            Assert.True(content != null, $"[{outerFormat}] 最终内容 content.bin 没有落到工作区（整组被跳过）");
            Assert.Equal(contentBytes, new FileInfo(content!).Length);

            // ② 分卷片解完必须回收，不许留在成品目录里
            int leftoverPieces = Directory
                .GetFiles(record.Workspace, "*.7z.00?", SearchOption.AllDirectories)
                .Count(path => Path.GetFileName(path).StartsWith(pieceBaseName, StringComparison.OrdinalIgnoreCase));

            Assert.True(leftoverPieces == 0, $"[{outerFormat}] 分卷片残留 {leftoverPieces} 个");

            /*
             * ③ 路径事实：外层源包已经在账本里被标成「已消费源包」。
             * 这一条就是不依赖后缀/基名的那条判据 —— 撤掉修复它必红。
             */
            string copiedOuter = Path.Combine(record.Workspace, "原包", Path.GetFileName(outerPath));
            ArchiveFixer.Detection.FirstStepFileLedger? ledger = Batch120Operation.LastLedger;

            Assert.True(ledger != null, "拿不到这次运行的第一大步账本");
            Assert.True(
                ledger!.TryGetByPath(copiedOuter, out ArchiveFixer.Detection.FirstStepFileLedgerEntry entry),
                $"[{outerFormat}] 外层源包没有进账本：{copiedOuter}");
            Assert.True(
                entry.IsConsumedSource,
                $"[{outerFormat}] 外层源包已解成功，但账本没有把它标成已消费源包 ⇒ 第二大步还会把它当分卷候选");

            Directory.Delete(sampleDir, recursive: true);
        }

        /// <summary>
        /// content.bin → 单个 7z → **按字节切成 5 片**（`.7z.001~005`，真机同一形态）→
        /// 外面套一层单一包（tar / 7z / rar 任选）。
        /// </summary>
        private static string BuildFixture(
            string sevenZip,
            string sourceDir,
            int contentBytes,
            string outerFormat,
            string outerBaseName,
            string pieceBaseName,
            string? rar)
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

            string archiveName = pieceBaseName + ".7z";
            Run(sevenZip, work, "a", "-t7z", "-mx=0", "-y", "-pDemo#Pass1", "-mhe=on", archiveName, "content.bin");
            File.Delete(content);

            string single = Path.Combine(work, archiveName);
            long total = new FileInfo(single).Length;
            long partSize = (total + 4) / 5;

            var pieces = new List<string>();
            using (var reader = new FileStream(single, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                for (int index = 0; index < 5; index++)
                {
                    string piece = Path.Combine(work, $"{pieceBaseName}.7z.{index + 1:000}");
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

                    pieces.Add($"{pieceBaseName}.7z.{index + 1:000}");
                }
            }

            Assert.Equal(5, pieces.Count);
            File.Delete(single);

            string outerPath;

            switch (outerFormat)
            {
                case "tar":
                    outerPath = Path.Combine(sourceDir, outerBaseName + ".tar");
                    Run("tar", work, new[] { "-cf", outerPath }.Concat(pieces).ToArray());
                    break;
                case "7z":
                    Run(sevenZip, work, new[] { "a", "-t7z", "-mx=0", "-y", outerBaseName + ".7z" }.Concat(pieces).ToArray());
                    outerPath = Path.Combine(sourceDir, outerBaseName + ".7z");
                    File.Move(Path.Combine(work, outerBaseName + ".7z"), outerPath);
                    break;
                case "rar":
                    Assert.NotNull(rar);
                    Run(rar!, work, new[] { "a", "-ep1", "-m0", "-y", outerBaseName + ".rar" }.Concat(pieces).ToArray());
                    outerPath = Path.Combine(sourceDir, outerBaseName + ".rar");
                    File.Move(Path.Combine(work, outerBaseName + ".rar"), outerPath);
                    break;
                default:
                    throw new InvalidOperationException("未知外层格式：" + outerFormat);
            }

            Directory.Delete(work, recursive: true);
            return outerPath;
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

        private static string? LocateRar()
        {
            foreach (string candidate in new[]
                     {
                         @"C:\Program Files\WinRAR\Rar.exe",
                         @"C:\Program Files (x86)\WinRAR\Rar.exe"
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
