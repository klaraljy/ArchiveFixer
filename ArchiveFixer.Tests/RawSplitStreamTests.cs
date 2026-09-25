using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Models;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「一组分卷的**第一卷**名字被改坏」这一档（用户 2026-09-25 第 36 条追加）。
    ///
    /// <para><b>为什么必须拦</b>：真机那个 12.22 GiB 的包里是一组三卷 7z，第一卷叫
    /// <c>Code Complete-BZ.7z(删掉.001</c>（打包者塞了「删掉」两个字、右括号还被吃掉了），
    /// 后两卷名字正常。7-Zip 顺着名字找不到后续卷，就把它当**通用分片**：
    /// 清单里只有一条（条目 = 文件自己），**照解不误、退出码 0**，解出来是一个与它等大的垃圾文件 ——
    /// 而结果校验比的就是"清单那一条"与"盘上那一个文件"，于是会判**通过**，
    /// 用户拿到 5 GB 垃圾却看到「解压成功」。</para>
    ///
    /// <para>这一组钉住：①真 7z 确实这么干（把 7-Zip 的行为固化成证据）；②本程序的判据认得出来
    /// （<see cref="RawSplitStreamDetector.IsBrokenVolumeChain"/>）；③名字正常、卷齐的那一组**绝不被误拦**
    /// （那是合法用法：通用分片拼起来正是用户要的东西）。</para>
    /// </summary>
    public class RawSplitStreamTests : IDisposable
    {
        private readonly string _root;
        private readonly string? _sevenZip;

        public RawSplitStreamTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerRawSplit", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _sevenZip = LocateSevenZip();
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
                // 临时目录清不掉不影响结论。
            }
        }

        // ================================================================ ① 判据（纯函数）

        /// <summary>
        /// 他那一档：单卷组 + 内容是归档 + 引擎说通用分片 + 清单只有"文件自己"一条 → **必须判成坏的**。
        /// </summary>
        [Fact]
        public void 单卷组_内容是归档_引擎当成通用分片_判成名字被改坏()
        {
            string path = CreateFile("Code Complete-BZ.7z(删掉.001", 4096);

            ArchiveListResult list = RawSplitListing("Code Complete-BZ.7z(删掉", 4096);

            Assert.True(
                RawSplitStreamDetector.IsBrokenVolumeChain(list, path, true, 1),
                "这就是真机那一档：不拦的话会解出一个与卷等大的垃圾文件、还判成功");
        }

        [Fact]
        public void 卷齐的时候绝不误拦()
        {
            string path = CreateFile("X.7z.001", 4096);

            // 一组三卷：7-Zip 同样报 Type = Split（条目 = X），但那是**合法**用法 —— 拼起来才是内容。
            ArchiveListResult list = RawSplitListing("X", 12288);

            Assert.False(
                RawSplitStreamDetector.IsBrokenVolumeChain(list, path, true, 3),
                "卷齐的通用分片拼起来正是用户要的东西，拦它就是破坏既有能力");
        }

        [Fact]
        public void 内容不是归档时不拦_通用分片本身是合法的()
        {
            // 一个视频被切成人 X.001：魔数不是归档 → 本来也不会走解压管线；
            // 万一走到这里，也绝不能因为"引擎说这是分片"就判它坏。
            string path = CreateFile("movie.001", 4096);

            ArchiveListResult list = RawSplitListing("movie", 4096);

            Assert.False(RawSplitStreamDetector.IsBrokenVolumeChain(list, path, false, 1));
        }

        [Fact]
        public void 条目名或大小对不上时不拦()
        {
            string path = CreateFile("X.7z.001", 4096);

            // 条目名不是"文件自己"：那是真的归档，条目就是包里的东西。
            Assert.False(RawSplitStreamDetector.IsBrokenVolumeChain(
                RawSplitListing("payload.bin", 4096), path, true, 1));

            // 大小对不上（清单比文件大）：说明引擎真的看到了别的卷，不是"通用分片"。
            Assert.False(RawSplitStreamDetector.IsBrokenVolumeChain(
                RawSplitListing("X", 8192), path, true, 1));
        }

        [Fact]
        public void 引擎没说通用分片时不拦()
        {
            string path = CreateFile("X.7z.001", 4096);

            var normalListing = new ArchiveListResult
            {
                Success = true,
                FileCount = 1,
                TotalUncompressedSize = 4096,
                Entries = new List<ArchiveEntry> { new() { Path = "X", Size = 4096 } },
                EngineId = "fake",
                EngineVersion = "1.0"
            };

            Assert.False(RawSplitStreamDetector.IsBrokenVolumeChain(normalListing, path, true, 1));
        }

        [Fact]
        public void 同目录里的后续卷与改名建议()
        {
            string path = CreateFile("Code Complete-BZ.7z(删掉.001", 4096);

            string[] names =
            {
                "Code Complete-BZ.7z(删掉.001",
                "Code Complete-BZ.7z.002",
                "Code Complete-BZ.7z.003",
                "别的包.7z.002",           // 与这一组无关，不许混进来
                "说明.txt"
            };

            IReadOnlyList<string> siblings = RawSplitStreamDetector.FindSiblingVolumes(names, path);

            Assert.Equal(
                new[] { "Code Complete-BZ.7z.002", "Code Complete-BZ.7z.003" },
                siblings.ToArray());

            Assert.Equal(
                "Code Complete-BZ.7z.001",
                RawSplitStreamDetector.SuggestStandardFirstName(siblings));
        }

        // ================================================================ ② 真 7z 的行为（证据）

        /// <summary>
        /// 把 7-Zip 的行为固化成回归：**第一卷名字被改坏时，7-Zip 会说"成功"，解出来的是垃圾**。
        ///
        /// <para>这条断言不是在测我们自己的代码，而是这条闸门的**理由**：
        /// 哪天 7-Zip 改成"报错"了，这条会红 —— 那时可以重新评估要不要保留这道闸门。</para>
        /// </summary>
        [Fact]
        public async Task 真7z_第一卷名字被改坏时会把这一卷当成通用分片并照解不误()
        {
            RequireSevenZip();

            string directory = Path.Combine(_root, "mangled");
            Directory.CreateDirectory(directory);

            string payload = CreatePayload(Path.Combine(_root, "payload"), 40_000);
            CreateVolumeSet(directory, "set.7z", payload);

            // 打包者的手法：第一卷塞字 + 吃掉一个字符（右括号）；后两卷名字不动。
            File.Move(
                Path.Combine(directory, "set.7z.001"),
                Path.Combine(directory, "set.7z(删掉.001"));

            string mangled = Path.Combine(directory, "set.7z(删掉.001");
            var engine = new SevenZipEngine();

            ArchiveListResult list = await engine.ListAsync(ArchiveRequest.For(mangled), CancellationToken.None);

            Assert.True(list.Success, $"真 7z 应该能列出它（当成通用分片）：{list.Message}");

            // 判据要认得出来 —— 这就是管线上那道闸门的输入。
            Assert.True(list.IsRawSplitStream, "7-Zip 把这一卷当成了通用分片（Type = Split）");
            Assert.Single(list.Entries);
            Assert.Equal("set.7z(删掉", list.Entries[0].Path);

            Assert.True(
                RawSplitStreamDetector.IsBrokenVolumeChain(list, mangled, true, 1),
                "真 7z 的清单 + 判据 → 必须判成「名字被改坏的分卷」");

            // 证据：引擎自己说"解压成功"，而出来的是与这一卷等大的垃圾文件（不是 a.bin）。
            string outDirectory = Path.Combine(_root, "mangled-out");
            Directory.CreateDirectory(outDirectory);

            ArchiveOperationResult extract = await engine.ExtractAsync(
                new ArchiveRequest { ArchivePath = mangled, OutputPath = outDirectory },
                new ExtractOptions(),
                CancellationToken.None);

            Assert.True(extract.Success, $"7-Zip 对它照解不误（退出码 0）：{extract.Message}");

            string junk = Path.Combine(outDirectory, "set.7z(删掉");
            Assert.True(File.Exists(junk), "解出来的是「文件自己」这一条");
            Assert.False(
                File.Exists(Path.Combine(outDirectory, "a.bin")),
                "真正的内容（a.bin）根本没出来 —— 这就是必须判「分卷缺失」而不是「成功」的原因");
        }

        /// <summary>反向对照：名字正常、三卷齐全时，同一个判据**不许**触发，解出来的才是真内容。</summary>
        [Fact]
        public async Task 真7z_名字正常的三卷不会被判坏且能解出真内容()
        {
            RequireSevenZip();

            string directory = Path.Combine(_root, "normal");
            Directory.CreateDirectory(directory);

            string payload = CreatePayload(Path.Combine(_root, "payload2"), 40_000);
            CreateVolumeSet(directory, "set.7z", payload);

            string first = Path.Combine(directory, "set.7z.001");
            var engine = new SevenZipEngine();

            ArchiveListResult list = await engine.ListAsync(ArchiveRequest.For(first), CancellationToken.None);

            Assert.True(list.Success, list.Message);
            Assert.False(
                RawSplitStreamDetector.IsBrokenVolumeChain(list, first, true, 3),
                "一组完整的分卷绝不能被这道闸门拦下");

            string outDirectory = Path.Combine(_root, "normal-out");
            Directory.CreateDirectory(outDirectory);

            ArchiveOperationResult extract = await engine.ExtractAsync(
                new ArchiveRequest { ArchivePath = first, OutputPath = outDirectory },
                new ExtractOptions(),
                CancellationToken.None);

            Assert.True(extract.Success, extract.Message);
            Assert.True(File.Exists(Path.Combine(outDirectory, "a.bin")), "正常那一组必须解出真内容");
        }

        // ================================================================ 工具

        private static ArchiveListResult RawSplitListing(string entryPath, long entrySize)
        {
            return new ArchiveListResult
            {
                Success = true,
                FileCount = 1,
                TotalUncompressedSize = entrySize,
                Entries = new List<ArchiveEntry> { new() { Path = entryPath, Size = entrySize } },
                IsRawSplitStream = true,
                EngineId = "fake",
                EngineVersion = "1.0"
            };
        }

        private string CreateFile(string fileName, int size)
        {
            string path = Path.Combine(_root, fileName);
            File.WriteAllBytes(path, new byte[size]);
            return path;
        }

        private static string CreatePayload(string directory, int size)
        {
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, "a.bin");
            var bytes = new byte[size];
            new Random(20260925).NextBytes(bytes);
            File.WriteAllBytes(path, bytes);

            return path;
        }

        /// <summary>造一组 16 KiB 一卷的真 7z 分卷（<c>&lt;name&gt;.001/.002/…</c>）。</summary>
        private void CreateVolumeSet(string directory, string archiveName, string payload)
        {
            var psi = new ProcessStartInfo(_sevenZip!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = directory
            };

            psi.ArgumentList.Add("a");
            psi.ArgumentList.Add("-t7z");
            psi.ArgumentList.Add("-mx0");
            psi.ArgumentList.Add("-v16k");
            psi.ArgumentList.Add(Path.Combine(directory, archiveName));
            psi.ArgumentList.Add(payload);

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 7z.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            Assert.True(process.WaitForExit(60_000), "7z 造分卷超时");
            Assert.True(process.ExitCode == 0, $"7z 造分卷失败：{stdout}{stderr}");

            Assert.True(File.Exists(Path.Combine(directory, archiveName + ".001")), "分卷没造出来");
            Assert.True(File.Exists(Path.Combine(directory, archiveName + ".002")), "第二卷没造出来");
        }

        private void RequireSevenZip()
        {
            if (string.IsNullOrEmpty(_sevenZip))
            {
                throw new InvalidOperationException(
                    "测试机上没有 7z.exe（ArchiveFixer/tools/7zip/7z.exe），本用例无法运行。");
            }
        }

        private static string? LocateSevenZip()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    string candidate = Path.Combine(directory.FullName, "ArchiveFixer", "tools", "7zip", "7z.exe");
                    return File.Exists(candidate) ? candidate : null;
                }

                directory = directory.Parent;
            }

            return null;
        }
    }
}
