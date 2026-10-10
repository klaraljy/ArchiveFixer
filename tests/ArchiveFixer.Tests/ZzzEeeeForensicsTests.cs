using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
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
    /// ⚠ **一次性取证用例**（不是守门用例）：把真机副本 `eeee-copy` 复制到临时目录，
    /// 用**无头夹具**跑两遍「一键处理」，把真日志打出来，用来回答：
    /// ① 落点目录里那份 `<包名>.zip` 是谁写的；② 第二次跑时它会不会被重新当成"用户原包"立项。
    /// 用完即删。
    /// </summary>
    public class ZzzEeeeForensicsTests
    {
        private readonly ITestOutputHelper _output;

        public ZzzEeeeForensicsTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task 取证_副本跑两遍一键处理_打印真日志()
        {
            string source = Environment.GetEnvironmentVariable("ARCHIVEFIXER_EEEE_COPY") ?? string.Empty;

            if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
            {
                _output.WriteLine("跳过：没有设置 ARCHIVEFIXER_EEEE_COPY 或目录不存在。");
                return;
            }

            string sevenZip = LocateSevenZip();

            if (sevenZip.Length == 0)
            {
                _output.WriteLine("跳过：找不到 7z.exe。");
                return;
            }

            /*
             * ⛔ 夹具落地位置：**必须落项目的 `_tmp`，绝不落 C 盘**。
             *
             * 出处：全局 `AGENTS.md` §3「临时文件统一放在 `E:\DeepSeekProjects\_tmp\<项目名>\`」、
             * 「临时文件不得散落在源码目录」，以及本项目「缓存绝不写进 C 盘的 %AppData%」同一口径。
             *
             * ⚠ 2026-10-09 实测教训：这里原来用 `Path.GetTempPath()`（= C 盘 `%TEMP%`），
             * 一轮夹具复制 448 MiB、跑了 22 轮 ⇒ **C 盘被塞 29.03 GiB、可用只剩 0.60 GB**
             * （用户严厉禁止写 C 盘）。⇒ 改成优先落 `_tmp`；`_tmp` 不可写时**直接在临时盘符下**落，
             * 仍然不碰 C 盘。
             */
            string root = BuildFixtureRoot();
            string input = Path.Combine(root, "in");
            string dataRoot = Path.Combine(root, "data");
            string outputRoot = Path.Combine(root, "out");

            Directory.CreateDirectory(input);
            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);
            CopyDirectory(source, input);

            _output.WriteLine($"夹具根目录 = {root}");
            _output.WriteLine($"输入 = {input}");
            _output.WriteLine("=== 输入初始清单 ===");

            foreach (string f in Directory.GetFiles(input, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                _output.WriteLine("  " + f.Substring(input.Length + 1) + "  " + new FileInfo(f).Length);
            }

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);
            AppSettings settings = AppSettings.CreateDefault();
            settings.CustomOutputDirectory = string.Empty;
            settings.ExtractToOriginalDirectory = true;
            settings.KeepArchiveNameFolder = true;
            settings.CustomSevenZipExePath = sevenZip;
            settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
            /*
             * ⚠ **按用户的本次选项**（他 2026-10-10 那次真机导出日志第 22 行逐字：
             * 「落点：以包名命名的子文件夹…；源包处理：放入其余物当中；其余物：彻底删除（直接省空间，不可恢复）」）
             * ⇒ 取证必须照他那一档跑，否则测的不是他遇到的那条路（「其余物保留」会掩盖"片被接走之后
             * 产出方自己那一份怎么收场"这一半）。⚠ 这一档**会删掉夹具里的东西**，所以只能跑在 `_tmp` 副本上。
             */
            settings.RestHandlingAfterVerify = RestHandlingModes.Delete;
            settings.VerboseLog = true;
            settings.EnableLog = true;
            settingsService.Save(settings);

            var engine = new SevenZipEngine();
            _output.WriteLine($"引擎可用 = {engine.IsAvailable}");

            /*
             * ⚠ 2026-10-09 **只读取证钩子**（挂在 `CreateHardLinkW` 的唯一调用点上）：
             * 真机 `…\111\111\111\111.zip` 与 `…\111\111\111.zip` 同 inode，而系统级观测证明它是
             * "建目录 25 ms 后、7 ms 内"合一次动作造出来的 ⇒ 只可能是硬链接。带调用栈的记录一次跑即可定案。
             * ⛔ 只读取证；定案后把这一挂摘掉。
             *
             * ⚠ 2026-10-10：`HardLinkHelper.Trace` 这个钩子**随这次还原一起撤掉了**（它本来属"未解决的改动"）
             * ⇒ 这一行不再挂任何钩子（⛔ 不是被注释掉的死代码，是"钩子已不存在"）。
             */

            /*
             * ⚠ 2026-10-10 **系统级毫秒观测器**（只读取证）。
             *
             * 为什么要有它：`…\111\111\111\111.zip` 与 `…\111\111\111.zip` 是同一 inode 的两条名字，
             * 而全项目唯一的 `CreateHardLinkW` 调用点上那份**无条件**记录整轮只有 2 次、都不指向那个
             * 嵌套路径 ⇒ 它一定是"别处搬进来的"。程序侧日志只有**秒**级精度，对不出是哪一步，
             * 所以这里在**盘**这一侧用 `FileSystemWatcher`（缓冲 1 MB；第 16 轮实测能抓到 165 个事件）
             * 把每一次 建目录 / 建文件 / 改名 / 删除 连**毫秒**记下来，再与程序侧同一毫秒的轨迹对齐。
             *
             * ⛔ 只读：一个字节都不写盘上的东西，只在测试输出里记一行。
             */
            var events = new List<string>();
            string timelineLog = Path.Combine(root, "timeline.txt");

            // 用户已授权：直接读他配好的那本密码本（只列条数，明文不出现在输出里）。
            string bookPath = Environment.GetEnvironmentVariable("ARCHIVEFIXER_PASSWORD_BOOK") ?? string.Empty;

            void Append(string kind, string path)
            {
                lock (events)
                {
                    events.Add($"{DateTime.Now:HH:mm:ss.fff} {kind,-28} {path}");
                }
            }

            using (var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                InternalBufferSize = 1024 * 1024
            })
            {
                watcher.Created += (_, e) => Append(e.ChangeType.ToString(), e.FullPath);
                watcher.Changed += (_, e) => Append(e.ChangeType.ToString(), e.FullPath);
                watcher.Renamed += (_, e) => Append($"Renamed({Path.GetFileName(e.OldFullPath)})", e.FullPath);
                watcher.Deleted += (_, e) => Append(e.ChangeType.ToString(), e.FullPath);
                watcher.Error += (_, e) => Append("WATCHER-ERROR", e.GetException().Message);
                watcher.EnableRaisingEvents = true;

                await RunTwoRoundsAndProbe();

                watcher.EnableRaisingEvents = false;
            }

            lock (events)
            {
                File.WriteAllLines(timelineLog, events, new UTF8Encoding(false));
                _output.WriteLine($"=== 时间线（{events.Count} 条）已写盘：{timelineLog} ===");
            }

            async Task RunTwoRoundsAndProbe()
            {
                /*
                 * ⚠ 只跑**一遍** —— 这正是用户的真实流程（「我每次测完都会将文件删除，然后从外面重新复制一份」）。
                 * 第二遍是"把上一遍产物又当输入"的探针，它会把落点层整个搬走、把这一组打散（实测），
                 * 混在一起就看不出第一遍到底成不成。要跑第二遍时把下面那个 1 改成 2。
                 */
                for (int round = 1; round <= 1; round++)
                {
                    var passwordService = new PasswordService();

                    if (bookPath.Length > 0 && File.Exists(bookPath))
                    {
                        int merged = passwordService.MergePasswordBook(
                            bookPath,
                            out int parsedCount,
                            out int skippedByTombstone,
                            out IReadOnlyList<string> warnings);
                        _output.WriteLine(
                            $"第 {round} 遍：密码本解析 {parsedCount} 条，并入 {merged} 条，墓碑跳过 {skippedByTombstone} 条，警告 {warnings.Count} 条");
                        _output.WriteLine($"第 {round} 遍：这本书里记住的值 = {passwordService.RememberedBookValueCount} 个");
                    }
                    else
                    {
                        _output.WriteLine($"第 {round} 遍：没设 ARCHIVEFIXER_PASSWORD_BOOK ⇒ 只有空密码一个候选");
                    }

                    var logService = new LogService(pathService);
                    string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
                    string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

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

                    RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
                    ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

                    var scan = new ScanCoordinator(vm, new FileScanService(), new ArchiveDetectService(), new DialogService());
                    var rename = new RenameCoordinator(vm, scan, new RenameService(), new DialogService());
                    var extraction = new ExtractionCoordinator(vm, engine, passwordService, pathService, new DialogService());
                    var oneClick = new OneClickCoordinator(vm, scan, rename, extraction, new DialogService());

                    await vm.AddPathsAsync(Directory.GetFiles(input, "*", SearchOption.AllDirectories));
                    _output.WriteLine($"=== 第 {round} 遍：导入后任务数 = {vm.Tasks.Count} ===");

                    foreach (ArchiveTask t in vm.Tasks.ToList())
                    {
                        _output.WriteLine($"   任务 {t.LogName} ｜ {t.CurrentPath}");
                    }

                    OneClickOutcome outcome = await oneClick.RunPipelineAsync(vm.Tasks.ToList());
                    _output.WriteLine($"=== 第 {round} 遍：汇总 = {outcome.Summary} ===");
                    _output.WriteLine($"=== 第 {round} 遍：日志（{logService.Logs.Count} 条）===");

                    foreach (OperationLogItem entry in logService.Logs)
                    {
                        _output.WriteLine("  " + entry.DisplayText);
                    }

                    _output.WriteLine($"=== 第 {round} 遍结束后的盘上清单 ===");

                    foreach (string f in Directory.GetFiles(input, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        _output.WriteLine("  " + f.Substring(input.Length + 1) + "  " + new FileInfo(f).Length);
                    }

                    /*
                     * 物理身份探针（判据的直接证据）：同一份文件在盘上可以有两条路径 ——
                     * 卷序列号 + 文件索引相同就是同一份（含硬链接双胞胎），出处 `Detection/FileIdentity.cs`。
                     * 这一段只读，用来证明"嵌套那一份"与"落地那一份"到底是不是同一份字节。
                     *
                     * ⚠ 2026-10-09 补充：**带上创建时间**。真机上 `111\111\111\111.zip` 与 `111\111\111.zip`
                     * 是同一 inode、而日志全文没有一行写过它 —— 创建时间能直接回答"它是在哪一段被造的"
                     * （解压期间 / 搬运期间 / 收尾期间），比继续翻日志快得多。
                     */
                    _output.WriteLine($"=== 第 {round} 遍结束后的 .zip 物理身份 ===");

                    var twins = new Dictionary<string, List<string>>();

                    foreach (string f in Directory.GetFiles(input, "*.zip", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        string rel = f.Substring(input.Length + 1);

                        if (FileIdentity.TryGetIdentity(f, out ulong vol, out ulong idx))
                        {
                            var info = new FileInfo(f);
                            _output.WriteLine($"  {rel}  vol={vol} idx={idx}  创建={info.CreationTime:HH:mm:ss.fff}  改动={info.LastWriteTime:HH:mm:ss.fff}");

                            string key = vol + ":" + idx;

                            if (!twins.TryGetValue(key, out List<string>? list))
                            {
                                list = new List<string>();
                                twins[key] = list;
                            }

                            list.Add(rel);
                        }
                        else
                        {
                            _output.WriteLine($"  {rel}  身份读不到");
                        }
                    }

                    _output.WriteLine($"=== 第 {round} 遍：同一份（同 inode）的双胞胎 ===");

                    foreach (KeyValuePair<string, List<string>> pair in twins.Where(p => p.Value.Count > 1))
                    {
                        _output.WriteLine("  ★ " + string.Join("  ==  ", pair.Value));
                    }
                }

                _output.WriteLine("=== 取证结束（临时目录保留： " + root + "）===");

                /*
                 * ===== 判据 ④（用户流程闭环）：把上一轮的**输出目录**再导入一次 =====
                 *
                 * 用户流程 = 删掉输出目录 → 从备份拷回原包 → 再跑。所以"再导入一次"这件事本身是
                 * 用户流程的一部分：如果一轮跑完之后，程序**自己的产物**（接片落地的入口包、移进其余物的
                 * 原包、还原改名的片、其余物里的副本）还会被当成新的用户原包立项，那么第二轮就会越跑越乱
                 * —— 真机症状就是"出现两个 111.zip"与 `111(1)` 这种分散产物。
                 *
                 * ⛔ 这里只报告数字，不断言（本轮是取证；等口径定了再转成守门断言）。
                 */
                _output.WriteLine("=== 判据④：把输出目录再导入一次 ===");
                _output.WriteLine("  输入目录 = " + input);

                foreach (string directory in new[] { input })
                {
                    var probePassword = new PasswordService();
                    var probeLog = new LogService(pathService);
                    string? previousWorkspace = RecursiveExtractor.ConfiguredWorkspaceRoot;
                    string previousSevenZip = ToolLocator.Default.CustomSevenZipExePath;

                    var probeVm = new MainViewModel(
                        new FileScanService(),
                        new ArchiveDetectService(),
                        new RenameService(),
                        engine,
                        probePassword,
                        probeLog,
                        settingsService,
                        pathService,
                        new TaskSummaryService(),
                        new ClipboardService(),
                        new DialogService());

                    RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspace;
                    ToolLocator.Default.CustomSevenZipExePath = previousSevenZip;

                    await probeVm.AddPathsAsync(Directory.GetFiles(input, "*", SearchOption.AllDirectories));

                    _output.WriteLine($"  {directory} ⇒ 导入后任务数 = {probeVm.Tasks.Count}");

                    foreach (ArchiveTask t in probeVm.Tasks.ToList())
                    {
                        _output.WriteLine("     任务 " + t.CurrentPath.Substring(input.Length + 1));
                    }

                    probeVm.ClearTasks();
                }
            }
        }

        /// <summary>
        /// 夹具的落地根目录 —— **只落 `E:\DeepSeekProjects\_tmp\ArchiveFixer\`**。
        ///
        /// <para>⛔ **绝不落 C 盘**（全局 `AGENTS.md` §3；本项目"缓存绝不写进 C 盘的 %AppData%"同一口径）。
        /// 2026-10-09 实测教训：原先用 `Path.GetTempPath()`（C 盘 `%TEMP%`），一轮复制 448 MiB、
        /// 22 轮把 C 盘塞掉 **29.03 GiB**（可用只剩 0.60 GB）。</para>
        ///
        /// <para>回落规则：`_tmp` 建不出来时**只退到 `E:\` 根下的 `_tmp`**，⛔ 不回落 `%TEMP%`；
        /// 两个都建不出来就抛（宁可让用例失败，也不许偷偷写 C 盘）。</para>
        /// </summary>
        private static string BuildFixtureRoot()
        {
            string name = "af-eeee-" + Guid.NewGuid().ToString("N");

            foreach (string baseDirectory in new[]
                     {
                         @"E:\DeepSeekProjects\_tmp\ArchiveFixer",
                         @"E:\_tmp\ArchiveFixer"
                     })
            {
                try
                {
                    Directory.CreateDirectory(baseDirectory);
                    return Path.Combine(baseDirectory, name);
                }
                catch
                {
                    // 换下一个候选；两个都不行就抛。
                }
            }

            throw new InvalidOperationException(
                @"夹具根目录建不出来：只允许落 E:\DeepSeekProjects\_tmp\ArchiveFixer\（⛔ 不许写 C 盘）。");
        }

        private static void CopyDirectory(string from, string to)
        {
            foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(to, dir.Substring(from.Length + 1)));
            }

            foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(to, file.Substring(from.Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }

        private static string LocateSevenZip()
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

            string local = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe");

            return File.Exists(local) ? local : string.Empty;
        }
    }
}
