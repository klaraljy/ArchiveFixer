using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Converters;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// "双面文件"：前面是视频等正常数据，尾部却拼着一个**完整的 ZIP**（ZIP 内部偏移仍相对它自己）。
    ///
    /// 为什么要有这一组测试：真实网盘资源就是这么发出来的 —— 双击能播（播放器只读前面的视频），
    /// 改成 <c>.zip</c> 也能打开（资源管理器的 ZIP 读取器容忍整体偏移），
    /// 唯独 7z.exe 直接报 "Cannot open the file as [zip] archive"（它按"偏移相对文件起点"读）。
    /// 老的识别只读文件头 34 KB，而 ZIP 在 17 MB 之后，于是这种文件被判成"格式未知"。
    ///
    /// 样本**全部自己造**（临时目录 + 项目内置 7z.exe），不读用户目录里的任何文件（AGENTS.md §8）。
    /// 造法刻意模仿真实文件：先把 ZIP 整个建好、**再**在前面拼数据，
    /// 这样 ZIP 的内部偏移保持相对它自己的起点 —— 这是这个 bug 的全部关键，用"重新打包"是造不出来的。
    /// </summary>
    public class EmbeddedArchiveTests : IDisposable
    {
        /// <summary>假视频头的长度：与真实现场同量级（3 万字节级），也保证 34 KB 的文件头读不到 ZIP。</summary>
        private const int FakeVideoPrefixLength = 32768;

        /// <summary>
        /// 超过 7-Zip 容忍上限的前置数据长度：9 MiB。
        ///
        /// 这是实测出来的硬边界（本机 7-Zip 26.01）：ZIP 前面垫的数据 **≤ 8 MiB（8,388,608）时
        /// 7z 容忍整体偏移、能正常打开；8,388,609 起就报 "Cannot open the file as archive"**。
        /// 用户那个文件垫了 17,031,321 字节，落在拒绝区，所以 <c>7z l</c> 直接失败 —— 这正是本功能存在的理由。
        /// 用它当样本，测试才真的复刻了现场（32 KB 前缀的样本 7z 是能打开的，证明不了什么）。
        /// </summary>
        private const int BeyondSevenZipTolerancePrefixLength = 9 * 1024 * 1024;

        private const string InnerText = "内层内容：这条链路的最后一站。\n";

        private readonly string _root;
        private readonly string _sevenZip;

        public EmbeddedArchiveTests()
        {
            _sevenZip = LocateSevenZip();
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerEmbedded", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
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
                // 临时目录清不掉不影响结论（句柄可能还被 7z 释放中）。
            }
        }

        // ---------------------------------------------------------------- 样本构造

        private static string LocateSevenZip()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ArchiveFixer.slnx")))
                {
                    string candidate = Path.Combine(dir.FullName, "ArchiveFixer", "tools", "7zip", "7z.exe");

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                dir = dir.Parent;
            }

            string local = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe");

            if (File.Exists(local))
            {
                return local;
            }

            throw new InvalidOperationException("找不到内置 7z.exe。");
        }

        private void Run7z(string workingDirectory, params object[] args)
        {
            var psi = new ProcessStartInfo(_sevenZip)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };

            foreach (object a in args)
            {
                psi.ArgumentList.Add(a.ToString() ?? string.Empty);
            }

            using Process p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);

            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"7z 失败（exit {p.ExitCode}）：{string.Join(' ', psi.ArgumentList)}\n{stdout}\n{stderr}");
            }
        }

        private static void WriteText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        /// <summary>
        /// 假 MP4 头：前 12 字节是真格式的 <c>ftyp</c> box（<c>00 00 00 20 66 74 79 70 69 73 6F 6D</c>），
        /// 后面填随机字节。固定种子是为了让样本每次完全一样（失败能复现）。
        /// </summary>
        private static byte[] BuildFakeVideoPrefix(int length = FakeVideoPrefixLength)
        {
            byte[] prefix = new byte[length];

            new Random(20260921).NextBytes(prefix);

            byte[] boxSize = { 0x00, 0x00, 0x00, 0x20 };
            byte[] ftyp = Encoding.ASCII.GetBytes("ftypisom");

            Array.Copy(boxSize, 0, prefix, 0, boxSize.Length);
            Array.Copy(ftyp, 0, prefix, 4, ftyp.Length);

            return prefix;
        }

        /// <summary>造一个真实的小 7z（里面是一个文本文件），返回它的路径。</summary>
        private string BuildInner7z()
        {
            string payloadDirectory = Path.Combine(_root, "payload");
            WriteText(Path.Combine(payloadDirectory, "inner.txt"), InnerText);

            string inner7z = Path.Combine(_root, "inner.7z");

            // 工作目录设成 payload：让归档里的条目就叫 inner.txt，断言路径才稳定。
            Run7z(payloadDirectory, "a", "-t7z", inner7z, "inner.txt");

            return inner7z;
        }

        /// <summary>把若干文件打成一个**普通 ZIP**（没有任何前置数据）。</summary>
        private string BuildPlainPackZip(params string[] files)
        {
            string staging = Path.Combine(_root, "staging_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);

            foreach (string file in files)
            {
                File.Copy(file, Path.Combine(staging, Path.GetFileName(file)), overwrite: true);
            }

            string packZip = Path.Combine(_root, "pack_" + Guid.NewGuid().ToString("N") + ".zip");

            // 用框架自带 ZipFile：它写出来的 ZIP 内部偏移相对 ZIP 起点，正好是我们要复现的形态。
            ZipFile.CreateFromDirectory(staging, packZip);

            return packZip;
        }

        /// <summary>把普通 ZIP 前面拼上假视频头，写出"双面文件"。</summary>
        private string BuildDisguisedFile(
            string plainPackZip,
            int prefixLength = FakeVideoPrefixLength,
            string fileName = "disguised.mp4")
        {
            byte[] zipBytes = File.ReadAllBytes(plainPackZip);
            string disguised = Path.Combine(_root, fileName);

            using (var output = new FileStream(disguised, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                output.Write(BuildFakeVideoPrefix(prefixLength));
                output.Write(zipBytes);
            }

            return disguised;
        }

        /// <summary>只有假视频头 + 随机数据，尾部没有任何 ZIP（反例用）。</summary>
        private string BuildFakeVideoOnlyFile()
        {
            string path = Path.Combine(_root, "fake-only.mp4");
            File.WriteAllBytes(path, BuildFakeVideoPrefix());

            return path;
        }

        private static int FindLastEndOfCentralDirectory(byte[] bytes)
        {
            for (int i = bytes.Length - 22; i >= 0; i--)
            {
                if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x05 && bytes[i + 3] == 0x06)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>篡改 EOCD 里"声明的中央目录偏移"（偏移 16，4 字节 LE），制造对不上的损坏样本。</summary>
        private string BuildWithCorruptedDeclaredOffset(string disguisedPath, uint declaredOffset)
        {
            byte[] bytes = File.ReadAllBytes(disguisedPath);
            int eocdIndex = FindLastEndOfCentralDirectory(bytes);

            Assert.True(eocdIndex > 0, "样本里应该能找到 EOCD");

            BitConverter.GetBytes(declaredOffset).CopyTo(bytes, eocdIndex + 16);

            string broken = Path.Combine(_root, "broken_" + Guid.NewGuid().ToString("N") + ".mp4");
            File.WriteAllBytes(broken, bytes);

            return broken;
        }

        /// <summary>给"双面文件"追加一个合法的 ZIP 注释：写 EOCD 偏移 20 的注释长度（2 字节 LE），再补上注释字节。</summary>
        private string BuildWithZipComment(string disguisedPath, int commentLength)
        {
            byte[] bytes = File.ReadAllBytes(disguisedPath);
            int eocdIndex = FindLastEndOfCentralDirectory(bytes);

            Assert.True(eocdIndex > 0, "样本里应该能找到 EOCD");

            byte[] withComment = new byte[bytes.Length + commentLength];

            Array.Copy(bytes, withComment, bytes.Length);
            BitConverter.GetBytes((ushort)commentLength).CopyTo(withComment, eocdIndex + 20);

            for (int i = 0; i < commentLength; i++)
            {
                withComment[bytes.Length + i] = (byte)'C';
            }

            string path = Path.Combine(_root, "commented_" + Guid.NewGuid().ToString("N") + ".mp4");
            File.WriteAllBytes(path, withComment);

            return path;
        }

        private SevenZipEngine CreateEngine()
        {
            var engine = new SevenZipEngine();
            Assert.True(engine.IsAvailable, $"测试需要内置 7z：{_sevenZip}");
            return engine;
        }

        // ---------------------------------------------------------------- 正向：检测器

        [Fact]
        public void 双面文件_检测器命中且偏移条目数正确()
        {
            string disguised = BuildDisguisedFile(BuildPlainPackZip(BuildInner7z()));
            long fileLength = new FileInfo(disguised).Length;

            EmbeddedArchiveInfo info = EmbeddedArchiveDetector.Detect(disguised);

            Assert.True(info.Found);
            Assert.Equal(FakeVideoPrefixLength, info.Offset);
            Assert.Equal("ZIP", info.Format);
            Assert.Equal(".zip", info.SuggestedExtension);
            Assert.Equal(1, info.EntryCount);
            Assert.Equal(fileLength - FakeVideoPrefixLength, info.ArchiveLength);
            Assert.Contains("尾部藏着一个 ZIP", info.Message);
            Assert.Contains("需要用偏移取出", info.Message);
        }

        /// <summary>
        /// 真实现场那个 ZIP 里有两个条目（<c>72569473.7z.001</c> 与 <c>.002</c>），
        /// 所以条目数也得能数对 —— 它是"里面到底是什么"的第一个线索。
        /// </summary>
        [Fact]
        public void 双面文件_两个条目也能数对()
        {
            string inner7z = BuildInner7z();
            string extra = Path.Combine(_root, "readme.txt");
            WriteText(extra, "说明文件\n");

            string disguised = BuildDisguisedFile(BuildPlainPackZip(inner7z, extra));

            EmbeddedArchiveInfo info = EmbeddedArchiveDetector.Detect(disguised);

            Assert.True(info.Found);
            Assert.Equal(FakeVideoPrefixLength, info.Offset);
            Assert.Equal(2, info.EntryCount);
        }

        [Fact]
        public void 双面文件_只读尾部窗口的边界()
        {
            string disguised = BuildDisguisedFile(BuildPlainPackZip(BuildInner7z()));

            // 带 2000 字节 ZIP 注释：EOCD 因此离文件末尾有 2022 字节。
            string commented = BuildWithZipComment(disguised, 2000);

            // 注释长度必须被算进去，否则"EOCD 是不是文件收尾"这一条会误判，命中整条丢。
            EmbeddedArchiveInfo info = EmbeddedArchiveDetector.Detect(commented);
            Assert.True(info.Found);
            Assert.Equal(FakeVideoPrefixLength, info.Offset);

            // 窗口比 22 + 注释长度还小：尾部这段里根本没有 EOCD 签名，只能报不命中。
            // 这也说明默认 128 KB 的来由（22 + 注释上限 65535），窗口不是一个随便定的数。
            Assert.False(EmbeddedArchiveDetector.Detect(commented, tailBytes: 64).Found);
        }

        // ---------------------------------------------------------------- 正向：识别 + 后缀状态 + 改名

        [Fact]
        public async Task 双面文件_识别服务报ZIP且带内嵌偏移()
        {
            string disguised = BuildDisguisedFile(BuildPlainPackZip(BuildInner7z()));

            var detect = new ArchiveDetectService();

            DetectResult result = await detect.DetectAsync(disguised);

            Assert.Equal("ZIP", result.Format);
            Assert.Equal(".zip", result.SuggestedExtension);
            Assert.True(result.IsArchive);
            Assert.True(result.IsKnownFormat);

            // 80 而不是 100：结论来自"尾部自洽性推断"，不是文件开头的直接证据。
            Assert.Equal(80, result.Confidence);
            Assert.Equal(FakeVideoPrefixLength, result.EmbeddedArchiveOffset);
            Assert.Contains("尾部藏着一个 ZIP", result.Message);

            var task = new ArchiveTask(disguised);
            await detect.ApplyDetectResultAsync(task);

            Assert.Equal(FakeVideoPrefixLength, task.EmbeddedArchiveOffset);
            Assert.Equal("ZIP", task.DetectedFormat);
            Assert.True(task.IsArchive);
        }

        [Fact]
        public async Task 双面文件_后缀状态是内嵌归档且不改名()
        {
            string disguised = BuildDisguisedFile(BuildPlainPackZip(BuildInner7z()));

            var detect = new ArchiveDetectService();
            var task = new ArchiveTask(disguised);

            await detect.ApplyDetectResultAsync(task);

            Assert.Equal(StatusText.ExtensionEmbedded, task.ExtensionStatus);

            // 结论一：改名会给出和原名一样的路径（"没有可修正的后缀"），预览里就是"将跳过"。
            var rename = new RenameService();
            string newPath = rename.BuildNewPath(task, RenameOptions.CreateFixByDetectedFormat(AppSettings.CreateDefault()));
            Assert.Equal(disguised, newPath);

            List<RenamePreviewItem> preview = rename.BuildPreview(
                new[] { task },
                RenameOptions.CreateFixByDetectedFormat(AppSettings.CreateDefault()));

            RenamePreviewItem item = Assert.Single(preview);
            Assert.False(item.CanRename, "内嵌归档不该被改名：改成 .zip 后 7z 照样打不开");

            // 结论二：统计把它算进"已识别"，而不是"未知"。
            TaskSummary summary = new TaskSummaryService().BuildSummary(new[] { task });
            Assert.Equal(1, summary.RecognizedCount);
            Assert.Equal(0, summary.UnknownCount);

            // 结论三：配中性色（跟分卷后缀一样），不要用警告色把人引去改名。
            var converter = new StatusToBrushConverter();
            Assert.Same(converter.InfoBrush, converter.Convert(StatusText.ExtensionEmbedded, typeof(object), null!, null!));
        }

        // ---------------------------------------------------------------- 正向：抠出 → 引擎列出 → 解出内层

        [Fact]
        public async Task 双面文件_抠出后能被真实引擎列出并解出内层()
        {
            string inner7z = BuildInner7z();
            string disguised = BuildDisguisedFile(BuildPlainPackZip(inner7z));
            long fileLength = new FileInfo(disguised).Length;

            EmbeddedArchiveInfo info = EmbeddedArchiveDetector.Detect(disguised);
            Assert.True(info.Found);

            SevenZipEngine engine = CreateEngine();

            // 第 1 步：按偏移抠出 [32768, EOF)。
            string carveTarget = Path.Combine(_root, "carved", "pack.zip");
            CarveResult carve = EmbeddedArchiveCarver.Carve(disguised, info.Offset, carveTarget);

            Assert.True(carve.Success, carve.Message);
            Assert.True(File.Exists(carve.OutputPath));
            Assert.Equal(fileLength - FakeVideoPrefixLength, carve.BytesWritten);
            Assert.Equal(File.ReadAllBytes(disguised).Skip(FakeVideoPrefixLength).ToArray(), File.ReadAllBytes(carve.OutputPath));

            // 第 2 步：抠出来的文件能被真实引擎列出（一条字节都不改，偏移自然就对齐了）。
            ArchiveListResult list = await engine.ListAsync(
                ArchiveRequest.For(carve.OutputPath),
                CancellationToken.None);

            Assert.True(list.Success, $"抠出来的文件应能被 7z 列出：{list.Message}");
            Assert.True(list.FileCount >= 1, $"应至少列出 1 个条目，实际 {list.FileCount}");
            Assert.Contains(list.Entries, e => e.Path.EndsWith("inner.7z", StringComparison.OrdinalIgnoreCase));

            // 第 3 步：解出 ZIP 里的内层 7z。
            string packOutput = Path.Combine(_root, "out-pack");

            ArchiveOperationResult unpack = await engine.ExtractAsync(
                new ArchiveRequest { ArchivePath = carve.OutputPath, OutputPath = packOutput, Password = string.Empty },
                new ExtractOptions(),
                CancellationToken.None);

            Assert.True(unpack.Success, $"解 ZIP 应成功：{unpack.Status} / {unpack.Message}");

            string innerExtracted = Path.Combine(packOutput, "inner.7z");
            Assert.True(File.Exists(innerExtracted), "ZIP 里应解出 inner.7z");

            // 第 4 步：再解一层，拿到最终文件 —— 整条链路（识别 → 抠出 → 列出 → 解出内层）跑通。
            string innerOutput = Path.Combine(_root, "out-inner");

            ArchiveOperationResult un7z = await engine.ExtractAsync(
                new ArchiveRequest { ArchivePath = innerExtracted, OutputPath = innerOutput, Password = string.Empty },
                new ExtractOptions(),
                CancellationToken.None);

            Assert.True(un7z.Success, $"解内层 7z 应成功：{un7z.Status} / {un7z.Message}");

            string finalFile = Path.Combine(innerOutput, "inner.txt");
            Assert.True(File.Exists(finalFile), "最终应解出 inner.txt");
            Assert.Equal(InnerText, File.ReadAllText(finalFile));
        }

        /// <summary>
        /// 真实现场复刻：前面的数据超过 7-Zip 的容忍上限时，**原文件直接被 7z 拒绝**，
        /// 只有抠出来才能解 —— 这就是用户遇到的那件事（他的文件垫了 17,031,321 字节）。
        ///
        /// 顺带记录了实测边界：本机 7-Zip 26.01 对"ZIP 前面垫数据"的容忍上限是 8 MiB，
        /// 8,388,608 能打开、8,388,609 就报 "Cannot open the file as archive"。
        /// 所以"7z 打不开双面文件"并不是绝对的 —— 前缀小的时候它自己就能打开，
        /// 但**抠出来这一步在任何情况下都成立**，不需要按前缀大小分情况处理。
        /// </summary>
        [Fact]
        public async Task 真实现场_前置数据超过七z容忍上限时_必须先抠出才能解()
        {
            string inner7z = BuildInner7z();
            string disguised = BuildDisguisedFile(
                BuildPlainPackZip(inner7z),
                BeyondSevenZipTolerancePrefixLength);

            SevenZipEngine engine = CreateEngine();

            // 7z 打不开原文件 —— 这正是要抠出来的原因（与本机对真实文件的实测一致）。
            ArchiveListResult onSource = await engine.ListAsync(
                ArchiveRequest.For(disguised),
                CancellationToken.None);

            Assert.False(onSource.Success, "前置数据超过 8 MiB 时 7z 必然拒绝原文件");

            EmbeddedArchiveInfo info = EmbeddedArchiveDetector.Detect(disguised);
            Assert.True(info.Found);
            Assert.Equal(BeyondSevenZipTolerancePrefixLength, info.Offset);

            CarveResult carve = EmbeddedArchiveCarver.Carve(
                disguised,
                info.Offset,
                Path.Combine(_root, "carved-big", "pack.zip"));

            Assert.True(carve.Success, carve.Message);

            ArchiveListResult list = await engine.ListAsync(
                ArchiveRequest.For(carve.OutputPath),
                CancellationToken.None);

            Assert.True(list.Success, $"抠出后应能列出：{list.Message}");
            Assert.True(list.FileCount >= 1);
        }

        // ---------------------------------------------------------------- 反例

        [Fact]
        public async Task 反例_没有尾部ZIP的假视频_不命中且仍是Unknown()
        {
            string fake = BuildFakeVideoOnlyFile();

            EmbeddedArchiveInfo info = EmbeddedArchiveDetector.Detect(fake);
            Assert.False(info.Found);
            Assert.Equal(0, info.Offset);
            Assert.Null(Record.Exception(() => EmbeddedArchiveDetector.Detect(fake)));

            DetectResult result = await new ArchiveDetectService().DetectAsync(fake);

            Assert.Equal("Unknown", result.Format);
            Assert.False(result.IsArchive);
            Assert.Equal(0, result.EmbeddedArchiveOffset);
        }

        [Fact]
        public async Task 反例_普通ZIP的delta为零_不由本检测器报出但文件头照旧识别()
        {
            string packZip = BuildPlainPackZip(BuildInner7z());

            // delta == 0：这是普通 ZIP，内部偏移本来就相对文件起点，不该由"内嵌归档"报出来。
            EmbeddedArchiveInfo info = EmbeddedArchiveDetector.Detect(packZip);
            Assert.False(info.Found);

            var detect = new ArchiveDetectService();
            DetectResult result = await detect.DetectAsync(packZip);

            Assert.Equal("ZIP", result.Format);
            Assert.True(result.IsArchive);
            Assert.Equal(0, result.EmbeddedArchiveOffset);
            Assert.Equal(100, result.Confidence);

            // 后缀状态也应该是"正常"，而不是被误判成"内嵌归档"。
            var task = new ArchiveTask(packZip);
            await detect.ApplyDetectResultAsync(task);

            Assert.Equal(StatusText.ExtensionNormal, task.ExtensionStatus);
        }

        [Fact]
        public void 反例_EOCD被截断或位置对不上_不抛异常且不命中()
        {
            string disguised = BuildDisguisedFile(BuildPlainPackZip(BuildInner7z()));
            byte[] bytes = File.ReadAllBytes(disguised);

            // ① EOCD 尾部被截断（签名还在，字段已经不全）。
            string truncated = Path.Combine(_root, "truncated.mp4");
            File.WriteAllBytes(truncated, bytes.Take(bytes.Length - 5).ToArray());
            Assert.False(EmbeddedArchiveDetector.Detect(truncated).Found);

            // ② 整个 EOCD 都被切掉。
            string noEocd = Path.Combine(_root, "no-eocd.mp4");
            File.WriteAllBytes(noEocd, bytes.Take(bytes.Length - 30).ToArray());
            Assert.False(EmbeddedArchiveDetector.Detect(noEocd).Found);

            // ③ 中央目录偏移被改成 0：算出来的起点处不是 PK 03 04。
            string wrongZero = BuildWithCorruptedDeclaredOffset(disguised, 0);
            Assert.False(EmbeddedArchiveDetector.Detect(wrongZero).Found);

            // ④ 中央目录偏移被改成一个比文件还大的值：delta < 0。
            string wrongHuge = BuildWithCorruptedDeclaredOffset(disguised, (uint)bytes.Length);
            Assert.False(EmbeddedArchiveDetector.Detect(wrongHuge).Found);
        }

        [Fact]
        public void 反例_太短的文件或空文件_不命中且不抛()
        {
            string tiny = Path.Combine(_root, "tiny.bin");
            File.WriteAllBytes(tiny, new byte[] { 0x50, 0x4B, 0x05, 0x06 });
            Assert.False(EmbeddedArchiveDetector.Detect(tiny).Found);

            string empty = Path.Combine(_root, "empty.bin");
            File.WriteAllBytes(empty, Array.Empty<byte>());
            Assert.False(EmbeddedArchiveDetector.Detect(empty).Found);

            string missing = Path.Combine(_root, "not-exists.bin");
            Assert.False(EmbeddedArchiveDetector.Detect(missing).Found);
        }

        [Fact]
        public async Task 反例_文件头已识别的格式_不去读尾部()
        {
            /*
             * 只有文件头"不认识"时才做尾部检测。
             * 这里造一个 7z 开头、后面又拼了假视频 + ZIP 的文件：
             * 文件头已经明确说是 7Z，就不该再多读 128 KB 去尾部翻 ZIP（否则会大面积误报）。
             */
            string inner7z = BuildInner7z();
            string disguised = BuildDisguisedFile(BuildPlainPackZip(inner7z));

            string mixed = Path.Combine(_root, "mixed.7z");
            using (var output = new FileStream(mixed, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                output.Write(File.ReadAllBytes(inner7z));
                output.Write(File.ReadAllBytes(disguised));
            }

            DetectResult result = await new ArchiveDetectService().DetectAsync(mixed);

            Assert.Equal("7Z", result.Format);
            Assert.Equal(0, result.EmbeddedArchiveOffset);
        }

        // ---------------------------------------------------------------- 反例/边界：抠出

        [Fact]
        public void 抠出_目标已存在时不覆盖而是改名()
        {
            string disguised = BuildDisguisedFile(BuildPlainPackZip(BuildInner7z()));
            long fileLength = new FileInfo(disguised).Length;

            EmbeddedArchiveInfo info = EmbeddedArchiveDetector.Detect(disguised);
            Assert.True(info.Found);

            string targetDirectory = Path.Combine(_root, "carved");
            Directory.CreateDirectory(targetDirectory);

            string occupied = Path.Combine(targetDirectory, "pack.zip");
            WriteText(occupied, "已存在的文件，绝不能被覆盖");

            CarveResult result = EmbeddedArchiveCarver.Carve(disguised, info.Offset, occupied);

            Assert.True(result.Success, result.Message);

            // 落点被改名（pack(1).zip），原文件一个字节都没动。
            Assert.NotEqual(occupied, result.OutputPath);
            Assert.Equal("pack(1).zip", Path.GetFileName(result.OutputPath));
            Assert.Equal("已存在的文件，绝不能被覆盖", File.ReadAllText(occupied));
            Assert.Equal(fileLength - FakeVideoPrefixLength, result.BytesWritten);
            Assert.Equal(fileLength - FakeVideoPrefixLength, new FileInfo(result.OutputPath).Length);
        }

        [Fact]
        public void 抠出_偏移越界或源文件不存在_失败而不抛()
        {
            string disguised = BuildDisguisedFile(BuildPlainPackZip(BuildInner7z()));
            long fileLength = new FileInfo(disguised).Length;

            string target = Path.Combine(_root, "carved2", "never.zip");

            CarveResult beyondEnd = EmbeddedArchiveCarver.Carve(disguised, fileLength, target);
            Assert.False(beyondEnd.Success);
            Assert.False(File.Exists(target));

            CarveResult negative = EmbeddedArchiveCarver.Carve(disguised, -1, target);
            Assert.False(negative.Success);

            CarveResult missing = EmbeddedArchiveCarver.Carve(
                Path.Combine(_root, "not-exists.mp4"),
                0,
                target);
            Assert.False(missing.Success);
        }

        [Fact]
        public void 抠出_目标目录不存在时会自动创建()
        {
            string disguised = BuildDisguisedFile(BuildPlainPackZip(BuildInner7z()));

            EmbeddedArchiveInfo info = EmbeddedArchiveDetector.Detect(disguised);
            Assert.True(info.Found);

            string target = Path.Combine(_root, "deep", "deeper", "pack.zip");

            CarveResult result = EmbeddedArchiveCarver.Carve(disguised, info.Offset, target);

            Assert.True(result.Success, result.Message);
            Assert.True(File.Exists(target));
        }
    }
}
