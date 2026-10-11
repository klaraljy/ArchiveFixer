using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **内容证据：这一份是不是分卷组的一员**（用户 2026-10-11 口径：
    /// 「判断一个文件，**绝对不能只看基名和后缀**，绝对还有其他的办法」）。
    ///
    /// <para>判据唯一出口 = <see cref="VolumeNumberFromContent.ClassifyMembership"/>，它读归档自己写的头：
    /// RAR5 主头的 <c>0x0001 Volume</c>（上游 `unrar/headers5.hpp` 的 <c>MHFL_VOLUME</c>）、
    /// RAR4 的 <c>MHD_FIRSTVOLUME</c>、跨盘 ZIP 的 EOCD 盘号。三档结论：是 / **可证不是** / 判不出。</para>
    ///
    /// <para>⚠ 本文件**不改任何既有用例**；这里只钉两件事：
    /// ① 分类器在"单卷普通归档"与"真分卷片"上给出不同答案；
    /// ② **协调器"接片"那一档对"可证不是分卷成员"的东西一个字节都不接**（第 125 轮那个真凶的结构性防线）。</para>
    /// </summary>
    public sealed class VolumeMembershipEvidenceTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "ArchiveFixerVolumeMembership",
            Guid.NewGuid().ToString("N"));

        public VolumeMembershipEvidenceTests()
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
        /// 单卷普通 ZIP（BCL 现造，不依赖任何外部工具）⇒ **可证不是**分卷组的一员。
        ///
        /// <para>红检：把 <see cref="VolumeNumberFromContent.ClassifyMembership"/> 里
        /// <c>ZipSingleDisk</c> 那一档去掉 ⇒ 本条报 <c>Expected: ProvablyNotVolumeMember / Actual: Unknown</c>。</para>
        /// </summary>
        [Fact]
        public void 单卷普通ZIP_内容证据可证不是分卷成员()
        {
            string zip = Path.Combine(_root, "111.rar");

            using (var stream = new FileStream(zip, FileMode.Create, FileAccess.Write))
            {
                using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create);
                System.IO.Compression.ZipArchiveEntry entry = archive.CreateEntry("payload.bin");
                using Stream target = entry.Open();
                target.Write(new byte[1024]);
            }

            Assert.Equal(
                VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember,
                VolumeNumberFromContent.ClassifyMembership(zip));
        }

        /// <summary>
        /// **判不出**那一档：既认不出魔数、文件也不在 ⇒ <c>Unknown</c>
        /// （⛔ 绝不许当成"不是分卷成员"—— 跨盘 ZIP 的中间片就在这一档，而它确实是分卷的片）。
        /// </summary>
        [Fact]
        public void 认不出的字节与不存在的路径_判不出()
        {
            string garbage = Path.Combine(_root, "111(3)");
            File.WriteAllBytes(garbage, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 });

            Assert.Equal(
                VolumeNumberFromContent.VolumeMembershipVerdict.Unknown,
                VolumeNumberFromContent.ClassifyMembership(garbage));

            Assert.Equal(
                VolumeNumberFromContent.VolumeMembershipVerdict.Unknown,
                VolumeNumberFromContent.ClassifyMembership(Path.Combine(_root, "根本没有这个文件.rar")));

            Assert.Equal(
                VolumeNumberFromContent.VolumeMembershipVerdict.Unknown,
                VolumeNumberFromContent.ClassifyMembership(null));
        }

        /// <summary>
        /// 真 <c>Rar.exe</c> 现造：一组两卷的 RAR ⇒ 两片都**自述是分卷成员**；
        /// 单独一个单卷 RAR ⇒ **可证不是**。本机没装 Rar.exe 就如实跳过（⛔ 跳过 ≠ 验过）。
        /// </summary>
        [Fact]
        public void 真RAR的两卷与单卷_内容证据给相反答案()
        {
            string? rar = LocateRar();

            if (rar == null)
            {
                return;
            }

            string multi = Path.Combine(_root, "multi");
            string single = Path.Combine(_root, "single");
            Directory.CreateDirectory(multi);
            Directory.CreateDirectory(single);

            File.WriteAllBytes(Path.Combine(multi, "payload.bin"), new byte[3 * 1024 * 1024]);
            RunRar(rar, multi, "a", "-m0", "-ma5", "-v1m", "111_.rar", "payload.bin");

            File.WriteAllBytes(Path.Combine(single, "payload.bin"), new byte[64 * 1024]);
            RunRar(rar, single, "a", "-m0", "-ma5", "222.rar", "payload.bin");

            string[] pieces = Directory.GetFiles(multi, "111_.part?.rar")
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            Assert.True(pieces.Length >= 2, "Rar.exe 没切出两卷以上：" + string.Join("、", pieces));

            foreach (string piece in pieces)
            {
                Assert.Equal(
                    VolumeNumberFromContent.VolumeMembershipVerdict.VolumeMember,
                    VolumeNumberFromContent.ClassifyMembership(piece));
            }

            Assert.Equal(
                VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember,
                VolumeNumberFromContent.ClassifyMembership(Path.Combine(single, "222.rar")));
        }

        /// <summary>
        /// **协调器"接片"那一档**：内容证据说"不是分卷组的一员" ⇒ 一个字节都不接
        /// （⛔ 不改名、⛔ 不搬、⛔ 不建链接）。
        ///
        /// <para>夹具刻意做成**名字与内容打架**那一档：文件名是脏名 `111.part删2.ra除r`
        /// （按名字那把尺子 ⇒ 规范卷名 `111.part2.rar`，也就是"该在它旁边多出一个新名字"），
        /// 而它的**内容是普通的单卷 ZIP**（EOCD 盘号 = 0 ⇒ 可证不是分卷成员）。
        /// 于是有没有这一条内容证据，盘面上的区别是**看得见**的。</para>
        ///
        /// <para>红检：把 <c>TryAdoptUnresolvedVolumePiece</c> 开头那一句
        /// <c>IsProvablyNotVolumeMember</c> 去掉 ⇒ 规范卷名 `111.part2.rar`（或 `111.z01` 那一档的
        /// 规范名）当场出现在这一层 ⇒ 本条报 <c>Assert.False() Failure</c>。</para>
        /// </summary>
        [Fact]
        public void 接片那一档_内容说不是分卷成员_一个字节都不接()
        {
            string layer = Path.Combine(_root, "layer");
            Directory.CreateDirectory(layer);
            string piece = Path.Combine(layer, "111.part删2.ra除r");

            using (var stream = new FileStream(piece, FileMode.Create, FileAccess.Write))
            {
                using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create);
                using Stream target = archive.CreateEntry("payload.bin").Open();
                target.Write(new byte[1024]);
            }

            Assert.Equal(
                VolumeNumberFromContent.VolumeMembershipVerdict.ProvablyNotVolumeMember,
                VolumeNumberFromContent.ClassifyMembership(piece));

            ExtractionCoordinator coordinator = BuildCoordinator();
            string before = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(piece)));

            bool adopted = coordinator.TryAdoptUnresolvedVolumePieceForTests(piece, owner: null, layerOverride: layer);

            Assert.False(adopted);
            Assert.True(File.Exists(piece), "这一片必须原地不动。");
            Assert.Equal(before, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(piece))));
            Assert.Equal(
                new[] { "111.part删2.ra除r" },
                Directory.GetFiles(layer).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        }

        /// <summary>最小装配（与既有夹具同一套：真 7z 路径、数据根在临时目录里）。</summary>
        private ExtractionCoordinator BuildCoordinator()
        {
            string dataRoot = Path.Combine(_root, "data");
            string outputRoot = Path.Combine(_root, "out");
            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);
            AppSettings settings = AppSettings.CreateDefault();
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.CustomSevenZipExePath = SevenZipFactAttribute.LocateSevenZipPath() ?? string.Empty;
            settingsService.Save(settings);

            var logService = new LogService(pathService);
            var engine = new SevenZipEngine();
            var passwordService = new PasswordService { DataRootDirectory = dataRoot };

            var vm = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                engine,
                passwordService,
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService());

            return new ExtractionCoordinator(vm, engine, passwordService, pathService, new DialogService());
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
