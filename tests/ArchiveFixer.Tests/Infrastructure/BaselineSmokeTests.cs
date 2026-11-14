using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// M1 验收用的基线冒烟测试。
    ///
    /// 目的：把 2026-08-09 基线的**真实行为**固定下来 —— 识别、后缀状态、改名、解压（含密码 / 分卷 / 损坏 / 非归档）。
    /// 这里断言的是"现状"，不是"理想"。发现现状不对时，改的是产品代码 + 这条测试，而不是把测试删掉。
    ///
    /// 样本由本测试自己用项目内置的 7z.exe 生成到临时目录，因此不依赖 samples/generated，
    /// 全新克隆也能直接 dotnet test。
    /// </summary>
    public sealed class SampleSetFixture : IDisposable
    {
        /// <summary>样本密码。合成密码，禁止写入任何真实密码（AGENTS.md §8）。</summary>
        public const string SamplePassword = "TestPass123!";

        public string Root { get; }

        public string SevenZipPath { get; }

        public SampleSetFixture()
        {
            SevenZipPath = LocateSevenZip();
            Root = Path.Combine(Path.GetTempPath(), "ArchiveFixerSmoke", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Build();
        }

        public string P(string relative) => Path.Combine(Root, relative);

        /// <summary>从测试输出目录向上找仓库根（含 ArchiveFixer.slnx），再定位内置 7z.exe。</summary>
        private static string LocateSevenZip()
        {
            DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ArchiveFixer.slnx")))
                {
                    string candidate = Path.Combine(dir.FullName, "src", "ArchiveFixer", "tools", "7zip", "7z.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                dir = dir.Parent;
            }

            // 退路：测试输出目录里的副本（项目引用会把 tools 一起复制过来）
            string local = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe");
            if (File.Exists(local))
            {
                return local;
            }

            throw new InvalidOperationException("找不到内置 7z.exe（既不在仓库 ArchiveFixer/tools/7zip，也不在测试输出目录）。");
        }

        private void Build()
        {
            string payload = Path.Combine(Root, "_payload");
            Directory.CreateDirectory(Path.Combine(payload, "docs"));
            WriteText(Path.Combine(payload, "hello.txt"), "hello ArchiveFixer\n");
            WriteText(Path.Combine(payload, "中文内容.txt"), "中文内容\n");
            WriteText(Path.Combine(payload, "docs", "说明.txt"), "说明文件\n");

            var payloadItems = new List<string>
            {
                Path.Combine(payload, "hello.txt"),
                Path.Combine(payload, "中文内容.txt"),
                Path.Combine(payload, "docs"),
            };

            // 01 普通
            string normal = Dir("01_normal");
            Run7z("a", "-tzip", Path.Combine(normal, "normal.zip"), payloadItems);
            Run7z("a", "-t7z", Path.Combine(normal, "normal.7z"), payloadItems);
            string tar = Path.Combine(normal, "normal.tar");
            Run7z("a", "-ttar", tar, payloadItems);
            Run7z("a", "-tgzip", Path.Combine(normal, "normal.tar.gz"), tar);

            // 02 加密
            string enc = Dir("02_encrypted");
            Run7z("a", "-t7z", Path.Combine(enc, "encrypted.7z"), "-p" + SamplePassword, "-mhe=on", payloadItems);
            Run7z("a", "-tzip", Path.Combine(enc, "encrypted.zip"), "-p" + SamplePassword, payloadItems);

            // 03 分卷（用不可压缩数据，保证一定切得开）
            string vol = Dir("03_volume");
            string big = Path.Combine(payload, "big.bin");
            byte[] bigBytes = new byte[2 * 1024 * 1024 + 512 * 1024];
            new Random(20260921).NextBytes(bigBytes);
            File.WriteAllBytes(big, bigBytes);
            Run7z("a", "-t7z", Path.Combine(vol, "volume.7z"), "-v1m", big);

            // 04 伪装后缀
            string fake = Dir("04_fakeext");
            string normal7z = Path.Combine(normal, "normal.7z");
            File.Copy(normal7z, Path.Combine(fake, "fake.jpg"));
            File.Copy(normal7z, Path.Combine(fake, "fake.7z.pdf.jpg"));
            File.Copy(normal7z, Path.Combine(fake, "noextension"));

            // 05 损坏 / 截断
            string broken = Dir("05_broken");
            byte[] all = File.ReadAllBytes(normal7z);
            byte[] head = new byte[(int)(all.Length * 0.4)];
            Array.Copy(all, head, head.Length);
            File.WriteAllBytes(Path.Combine(broken, "corrupted.7z"), head);

            // 06 仅名字像压缩包
            string notArchive = Dir("06_notarchive");
            WriteText(Path.Combine(notArchive, "text.7z"), "这不是压缩包。\n");

            // 07 复合后缀
            string compound = Dir("07_compound");
            string tar2 = Path.Combine(compound, "data.tar");
            Run7z("a", "-ttar", tar2, payloadItems);
            Run7z("a", "-tgzip", Path.Combine(compound, "data.tar.gz"), tar2);
        }

        private string Dir(string name)
        {
            string path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        private static void WriteText(string path, string text)
        {
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
        }

        private void Run7z(params object[] args)
        {
            var psi = new ProcessStartInfo(SevenZipPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Root,
            };

            foreach (object a in args)
            {
                if (a is IEnumerable<string> many)
                {
                    foreach (string s in many)
                    {
                        psi.ArgumentList.Add(s);
                    }
                }
                else
                {
                    psi.ArgumentList.Add(a.ToString() ?? string.Empty);
                }
            }

            using Process p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);

            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"生成样本失败（exit {p.ExitCode}）：{string.Join(' ', psi.ArgumentList)}\n{stdout}\n{stderr}");
            }
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
                // 样本目录在临时目录，清不掉不影响测试结论。
            }
        }
    }

    public class BaselineSmokeTests : IClassFixture<SampleSetFixture>
    {
        private readonly SampleSetFixture _fx;

        public BaselineSmokeTests(SampleSetFixture fx) => _fx = fx;

        private async Task<ArchiveTask> ScanAsync(string path)
        {
            var task = new ArchiveTask(path);
            await new ArchiveDetectService().ApplyDetectResultAsync(task, CancellationToken.None);
            return task;
        }

        [Fact]
        public void BuiltinSevenZipExists()
        {
            Assert.True(File.Exists(_fx.SevenZipPath), $"内置 7z.exe 不存在：{_fx.SevenZipPath}");
            Assert.True(new SevenZipEngine().IsAvailable, "SevenZipEngine 找不到 7z.exe");
        }

        // ---------------- 识别 ----------------

        [Theory]
        [InlineData("01_normal/normal.zip", "ZIP", "后缀正常")]
        [InlineData("01_normal/normal.7z", "7Z", "后缀正常")]
        [InlineData("01_normal/normal.tar.gz", "GZIP", "后缀正常")]
        [InlineData("07_compound/data.tar.gz", "GZIP", "后缀正常")]
        [InlineData("04_fakeext/fake.jpg", "7Z", "后缀不匹配")]
        [InlineData("04_fakeext/fake.7z.pdf.jpg", "7Z", "多重后缀疑似伪装")]
        [InlineData("04_fakeext/noextension", "7Z", "后缀缺失")]
        [InlineData("03_volume/volume.7z.001", "7Z", "分卷后缀")]
        public async Task 识别真实格式与后缀状态(string relative, string expectedFormat, string expectedExtensionStatus)
        {
            ArchiveTask task = await ScanAsync(_fx.P(relative));

            Assert.Equal(expectedFormat, task.DetectedFormat);
            Assert.True(task.IsArchive, $"{relative} 应被识别为归档，实际 IsArchive=false");
            Assert.Equal(expectedExtensionStatus, task.ExtensionStatus);
        }

        [Theory]
        [InlineData("06_notarchive/text.7z")]
        [InlineData("05_broken/corrupted.7z")]
        public async Task 不能把非归档或损坏文件当成正常归档(string relative)
        {
            ArchiveTask task = await ScanAsync(_fx.P(relative));

            // 注意：损坏文件靠文件头仍会被认成 7Z（设计.md 也承认这一点）。
            // 这里只断言"不会被当成 Unknown 之外的东西"这一现状，真正的判定交给 7z 测试。
            Assert.False(string.IsNullOrWhiteSpace(task.DetectedFormat));

            if (relative.Contains("notarchive", StringComparison.Ordinal))
            {
                Assert.False(task.IsArchive, "纯文本改名成 .7z 不应被识别为归档");
                Assert.Equal("Unknown", task.DetectedFormat);
                Assert.Equal("格式未知", task.ExtensionStatus);
            }
        }

        // ---------------- 改名 ----------------

        [Fact]
        public async Task 智能修正后缀_预览与实际改名一致()
        {
            string source = _fx.P("04_fakeext/fake.jpg");
            var task = new ArchiveTask(source);
            var detect = new ArchiveDetectService();
            await detect.ApplyDetectResultAsync(task, CancellationToken.None);

            var rename = new RenameService();
            RenameOptions options = RenameOptions.CreateFixByDetectedFormat(AppSettings.CreateDefault());

            List<RenamePreviewItem> preview = rename.BuildPreview(new[] { task }, options);

            RenamePreviewItem item = Assert.Single(preview);
            Assert.True(item.CanRename, $"预览应可改名，实际：{item.Status} / {item.ErrorMessage}");
            Assert.Equal("fake.7z", item.NewFileName);

            await rename.ExecuteRenameAsync(preview, new[] { task });

            Assert.True(File.Exists(_fx.P("04_fakeext/fake.7z")), "改名后文件不存在");
            Assert.False(File.Exists(source), "改名后旧文件仍然存在");
        }

        [Fact]
        public async Task 复合后缀改名_不得把tar_gz破坏成gz()
        {
            string source = _fx.P("07_compound/data.tar.gz");
            var task = new ArchiveTask(source);
            await new ArchiveDetectService().ApplyDetectResultAsync(task, CancellationToken.None);

            List<RenamePreviewItem> preview = new RenameService().BuildPreview(
                new[] { task },
                RenameOptions.CreateFixByDetectedFormat(AppSettings.CreateDefault()));

            RenamePreviewItem item = Assert.Single(preview);

            // 现状：tar.gz 被识别为 GZIP，建议后缀 .gz，最后一段已是 .gz → 不需要改名。
            // 关键红线是"不得变成 data.gz"这一类破坏，所以断言目标名要么不变、要么仍是 .tar.gz。
            Assert.True(
                item.NewFileName == "data.tar.gz" || item.NewFileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase),
                $"复合后缀被破坏了：{item.NewFileName}");
        }

        [Fact]
        public async Task 分卷文件不得被智能修正后缀改名()
        {
            // 回归：volume.7z.001 被识别成 7Z 后，多重后缀修正会把它改成 volume.7z，
            // 而 7z 只认 .001 这一套命名 —— 改名等于切断分卷链，整包再也解不开。
            string source = _fx.P("03_volume/volume.7z.001");
            var task = new ArchiveTask(source);
            await new ArchiveDetectService().ApplyDetectResultAsync(task, CancellationToken.None);

            List<RenamePreviewItem> preview = new RenameService().BuildPreview(
                new[] { task },
                RenameOptions.CreateFixByDetectedFormat(AppSettings.CreateDefault()));

            RenamePreviewItem item = Assert.Single(preview);

            Assert.Equal("volume.7z.001", item.NewFileName);
            Assert.False(item.CanRename, $"分卷文件不应可改名，实际状态：{item.Status}");
        }

        // ---------------- 解压 ----------------

        [Fact]
        public async Task 解压_普通zip_无密码()
        {
            ArchiveOperationResult r = await Extract("01_normal/normal.zip", "out_zip", password: string.Empty);

            Assert.True(r.Success, $"解压失败：{r.Status} / {r.Message} / {r.DetectedErrorType}");
            Assert.True(File.Exists(_fx.P("out_zip/hello.txt")), "解压产物缺少 hello.txt");
            Assert.True(File.Exists(_fx.P("out_zip/docs/说明.txt")), "解压产物缺少子目录文件");
        }

        [Fact]
        public async Task 解压_普通7z_无密码()
        {
            ArchiveOperationResult r = await Extract("01_normal/normal.7z", "out_7z", password: string.Empty);

            Assert.True(r.Success, $"解压失败：{r.Status} / {r.Message} / {r.DetectedErrorType}");
            Assert.True(File.Exists(_fx.P("out_7z/中文内容.txt")), "Unicode 文件名解压后丢失");
        }

        [Fact]
        public async Task 解压_加密7z_正确密码()
        {
            ArchiveOperationResult r = await Extract("02_encrypted/encrypted.7z", "out_enc_ok", SampleSetFixture.SamplePassword);

            Assert.True(r.Success, $"正确密码应解压成功：{r.Status} / {r.Message} / {r.DetectedErrorType}");
            Assert.True(File.Exists(_fx.P("out_enc_ok/hello.txt")));
        }

        [Fact]
        public async Task 解压_加密7z_空密码必须失败且分类为密码问题()
        {
            ArchiveOperationResult r = await Extract("02_encrypted/encrypted.7z", "out_enc_bad", password: string.Empty);

            Assert.False(r.Success, "加密包用空密码不应成功");
            Assert.True(
                r.IsWrongPassword || r.IsNeedPassword,
                $"密码问题应分类为 WrongPassword/NeedPassword，实际 {r.DetectedErrorType} / {r.Message}");
        }

        [Fact]
        public async Task 解压_分卷7z_只给001即可()
        {
            ArchiveOperationResult r = await Extract("03_volume/volume.7z.001", "out_vol", password: string.Empty);

            Assert.True(r.Success, $"分卷解压失败：{r.Status} / {r.Message} / {r.DetectedErrorType}");
            Assert.True(File.Exists(_fx.P("out_vol/big.bin")), "分卷解压产物缺少 big.bin");
        }

        [Fact]
        public async Task 解压_损坏包_必须失败()
        {
            ArchiveOperationResult r = await Extract("05_broken/corrupted.7z", "out_broken", password: string.Empty);

            Assert.False(r.Success, "截断的包不应解压成功");
            Assert.False(string.IsNullOrWhiteSpace(r.Message), "失败必须带可读原因");
        }

        [Fact]
        public async Task 解压_非归档_必须失败且不产生垃圾文件()
        {
            ArchiveOperationResult r = await Extract("06_notarchive/text.7z", "out_notarchive", password: string.Empty);

            Assert.False(r.Success, "纯文本改名成 .7z 不应解压成功");
            Assert.Equal("UnsupportedFormat", r.DetectedErrorType);
        }

        private async Task<ArchiveOperationResult> Extract(string relative, string outDirName, string password)
        {
            string archive = _fx.P(relative);
            string outDir = _fx.P(outDirName);

            return await new SevenZipEngine().ExtractAsync(
                new ArchiveRequest
                {
                    ArchivePath = archive,
                    OutputPath = outDir,
                    Password = password
                },
                new ExtractOptions(),
                CancellationToken.None);
        }
    }
}
