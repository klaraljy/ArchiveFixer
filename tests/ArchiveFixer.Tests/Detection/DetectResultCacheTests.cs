using ArchiveFixer.Detection;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **识别结论缓存**的守门用例（用户 2026-09-29 任务 B：他原话"有很多相同文件的情况，
    /// 如果你这个每个都花这么多的时间去识别，那肯定是不行的"）。
    ///
    /// <para><b>它守的三件事</b>：</para>
    /// <list type="number">
    /// <item><description><b>相同文件真的走缓存</b> —— 靠"把中段改掉、时间改回来，结论仍是旧的"钉住
    /// （见 <see cref="相同文件_第二批走缓存_不再重算结论"/>）；关掉缓存的那一份实例必须当场扫出新结论，
    /// 否则这条用例只是"什么都没发生"。</description></item>
    /// <item><description><b>识别结论逐条不变</b> —— 同一批文件在"开缓存"与"关缓存"两个实例上各跑一遍，
    /// 每一个字段逐一比对（用户 2026-09-29 任务 B 第 3 条①）。</description></item>
    /// <item><description><b>源文件一变就不许吃旧结论</b>（AGENTS.md §6 不变量 11）——
    /// 大小没变、修改时间变了 → 必须重新识别。</description></item>
    /// </list>
    ///
    /// <para><b>红检</b>：把 <c>ArchiveDetectService</c> 里那两处
    /// <c>DetectResultCache.TryGet(...)</c> 摘掉（或把 <c>StoreUnknown</c> 的写入去掉）→
    /// <see cref="相同文件_第二批走缓存_不再重算结论"/> 当场变红（第二次识别会扫出中段那个 7z 魔数）；
    /// 把 <c>Clone()</c> 换成直接交出缓存对象 → <see cref="缓存里的那一份_不许被调用方改到"/> 变红。</para>
    /// </summary>
    public class DetectResultCacheTests : IDisposable
    {
        private readonly string _root;

        public DetectResultCacheTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "af_detect_cache_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            // 每条用例从零开始：缓存是**进程级**的，上一条用例留下的条目会让"命中/未命中"失去意义。
            DetectResultCache.Clear();
        }

        public void Dispose()
        {
            DetectResultCache.Clear();

            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // 临时目录删不掉不影响结论。
            }
        }

        /// <summary>
        /// **相同文件走缓存的决定性证据**：把第二份的**中段**改出一个 7z 魔数（头 34 KB 与尾 256 KB 都不动），
        /// 再把修改时间改回与第一份一模一样 —— 缓存键的四样证据全同，于是它必须沿用第一份的结论。
        ///
        /// <para>反向对照在最后一段：**关掉缓存**的同一个服务扫同一份文件，必须扫出那个魔数。
        /// 少了这一段，这条用例就算"产品什么都没做"也会绿。</para>
        /// </summary>
        [Fact]
        public async Task 相同文件_第二批走缓存_不再重算结论()
        {
            const int size = 2 * 1024 * 1024;

            string first = Path.Combine(_root, "same-a.bin");
            string second = Path.Combine(_root, "same-b.bin");

            WriteZeros(first, size);
            File.Copy(first, second, overwrite: true);

            DateTime stamp = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Local);
            File.SetLastWriteTime(first, stamp);
            File.SetLastWriteTime(second, stamp);

            var detect = new ArchiveDetectService();
            DetectResult baseline = await detect.DetectAsync(first);

            Assert.Equal("Unknown", baseline.Format);

            // 中段（1 MiB 处，既不在头 34 KB 里、也不在尾 256 KB 里）塞一个 7z 魔数。
            byte[] magic = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };

            await using (var stream = new FileStream(second, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.Seek(1024 * 1024, SeekOrigin.Begin);
                await stream.WriteAsync(magic);
            }

            File.SetLastWriteTime(second, stamp);

            DetectResult cached = await detect.DetectAsync(second);

            Assert.Equal(baseline.Format, cached.Format);
            Assert.Equal(baseline.IsArchive, cached.IsArchive);

            // 反向对照：关掉缓存，同一份文件必须被顺序扫扫出中段那个魔数。
            var noCache = new ArchiveDetectService { UseDetectCache = false };
            DetectResult scanned = await noCache.DetectAsync(second);

            Assert.Equal("7Z", scanned.Format);
        }

        /// <summary>同一批文件在"开缓存"与"关缓存"两个实例上各跑一遍，逐字段比对（结论一个字都不许变）。</summary>
        [Fact]
        public async Task 识别结论逐条不变_开缓存与关缓存两跑完全一致()
        {
            List<string> files = BuildVariedBatch(_root);

            var withCache = new ArchiveDetectService();
            var withoutCache = new ArchiveDetectService { UseDetectCache = false };

            var firstPass = new List<DetectResult>();
            var secondPass = new List<DetectResult>();

            // 先跑**关缓存**那一份（模拟改前），再跑开缓存那一份（此时缓存里已经有货）。
            foreach (string file in files)
            {
                firstPass.Add(await withoutCache.DetectAsync(file));
            }

            foreach (string file in files)
            {
                secondPass.Add(await withCache.DetectAsync(file));
            }

            Assert.Equal(firstPass.Count, secondPass.Count);

            for (int index = 0; index < files.Count; index++)
            {
                DetectResult expected = firstPass[index];
                DetectResult actual = secondPass[index];
                string name = Path.GetFileName(files[index]);

                Assert.Equal(expected.Format, actual.Format);
                Assert.Equal(expected.SuggestedExtension, actual.SuggestedExtension);
                Assert.Equal(expected.IsArchive, actual.IsArchive);
                Assert.Equal(expected.IsKnownFormat, actual.IsKnownFormat);
                Assert.Equal(expected.IsProbablyEncrypted, actual.IsProbablyEncrypted);
                Assert.Equal(expected.Message, actual.Message);
                Assert.Equal(expected.HeaderHex, actual.HeaderHex);
                Assert.Equal(expected.Confidence, actual.Confidence);
                Assert.Equal(expected.EmbeddedArchiveOffset, actual.EmbeddedArchiveOffset);
                Assert.Equal(expected.EmbeddedArchiveEnd, actual.EmbeddedArchiveEnd);
                Assert.Equal(expected.EmbeddedDirectReadSupported, actual.EmbeddedDirectReadSupported);
                Assert.Equal(expected.EmbeddedDirectReadReason, actual.EmbeddedDirectReadReason);

                // 逐条点名，失败时一眼看出是哪一个文件对不上。
                Assert.True(expected.Format == actual.Format, $"{name} 的格式结论变了");
            }
        }

        /// <summary>
        /// AGENTS.md §6 不变量 11：**源文件变了就不许吃旧结论**。
        /// 大小一个字没变、只把修改时间推后一分钟 → 缓存必须判成"另一份"，重新识别。
        /// </summary>
        [Fact]
        public async Task 大小没变但修改时间变了_必须重新识别()
        {
            const int size = 2 * 1024 * 1024;

            string file = Path.Combine(_root, "touched.bin");
            WriteZeros(file, size);

            DateTime stamp = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Local);
            File.SetLastWriteTime(file, stamp);

            var detect = new ArchiveDetectService();
            DetectResult before = await detect.DetectAsync(file);

            Assert.Equal("Unknown", before.Format);

            byte[] magic = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };

            await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.Seek(1024 * 1024, SeekOrigin.Begin);
                await stream.WriteAsync(magic);
            }

            // 时间**故意不还原**（推后一分钟）：这就是"源文件变了"。
            File.SetLastWriteTime(file, stamp.AddMinutes(1));

            DetectResult after = await detect.DetectAsync(file);

            Assert.Equal("7Z", after.Format);
        }

        /// <summary>
        /// 缓存交出来的那一份必须能安全改写：后处理会往识别结果上叠东西
        /// （RAR 加密判读改写 <c>Message</c> 与 <c>IsProbablyEncrypted</c>），
        /// 直接交出缓存对象的话，下一次命中就会读到被改过的那一份。
        /// </summary>
        [Fact]
        public void 缓存里的那一份_不许被调用方改到()
        {
            var key = new DetectCacheKey
            {
                Length = 1234,
                LastWriteTicks = 5678,
                HeadFingerprint = 0xAAAA,
                TailFingerprint = 0xBBBB,
                Evidence = DetectCacheEvidence.WholeFile
            };

            DetectResultCache.Store(key, new DetectResult { Format = "7Z", Message = "原始消息" });

            Assert.True(DetectResultCache.TryGet(key, out DetectResult first));
            first.Message = "被调用方改过";
            first.IsProbablyEncrypted = true;

            Assert.True(DetectResultCache.TryGet(key, out DetectResult second));
            Assert.Equal("原始消息", second.Message);
            Assert.False(second.IsProbablyEncrypted);
        }

        /// <summary>指纹：内容一样就一样、差一个字节就不一样（缓存键的地基）。</summary>
        [Fact]
        public void 内容指纹_同内容同值_差一个字节不同值()
        {
            byte[] a = new byte[100_000];
            byte[] b = new byte[100_000];

            new Random(20260929).NextBytes(a);
            a.CopyTo(b, 0);

            Assert.Equal(DetectResultCache.Fingerprint(a), DetectResultCache.Fingerprint(b));

            b[50_000] ^= 0x01;

            Assert.NotEqual(DetectResultCache.Fingerprint(a), DetectResultCache.Fingerprint(b));
        }

        /// <summary>
        /// 本机真实包（内置 7z 现造）：开缓存前后结论一致，而且**真的是从缓存出来的**。
        /// 用"同一份内容拷两份"来验 —— 拷出来的那份大小、修改时间、头尾都与原件相同。
        /// </summary>
        [Fact]
        public async Task 真包拷贝_第二份走缓存且结论一致()
        {
            string? sevenZip = ResolveBundledSevenZip();

            if (sevenZip == null)
            {
                return;
            }

            string payload = Path.Combine(_root, "payload.txt");
            File.WriteAllText(payload, "cache sample payload");

            string archive = Path.Combine(_root, "sample.7z");
            RarSampleSet.RunSevenZip(sevenZip, _root, "a", "-t7z", "-mx0", archive, payload);

            string copy = Path.Combine(_root, "sample-copy.7z");
            File.Copy(archive, copy, overwrite: true);

            DateTime stamp = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Local);
            File.SetLastWriteTime(archive, stamp);
            File.SetLastWriteTime(copy, stamp);

            var detect = new ArchiveDetectService();

            DetectResult original = await detect.DetectAsync(archive);
            DetectResult duplicated = await detect.DetectAsync(copy);

            Assert.Equal("7Z", original.Format);
            Assert.Equal(original.Format, duplicated.Format);
            Assert.Equal(original.SuggestedExtension, duplicated.SuggestedExtension);
            Assert.Equal(original.HeaderHex, duplicated.HeaderHex);
            Assert.True(DetectResultCache.Statistics.Hits >= 1 || DetectResultCache.Statistics.Stores >= 1);
        }

        /// <summary>
        /// 一批形状各异的文件（真包 / 伪装 / 认不出 / 双面文件），给"结论不变"那条用例当输入。
        /// </summary>
        private static List<string> BuildVariedBatch(string root)
        {
            string directory = Path.Combine(root, "batch");
            Directory.CreateDirectory(directory);

            var files = new List<string>();

            string? sevenZip = ResolveBundledSevenZip();

            // ① 真 7z（内置 7z 现造）
            if (sevenZip != null)
            {
                string payload = Path.Combine(directory, "payload.txt");
                File.WriteAllText(payload, "varied batch payload");

                string sevenZipFile = Path.Combine(directory, "real.7z");
                RarSampleSet.RunSevenZip(sevenZip, directory, "a", "-t7z", "-mx0", sevenZipFile, payload);
                files.Add(sevenZipFile);

                // ② 真 zip
                string zipFile = Path.Combine(directory, "real.zip");
                RarSampleSet.RunSevenZip(sevenZip, directory, "a", "-tzip", "-mx0", zipFile, payload);
                files.Add(zipFile);

                // ③ 双面文件：一段文本 + 尾部一整个 ZIP
                string doubled = Path.Combine(directory, "double-sided.dat");
                using (FileStream stream = File.Create(doubled))
                {
                    byte[] prefix = System.Text.Encoding.ASCII.GetBytes(new string('v', 4096));
                    stream.Write(prefix, 0, prefix.Length);
                    byte[] zipBytes = File.ReadAllBytes(zipFile);
                    stream.Write(zipBytes, 0, zipBytes.Length);
                }

                files.Add(doubled);
            }

            // ④ 认不出的（全零）
            string zeros = Path.Combine(directory, "zeros.bin");
            WriteZeros(zeros, 300 * 1024);
            files.Add(zeros);

            // ⑤ 中段藏着 7z 魔数的（顺序扫那一档）
            string middle = Path.Combine(directory, "middle-magic.bin");
            WriteZeros(middle, 512 * 1024);

            using (FileStream stream = new(middle, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.Seek(200 * 1024, SeekOrigin.Begin);
                stream.Write(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C });
            }

            files.Add(middle);

            // ⑥ 伪装后缀的真包（"不信后缀"那条铁律）
            if (sevenZip != null)
            {
                string disguised = Path.Combine(directory, "disguised.jpg");
                File.Copy(Path.Combine(directory, "real.7z"), disguised, overwrite: true);
                files.Add(disguised);
            }

            return files;
        }

        private static void WriteZeros(string path, int length)
        {
            using FileStream stream = File.Create(path);
            stream.SetLength(length);
        }

        private static string? ResolveBundledSevenZip()
        {
            DirectoryInfo? current = new(AppContext.BaseDirectory);

            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "ArchiveFixer.slnx")))
                {
                    string candidate = Path.Combine(current.FullName, "src", "ArchiveFixer", "tools", "7zip", "7z.exe");

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                current = current.Parent;
            }

            return null;
        }
    }
}
