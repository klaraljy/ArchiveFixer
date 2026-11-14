using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **ZIP 不在文件末尾**（用户 2026-09-25 第 40 条：他列的伪装方法里最后一条还没覆盖的形状）。
    ///
    /// <para>形状：`封面.jpg + 资料.zip + 别的东西`。ZIP 前面垫了图片、后面还垫了数据 ——
    /// 尾部那 256 KB 里根本没有 EOCD，原来"从尾部反推 ZIP 起点"那条路整条失效，
    /// 文件被判成"格式未知"。</para>
    ///
    /// <para>落地：<c>TailArchiveScanner</c> 那**一遍顺序扫**顺手把 <c>PK\x05\x06</c> 候选位置记下来，
    /// 交给 <c>EmbeddedArchiveDetector.DetectAt</c> 逐个做自洽校验（判据一个字没放宽：
    /// 中央目录签名必须正好在算出来的位置、归档起点必须正好是局部文件头）。
    /// 命中的结论与尾部那条路**同一形状**，管线照旧"抠出 [起点, 终点) → 直读或交给引擎"。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class ZipNotAtEndTests : IDisposable
    {
        /// <summary>前缀（"封面图"）：2 MiB，超过任何"引擎自己容忍偏移"的量级。</summary>
        private const int PrefixBytes = 2 * 1024 * 1024;

        /// <summary>后缀（"后面又垫的东西"）：4 MiB，**必须远大于尾部扫描窗口 256 KB**，否则这条测试证明不了任何事。</summary>
        private const int SuffixBytes = 4 * 1024 * 1024;

        private readonly string _root;
        private readonly string? _sevenZip;

        public ZipNotAtEndTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerZipNotAtEnd", Guid.NewGuid().ToString("N"));
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

        /// <summary>
        /// 中部 ZIP：识别必须报出"内嵌归档 ZIP + 精确区间"，而且直读（只读那一段）能生效。
        /// </summary>
        [Fact]
        public async Task 中部zip_前后都垫了数据_识别成内嵌归档并给出精确区间()
        {
            RequireSevenZip();

            (string container, byte[] payloadBytes, byte[] zipBytes) = BuildMiddleZip("资料.zip", 5 * 1024 * 1024);

            DetectResult detected = await new ArchiveDetectService().DetectAsync(container);

            Assert.True(detected.IsArchive, "前面垫图片、后面垫数据的中部 ZIP 必须被当成归档处理");
            Assert.True(detected.IsKnownFormat, "格式必须认出来（不能是 Unknown）");
            Assert.Equal("ZIP", detected.Format);
            Assert.Equal(".zip", detected.SuggestedExtension);

            // 判据全用机器事实（偏移 / 区间 / 置信度 / 直读结论），不比对中文文案（AGENTS.md §7）。
            Assert.Equal(PrefixBytes, detected.EmbeddedArchiveOffset);
            Assert.Equal(PrefixBytes + zipBytes.Length, detected.EmbeddedArchiveEnd);
            Assert.Equal(80, detected.Confidence);
            Assert.True(detected.EmbeddedDirectReadSupported, detected.EmbeddedDirectReadReason);

            // 区间必须**截断在 ZIP 自己结束的地方**，不是"一直到文件末尾"。
            Assert.True(
                detected.EmbeddedArchiveEnd < new FileInfo(container).Length,
                "归档终点必须落在文件末尾之前（后面那 4 MiB 不是归档的一部分）");
        }

        /// <summary>直读那条路：只读 [起点, 终点)，解出来的内容与原件逐字节相同。</summary>
        [Fact]
        public async Task 中部zip_直读解出的内容与原文件逐字节相同()
        {
            RequireSevenZip();

            (string container, byte[] payloadBytes, _) = BuildMiddleZip("资料.zip", 5 * 1024 * 1024);

            DetectResult detected = await new ArchiveDetectService().DetectAsync(container);

            Assert.True(detected.IsArchive, "样本必须先是被识别出来的，否则这条测试证明不了任何事");
            Assert.True(detected.EmbeddedDirectReadSupported, detected.EmbeddedDirectReadReason);

            string output = Path.Combine(_root, "direct-out");
            Directory.CreateDirectory(output);

            EmbeddedZipProbeResult probe = EmbeddedZipStreamExtractor.Probe(
                container,
                detected.EmbeddedArchiveOffset,
                detected.EmbeddedArchiveEnd);

            Assert.True(probe.Supported, probe.Reason);

            EmbeddedZipExtractResult result = await Task.Run(() => EmbeddedZipStreamExtractor.Extract(
                container,
                detected.EmbeddedArchiveOffset,
                detected.EmbeddedArchiveEnd,
                output,
                null,
                CancellationToken.None));

            Assert.True(result.Success, result.Message);
            Assert.Equal(payloadBytes, File.ReadAllBytes(Path.Combine(output, "资料.bin")));
        }

        /// <summary>
        /// 抠取那条路（直读不支持时的回落）：按识别出来的区间抠出来的，必须是一个**能解的完整 ZIP** ——
        /// 这条同时钉住"终点算对了"：多抠后面那 4 MiB 就解不开了（引擎对尾部垃圾的容忍度有限）。
        /// </summary>
        [Fact]
        public async Task 中部zip_按识别区间抠出来是一个完整zip_引擎能解出真内容()
        {
            RequireSevenZip();

            (string container, byte[] payloadBytes, byte[] zipBytes) = BuildMiddleZip("资料.zip", 5 * 1024 * 1024);

            DetectResult detected = await new ArchiveDetectService().DetectAsync(container);

            Assert.True(detected.IsArchive, "样本必须先是被识别出来的，否则这条测试证明不了任何事");

            string carved = Path.Combine(_root, "carved.zip");
            CopyRange(container, detected.EmbeddedArchiveOffset, detected.EmbeddedArchiveEnd, carved);

            Assert.Equal(zipBytes.Length, new FileInfo(carved).Length);

            string output = Path.Combine(_root, "carved-out");
            Directory.CreateDirectory(output);

            ArchiveOperationResult extract = await new SevenZipEngine().ExtractAsync(
                new ArchiveRequest { ArchivePath = carved, OutputPath = output },
                new ExtractOptions(),
                CancellationToken.None);

            Assert.True(extract.Success, extract.Message);
            Assert.Equal(payloadBytes, File.ReadAllBytes(Path.Combine(output, "资料.bin")));
        }

        /// <summary>
        /// **不误报**：随机数据里塞一个字段全是垃圾的 <c>PK\x05\x06</c>，不许被当成内嵌归档。
        /// （"扫到的候选多"和"结论成立"是两件事 —— 每个候选都要过自洽校验。）
        /// </summary>
        [Fact]
        public async Task 假的EOCD候选_不许被当成内嵌归档()
        {
            string container = Path.Combine(_root, "假收尾.jpg");

            var prefix = new byte[PrefixBytes + 4096];
            new Random(61).NextBytes(prefix);

            // 在"中部"放一个 EOCD 签名：后面跟的字段随机（中央目录大小/偏移都是乱数）。
            prefix[PrefixBytes] = 0x50;
            prefix[PrefixBytes + 1] = 0x4B;
            prefix[PrefixBytes + 2] = 0x05;
            prefix[PrefixBytes + 3] = 0x06;

            var suffix = new byte[SuffixBytes];
            new Random(62).NextBytes(suffix);

            using (var stream = File.Create(container))
            {
                stream.Write(prefix);
                stream.Write(suffix);
            }

            DetectResult detected = await new ArchiveDetectService().DetectAsync(container);

            Assert.False(detected.IsArchive, "凑出来的 EOCD 签名不许被当成压缩包");
            Assert.Equal(0L, detected.EmbeddedArchiveOffset);
        }

        /// <summary>
        /// **优先级**：ZIP 里面装着 RAR 文件时（打包者常这么套），扫描会同时扫到"ZIP 的 EOCD"与
        /// "内层 RAR 的魔数"。结论必须是**外层那个 ZIP** —— 只认魔数会把用户的东西解成一个内层文件，
        /// 而自洽校验过的 ZIP 才是"这里有一个 ZIP"的实打实证据。
        ///
        /// <para>这条同时钉住"见过 <c>PK\x03\x04</c> 才继续扫"的取舍：只有那样，
        /// 常见的"图片挂个 RAR"才不会被这条逻辑拖慢。</para>
        /// </summary>
        [Fact]
        public async Task zip里面装着rar时_优先认外层zip而不是内层rar魔数()
        {
            RequireSevenZip();

            // 用一个**内容以 RAR5 魔数开头**的文件冒充"ZIP 里的内层 RAR"（-mx0 存方式 → 字节原样落进 ZIP）。
            byte[] payloadBytes = new byte[3 * 1024 * 1024];
            new Random(71).NextBytes(payloadBytes);

            byte[] rarMagic = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 };
            Array.Copy(rarMagic, payloadBytes, rarMagic.Length);

            (string container, _, byte[] zipBytes) = BuildMiddleZip("里层有rar.zip", payloadBytes);

            DetectResult detected = await new ArchiveDetectService().DetectAsync(container);

            Assert.True(detected.IsArchive, "样本必须先是被识别出来的，否则这条测试证明不了任何事");
            Assert.Equal("ZIP", detected.Format);
            Assert.Equal(PrefixBytes, detected.EmbeddedArchiveOffset);
            Assert.Equal(PrefixBytes + zipBytes.Length, detected.EmbeddedArchiveEnd);
        }

        // ================================================================ 造样本

        /// <summary>把"真 ZIP"夹在一段乱码前缀与一段乱码后缀之间。</summary>
        private (string Container, byte[] PayloadBytes, byte[] ZipBytes) BuildMiddleZip(string zipName, int payloadSize)
        {
            var payloadBytes = new byte[payloadSize];
            new Random(41).NextBytes(payloadBytes);

            return BuildMiddleZip(zipName, payloadBytes);
        }

        private (string Container, byte[] PayloadBytes, byte[] ZipBytes) BuildMiddleZip(string zipName, byte[] payloadBytes)
        {
            string payload = Path.Combine(_root, "资料.bin");
            File.WriteAllBytes(payload, payloadBytes);

            string zip = Path.Combine(_root, zipName);
            Run7z("a", "-tzip", "-mx0", zip, Path.GetFileName(payload));

            byte[] zipBytes = File.ReadAllBytes(zip);

            var prefix = new byte[PrefixBytes];
            new Random(43).NextBytes(prefix);

            var suffix = new byte[SuffixBytes];
            new Random(44).NextBytes(suffix);

            string container = Path.Combine(_root, "封面-带包.jpg");

            using (var stream = File.Create(container))
            {
                stream.Write(prefix);
                stream.Write(zipBytes);
                stream.Write(suffix);
            }

            File.Delete(zip);
            File.Delete(payload);

            return (container, payloadBytes, zipBytes);
        }

        private static void CopyRange(string source, long offset, long end, string destination)
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);

            input.Seek(offset, SeekOrigin.Begin);

            var buffer = new byte[4 * 1024 * 1024];
            long remaining = end - offset;

            while (remaining > 0)
            {
                int want = (int)Math.Min(buffer.Length, remaining);
                int read = input.Read(buffer, 0, want);

                if (read <= 0)
                {
                    break;
                }

                output.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        // ================================================================ 工具

        private void Run7z(params object[] args)
        {
            RequireSevenZip();

            var psi = new ProcessStartInfo(_sevenZip!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = _root
            };

            foreach (object arg in args)
            {
                psi.ArgumentList.Add(arg?.ToString() ?? string.Empty);
            }

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 7z.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            Assert.True(process.WaitForExit(120_000), "7z 超时");
            Assert.True(process.ExitCode == 0, $"7z 失败（{process.ExitCode}）：{stdout}{stderr}");
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
                    string candidate = Path.Combine(directory.FullName, "src", "ArchiveFixer", "tools", "7zip", "7z.exe");
                    return File.Exists(candidate) ? candidate : null;
                }

                directory = directory.Parent;
            }

            return null;
        }
    }
}
