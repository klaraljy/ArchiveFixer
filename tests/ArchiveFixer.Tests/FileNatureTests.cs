using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **底层识别的唯一出口**（用户 2026-10-11 口径：「统筹到同一组上面去…我们就是**先看看底层能够识别到文件是什么东西**」）：
    /// `FileNature.Inspect(路径)` ⇒ 一张 <see cref="FileNatureCard"/>，四类事实各就各位 ——
    /// ① 内容格式（魔数）② 内容分卷身份（归档自己写的头，三档）③ 名字线索（分卷后缀那套算法）④ 盘上物理身份。
    ///
    /// <para>⚠ 本文件**不改任何既有用例**；这里钉的是"四类事实确实同时摆在一张卡上"以及
    /// "名字线索与内容结论**冲突**时，卡上两个都如实写出来"（那正是"不许只看名字"的机器证据）。</para>
    /// </summary>
    public sealed class FileNatureTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "ArchiveFixerFileNature",
            Guid.NewGuid().ToString("N"));

        public FileNatureTests()
        {
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
                // 清理失败不影响判据。
            }
        }

        /// <summary>
        /// 单卷普通 ZIP、名字却是"分卷的样子"（`111.rar`）⇒ 卡上**两个结论都写着**：
        /// 名字线索说"卷号 = 1"（归档本体就是本族第 1 卷），而内容证据说"可证不是分卷组的一员"
        /// ⇒ `参与分卷那一套 = False`。这就是"把内容证据接进判据"之后，误判在底层就被挡住的样子。
        /// </summary>
        [Fact]
        public void 卡上同时写出名字线索与内容结论_冲突时以内容为准()
        {
            string path = Path.Combine(_root, "111.rar");
            WriteSingleDiskZip(path);

            FileNatureCard card = FileNature.Inspect(path);

            // 名字线索：确实像分卷（这就是"只看名字"会踩的坑）。
            Assert.Equal(1, card.VolumeIndexByName);
            Assert.Equal("111", card.ArchiveBaseName);

            // 内容结论：可证不是分卷组的一员。
            Assert.Equal(VolumeContentFormat.Zip, card.Format);
            Assert.Equal(
                VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember,
                card.Membership);
            Assert.Equal(VolumeNumberFail.ZipSingleDisk, card.ContentFail);

            // ⇒ 底层那一句判据。
            Assert.False(card.ParticipatesInVolumeHandling);

            // 盘上事实。
            Assert.True(card.Size > 0);
            Assert.True(card.HasIdentity);
            Assert.Contains("参与分卷那一套=False", card.Describe(), StringComparison.Ordinal);
        }

        /// <summary>
        /// **判不出那一档**：认不出的字节 + 名字像分卷（`333.r00`）⇒ 卡上三档结论是 `Unknown`，
        /// `参与分卷那一套` 仍然是 **true**（⛔ 判不出**不许**当"不是分卷"）。
        /// </summary>
        [Fact]
        public void 判不出时参与分卷那一套_照旧按名字()
        {
            string path = Path.Combine(_root, "333.r00");
            File.WriteAllBytes(path, new byte[] { 0x01, 0x02, 0x03 });

            FileNatureCard card = FileNature.Inspect(path);

            Assert.Equal(VolumeContentFormat.Unknown, card.Format);
            Assert.Equal(VolumeNumberFromContent.VolumeMembershipVerdict.Unknown, card.Membership);
            Assert.NotNull(card.VolumeIndexByName);
            Assert.True(card.ParticipatesInVolumeHandling);
        }

        /// <summary>
        /// **物理身份那一层**：同一卷里**改名之后身份不变**（卷序列号 + 文件索引）——
        /// 所以"是不是同一份文件"永远问身份，⛔ 不问路径。
        /// </summary>
        [Fact]
        public void 同卷改名之后物理身份不变()
        {
            string before = Path.Combine(_root, "111.par删t1.ra除r");
            WriteSingleDiskZip(before);

            FileNatureCard first = FileNature.Inspect(before);

            Assert.True(first.HasIdentity);

            string after = Path.Combine(_root, "111.part1.rar");
            File.Move(before, after);

            FileNatureCard second = FileNature.Inspect(after);

            Assert.True(second.HasIdentity);
            Assert.Equal(first.VolumeSerial, second.VolumeSerial);
            Assert.Equal(first.FileIndex, second.FileIndex);
            Assert.NotEqual(first.Path, second.Path);
        }

        /// <summary>
        /// 真 <c>Rar.exe</c> 切两卷 ⇒ 卡上写的是"分卷成员"，而且**内容里的卷号 = 1 / 2**
        /// （RAR5 主头 <c>MHFL_VOLNUMBER</c>：第 1 卷没有这个字段 ⇒ 1；第 2 卷字段值 1 ⇒ 2）。
        /// 本机没装 Rar.exe 就如实跳过（⛔ 跳过 ≠ 验过）。
        /// </summary>
        [Fact]
        public void 真两卷RAR_卡上是分卷成员且卷号来自内容()
        {
            string? rar = LocateRar();

            if (rar == null)
            {
                return;
            }

            string work = Path.Combine(_root, "multi");
            Directory.CreateDirectory(work);
            File.WriteAllBytes(Path.Combine(work, "payload.bin"), new byte[3 * 1024 * 1024]);
            RunRar(rar, work, "a", "-m0", "-ma5", "-v1m", "555.rar", "payload.bin");

            string[] pieces = Directory.GetFiles(work, "555.part?.rar")
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            Assert.True(pieces.Length >= 2, "Rar.exe 没切出两卷以上：" + string.Join("、", pieces));

            FileNatureCard first = FileNature.Inspect(pieces[0]);
            FileNatureCard second = FileNature.Inspect(pieces[1]);

            Assert.Equal(VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember, first.Membership);
            Assert.Equal(VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember, second.Membership);
            Assert.True(first.ParticipatesInVolumeHandling);
            Assert.True(second.ParticipatesInVolumeHandling);
            Assert.Equal(1, first.VolumeNumber);
            Assert.Equal(2, second.VolumeNumber);
        }

        /// <summary>
        /// **跨盘 ZIP 第一片不一定以 `PK\x03\x04` 开头**：真机 940 批 `16161616` 的第一片
        /// 开头是数据描述符 `PK\x07\x08`，紧跟本地文件头 `PK\x03\x04`。当前判据只看前 4 字节，
        /// 会把它判成“没有本地文件头” ⇒ 第一片身份丢失 ⇒ 整组判不出。
        /// 红检：修前 `HasZipLocalHeader=false`，修后为 true。
        /// </summary>
        [Fact]
        public void 跨盘ZIP第一片_数据描述符前缀后面的本地文件头也要认出来()
        {
            string path = Path.Combine(_root, "aaa-L2.z01");
            byte[] bytes = new byte[128];

            bytes[0] = 0x50; bytes[1] = 0x4B; bytes[2] = 0x07; bytes[3] = 0x08;
            bytes[4] = 0x50; bytes[5] = 0x4B; bytes[6] = 0x03; bytes[7] = 0x04;
            bytes[8] = 20;   // version needed，结构合理
            bytes[30] = 1;   // name length
            bytes[34] = (byte)'a';

            File.WriteAllBytes(path, bytes);

            FileNatureCard card = FileNature.Inspect(path);

            Assert.Equal(
                VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember,
                card.Membership);
            Assert.True(card.HasZipLocalHeader, "数据描述符之后就是 PK\\x03\\x04，第一片身份不能丢");
        }

        private static void WriteSingleDiskZip(string path)
        {
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create);
            using Stream target = archive.CreateEntry("payload.bin").Open();
            target.Write(new byte[1024]);
        }

        private static string? LocateRar()
        {
            string[] candidates =
            {
                @"C:\Program Files\WinRAR\Rar.exe",
                @"C:\Program Files (x86)\WinRAR\Rar.exe"
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        private static void RunRar(string rar, string workDirectory, params string[] args)
        {
            var info = new ProcessStartInfo(rar)
            {
                WorkingDirectory = workDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (string arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(info)!;
            process.WaitForExit(60000);
        }
    }
}
