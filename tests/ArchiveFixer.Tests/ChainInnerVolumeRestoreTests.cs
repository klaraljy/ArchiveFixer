using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **第一大步的缺口：链内内层片的「还原 → 再匹配」**（用户 2026-10-10 夜点名的根因）。
    ///
    /// <para><b>真机 FFFF 现场（逐字）</b>：四个外层包各含**一片**真 RAR 新式分卷，四片名字分别被改坏：
    /// `111_.par删t1.ra除r`、`111_.part2`（少了 `.rar`）、`111_.pasrt3.rcar`、`111.z0删除1`（跨盘 ZIP 那一片）。
    /// 结果三单都停在「无法找到卷 `111_.part3` / `111_.pasrt4.rcar`」，
    /// 然后落 `产物校验未通过：输出目录是空目录，没有产物` ⇒ 失败；批末「成功 0 / 失败 3 / 部分完成 1」。</para>
    ///
    /// <para>⇒ 第一大步 ②「还原」**一次都没作用在这些内层片上**（链名里它们还是脏的），
    /// 于是 ③「再匹配」拿到脏名 ⇒ 组永远凑不齐 ⇒ 每片单独去解 ⇒ 找不着兄弟卷 ⇒ 报"密码/数据坏了"（方向也错）。</para>
    ///
    /// <para><b>要的行为</b>：链内每层开工前，把这一层里属于某一组的片**还原成规范卷名**（只改名、内容不动），
    /// 再交给"同一组"的尺子重判 ⇒ 整组凑齐 ⇒ 解开。⛔ 判据不许放宽：认不出来就要**在日志里点名**（目标 C）。</para>
    ///
    /// <para><b>红检</b>：本条现在就该红（用户现场就是这么失败的）；改好之后必须绿。</para>
    /// </summary>
    public sealed class ChainInnerVolumeRestoreTests : IDisposable
    {
        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        private readonly string _root;
        private readonly string? _sevenZip;
        private readonly string? _winRar;
        private readonly ITestOutputHelper _output;

        public ChainInnerVolumeRestoreTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerInnerRestore", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _sevenZip = SevenZipFactAttribute.LocateSevenZipPath();
            _winRar = new ToolLocator().WinRarExePath;
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

        /// <summary>
        /// **复刻 FFFF 的形状**：真 RAR 新式分卷（`X.partN.rar`）切成 4 片 ⇒ 三片名字改坏，
        /// 各塞进一个外层包（真机那三个 `111__.rar` / `1112__.rar` / `1113__.rar`）⇒ 跑真管线，
        /// 断言**整组被收拢并解开**（产物与原始字节一致）。
        /// </summary>
        [SevenZipFact]
        public async Task 真机形状_四片脏名分在四个外层包里_整组照样凑齐并解开()
        {
            (string input, byte[] payload, string payloadName) = BuildFfffShape(eachPackageInOwnSubdirectory: false);

            await RunPipelineAndAssertGroupExtractedAsync(input, payload, payloadName);
        }

        /// <summary>
        /// **真机那一批的真正布局：每个外层包各在自己的子目录里**（`FFFF\111__\111__.rar`、
        /// `FFFF\1112__\1112__.rar`、`FFFF\1113__\1113__.rar`、`FFFF\1114__\111_.part4.rar`）。
        ///
        /// <para><b>为什么这条非有不可</b>：上面那条把四个包平铺在**同一个输入目录**里 ——
        /// 那个形状下四片会落到同一棵树里、收拢窗口够得着，所以它是绿的；而真机那批是**一包一目录**，
        /// 四片分别落在四个互不相邻的成品目录里 ⇒ 收拢那一档一个成员都收不出来
        /// （2026-10-10 夜实测：成功 0 / 失败 0 / 跳过 3 / 部分完成 3，整组到最后也没解开）。
        /// 教训同 `AGENTS.md` 里那条：**夹具必须把真机那种形状摆出来**，否则绿得没有意义。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 真机布局_四个外层包各在一个子目录里_四片分处四地_整组照样凑齐并解开()
        {
            (string input, byte[] payload, string payloadName) = BuildFfffShape(eachPackageInOwnSubdirectory: true);

            await RunPipelineAndAssertGroupExtractedAsync(input, payload, payloadName);
        }

        /// <summary>
        /// 造 FFFF 的形状：真 RAR 新式分卷切成 ≥4 片 ⇒ 三片名字改坏、各塞进一个外层 ZIP，
        /// 第 4 片散着 ⇒ 返回输入目录。
        /// </summary>
        /// <param name="eachPackageInOwnSubdirectory">
        /// <c>true</c> = 每个外层包各放进自己的子目录（真机那批的布局）；
        /// <c>false</c> = 全部平铺在同一个输入目录（老夹具的布局）。
        /// </param>
        private (string Input, byte[] Payload, string PayloadName) BuildFfffShape(bool eachPackageInOwnSubdirectory, string? root = null)
        {
            if (string.IsNullOrEmpty(_sevenZip))
            {
                throw new InvalidOperationException("找不到内置 7z.exe，[SevenZipFact] 没跳过：环境与探测结果不一致。");
            }

            if (string.IsNullOrWhiteSpace(_winRar) || !File.Exists(_winRar))
            {
                _output.WriteLine("这台机器没有 WinRAR ⇒ 造不出 RAR 新式分卷，本条跳过（⛔ 不是验过了）。");
                return (string.Empty, Array.Empty<byte>(), string.Empty);
            }

            string baseRoot = root ?? _root;

            // ① 真 RAR 新式分卷：`-v512k` ⇒ inner.part1.rar / part2.rar / …（新式族的规范名）。
            string build = Path.Combine(baseRoot, "build-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(build);

            string payloadName = "payload.bin";

            /*
             * ⚠ **载荷必须让 RAR 正好切出 4 片**（真机那组就是 4 卷）：`-v512k` 每卷 512 KiB，
             * 4 卷最多装 2 MiB ⇒ 载荷取 2 MiB 减 64 KiB，稳稳落在 4 卷里。
             * 以前这里写的是 2 MiB + 256 KiB ⇒ 实际切出 5 片，而夹具只摆了 4 片 ⇒
             * 整组凑齐之后引擎照样报 `Missing volume : 111_.part5.rar`
             * （2026-10-10 实测：那条红是**夹具自己少摆了一卷**，不是产品的问题）。
             */
            byte[] payload = new byte[(2 * 1024 * 1024) - (64 * 1024)];
            new Random(20261010).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(build, payloadName), payload);

            RunTool(_winRar, build, new[] { "a", "-cfg-", "-ibck", "-m0", "-v512k", "111_.part1.rar", payloadName });
            File.Delete(Path.Combine(build, payloadName));

            string[] parts = Directory.GetFiles(build, "111_.part*.rar").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

            // 真机那组 = 4 卷；夹具少摆一卷就会造出"永远凑不齐的组"，那种红没有意义。
            Assert.True(parts.Length == 4, $"夹具要求**恰好 4 片**（真机那组就是 4 卷），实际 {parts.Length} 片");

            // ② 三片名字改坏（照真机那三种坏法），第四片保持规范名（真机里它就是散着的那一单）。
            string dirty1 = Path.Combine(build, "111_.par删t1.ra除r");
            string dirty2 = Path.Combine(build, "111_.part2");
            string dirty3 = Path.Combine(build, "111_.pasrt3.rcar");

            File.Move(parts[0], dirty1);
            File.Move(parts[1], dirty2);
            File.Move(parts[2], dirty3);

            // ③ 三片各塞进一个外层包（外层包本身名字也改坏：真机是 `111__.rLLLLar` 那种）。
            string input = Path.Combine(baseRoot, "in-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(input);

            foreach ((string piece, string outerName, string folderName) in new[]
                     {
                         (dirty1, "111__.rLLLLar", "111__"),
                         (dirty2, "1112__.rLLLLar", "1112__"),
                         (dirty3, "1113__.rLLLLar", "1113__")
                     })
            {
                string staging = Path.Combine(baseRoot, "stage-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);

                string moved = Path.Combine(staging, Path.GetFileName(piece));
                File.Move(piece, moved);

                string packageDirectory = eachPackageInOwnSubdirectory
                    ? Path.Combine(input, folderName)
                    : input;

                Directory.CreateDirectory(packageDirectory);

                string outer = Path.Combine(packageDirectory, outerName);
                RunTool(_sevenZip, staging, new[] { "a", "-tzip", "-mx0", outer, Path.GetFileName(piece) });
            }

            // 第四片：真机里它在**自己那一个子目录**里（`FFFF\1114__\111_.part4.rar`），单独立一单。
            string fourthDirectory = eachPackageInOwnSubdirectory
                ? Path.Combine(input, "1114__")
                : input;

            Directory.CreateDirectory(fourthDirectory);
            File.Move(parts[3], Path.Combine(fourthDirectory, "111_.part4.rar"));

            _output.WriteLine($"输入目录（一包一目录 = {eachPackageInOwnSubdirectory}）：");

            foreach (string file in Directory.GetFiles(input, "*", SearchOption.AllDirectories)
                         .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                _output.WriteLine("  " + file.Substring(input.Length + 1) + "  " + new FileInfo(file).Length);
            }

            return (input, payload, payloadName);
        }

        /// <summary>跑真管线，断言**整组被收拢并解开**（产物与原始字节一致）。</summary>
        private async Task RunPipelineAndAssertGroupExtractedAsync(
            string input,
            byte[] payload,
            string payloadName,
            string? root = null)
        {
            if (input.Length == 0)
            {
                // 没有 WinRAR 那一档：夹具没造出来，断言无从谈起（上面已写明"跳过、⛔ 不是验过了"）。
                return;
            }

            (MainViewModel vm, ExtractionCoordinator extraction, LogService logs) = BuildPipeline(root);

            await vm.AddPathsAsync(Directory.GetFiles(input, "*", SearchOption.AllDirectories));

            foreach (ArchiveTask task in vm.Tasks)
            {
                task.IsSelected = true;
            }

            await extraction.StartExtractAsync();

            _output.WriteLine("===== 本批日志 =====");

            foreach (OperationLogItem entry in logs.Logs)
            {
                _output.WriteLine("  " + entry.DisplayText);
            }

            /*
             * 出问题时"盘上到底有什么 + 每一单落在哪一档"是第一现场：光看日志会漏掉
             * "产物其实解出来了、只是落在别处"与"这一单被算成哪一档"这两件事。
             */
            string outRoot = Path.Combine(root ?? _root, "out");

            _output.WriteLine("===== 输出树 =====");

            foreach (string file in Directory.GetFiles(outRoot, "*", SearchOption.AllDirectories)
                         .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                _output.WriteLine("  " + file.Substring(outRoot.Length + 1) + "  " + new FileInfo(file).Length);
            }

            _output.WriteLine("===== 每一单的落点 =====");

            foreach (ArchiveTask task in vm.Tasks)
            {
                _output.WriteLine(
                    $"  {task.LogName,-22} 状态=[{task.Status}] 机器终态=[{task.Outcome}] 落点=[{task.OutputPath}]");
            }

            // ⑤ 断言：整组凑齐并解开 ⇒ 产物（原始字节）出现在输出树里。
            string? produced = Directory
                .GetFiles(outRoot, payloadName, SearchOption.AllDirectories)
                .FirstOrDefault();

            Assert.NotNull(produced);
            Assert.Equal(payload, File.ReadAllBytes(produced!));
        }

        // ================================================================ 装配

        private (MainViewModel Vm, ExtractionCoordinator Extraction, LogService Logs) BuildPipeline(string? root = null)
        {
            string baseRoot = root ?? _root;
            string dataRoot = Path.Combine(baseRoot, "data");
            string outputRoot = Path.Combine(baseRoot, "out");
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
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
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

        private static void RunTool(string exe, string workDirectory, IReadOnlyList<string> args)
        {
            var psi = new ProcessStartInfo(exe)
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

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("起不来：" + exe);
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"{Path.GetFileName(exe)} 退出码 {process.ExitCode}：{stdout}{stderr}");
            }
        }
    }
}
