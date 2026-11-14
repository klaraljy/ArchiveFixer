using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using ArchiveFixer.Detection;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **三种分卷格式的内容级判读能力**（用户 2026-10-01 追问：「7z / rar / zip 分卷文件的情况假设都是一样的」
    /// —— 这条假设**不成立**，三种格式"自述能力"差得很远）。
    ///
    /// <para>这一组用**真造的分卷**（本项目自带 7z；RAR 需要本机 Rar.exe，没有就跳过）把三件事钉死，
    /// 判据全部来自 <see cref="VolumeNumberFromContent.Read"/> 的机器字段（⛔ 不比中文）：</para>
    /// <list type="number">
    /// <item><description><b>7z</b>：只有**首片**带魔数 <c>37 7A BC AF 27 1C</c>；中间片与末片是裸切片，
    /// 内容里**没有任何卷号、也没有"我是末卷"** ⇒ 定序只能靠"尺寸排序候补 + 硬链接试开"。</description></item>
    /// <item><description><b>7-Zip 造的跨盘 zip</b>（<c>-tzip -v</c>）：末片虽然带 EOCD，但盘号字段写的是
    /// <c>0 / 0</c>（7-Zip 就是这么写的）⇒ 内容级判不出它是末片；首片带本地头 ⇒ 只判得出"是这一组的一员"。</description></item>
    /// <item><description><b>RAR（RAR4 新编号）</b>：**每一卷的卷号都写在内容里**（真测 0/1/2/3），
    /// 首卷还带"首卷"标记 ⇒ 不看名字也能知道自己是第几卷。⚠ 总片数内容里没有（要看整组）。</description></item>
    /// </list>
    ///
    /// <para>另外钉一条**不依赖用户数据**的跨盘 zip 末片事实：PKZIP 那种跨盘 zip 的末片，
    /// EOCD 里的盘号字段就是"我这一片是第几片 + 一共几片"（真机 DDD 的 <c>222.zip</c> 读出「卷号 4 / 共 4 片」）。</para>
    /// </summary>
    public class VolumeContentProbeCapabilityTests : IDisposable
    {
        private readonly string _root;
        private readonly string? _sevenZip;
        private readonly string? _rar;

        public VolumeContentProbeCapabilityTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerVolumeCapability", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _sevenZip = LocateSevenZip();
            _rar = LocateRar();
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

        /// <summary>7z 分卷：首片有魔数（⇒ 必是首卷），中间片与末片内容里什么都没有。</summary>
        [Fact]
        public void 七Z分卷_只有首片带魔数_中间片与末片内容里没有任何卷信息()
        {
            if (_sevenZip == null)
            {
                return;   // 没有 7z.exe 的机器上不假装验过（AGENTS.md §11.2 同一口径）
            }

            List<string> parts = MakeSevenZipSplitSet("7z-set", "set.7z");

            Assert.True(parts.Count >= 3, "样本至少要 3 片才说明得了问题");

            // 首片：魔数在 ⇒ 认得出"这是 7z"，而它只可能是第 1 卷。
            VolumeNumberReading first = VolumeNumberFromContent.Read(parts[0]);
            Assert.Equal(VolumeContentFormat.SevenZip, first.Format);

            // 中间片与末片：不是归档、也没有卷号 —— ⛔ 内容里根本没有这些信息。
            foreach (string part in parts.Skip(1))
            {
                VolumeNumberReading reading = VolumeNumberFromContent.Read(part);
                Assert.Null(reading.Number);
                Assert.False(reading.IsVolumeMember);
            }
        }

        /// <summary>7-Zip 造的跨盘 zip：首片只判得出"是成员"，末片（EOCD 盘号 0/0）连"是成员"都判不出。</summary>
        [Fact]
        public void 七Zip造的跨盘zip_末片EOCD自述盘号0_内容级判不出末片()
        {
            if (_sevenZip == null)
            {
                return;
            }

            List<string> parts = MakeSevenZipSplitSet("zip-set", "set.zip", "-tzip");

            Assert.True(parts.Count >= 3, "样本至少要 3 片");

            VolumeNumberReading first = VolumeNumberFromContent.Read(parts[0]);
            Assert.True(first.IsVolumeMember, "首片带本地头，应当判得出'是这一组的一员'");
            Assert.Null(first.Number);   // 但判不出"是第 1 片"

            // 末片：EOCD 在，但盘号字段是 0/0 ⇒ 内容级认不出它是末片（已知限制，⛔ 不许当成"能判"）。
            string last = parts[^1];
            byte[] tail = File.ReadAllBytes(last);
            Assert.True(
                tail.Length >= 22 && tail[^22] == 0x50 && tail[^21] == 0x4B && tail[^20] == 0x05 && tail[^19] == 0x06,
                "7-Zip 造的末片应当在最末尾带 EOCD");

            VolumeNumberReading lastReading = VolumeNumberFromContent.Read(last);
            Assert.Null(lastReading.Number);
            Assert.False(lastReading.IsVolumeMember);
        }

        /// <summary>
        /// **PKZIP 那种跨盘 zip 的末片自述盘号**（不依赖用户数据：手工拼一个格式正确的 EOCD）。
        /// 真机 DDD 的 <c>222.zip</c>（49 MB，4 片里的第 4 片）实测就是 <c>卷号=4 / 总片数=4</c>。
        /// ⛔ 注意：这是**末片**才有的能力，中间片（裸切片）照样判不出。
        /// </summary>
        [Fact]
        public void 跨盘zip的末片_EOCD自述盘号_能判出自己是末片且共几片()
        {
            string dir = Path.Combine(_root, "pkzip-tail");
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, "222.zip");

            // EOCD：签名 + 本盘号(3) + 中央目录所在盘号(3) + 本盘条目数 + 总条目数 + CD 大小/偏移 + 注释长度 0。
            var eocd = new List<byte> { 0x50, 0x4B, 0x05, 0x06, 0x03, 0x00, 0x03, 0x00, 0x06, 0x00, 0x06, 0x00 };
            eocd.AddRange(new byte[] { 0x5E, 0x02, 0x00, 0x00, 0x03, 0x15, 0xEC, 0x02, 0x00, 0x00 });
            File.WriteAllBytes(path, eocd.ToArray());

            VolumeNumberReading reading = VolumeNumberFromContent.Read(path);

            Assert.True(reading.IsVolumeMember);
            Assert.Equal(4, reading.Number);   // 盘号 3（0 基）+ 1
            Assert.Equal(4, reading.Total);
        }

        /// <summary>
        /// RAR 分卷（RAR4 新编号）：**每一卷的卷号都在内容里**，首卷还带"首卷"标记。
        /// ⛔ 这条比另外两种格式强得多 —— 名字被改成什么样都不影响它知道自己是第几卷。
        /// </summary>
        [Fact]
        public void RAR分卷_每一卷的卷号都写在内容里_首卷还带首卷标记()
        {
            if (_rar == null)
            {
                return;   // 本机没装 Rar.exe 就跳过（UnRAR 只能解、不能造）
            }

            string dir = Path.Combine(_root, "rar-set");
            Directory.CreateDirectory(dir);

            var payload = new byte[3 * 1024 * 1024];
            new Random(20261001).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(dir, "payload.bin"), payload);

            Run(_rar, dir, "a", "-m0", "-v1m", "-ma4", Path.Combine(dir, "set.rar"), "payload.bin");

            List<string> parts = Directory.GetFiles(dir, "set.part*.rar")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Assert.True(parts.Count >= 3, "样本至少要 3 片");

            for (int i = 0; i < parts.Count; i++)
            {
                VolumeNumberReading reading = VolumeNumberFromContent.Read(parts[i]);

                Assert.True(reading.IsVolumeMember, $"{Path.GetFileName(parts[i])} 应当判得出是分卷成员");
                Assert.Equal(i, reading.RawField);                       // 卷号（0 基）就在内容里
                Assert.Equal(i == 0, reading.FirstVolumeFlag);           // 只有首卷带这一位
            }
        }

        // ================================================================ 装配

        private List<string> MakeSevenZipSplitSet(string directoryName, string archiveName, string type = "-t7z")
        {
            string dir = Path.Combine(_root, directoryName);
            Directory.CreateDirectory(dir);

            var payload = new byte[3 * 1024 * 1024];
            new Random(20261001).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(dir, "payload.bin"), payload);

            Run(_sevenZip!, dir, "a", type, "-mx0", "-v1m", Path.Combine(dir, archiveName), "payload.bin");

            return Directory.GetFiles(dir, Path.GetFileName(archiveName) + ".*")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void Run(string exe, string workDir, params string[] args)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDir
            };

            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("起不来：" + exe);

            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

            Assert.True(process.WaitForExit(180_000), "外部工具超时：" + exe);
            Assert.True(process.ExitCode == 0, $"外部工具失败（{process.ExitCode}）：{exe}\n{output}");
        }

        private static string? LocateSevenZip()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, "src", "ArchiveFixer", "tools", "7zip", "7z.exe");

                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
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
