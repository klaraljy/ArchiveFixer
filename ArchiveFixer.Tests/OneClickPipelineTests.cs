using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// M2 验收：「一个包一次搞定」的**端到端逻辑链路**测试（AGENTS.md §10 M2）。
    ///
    /// 覆盖一个真实资源包会遇到的三件事同时出现：
    /// - 伪装后缀（<c>movie.7z.pdf.jpg</c>，内容其实是加密 7z）
    /// - 分卷（<c>series.7z.001</c> / <c>.002</c>，且文件头加密）
    /// - 密码本映射式（<c>名称:密码</c>，每个包一个密码）
    ///
    /// 链路：扫描 → 识别 → 修正伪装后缀 → 密码本命中 → 解压到独立目录 → 产物落盘。
    /// 这里跑的是服务与引擎（GUI 之外的全部逻辑），GUI 上就是"一键处理"按钮串的那几步。
    /// </summary>
    public class OneClickPipelineTests : IDisposable
    {
        private const string MoviePassword = "MoviePass1";
        private const string SeriesPassword = "SeriesPass2";

        private readonly string _root;
        private readonly string _sevenZip;

        public OneClickPipelineTests()
        {
            _sevenZip = LocateSevenZip();
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerOneClick", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论。
            }
        }

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

        private void Run7z(params object[] args)
        {
            var psi = new ProcessStartInfo(_sevenZip)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = _root
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
                    $"7z 失败（exit {p.ExitCode}）：{string.Join(' ', psi.ArgumentList)}\n{stdout}\n{stderr}");
            }
        }

        private void WriteText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        /// <summary>
        /// 造两个包：
        /// movie.7z.pdf.jpg —— 单文件加密归档 + 三重伪装后缀
        /// series.7z.001/.002 —— 加密分卷（40KB 一卷，保证切得开）
        /// </summary>
        private (string movie, string seriesFirst) BuildPackages()
        {
            string source = Path.Combine(_root, "_src");
            WriteText(Path.Combine(source, "movie", "movie-info.txt"), "电影说明\n");
            WriteText(Path.Combine(source, "series", "ep01.txt"), "第一集\n");

            string movieBig = Path.Combine(source, "movie", "movie.bin");
            byte[] movieBytes = new byte[64 * 1024];
            new Random(11).NextBytes(movieBytes);
            File.WriteAllBytes(movieBig, movieBytes);

            string seriesBig = Path.Combine(source, "series", "series.bin");
            byte[] seriesBytes = new byte[150 * 1024];
            new Random(22).NextBytes(seriesBytes);
            File.WriteAllBytes(seriesBig, seriesBytes);

            string work = Path.Combine(_root, "work");
            Directory.CreateDirectory(work);

            // 单文件加密归档（文件头也加密），先生成再改成伪装后缀
            string moviePlain = Path.Combine(work, "movie.7z");
            Run7z("a", "-t7z", moviePlain, "-p" + MoviePassword, "-mhe=on",
                Path.Combine(source, "movie"));

            string movieFake = Path.Combine(_root, "movie.7z.pdf.jpg");
            File.Move(moviePlain, movieFake);

            // 加密分卷
            string seriesFirst = Path.Combine(_root, "series.7z.001");
            Run7z("a", "-t7z", Path.Combine(_root, "series.7z"), "-v40k", "-p" + SeriesPassword, "-mhe=on",
                Path.Combine(source, "series", "series.bin"));

            Directory.Delete(source, recursive: true);
            Directory.Delete(work, recursive: true);

            return (movieFake, seriesFirst);
        }

        private string WritePasswordBook()
        {
            // 映射式：一个包一个密码。名称故意用中文与英文各一条，覆盖两种写法。
            string path = Path.Combine(_root, "密码本.txt");
            WriteText(path,
                "# 映射式密码本：名称:密码\n" +
                "movie:" + MoviePassword + "\n" +
                "series：" + SeriesPassword + "\n");

            return path;
        }

        [Fact]
        public async Task 伪装后缀加密码本映射_端到端跑通()
        {
            (string movie, _) = BuildPackages();
            string bookPath = WritePasswordBook();

            var scan = new FileScanService();
            var detect = new ArchiveDetectService();
            var rename = new RenameService();
            var engine = new SevenZipEngine();
            var passwordService = new PasswordService();

            List<PasswordItem> imported = passwordService.ImportPasswordList(bookPath);
            Assert.Equal(2, imported.Count);

            List<ArchiveTask> tasks = await scan.ScanPathsAsync(new[] { _root }, new ScanOptions { ScanMode = "ScanAllFiles" }, CancellationToken.None);

            ArchiveTask movieTask = tasks.Single(t => t.CurrentPath.EndsWith("movie.7z.pdf.jpg", StringComparison.OrdinalIgnoreCase));

            await detect.ApplyDetectResultAsync(movieTask, CancellationToken.None);
            Assert.Equal("7Z", movieTask.DetectedFormat);
            Assert.Equal(StatusText.ExtensionMultiFake, movieTask.ExtensionStatus);

            // 第 1 步：修正伪装后缀（GUI 上这一步会先给用户看预览，测试里直接执行）
            List<RenamePreviewItem> preview = rename.BuildPreview(
                new[] { movieTask },
                RenameOptions.CreateFixByDetectedFormat(AppSettings.CreateDefault()));

            RenamePreviewItem item = Assert.Single(preview);
            Assert.Equal("movie.7z", item.NewFileName);

            await rename.ExecuteRenameAsync(preview, new[] { movieTask });

            string fixedPath = Path.Combine(_root, "movie.7z");
            Assert.True(File.Exists(fixedPath), "改名后应存在 movie.7z");

            // 第 2 步：密码本按名称命中（改完名之后仍然要能命中，因为匹配用的是基名）
            movieTask.CurrentPath = fixedPath;
            IReadOnlyList<PasswordEntry> matched = passwordService.MatchMappedEntries("movie.7z");

            PasswordEntry hit = Assert.Single(matched);
            Assert.Equal(MoviePassword, hit.Password);

            // 第 3 步：候选顺序 —— 空密码在最前，映射命中紧随其后
            List<PasswordItem> candidates = passwordService.GetPasswordCandidates(
                movieTask,
                globalPassword: string.Empty,
                passwordList: passwordService.Passwords,
                tryEmptyFirst: true);

            Assert.Equal("Empty", candidates[0].Source);
            Assert.Equal("BookMapped", candidates[1].Source);
            Assert.Equal(MoviePassword, candidates[1].Value);

            // 第 4 步：用命中密码真解压到独立目录
            string outputDir = Path.Combine(_root, "out", "movie");

            ArchiveOperationResult result = await engine.ExtractAsync(
                new ArchiveRequest { ArchivePath = fixedPath, OutputPath = outputDir, Password = hit.Password },
                new ExtractOptions(),
                CancellationToken.None);

            Assert.True(result.Success, $"解压应成功：{result.Status} / {result.Message} / {result.DetectedErrorType}");
            Assert.True(File.Exists(Path.Combine(outputDir, "movie", "movie-info.txt")), "解压产物缺少 movie-info.txt");
        }

        [Fact]
        public async Task 加密分卷_一组一个任务_只从001启动且密码本命中()
        {
            (_, string seriesFirst) = BuildPackages();
            string bookPath = WritePasswordBook();

            var scan = new FileScanService();
            var detect = new ArchiveDetectService();
            var engine = new SevenZipEngine();
            var passwordService = new PasswordService();

            passwordService.ImportPasswordList(bookPath);

            List<ArchiveTask> tasks = await scan.ScanPathsAsync(new[] { _root }, new ScanOptions { ScanMode = "ScanAllFiles" }, CancellationToken.None);

            // 三卷（.001/.002/.003…）在扫描结果里必须只占一行
            ArchiveTask seriesTask = tasks.Single(t => t.VolumeGroupKey.Contains("series", StringComparison.OrdinalIgnoreCase));
            Assert.True(seriesTask.IsVolumeGroup);
            Assert.True(seriesTask.VolumeCount >= 2, $"应至少识别出 2 卷，实际 {seriesTask.VolumeCount}");
            Assert.True(seriesTask.IsVolumeComplete, $"分卷应完整：{seriesTask.VolumeInfoText}");
            Assert.EndsWith("series.7z.001", seriesTask.CurrentPath, StringComparison.OrdinalIgnoreCase);

            await detect.ApplyDetectResultAsync(seriesTask, CancellationToken.None);
            Assert.Equal(StatusText.ExtensionVolume, seriesTask.ExtensionStatus);

            // 分卷文件不该被"智能修正后缀"改名
            List<RenamePreviewItem> preview = new RenameService().BuildPreview(
                new[] { seriesTask },
                RenameOptions.CreateFixByDetectedFormat(AppSettings.CreateDefault()));

            Assert.False(Assert.Single(preview).CanRename, "分卷文件不允许改名");

            // 密码本按名称命中分卷组
            IReadOnlyList<PasswordEntry> matched = passwordService.MatchMappedEntries("series.7z.001");
            Assert.NotEmpty(matched);
            Assert.Equal(SeriesPassword, matched[0].Password);

            string outputDir = Path.Combine(_root, "out", "series");

            ArchiveOperationResult result = await engine.ExtractAsync(
                new ArchiveRequest { ArchivePath = seriesFirst, OutputPath = outputDir, Password = SeriesPassword },
                new ExtractOptions(),
                CancellationToken.None);

            Assert.True(result.Success, $"分卷解压应成功：{result.Status} / {result.Message} / {result.DetectedErrorType}");
            Assert.True(File.Exists(Path.Combine(outputDir, "series.bin")), "分卷解压产物缺少 series.bin");
        }

        [Fact]
        public async Task 名单式密码本对分卷组也能命中()
        {
            (_, _) = BuildPackages();

            // 列表式：一行一个密码，没有名称
            string listBook = Path.Combine(_root, "列表密码本.txt");
            WriteText(listBook, "# 一行一个\n" + SeriesPassword + "\n" + MoviePassword + "\n");

            var passwordService = new PasswordService();
            List<PasswordItem> imported = passwordService.ImportPasswordList(listBook);

            Assert.Equal(2, imported.Count);
            Assert.Empty(passwordService.MatchMappedEntries("anything.7z"));

            var task = new ArchiveTask(Path.Combine(_root, "anything.7z"));
            List<PasswordItem> candidates = passwordService.GetPasswordCandidates(
                task,
                globalPassword: string.Empty,
                passwordList: passwordService.Passwords,
                tryEmptyFirst: true);

            // 空密码 + 列表两项
            Assert.Equal(3, candidates.Count);
            Assert.Contains(candidates, c => c.Value == SeriesPassword);
        }
    }
}
