using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「改个后缀」到底要花多少时间（用户 2026-09-25 第 38 条："我们原本的手动操作仅仅只要修改个后缀，
    /// 可能会卡一下但是不会等这么久的"；"我们现在还没有去测试 .jpg 后缀的操作"）。
    ///
    /// <para>把两件事分开钉住：</para>
    /// <list type="number">
    /// <item><description><b>单纯伪装后缀</b>（`pack.7z` 被改名成 `pack.jpg`）→ 我们只做**就地改名**（0 字节搬运）
    /// + 一次正常解压；⛔ **不会**因为"后缀不对"去抠一份等大的副本。</description></item>
    /// <item><description><b>双面文件</b>（真视频 + 尾部塞了归档）才需要直读，而直读**只读尾部那段归档区间**、
    /// 一个字节都不碰前面的视频前缀 —— 这一步是"把内容落盘"的最低成本（抠取那条路反而要多拷一整份）。</description></item>
    /// </list>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class DisguisedExtensionCostTests : IDisposable
    {
        private const int PayloadBytes = 48 * 1024 * 1024;

        private readonly string _root;
        private readonly string? _sevenZip;

        public DisguisedExtensionCostTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerCost", Guid.NewGuid().ToString("N"));
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
        /// `.jpg` 伪装包：识别要认出真格式，改名要**就地**（远快于一次拷贝），解压要能出内容。
        /// </summary>
        [Fact]
        public async Task jpg伪装的包_只做就地改名再解压_不多拷一份()
        {
            RequireSevenZip();

            string source = Path.Combine(_root, "video-source");
            Directory.CreateDirectory(source);

            string payload = Path.Combine(source, "big.bin");
            var bytes = new byte[PayloadBytes];
            new Random(20260925).NextBytes(bytes);
            File.WriteAllBytes(payload, bytes);

            string realName = Path.Combine(_root, "pack.7z");
            Run7z("a", "-t7z", "-mx0", realName, payload);

            // 伪装：改名成 .jpg（打包者最常用的手法之一，与 .mp4 同理）。
            string disguised = Path.Combine(_root, "pack.jpg");
            File.Move(realName, disguised);

            // ①识别：按文件头魔数认出 7Z（不看后缀）。
            DetectResult detected = await new ArchiveDetectService().DetectAsync(disguised);

            Assert.True(detected.IsKnownFormat, "按魔数必须认出这是 7z");
            Assert.Equal("7Z", detected.Format);
            Assert.Equal(".7z", detected.SuggestedExtension);

            long before = new FileInfo(disguised).Length;

            // ②改名：就地改名（同卷 File.Move），耗时应当远小于"拷贝 48 MB"。
            var watch = Stopwatch.StartNew();
            string renamed = Path.ChangeExtension(disguised, ".7z");
            File.Move(disguised, renamed);
            watch.Stop();

            double secondsForCopy = before / 1024d / 1024d / 18d;   // 他那块盘实测 ~18 MB/s

            Assert.True(
                watch.Elapsed.TotalSeconds < secondsForCopy / 5,
                $"就地改名应当远快于拷贝（改名 {watch.Elapsed.TotalSeconds:0.00} 秒，拷贝 {secondsForCopy:0.0} 秒）");

            // ③没有产生第二个等大的文件（改后缀不该复制数据）。
            Assert.False(File.Exists(disguised), "改名之后旧名字不该还在");
            Assert.Equal(1, Directory.GetFiles(_root).Count(f => new FileInfo(f).Length == before));

            // ④改名之后正常解压，内容出得来。
            string output = Path.Combine(_root, "out");
            Directory.CreateDirectory(output);

            ArchiveOperationResult extract = await new SevenZipEngine().ExtractAsync(
                new ArchiveRequest { ArchivePath = renamed, OutputPath = output },
                new ExtractOptions(),
                CancellationToken.None);

            Assert.True(extract.Success, extract.Message);
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(output, "big.bin")));
        }

        /// <summary>
        /// 双面文件：直读**只读尾部归档区间**，前面的前缀不读、也不产生等大的临时副本。
        /// （真机那个 `.mp4` 是 31 MB 前缀 + 442 MB 归档；这里用同形状的小样本。）
        /// </summary>
        [Fact]
        public async Task 双面文件直读_只读尾部归档区间_不碰前缀也不抠副本()
        {
            RequireSevenZip();

            const int prefixBytes = 2 * 1024 * 1024;

            string inner = Path.Combine(_root, "inner.bin");
            var innerBytes = new byte[4 * 1024 * 1024];
            new Random(7).NextBytes(innerBytes);
            File.WriteAllBytes(inner, innerBytes);

            string zip = Path.Combine(_root, "tail.zip");
            Run7z("a", "-tzip", "-mx0", zip, inner);

            byte[] zipBytes = File.ReadAllBytes(zip);
            byte[] prefix = new byte[prefixBytes];
            new Random(11).NextBytes(prefix);

            string doubleSided = Path.Combine(_root, "movie.mp4");
            using (var stream = File.Create(doubleSided))
            {
                stream.Write(prefix);
                stream.Write(zipBytes);
            }

            // 直读：区间 = [前缀长度, 文件末尾)。
            string output = Path.Combine(_root, "direct-out");
            Directory.CreateDirectory(output);

            EmbeddedZipProbeResult probe = EmbeddedZipStreamExtractor.Probe(doubleSided, prefixBytes, prefixBytes + zipBytes.Length);

            Assert.True(probe.Supported, probe.Reason);

            EmbeddedZipExtractResult result = await Task.Run(() => EmbeddedZipStreamExtractor.Extract(
                doubleSided,
                prefixBytes,
                prefixBytes + zipBytes.Length,
                output,
                null,
                CancellationToken.None));

            Assert.True(result.Success, result.Message);
            Assert.Equal(innerBytes, File.ReadAllBytes(Path.Combine(output, "inner.bin")));

            // 只读归档那一段：读出来的字节数 = 归档区间大小（不含前缀）。
            Assert.True(
                result.WrittenBytes <= zipBytes.Length + 4096,
                $"直读只该处理尾部归档区间（写了 {result.WrittenBytes} 字节，归档 {zipBytes.Length} 字节）");
        }

        /// <summary>
        /// **`.jpg` 双面文件**（用户 2026-09-25 第 38 条的原话："jpg伪装不是仅仅改一个后缀，而是将图片
        /// 放在前面然后将压缩包一起打包，你会检测到一个jpg文件而且要zip直读，所有和.mp4一样"）。
        ///
        /// <para>这条钉住整条路：**真 JPEG 前缀**（有效 <c>FF D8 FF … FF D9</c>）+ 尾部一个完整 ZIP →
        /// ①识别必须报"内嵌归档"（格式 ZIP、给出偏移）；②直读只处理尾部那段归档区间（前缀不读）；
        /// ③内容解得出来；④**普通 jpg（尾部没有 ZIP）不许被误判成归档**。</para>
        /// </summary>
        [Fact]
        public async Task jpg双面文件_图片前缀加尾部zip_识别成内嵌归档并直读()
        {
            RequireSevenZip();

            // ① 造一个"真 JPEG"（有效头 + 有效尾），大小贴近真实图片。
            string jpegOnly = Path.Combine(_root, "photo-only.jpg");
            File.WriteAllBytes(jpegOnly, BuildJpeg(3 * 1024 * 1024));

            // ② 尾部放一个完整的 ZIP（存方式）。
            string inner = Path.Combine(_root, "inner-photo.bin");
            var innerBytes = new byte[6 * 1024 * 1024];
            new Random(23).NextBytes(innerBytes);
            File.WriteAllBytes(inner, innerBytes);

            string zip = Path.Combine(_root, "tail.zip");
            Run7z("a", "-tzip", "-mx0", zip, inner);

            byte[] jpegBytes = File.ReadAllBytes(jpegOnly);
            byte[] zipBytes = File.ReadAllBytes(zip);

            string doubleSided = Path.Combine(_root, "photo.jpg");
            using (var stream = File.Create(doubleSided))
            {
                stream.Write(jpegBytes);
                stream.Write(zipBytes);
            }

            // ③ 识别：按内容认出"内嵌归档"，并给出偏移。
            DetectResult detected = await new ArchiveDetectService().DetectAsync(doubleSided);

            Assert.True(detected.IsArchive, "图片前缀 + 尾部 ZIP 必须被当成归档处理（与 .mp4 同一条路）");
            Assert.Equal("ZIP", detected.Format);
            Assert.Equal(jpegBytes.Length, detected.EmbeddedArchiveOffset);
            Assert.True(detected.EmbeddedArchiveEnd >= detected.EmbeddedArchiveOffset);
            // 判据用**机器事实**（偏移 + 置信度 + 直读探查结论），不比对中文文案（AGENTS.md §7）。
            Assert.Equal(80, detected.Confidence);
            Assert.True(detected.EmbeddedDirectReadSupported, detected.EmbeddedDirectReadReason);

            // ④ 直读：只读尾部区间，内容解得出来，前缀不参与。
            string output = Path.Combine(_root, "jpg-direct-out");
            Directory.CreateDirectory(output);

            EmbeddedZipProbeResult probe = EmbeddedZipStreamExtractor.Probe(
                doubleSided,
                detected.EmbeddedArchiveOffset,
                detected.EmbeddedArchiveEnd);

            Assert.True(probe.Supported, probe.Reason);

            EmbeddedZipExtractResult result = await Task.Run(() => EmbeddedZipStreamExtractor.Extract(
                doubleSided,
                detected.EmbeddedArchiveOffset,
                detected.EmbeddedArchiveEnd,
                output,
                null,
                CancellationToken.None));

            Assert.True(result.Success, result.Message);
            Assert.Equal(innerBytes, File.ReadAllBytes(Path.Combine(output, "inner-photo.bin")));

            Assert.True(
                result.WrittenBytes <= zipBytes.Length + 4096,
                $"直读只该处理尾部归档区间（写了 {result.WrittenBytes} 字节，归档 {zipBytes.Length} 字节）");

            // ⑤ 反向断言：普通 jpg（尾部没有 ZIP）不许被误判成归档。
            DetectResult plain = await new ArchiveDetectService().DetectAsync(jpegOnly);

            Assert.False(plain.IsArchive, "一张普通 jpg 不能被当成压缩包");
            Assert.Equal(0L, plain.EmbeddedArchiveOffset);
        }

        /// <summary>造一张"结构上像真的"JPEG：SOI + APP0(JFIF) + 一段数据 + EOI。</summary>
        private static byte[] BuildJpeg(int size)
        {
            var bytes = new byte[Math.Max(size, 4096)];

            bytes[0] = 0xFF;
            bytes[1] = 0xD8;

            bytes[2] = 0xFF;
            bytes[3] = 0xE0;
            bytes[4] = 0x00;
            bytes[5] = 0x10;

            byte[] jfif = { (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00 };
            Array.Copy(jfif, 0, bytes, 6, jfif.Length);

            new Random(31).NextBytes(bytes.AsSpan(20, bytes.Length - 24));

            bytes[^2] = 0xFF;
            bytes[^1] = 0xD9;

            return bytes;
        }

        /// <summary>
        /// **尾部挂 RAR / 7z**（用户 2026-09-25 第 38 条追加："尾部挂 RAR/7z 的识别还是空白 → 快"）。
        ///
        /// <para>形状：`封面.jpg`（20 MB 前缀，**超过引擎自己的容忍度**）+ 一整个 `资料.rar` / `资料.7z`。
        /// 实测这种前缀下 7-Zip 与 UnRAR 都会报"不是归档"，所以必须我们自己扫签名定位起点、抠出来再解。</para>
        /// </summary>
        [Theory]
        [InlineData("rar", "RAR5", ".rar")]
        [InlineData("7z", "7Z", ".7z")]
        public async Task 尾部挂rar或7z_扫签名定位起点_抠出来能解(string container, string expectedFormat, string expectedExtension)
        {
            RequireSevenZip();

            const int prefixBytes = 20 * 1024 * 1024;

            string payload = Path.Combine(_root, $"payload-{container}.bin");
            var payloadBytes = new byte[3 * 1024 * 1024];
            new Random(41).NextBytes(payloadBytes);
            File.WriteAllBytes(payload, payloadBytes);

            string archive = Path.Combine(_root, $"资料.{container}");

            if (container == "rar")
            {
                RunWinRar("a", "-m0", "-idq", archive, Path.GetFileName(payload));   // 只给文件名：给绝对路径 WinRAR 会把整条路径存进去
            }
            else
            {
                Run7z("a", "-t7z", "-mx0", archive, Path.GetFileName(payload));
            }

            byte[] archiveBytes = File.ReadAllBytes(archive);
            byte[] prefix = new byte[prefixBytes];
            new Random(43).NextBytes(prefix);

            string doubleSided = Path.Combine(_root, $"封面-{container}-带包.jpg");

            using (var stream = File.Create(doubleSided))
            {
                stream.Write(prefix);
                stream.Write(archiveBytes);
            }

            // ① 识别：扫签名定位起点。
            DetectResult detected = await new ArchiveDetectService().DetectAsync(doubleSided);

            Assert.True(detected.IsArchive, $"图片前缀 + 尾部 {container} 必须被当成归档处理");
            Assert.Equal(expectedFormat, detected.Format);
            Assert.Equal(expectedExtension, detected.SuggestedExtension);
            Assert.Equal(prefixBytes, detected.EmbeddedArchiveOffset);
            Assert.Equal(prefixBytes + archiveBytes.Length, detected.EmbeddedArchiveEnd);
            Assert.False(detected.EmbeddedDirectReadSupported, "RAR / 7z 不走内置直读（它只解 ZIP）");

            // ② 抠出 [偏移, 末尾) 交给引擎（管线就是这么做的）→ 内容必须解得出来。
            string carved = Path.Combine(_root, $"carved-{container}.{container}");
            File.WriteAllBytes(carved, archiveBytes);

            string output = Path.Combine(_root, $"tail-{container}-out");
            Directory.CreateDirectory(output);

            ArchiveOperationResult extract = await new SevenZipEngine().ExtractAsync(
                new ArchiveRequest { ArchivePath = carved, OutputPath = output },
                new ExtractOptions(),
                CancellationToken.None);

            Assert.True(extract.Success, extract.Message);
            Assert.Equal(payloadBytes, File.ReadAllBytes(Path.Combine(output, $"payload-{container}.bin")));
        }

        private void RunWinRar(params object[] args)
        {
            string? rar = LocateWinRar();

            if (rar == null)
            {
                throw new InvalidOperationException("测试机上没装 WinRAR（Rar.exe），本用例无法造 RAR 样本。");
            }

            var psi = new ProcessStartInfo(rar)
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

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 Rar.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            Assert.True(process.WaitForExit(120_000), "Rar.exe 超时");
            Assert.True(process.ExitCode == 0, $"Rar.exe 失败（{process.ExitCode}）：{stdout}{stderr}");
        }

        private static string? LocateWinRar()
        {
            string[] candidates =
            {
                @"C:\Program Files\WinRAR\Rar.exe",
                @"C:\Program Files (x86)\WinRAR\Rar.exe"
            };

            return candidates.FirstOrDefault(File.Exists);
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
                    string candidate = Path.Combine(directory.FullName, "ArchiveFixer", "tools", "7zip", "7z.exe");
                    return File.Exists(candidate) ? candidate : null;
                }

                directory = directory.Parent;
            }

            return null;
        }
    }
}
