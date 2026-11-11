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
    /// 端到端：**脏后缀 7z 首片 + 同目录普通 WebP 改名 .png**（2026-11-11 真机 336 形状的缩小版）。
    ///
    /// <para>钉三件事：① WebP 内容物不撑大分卷组的已收字节；② 快通道仍按标准名
    /// （`.001删` → `.001`）建链接并解出内容；③ 成功后过程物/工作区按既有收片账清理，
    /// 不留下"解出来了但按 Pending 收场"的尾巴。</para>
    /// </summary>
    public sealed class SecondStepMediaContentEndToEndTests
    {
        [Fact]
        public async Task 脏后缀首片加同基名WebP_快通道能解出内容且不判Pending()
        {
            string? sevenZip = LocateFirst(
                @"E:\ArchiveFixer\tools\7zip\7z.exe",
                Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe"));

            if (sevenZip == null)
            {
                FixtureGate.Skip(nameof(SecondStepMediaContentEndToEndTests), "找不到 7z.exe");
                return;
            }

            const string sampleId = "999000007";
            string sampleDir = Path.Combine(Batch120Operation.BackupBatchRoot, sampleId);
            string sourceDir = Path.Combine(sampleDir, "原包");

            if (Directory.Exists(sampleDir))
            {
                Directory.Delete(sampleDir, recursive: true);
            }

            Directory.CreateDirectory(sourceDir);
            BuildFixture(sevenZip, sampleDir, sourceDir);

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
                Batch120RunRecord record = await Batch120Operation.RunOneAsync(sampleId);

                Assert.True(string.IsNullOrEmpty(record.Error), "一键处理报错：" + record.Error);

                string? content = record.After
                    .Select(item => Path.Combine(record.Workspace, item.Path))
                    .FirstOrDefault(path => Path.GetFileName(path) == "content.bin" && File.Exists(path));

                Assert.True(content != null, "最终内容 content.bin 没有落地（脏后缀首片没被快通道解开）");
                Assert.True(new FileInfo(content!).Length > 0, "content.bin 是 0 字节");

                Batch120TaskRecord? entryTask = record.Tasks.FirstOrDefault(task =>
                    task.Name.Contains("336.7z.001", StringComparison.OrdinalIgnoreCase));

                Assert.True(entryTask != null, "任务表里没有 336.7z.001 这一单");
                Assert.Equal("336.7z.001删", entryTask!.Name);
                Assert.NotEqual("Pending", entryTask.Outcome);
                Assert.NotEqual("Failed", entryTask.Outcome);

                /*
                 * 第二大步不再物理改名：源片仍叫 `.001删`，标准名只活在入口层/链接里。
                 */
                Assert.NotEmpty(
                    Directory.GetFiles(record.Workspace, "336.7z.001删", SearchOption.AllDirectories));
                Assert.Empty(
                    Directory.GetFiles(record.Workspace, "336.7z.001", SearchOption.AllDirectories));

                string[] workspaceShells = Directory.GetDirectories(
                    record.Workspace,
                    ".ArchiveFixer.work",
                    SearchOption.AllDirectories);

                Assert.True(
                    workspaceShells.Length == 0,
                    "成功链结束后工作区壳没清掉：" + string.Join("；", workspaceShells));

                /*
                 * 日志断言交给同批的既有端到端用例（`SecondStepFastPathEndToEndTests`）。
                 * 这里不再读 `logs-<id>`：xUnit 并行跑多个 Batch120 用例时，
                 * `ARCHIVEFIXER_120_KEEP_LOGS` 是进程环境变量，会被别的用例恢复掉 ⇒ 日志文件可能没复制。
                 * 本用例只钉用户可见结果：内容落地、入口任务不是 Pending/Failed、工作区壳清掉。
                 */
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

        private static void BuildFixture(string sevenZip, string sampleDir, string sourceDir)
        {
            string work = Path.Combine(sampleDir, "work");

            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }

            Directory.CreateDirectory(work);

            const int contentBytes = 2 * 1024 * 1024;
            var buffer = new byte[contentBytes];
            new Random(20261111).NextBytes(buffer);
            File.WriteAllBytes(Path.Combine(work, "content.bin"), buffer);

            string archive = Path.Combine(work, "336.7z");
            Run(sevenZip, work, "a", "-t7z", "-mx=0", "-y", "336.7z", "content.bin");

            long total = new FileInfo(archive).Length;
            long firstLength = total * 3 / 5;

            CopyRange(archive, Path.Combine(sourceDir, "336.7z.001删"), 0, firstLength);
            CopyRange(archive, Path.Combine(sourceDir, "336.7z.002"), firstLength, total - firstLength);

            File.Delete(archive);
            File.WriteAllBytes(Path.Combine(sourceDir, "336.png"), BuildWebp());
            Directory.Delete(work, recursive: true);
        }

        private static void CopyRange(string source, string destination, long offset, long length)
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);

            input.Seek(offset, SeekOrigin.Begin);

            var buffer = new byte[1024 * 1024];
            long remaining = length;

            while (remaining > 0)
            {
                int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));

                if (read <= 0)
                {
                    break;
                }

                output.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        /// <summary>最小 WebP：RIFF 头 + 一个 `VP8 ` 块；文件名叫 `.png`，模拟 336 真机。</summary>
        private static byte[] BuildWebp()
        {
            var bytes = new byte[24];

            byte[] signature = { (byte)'R', (byte)'I', (byte)'F', (byte)'F' };
            signature.CopyTo(bytes, 0);

            bytes[4] = 16;
            bytes[8] = (byte)'W';
            bytes[9] = (byte)'E';
            bytes[10] = (byte)'B';
            bytes[11] = (byte)'P';
            bytes[12] = (byte)'V';
            bytes[13] = (byte)'P';
            bytes[14] = (byte)'8';
            bytes[15] = (byte)' ';
            bytes[16] = 4;

            return bytes;
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
}
