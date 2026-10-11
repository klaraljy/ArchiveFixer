using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **轮判定的内容证据那一半**（用户 2026-10-11 口径：「判断一个文件，**绝对不能只看基名和后缀**，
    /// 绝对还有其他的办法」）。
    ///
    /// <para>轮判定是**本程序的基石**（`Extraction/RoundProductClassifier.cs`）：一轮收尾按"解出来的东西是什么"
    /// 决定下一轮走第一大步还是第二大步。它的第一刀原来是**只看名字**的
    /// （`VolumeGroupDetector.TryGetVolumeIndex(name) != null` ⇒ "是分卷的一片"），
    /// 而**一个归档本体自己就是它那一族的第 1 卷** ⇒ 一份普通的**单卷**包会被算成分卷的一片
    /// ⇒ 轮判定报「分卷 N 组 ⇒ 下一轮第二大步」，方向就错了。</para>
    ///
    /// <para>⚠ 本文件**不改任何既有用例**：这里钉的是"内容证据说它不是分卷 ⇒ 按单一压缩包算"
    /// 与"真分卷组照样算分卷"这两头。</para>
    /// </summary>
    public sealed class RoundProductClassifierEvidenceTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "ArchiveFixerRoundProduct",
            Guid.NewGuid().ToString("N"));

        public RoundProductClassifierEvidenceTests()
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
        /// **单卷普通 ZIP，名字却是"分卷的样子"**（`111.rar`：按名字那把尺子，归档本体就是第 1 卷）
        /// ⇒ 内容证据写着"不是分卷组的一员" ⇒ 轮判定必须按**单一压缩包**算（下一轮第一大步），
        /// ⛔ 不许报"分卷 1 组"。
        ///
        /// <para>红检：把 `RoundProductClassifier` 里那句 `IsProvablyNotVolumeMember` 去掉 ⇒
        /// 本条当场红，逐字 `Expected: SinglePackageStep / Actual: VolumeStep`
        /// 且 `VolumeEntries` 从空变成 1 条。</para>
        /// </summary>
        [Fact]
        public void 单卷包名字像分卷_内容证据说不是分卷_该走第一大步()
        {
            string piece = Path.Combine(_root, "111.rar");
            WriteSingleDiskZip(piece);

            Assert.Equal(
                VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember,
                VolumeNumberFromContent.ClassifyMembership(piece));

            RoundProductClassifier.Summary summary = RoundProductClassifier.Classify(new[] { piece });

            Assert.Equal(RoundProductClassifier.NextStep.SinglePackageStep, summary.Step);
            Assert.Empty(summary.VolumeEntries);
            Assert.Single(summary.SinglePackages);
        }

        /// <summary>
        /// 同一档的 `.zip` 名字：`X.zip` 按名字是本族第 1 卷，而内容是**单盘 zip**（EOCD 盘号 = 0）
        /// ⇒ 同样按单一压缩包算。
        /// </summary>
        [Fact]
        public void 单卷ZIP_内容证据说不是分卷_该走第一大步()
        {
            string piece = Path.Combine(_root, "222.zip");
            WriteSingleDiskZip(piece);

            RoundProductClassifier.Summary summary = RoundProductClassifier.Classify(new[] { piece });

            Assert.Equal(RoundProductClassifier.NextStep.SinglePackageStep, summary.Step);
            Assert.Empty(summary.VolumeEntries);
        }

        /// <summary>
        /// **真分卷组必须照旧算分卷**（内容自述"我是分卷组的一员"）—— 这是上一条的对照面：
        /// 真 <c>Rar.exe</c> 切两卷 ⇒ 轮判定报"分卷 1 组 / 下一轮第二大步"，而且入口包是第 1 卷。
        /// 本机没装 Rar.exe 就如实跳过（⛔ 跳过 ≠ 验过）。
        /// </summary>
        [Fact]
        public void 真两卷RAR_内容说自己是分卷_该走第二大步()
        {
            string? rar = LocateRar();

            if (rar == null)
            {
                return;
            }

            string work = Path.Combine(_root, "multi");
            Directory.CreateDirectory(work);
            File.WriteAllBytes(Path.Combine(work, "payload.bin"), new byte[3 * 1024 * 1024]);
            RunRar(rar, work, "a", "-m0", "-ma5", "-v1m", "333.rar", "payload.bin");

            string[] pieces = Directory.GetFiles(work, "333.part?.rar")
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            Assert.True(pieces.Length >= 2, "Rar.exe 没切出两卷以上：" + string.Join("、", pieces));

            foreach (string piece in pieces)
            {
                Assert.Equal(
                    VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember,
                    VolumeNumberFromContent.ClassifyMembership(piece));
            }

            RoundProductClassifier.Summary summary = RoundProductClassifier.Classify(pieces);

            Assert.Equal(RoundProductClassifier.NextStep.VolumeStep, summary.Step);
            Assert.Single(summary.VolumeEntries);
            Assert.Equal("333.part1.rar", Path.GetFileName(summary.VolumeEntries[0]));
        }

        /// <summary>
        /// **名字不是分卷、内容也认不出** ⇒ 算**内容物**（⛔ 不当归档：「判不出 ⇒ 什么都不做」）。
        /// </summary>
        [Fact]
        public void 名字不是分卷_内容也认不出_算内容物()
        {
            string junk = Path.Combine(_root, "333.dat");
            File.WriteAllBytes(junk, new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });

            RoundProductClassifier.Summary summary = RoundProductClassifier.Classify(new[] { junk });

            Assert.Equal(RoundProductClassifier.NextStep.Finished, summary.Step);
            Assert.Empty(summary.VolumeEntries);
            Assert.Empty(summary.SinglePackages);
            Assert.Equal(1, summary.ContentCount);
        }

        /// <summary>
        /// **名字像分卷、内容判不出** ⇒ **照旧按名字算分卷**（这是"内容证据优先"的另一半：
        /// ⛔ 判不出**不许**当"不是分卷"—— 跨盘 ZIP 的中间片就在这一档，而它确实是分卷的片）。
        /// </summary>
        [Fact]
        public void 名字像分卷_内容判不出_照旧按名字算()
        {
            string junk = Path.Combine(_root, "333.r00");
            File.WriteAllBytes(junk, new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });

            Assert.Equal(
                VolumeNumberFromContent.VolumeMembershipVerdict.Unknown,
                VolumeNumberFromContent.ClassifyMembership(junk));

            RoundProductClassifier.Summary summary = RoundProductClassifier.Classify(new[] { junk });

            Assert.Single(summary.VolumeEntries);
            Assert.Equal(0, summary.ContentCount);
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
