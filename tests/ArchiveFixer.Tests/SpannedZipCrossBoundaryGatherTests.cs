using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **跨盘 ZIP：末片在手、其余几片散在源目录里**（2026-10-05 真机第八批，用户原话
    /// 「这不是让你看着答案写过程」）。
    ///
    /// <para><b>现场</b>：`111.z0删除1/2/3` 三片散在三个源目录里，而这一组的末片 `111.zip`
    /// 压在两层层层加密的 RAR 里面。批首那一刻末片还在包里 ⇒ 源包那条路看不见它；链把末片解出来之后，
    /// 其余几片又全在用户源目录里 ⇒ 递归那条路的候选池刻意不碰源目录 ⇒ **两条路各自封闭**，
    /// 7-Zip 报 `Missing volume : 111.z01`、整条链判「部分完成」、什么都没发布。</para>
    ///
    /// <para><b>判据是硬证据</b>：末片的 EOCD 是明文（`-p` 只加密数据）⇒ 它自述"我是第 k 片、一共 n 片"。
    /// 动作 = 给源目录里那几片在入口这一层**多起一个规范卷名**（硬链接，零字节）——
    /// ⛔ 不改名、不搬、不复制；判不出 / 凑不齐 ⇒ 一个字节都不动。</para>
    /// </summary>
    public class SpannedZipCrossBoundaryGatherTests : IDisposable
    {
        private readonly string _root;

        public SpannedZipCrossBoundaryGatherTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerSpannedGather", Guid.NewGuid().ToString("N"));
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
                // 临时目录删不掉不算失败。
            }
        }

        /// <summary>
        /// **真机那个形状**：末片（自述"我是第 4 片、共 4 片"）+ 三片脏名外壳散在**别的目录**里
        /// ⇒ 必须在末片旁边接出 `111.z01/02/03` 三个规范名，而**源目录里的三片一个字节都没动、名字也没改**。
        /// </summary>
        [Fact]
        public void 末片加三片脏名外壳_接上规范卷名_源文件一个字节都没动()
        {
            string entryDirectory = Path.Combine(_root, "入口");
            string sourceDirectory = Path.Combine(_root, "源");
            Directory.CreateDirectory(entryDirectory);
            Directory.CreateDirectory(sourceDirectory);

            string tail = WriteSpannedZipTail(entryDirectory, "111.zip", disk: 3);
            var sources = new List<(string Name, string Path, long Size)>();

            foreach (string name in new[] { "111.z0删除1", "111.z0删除2", "111.z0删除3" })
            {
                string path = Path.Combine(sourceDirectory, name);
                File.WriteAllBytes(path, Enumerable.Repeat((byte)name.Length, 4096).ToArray());
                sources.Add((name, path, new FileInfo(path).Length));
            }

            VolumeNameRepair.SpannedZipDiskGather result = VolumeNameRepair.ResolveSpannedZipDiskGather(
                tail,
                sources.Select(s => new VolumeCandidate { Path = s.Path, Size = s.Size }).ToList());

            Assert.True(result.Applicable, "末片自述了盘号 ⇒ 这一档必须适用");
            Assert.True(result.Complete, $"必须凑齐：{result.Detail}");
            Assert.Equal(4, result.DiskCount);
            Assert.Equal(3, result.LinkedCount);

            foreach (int disk in new[] { 1, 2, 3 })
            {
                string link = Path.Combine(entryDirectory, VolumeNameRepair.CanonicalDiskName("111", disk));
                Assert.True(File.Exists(link), $"必须在末片旁边接出 {Path.GetFileName(link)}");
            }

            // 源目录里的三片：名字、位置、字节数一个都没变（⛔ 不改名、不搬、不复制）。
            foreach ((string name, string path, long size) in sources)
            {
                Assert.True(File.Exists(path), $"源片 {name} 必须原地不动");
                Assert.Equal(size, new FileInfo(path).Length);
                Assert.False(File.Exists(Path.Combine(entryDirectory, name)), "⛔ 不许把源片搬进工作区");
            }

            // 链接是**同一份数据**：从链接读出来的字节与源片一致。
            byte[] fromLink = File.ReadAllBytes(Path.Combine(entryDirectory, "111.z01"));
            byte[] fromSource = File.ReadAllBytes(sources[0].Path);
            Assert.Equal(fromSource, fromLink);
        }

        /// <summary>
        /// **接出来的那几个名字必须报给调用方，而且用完删掉之后源片一个字节都不少**（2026-10-05 真机第八批
        /// 当场踩到的坑）：它们是**临时名字**，留在那一层产物目录里会被发布侧当成品搬进用户目录
        /// （真机：成品里凭空多出三个 200 MiB 的同名副本），还会把「半套分卷」闸门自己绊倒
        /// （逐层回收与链尾其余物处理全被拦下）。⇒ 契约是"**谁建的谁删**"，这条用例把它钉住。
        /// </summary>
        [Fact]
        public void 接出来的名字要报给调用方_删掉之后源片一个字节都不少()
        {
            string entryDirectory = Path.Combine(_root, "入口");
            string sourceDirectory = Path.Combine(_root, "源");
            Directory.CreateDirectory(entryDirectory);
            Directory.CreateDirectory(sourceDirectory);

            string tail = WriteSpannedZipTail(entryDirectory, "111.zip", disk: 3);
            var sources = new List<string>();

            foreach (string name in new[] { "111.z0删除1", "111.z0删除2", "111.z0删除3" })
            {
                string path = Path.Combine(sourceDirectory, name);
                File.WriteAllBytes(path, new byte[4096]);
                sources.Add(path);
            }

            VolumeNameRepair.SpannedZipDiskGather result = VolumeNameRepair.ResolveSpannedZipDiskGather(
                tail,
                sources.Select(p => new VolumeCandidate { Path = p, Size = 4096 }).ToList());

            Assert.Equal(3, result.LinkedPaths.Count);
            Assert.Equal(3, result.LinkedSources.Count);

            foreach (string link in result.LinkedPaths)
            {
                Assert.True(File.Exists(link));
                Assert.Equal(
                    Path.GetFullPath(entryDirectory),
                    Path.GetFullPath(Path.GetDirectoryName(link)!),
                    ignoreCase: true);
            }

            // 调用方（RecursiveExtractor）在这一层用完就删：删名字不动数据。
            foreach (string link in result.LinkedPaths)
            {
                File.Delete(link);
            }

            foreach (string source in sources)
            {
                Assert.True(File.Exists(source), $"源片必须还在：{Path.GetFileName(source)}");
                Assert.Equal(4096, new FileInfo(source).Length);
            }

            // 入口那一层现在只剩末片自己（临时名字一个都不留）。
            Assert.Equal(
                new[] { "111.zip" },
                Directory.GetFiles(entryDirectory).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }

        /// <summary>已经逐字规范名地摆在末片旁边 ⇒ 一个链接都不建（不必收）。</summary>
        [Fact]
        public void 其余几片本来就摆在末片旁边_一条链接都不建()
        {
            string entryDirectory = Path.Combine(_root, "入口");
            Directory.CreateDirectory(entryDirectory);

            string tail = WriteSpannedZipTail(entryDirectory, "111.zip", disk: 2);

            foreach (string name in new[] { "111.z01", "111.z02" })
            {
                File.WriteAllBytes(Path.Combine(entryDirectory, name), new byte[1024]);
            }

            VolumeNameRepair.SpannedZipDiskGather result =
                VolumeNameRepair.ResolveSpannedZipDiskGather(tail, Array.Empty<VolumeCandidate>());

            Assert.True(result.Applicable);
            Assert.True(result.Complete);
            Assert.Equal(0, result.LinkedCount);
        }

        /// <summary>凑不齐 ⇒ 一片都不接，并点名缺哪几片（⛔ 不半套）。</summary>
        [Fact]
        public void 凑不齐_一片都不接_并点名缺的是哪一片()
        {
            string entryDirectory = Path.Combine(_root, "入口");
            string sourceDirectory = Path.Combine(_root, "源");
            Directory.CreateDirectory(entryDirectory);
            Directory.CreateDirectory(sourceDirectory);

            string tail = WriteSpannedZipTail(entryDirectory, "111.zip", disk: 3);
            var candidates = new List<VolumeCandidate>();

            foreach (string name in new[] { "111.z0删除1", "111.z0删除2" })
            {
                string path = Path.Combine(sourceDirectory, name);
                File.WriteAllBytes(path, new byte[4096]);
                candidates.Add(new VolumeCandidate { Path = path, Size = 4096 });
            }

            VolumeNameRepair.SpannedZipDiskGather result =
                VolumeNameRepair.ResolveSpannedZipDiskGather(tail, candidates);

            Assert.True(result.Applicable);
            Assert.False(result.Complete);
            Assert.Equal(0, result.LinkedCount);
            Assert.Contains("111.z03", result.MissingNames);
            Assert.False(File.Exists(Path.Combine(entryDirectory, "111.z01")), "⛔ 凑不齐 ⇒ 一条链接都不许建");
            Assert.False(File.Exists(Path.Combine(entryDirectory, "111.z02")), "⛔ 凑不齐 ⇒ 一条链接都不许建");
        }

        /// <summary>除末片外那几片大小彼此对不上 ⇒ 判不出 ⇒ 什么都不做。</summary>
        [Fact]
        public void 除末片外大小对不上_判不出_什么都不做()
        {
            string entryDirectory = Path.Combine(_root, "入口");
            string sourceDirectory = Path.Combine(_root, "源");
            Directory.CreateDirectory(entryDirectory);
            Directory.CreateDirectory(sourceDirectory);

            string tail = WriteSpannedZipTail(entryDirectory, "111.zip", disk: 2);
            var candidates = new List<VolumeCandidate>();
            int[] sizes = { 4096, 8192 };

            for (int index = 0; index < 2; index++)
            {
                string path = Path.Combine(sourceDirectory, $"111.z0删除{index + 1}");
                File.WriteAllBytes(path, new byte[sizes[index]]);
                candidates.Add(new VolumeCandidate { Path = path, Size = sizes[index] });
            }

            VolumeNameRepair.SpannedZipDiskGather result =
                VolumeNameRepair.ResolveSpannedZipDiskGather(tail, candidates);

            Assert.True(result.Applicable);
            Assert.False(result.Complete);
            Assert.Equal(0, result.LinkedCount);
            Assert.False(File.Exists(Path.Combine(entryDirectory, "111.z01")));
        }

        /// <summary>同一个卷号上两份候选 ⇒ 判不出哪一份属于这一组 ⇒ 什么都不做。</summary>
        [Fact]
        public void 同一卷号两份候选_判不出_什么都不做()
        {
            string entryDirectory = Path.Combine(_root, "入口");
            string firstDirectory = Path.Combine(_root, "甲");
            string secondDirectory = Path.Combine(_root, "乙");
            Directory.CreateDirectory(entryDirectory);
            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(secondDirectory);

            string tail = WriteSpannedZipTail(entryDirectory, "111.zip", disk: 1);

            string a = Path.Combine(firstDirectory, "111.z01");
            string b = Path.Combine(secondDirectory, "111.z0删除1");
            File.WriteAllBytes(a, new byte[2048]);
            File.WriteAllBytes(b, new byte[2048]);

            VolumeNameRepair.SpannedZipDiskGather result = VolumeNameRepair.ResolveSpannedZipDiskGather(
                tail,
                new[]
                {
                    new VolumeCandidate { Path = a, Size = 2048 },
                    new VolumeCandidate { Path = b, Size = 2048 }
                });

            Assert.True(result.Applicable);
            Assert.False(result.Complete);
            Assert.Equal(0, result.LinkedCount);
            Assert.False(File.Exists(Path.Combine(entryDirectory, "111.z01")));
        }

        /// <summary>不是"跨盘 zip 的末片"（单盘 zip / 随便一个文件）⇒ 这一档压根不适用。</summary>
        [Fact]
        public void 不是跨盘末片_这一档不适用()
        {
            string entryDirectory = Path.Combine(_root, "入口");
            Directory.CreateDirectory(entryDirectory);

            string single = WriteSpannedZipTail(entryDirectory, "222.zip", disk: 0);
            string plain = Path.Combine(entryDirectory, "333.bin");
            File.WriteAllBytes(plain, new byte[128]);

            Assert.False(VolumeNameRepair.ResolveSpannedZipDiskGather(single, Array.Empty<VolumeCandidate>()).Applicable);
            Assert.False(VolumeNameRepair.ResolveSpannedZipDiskGather(plain, Array.Empty<VolumeCandidate>()).Applicable);
            Assert.False(VolumeNameRepair.ResolveSpannedZipDiskGather(null, Array.Empty<VolumeCandidate>()).Applicable);
        }

        /// <summary>
        /// 造一片跨盘 zip 的**末片**：先造一个**真的单盘 zip**（真实的中央目录 + EOCD），
        /// 再把 EOCD 里那两个盘号字段改成 <paramref name="disk"/> —— 真 PKZIP 跨盘的末片就是这个形状
        /// （中央目录整段在末片上、盘号自述"我是第几片"）。<paramref name="disk"/> = 0 ⇒ 保持单盘（不适用那一档）。
        /// </summary>
        private static string WriteSpannedZipTail(string directory, string fileName, int disk)
        {
            string path = Path.Combine(directory, fileName);

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var archive = new System.IO.Compression.ZipArchive(
                       stream,
                       System.IO.Compression.ZipArchiveMode.Create))
            {
                System.IO.Compression.ZipArchiveEntry entry = archive.CreateEntry("payload.bin");
                using Stream entryStream = entry.Open();
                entryStream.Write(new byte[512], 0, 512);
            }

            if (disk > 0)
            {
                byte[] bytes = File.ReadAllBytes(path);
                int eocd = FindEndOfCentralDirectory(bytes);
                Assert.True(eocd > 0, "造样本失败：找不到 EOCD");

                bytes[eocd + 4] = (byte)(disk & 0xFF);
                bytes[eocd + 5] = (byte)((disk >> 8) & 0xFF);
                bytes[eocd + 6] = (byte)(disk & 0xFF);
                bytes[eocd + 7] = (byte)((disk >> 8) & 0xFF);
                File.WriteAllBytes(path, bytes);
            }

            return path;
        }

        private static int FindEndOfCentralDirectory(byte[] bytes)
        {
            for (int index = bytes.Length - 22; index >= 0; index--)
            {
                if (bytes[index] == 0x50 && bytes[index + 1] == 0x4B
                    && bytes[index + 2] == 0x05 && bytes[index + 3] == 0x06)
                {
                    return index;
                }
            }

            return -1;
        }
    }
}
