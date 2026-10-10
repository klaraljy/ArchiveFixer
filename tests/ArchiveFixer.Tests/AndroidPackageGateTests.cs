using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **⛔ 禁止解压真正的 APK**（用户 2026-10-10 拍板：「现在我觉得禁止解压真正的 apk 文件，
    /// 因为有些是用户真的要转移到手机上进行安装的」）。
    ///
    /// <para>现场（问题单 `2026-10-06-程序不该碰真正的APK（被解开并递归进内部）`）：APK 的内容本来就是 ZIP
    /// ⇒ 按魔数一定认得出 ⇒ 老版本把它当普通包解开、还在 `…\魔方.apk\lib\arm64-v8a\` **里面**建工作区、
    /// 落产物、继续递归探内层包（派生出一个注定失败的 `.so` 任务 + 一句错方向的"补密码"）。</para>
    ///
    /// <para>口径：判据 = **内容证据**（ZIP 里同时有 `AndroidManifest.xml` 与 `classes.dex`，⛔ 不看后缀，
    /// 唯一出口 <see cref="AndroidPackageDetector"/>）；行为 = **不解压 / 不递归 / 不改名** + 一句人话；
    /// 三条路（单层入口 / 递归内层包候选 / 一键处理产物扫描）同一个出口。
    /// **只挡 `.apk`**：`.jar/.docx/.epub/…` 那 18 个容器后缀照旧当正常归档处理。</para>
    ///
    /// <para><b>红检</b>：撤掉 `ExtractSingleTaskAsync` 开头那道闸门 ⇒ 第二条用例当场红
    /// （APK 被解开、产物落盘、终态不是「跳过」）。</para>
    /// </summary>
    public sealed class AndroidPackageGateTests : IDisposable
    {
        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        private readonly string _root;
        private readonly string? _sevenZip;
        private readonly ITestOutputHelper _output;

        public AndroidPackageGateTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerApkGate", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _sevenZip = SevenZipFactAttribute.LocateSevenZipPath();
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
                // 临时目录删不掉不影响结论。
            }
        }

        // ================================================================ 判据（纯函数）

        /// <summary>
        /// 判据 = **那两个条目**（⛔ 不看后缀）：同一份"APK 内容"叫 `.zip` 也照样认得出；
        /// 没有那两个条目的普通 ZIP / 文档容器 / 非 ZIP 一律**不认**（判不出 ⇒ 不拦）。
        /// </summary>
        [Fact]
        public void 判据_只看那两个条目_不看后缀()
        {
            string apkNamedZip = Path.Combine(_root, "伪装成 zip 的安装包.zip");
            WriteZip(apkNamedZip, "AndroidManifest.xml", "classes.dex", "resources.arsc");

            string apkNamedApk = Path.Combine(_root, "正常名.apk");
            WriteZip(apkNamedApk, "AndroidManifest.xml", "classes.dex");

            string plainZip = Path.Combine(_root, "普通包.zip");
            WriteZip(plainZip, "readme.txt", "payload.mp4");

            string docxLike = Path.Combine(_root, "文档.docx");
            WriteZip(docxLike, "word/document.xml", "[Content_Types].xml");

            // ⛔ 只有其中一个条目 ⇒ 不认（两个都要）。
            string onlyManifest = Path.Combine(_root, "只有清单.apk");
            WriteZip(onlyManifest, "AndroidManifest.xml");

            string notZip = Path.Combine(_root, "不是压缩包.bin");
            File.WriteAllText(notZip, "这不是 ZIP", Utf8NoBom);

            Assert.True(AndroidPackageDetector.IsAndroidPackage(apkNamedZip), "判据不看后缀：叫 .zip 也是安装包");
            Assert.True(AndroidPackageDetector.IsAndroidPackage(apkNamedApk));
            Assert.False(AndroidPackageDetector.IsAndroidPackage(plainZip));
            Assert.False(AndroidPackageDetector.IsAndroidPackage(docxLike), "文档容器照旧当正常归档（只挡 .apk）");
            Assert.False(AndroidPackageDetector.IsAndroidPackage(onlyManifest));
            Assert.False(AndroidPackageDetector.IsAndroidPackage(notZip));
            Assert.False(AndroidPackageDetector.IsAndroidPackage(Path.Combine(_root, "不存在.apk")));
            Assert.False(AndroidPackageDetector.IsAndroidPackage(null));
        }

        /// <summary>一键处理的产物扫描：APK 不进"下一轮的内层包"名单（第三条路）。</summary>
        [Fact]
        public void 一键处理的产物扫描_不把APK当内层包()
        {
            string apk = Path.Combine(_root, "扫出来的.apk");
            WriteZip(apk, "AndroidManifest.xml", "classes.dex");

            string plain = Path.Combine(_root, "扫出来的.zip");
            WriteZip(plain, "readme.txt");

            var empty = Array.Empty<string>();

            Assert.False(OneClickCoordinator.ShouldScanProducedFile(apk, empty, empty));
            Assert.True(OneClickCoordinator.ShouldScanProducedFile(plain, empty, empty));
        }

        // ================================================================ 真管线（单层入口 = 第一条路）

        /// <summary>
        /// 真管线：一个 `.apk` 进任务表 ⇒ **不解压、产物一个字节都不写、源文件一个字节不动**、
        /// 终态「跳过」+ 那句人话。
        /// </summary>
        [SevenZipFact]
        public async Task 真管线_APK不被解开_源文件一个字节不动()
        {
            RequireSevenZip();

            string input = Path.Combine(_root, "in");
            Directory.CreateDirectory(input);

            string apk = Path.Combine(input, "魔方.apk");
            WriteZip(apk, "AndroidManifest.xml", "classes.dex", "resources.arsc", "lib/arm64-v8a/libgojni.so");

            byte[] before = File.ReadAllBytes(apk);
            string beforeHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(before));

            (MainViewModel vm, ExtractionCoordinator extraction, LogService logService) = BuildPipeline();

            await vm.AddPathsAsync(new[] { apk });

            ArchiveTask task = Assert.Single(
                vm.Tasks,
                item => item.CurrentPath.EndsWith("魔方.apk", StringComparison.Ordinal));

            // 批量只跑勾选中的（与 ①页那颗勾选框同一件事）。
            task.IsSelected = true;

            await extraction.StartExtractAsync();

            // 排障用：把这批日志原样打出来（先看清它到底走了哪条路，再谈判据）。
            _output.WriteLine($"任务：状态={task.Status}｜终态={task.Outcome}｜勾选={task.IsSelected}｜错误={task.ErrorMessage}");
            _output.WriteLine($"===== 本批日志（{logService.Logs.Count} 条）=====");

            foreach (OperationLogItem entry in logService.Logs)
            {
                _output.WriteLine("  " + entry.DisplayText);
            }

            // ① 终态 = 跳过（不是失败、也不是成功），并且那句话说明白了为什么。
            Assert.Equal(TaskOutcome.Skipped, task.Outcome);
            Assert.Contains("Android 应用安装包", task.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("不解压", task.ErrorMessage, StringComparison.Ordinal);

            // ② 源文件一个字节不动（内容与名字都不动）。
            Assert.True(File.Exists(apk), "源文件必须还在原地");
            Assert.Equal(
                beforeHash,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(apk))));

            // ③ 没有被解开：输入目录里除了那一个 .apk 之外什么都没有（没有产物目录、没有工作区壳）。
            string[] leftovers = Directory
                .GetFileSystemEntries(input)
                .Select(Path.GetFileName)
                .Where(name => !string.Equals(name, "魔方.apk", StringComparison.Ordinal))
                .ToArray()!;

            Assert.Empty(leftovers);

            // ④ 日志里那句 WARN 得在（成功任务只留一行，所以这一档必须写成 WARN 才看得见）。
            Assert.Contains(
                logService.Logs,
                entry => entry.Level == "WARN" && entry.Message.Contains("Android 应用安装包", StringComparison.Ordinal));
        }

        // ================================================================ 真管线（递归层的内层包候选 = 第二条路）

        /// <summary>
        /// **第二条路**：APK **压在别的包里**时也不许被递归打开 —— 真机那一单就是
        /// 「APK 被当普通包解开 ⇒ 它里面的 `.so` / `.gz` 全被当内层包接着解」。
        ///
        /// <para>断言：外层包照常解开（APK 作为**内容物**落出来是用户要的），但**没有第二层去打开它**
        /// （递归日志里不许出现「第 1 层：inner.apk」这种再解一次的行）。</para>
        ///
        /// <para><b>红检</b>：撤掉 `RecursiveExtractor` 里那道候选闸门 ⇒ 本条当场红
        /// （日志里会出现"递归第 1 层开始解压：inner.apk"）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 递归层_压在包里的APK不被继续解开()
        {
            RequireSevenZip();

            string build = Path.Combine(_root, "build");
            Directory.CreateDirectory(build);

            string innerApk = Path.Combine(build, "inner.apk");
            WriteZip(innerApk, "AndroidManifest.xml", "classes.dex", "lib/arm64-v8a/libgojni.so");

            File.WriteAllText(Path.Combine(build, "note.txt"), "外层包自己的内容", Utf8NoBom);

            string outer = Path.Combine(build, "outer.7z");
            RunSevenZip(build, new[] { "a", "-t7z", "-mx0", outer, "inner.apk", "note.txt" });

            string input = Path.Combine(_root, "in2");
            Directory.CreateDirectory(input);
            string outerIn = Path.Combine(input, "outer.7z");
            File.Move(outer, outerIn);

            (MainViewModel vm, ExtractionCoordinator extraction, LogService logs) = BuildPipeline();

            await vm.AddPathsAsync(new[] { outerIn });

            foreach (ArchiveTask item in vm.Tasks)
            {
                item.IsSelected = true;
            }

            await extraction.StartExtractAsync();

            _output.WriteLine($"===== 递归用例日志（{logs.Logs.Count} 条）=====");

            foreach (OperationLogItem entry in logs.Logs)
            {
                _output.WriteLine("  " + entry.DisplayText);
            }

            // ① 外层包真的解开了（不然"没递归进去"可能只是整条链没跑）。
            Assert.Contains(vm.Tasks, item => item.Outcome == TaskOutcome.Succeeded);

            // ② APK 作为内容物落了出来（用户要的是"别碰它"，不是"别交给我"）。
            Assert.NotNull(Directory
                .GetFiles(_root, "inner.apk", SearchOption.AllDirectories)
                .FirstOrDefault());

            // ③ ⛔ 没有第二层去打开它：**APK 内部的东西一个都不许落进产物树**。
            //
            // ⚠ 判据必须读**盘面**，⛔ 不能读日志：成功任务只留一行（项目口径），
            // 递归每一层那些 INFO 行根本不进日志 —— 实测踩过：按日志断言时，
            // 把闸门撤掉这条用例**照样绿**（假绿）。
            // ⚠ 搜索范围只许是**产物树**（`out\`）：夹具自己的暂存目录里就有同名桩文件，
            // 搜整个 `_root` 会把它当成"漏出来的产物"（实测踩过，假红）。
            string outputTree = Path.Combine(_root, "out");

            foreach (string inner in new[] { "AndroidManifest.xml", "classes.dex", "libgojni.so" })
            {
                Assert.Null(
                    Directory.GetFiles(outputTree, inner, SearchOption.AllDirectories).FirstOrDefault());
            }
        }

        // ================================================================ 装配

        /// <summary>
        /// 装配一条真管线（真引擎、真设置、真导入；与项目里其它夹具同一套顺序）。
        /// 落点按"以包名命名的子文件夹 + 指定输出根"配（不这么配，批首那关会因为
        /// "工作区建不出来"直接不开工 —— 实测踩过）。
        /// </summary>
        private (MainViewModel Vm, ExtractionCoordinator Extraction, LogService Logs) BuildPipeline()
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
            settings.CustomSevenZipExePath = _sevenZip!;
            settings.RecursionMode = "AllBranches";
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

            var extraction = new ExtractionCoordinator(
                vm,
                engine,
                passwordService,
                pathService,
                new DialogService());

            return (vm, extraction, logService);
        }

        /// <summary>跑一次内置 7-Zip（造夹具用）。</summary>
        private void RunSevenZip(string workDirectory, IReadOnlyList<string> args)
        {
            var psi = new ProcessStartInfo(_sevenZip!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workDirectory
            };

            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("起不来 7z.exe");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"7z 退出码 {process.ExitCode}：{stdout}{stderr}");
            }
        }

        private void RequireSevenZip()
        {
            if (string.IsNullOrEmpty(_sevenZip))
            {
                throw new InvalidOperationException(
                    "找不到内置 7z.exe，且 [SevenZipFact] 没有把它跳过：环境与特性探测结果不一致。");
            }
        }

        /// <summary>造一个真 ZIP（用内置 7-Zip；内容都是几字节的桩）。</summary>
        private void WriteZip(string zipPath, params string[] entryPaths)
        {
            string staging = Path.Combine(_root, "staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);

            var args = new List<string> { "a", "-tzip", "-mx0", zipPath };

            foreach (string entry in entryPaths)
            {
                string full = Path.Combine(staging, entry.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "桩内容：" + entry, Utf8NoBom);
                args.Add(Path.GetRelativePath(staging, full));
            }

            var psi = new ProcessStartInfo(_sevenZip!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = staging
            };

            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("起不来 7z.exe");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"造夹具失败（7z 退出码 {process.ExitCode}）：{stdout}{stderr}");
            }

            Assert.True(File.Exists(zipPath), "夹具没造出来：" + zipPath);
        }
    }
}
