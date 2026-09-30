using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **日志瘦身**（用户 2026-09-25 第 44 条："你看看一次导出 713KB……这个多吓人"）。
    ///
    /// <para>他那一次的 4025 行 / 713 KB 是这么构成的：进度 1181 行（16%）、删除 412 行（16%）、
    /// 其余 2432 行里绝大多数是**每个任务十几条一样形状的样板**（落点 / 入仓 / 空间门 / 资源预算 /
    /// 特定解压 / 结果校验 / 定稿完成 / 工作区清理 / 其余物…），68 个包就是 68 份。</para>
    ///
    /// <para>他给的三条要求，在这一组里逐条钉住：</para>
    /// <list type="number">
    /// <item><description><b>进度别这么详细</b> —— 成功的任务日志里**一条进度行都不许有**；</description></item>
    /// <item><description><b>不用汇报那么详细</b> —— 成功 = **一行**（"开始解压 + 收尾摘要"合成的那一行），样板行全丢；</description></item>
    /// <item><description><b>删文件别写那么多</b> —— 逐任务一行 + 批末一条汇总；</description></item>
    /// </list>
    /// <para>⚠ 反过来同样要钉住：**失败 / 取消时细节一个都不许少**（他原话："如果失败的话，
    /// 你就可以多一点"）—— 排查要的就是那些数字与路径。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class LogVolumePolicyTests : IDisposable
    {
        private readonly string _root;
        private readonly string? _sevenZip;

        public LogVolumePolicyTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerLogVolume", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _sevenZip = LocateSevenZip();
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
            }
        }

        /// <summary>① 成功的任务：日志里没有进度行、没有样板行，只有一行收尾摘要。</summary>
        [Fact]
        public async Task 成功的任务_日志只剩一行摘要_没有进度与样板()
        {
            RequireSevenZip();

            string package = BuildPackage("plain.7z", 64 * 1024);
            Harness harness = CreateHarness();

            await harness.AddTaskAsync(package);
            await harness.Coordinator.StartExtractAsync();

            List<string> mine = harness.LogTexts
                .Where(line => line.Contains("] plain.7z：", StringComparison.Ordinal))
                .ToList();

            // 进度：一条都不许有（用户第 1 条要求）。
            Assert.DoesNotContain(mine, line => line.Contains("：进度 ", StringComparison.Ordinal));

            // 样板行：成功时全丢（用户第 2 条要求）。
            foreach (string boilerplate in new[] { "空间门通过", "入仓目录", "资源预算提示", "定稿完成", "结果校验", "工作区已清理", "中间工作区已清理", "本次内容物" })
            {
                Assert.DoesNotContain(mine, line => line.Contains(boilerplate, StringComparison.Ordinal));
            }

            // 收尾摘要：**一行**说清结果（"解压 100%" + 状态 + 落点；用户第 44 条追加：
            // "把开始解压 + 摘要合成一行"）；其余物那一档再补一行短句，⛔ 绝不是以前那种十几行。
            Assert.True(
                mine.Count <= 3,
                $"成功的任务日志最多三行（合成的那一行 / 其余物短句），实际 {mine.Count} 行：" + string.Join(" | ", mine));

            // 合成的那一行：既要"解压 100%"、也要"解压成功"（以前它们是分开的两行）。
            Assert.Single(mine, line => line.Contains("解压 100%", StringComparison.Ordinal));
            Assert.Contains(
                mine,
                line => line.Contains("解压 100%", StringComparison.Ordinal)
                    && line.Contains(StatusText.ExtractSuccess, StringComparison.Ordinal));
        }

        /// <summary>② 失败的任务：细节照旧全留（⛔ 不许"为了瘦身把失败现场也吃了"）。</summary>
        [Fact]
        public async Task 失败的任务_细节照旧全留()
        {
            // 7z 魔数 + 垃圾内容：识别得出来、解压必失败（不需要真引擎就能走到失败收尾）。
            string broken = Path.Combine(_root, "broken.7z");
            byte[] bytes = new byte[4096];
            new Random(20260925).NextBytes(bytes);
            bytes[0] = 0x37;
            bytes[1] = 0x7A;
            bytes[2] = 0xBC;
            bytes[3] = 0xAF;
            bytes[4] = 0x27;
            bytes[5] = 0x1C;
            File.WriteAllBytes(broken, bytes);

            Harness harness = CreateHarness();
            await harness.AddTaskAsync(broken);
            await harness.Coordinator.StartExtractAsync();

            List<string> mine = harness.LogTexts.Where(line => line.Contains("broken.7z", StringComparison.Ordinal)).ToList();

            Assert.True(mine.Count > 3, "失败任务的细节必须留下来，实际只有：" + string.Join(" | ", mine));
            Assert.Contains(mine, line => line.Contains("空间门通过", StringComparison.Ordinal)
                || line.Contains("资源预算", StringComparison.Ordinal)
                || line.Contains("入仓目录", StringComparison.Ordinal));
        }

        /// <summary>③ 其余物：逐任务一行（短），批末一条汇总；⛔ 不再逐条念理由与"不可逆"。</summary>
        [Fact]
        public async Task 其余物删除_逐任务一行加批末汇总()
        {
            RequireSevenZip();

            string package = BuildPackage("rest.7z", 32 * 1024);
            Harness harness = CreateHarness(settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.MoveToRest);
                settings.RestHandlingAfterVerify = RestHandlingModes.Delete;
            });

            await harness.AddTaskAsync(package);
            await harness.Coordinator.StartExtractAsync();

            List<string> purgeLines = harness.LogTexts
                .Where(line => line.Contains(StatusText.RestActionDelete, StringComparison.Ordinal))
                .ToList();

            Assert.NotEmpty(purgeLines);

            // 逐任务那一行必须短（旧的是 200+ 字符的长文案，里头还塞着理由与"不可逆"）。
            Assert.All(purgeLines, line => Assert.True(line.Length < 160, "删除行还是太长了：" + line));
            Assert.DoesNotContain(purgeLines, line => line.Contains("理由=", StringComparison.Ordinal));
            Assert.DoesNotContain(purgeLines, line => line.Contains("这一步不可逆", StringComparison.Ordinal));
        }

        /// <summary>④ 批末汇总：一眼看到"这批成不成"（第 44 条追加）。</summary>
        [Fact]
        public async Task 批末汇总_一眼看到成功失败与逐条失败()
        {
            RequireSevenZip();

            string good = BuildPackage("good.7z", 32 * 1024);

            string broken = Path.Combine(_root, "packages", "broken.7z");
            byte[] bytes = new byte[4096];
            new Random(7).NextBytes(bytes);
            bytes[0] = 0x37;
            bytes[1] = 0x7A;
            bytes[2] = 0xBC;
            bytes[3] = 0xAF;
            bytes[4] = 0x27;
            bytes[5] = 0x1C;
            File.WriteAllBytes(broken, bytes);

            Harness harness = CreateHarness();

            await harness.AddTaskAsync(good);
            await harness.AddTaskAsync(broken);
            await harness.Coordinator.StartExtractAsync();

            string? summary = harness.LogTexts.FirstOrDefault(line => line.Contains("本批汇总：", StringComparison.Ordinal));

            Assert.NotNull(summary);
            Assert.True(
                summary!.Contains("2 个任务", StringComparison.Ordinal) && summary.Contains("失败 1", StringComparison.Ordinal),
                $"批末汇总不对：{summary}\nbroken 相关日志：\n{string.Join("\n", harness.LogTexts.Where(l => l.Contains("broken", StringComparison.Ordinal)))}");

            // 失败要**逐条点名**（不用他去数行）。
            Assert.Contains(
                harness.LogTexts,
                line => line.Contains("  失败：", StringComparison.Ordinal)
                    && line.Contains("broken.7z", StringComparison.Ordinal));
        }

        /// <summary>⑤ 老日志保留策略：超期 / 超量的收掉，最近若干份与"当前这一份"永远留着。</summary>
        [Fact]
        public void 日志保留_清理老日志但不碰当前这一份与最近几份()
        {
            string dataRoot = Path.Combine(_root, "retention-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var logService = new LogService(pathService);

            logService.Initialize(enableFileLog: true);

            string logsDirectory = logService.LogDirectory;
            string current = logService.CurrentLogFilePath;

            Assert.True(File.Exists(current), "当前日志文件应该已经建好");

            // 造 40 份"很旧"的日志（每份 2 MB → 共 80 MB，超过 50 MB 上限），再各留一份的修改时间。
            DateTime old = DateTime.UtcNow.AddDays(-90);

            for (int i = 0; i < 40; i++)
            {
                string fake = Path.Combine(logsDirectory, $"ArchiveFixer_20250101_{i:D6}.log");
                File.WriteAllBytes(fake, new byte[2 * 1024 * 1024]);
                File.SetLastWriteTimeUtc(fake, old.AddMinutes(i));
            }

            // 不匹配命名规则的文件：⛔ 一个都不许动。
            string stranger = Path.Combine(logsDirectory, "别人的日志.txt");
            File.WriteAllText(stranger, "不要动我");
            File.SetLastWriteTimeUtc(stranger, old);

            var second = new LogService(pathService);
            second.Initialize(enableFileLog: true);

            int remaining = Directory.GetFiles(logsDirectory, "ArchiveFixer_*.log").Length;

            // 至少留 LogRetentionMinFiles 份，且总量落回上限以内（当前这一份 + 最近几份）。
            Assert.True(remaining >= LogService.LogRetentionMinFiles, $"老日志被清得太狠：只剩 {remaining} 份");

            long total = Directory.GetFiles(logsDirectory, "ArchiveFixer_*.log").Sum(file => new FileInfo(file).Length);
            Assert.True(total <= LogService.LogRetentionTotalBytes, $"日志总量仍超上限：{total / 1024 / 1024} MB");

            Assert.True(File.Exists(current), "当前这一份永远不许被删");
            Assert.True(File.Exists(stranger), "不匹配命名规则的文件一个字节都不许动");
        }

        // ================================================================ 第 44 条追加：三个新开关

        /// <summary>
        /// ⑥ **详细日志开关**（⑥设置 →「详细日志（排查用）」，默认关）。
        ///
        /// <para>默认档"成功就丢"是为了几十个同形包不刷屏；但排查**单个**包时要能拿回全过程 ——
        /// 所以给一个显式开关，而不是让他去改代码。</para>
        ///
        /// <para>⚠ 这里同时钉住一个**容易写错的地方**：详细档（<c>capture == null</c>）**不是**
        /// "连收尾摘要也没有" —— 那一行是"开始解压 + 摘要合成一行"里的那一行，两种档位都必须有。</para>
        /// </summary>
        [Fact]
        public async Task 详细日志开关打开_成功任务写全过程_摘要那一行照旧在()
        {
            RequireSevenZip();

            string package = BuildPackage("verbose.7z", 64 * 1024);
            Harness harness = CreateHarness(settings => settings.VerboseLog = true);

            await harness.AddTaskAsync(package);
            await harness.Coordinator.StartExtractAsync();

            List<string> mine = harness.LogTexts
                .Where(line => line.Contains("] verbose.7z：", StringComparison.Ordinal))
                .ToList();

            // 默认档会丢掉的样板必须回来（至少一条：入仓目录 / 空间门 / 定稿）。
            Assert.Contains(
                mine,
                line => line.Contains("入仓目录", StringComparison.Ordinal)
                    || line.Contains("空间门通过", StringComparison.Ordinal)
                    || line.Contains("定稿完成", StringComparison.Ordinal));

            // ⛔ 详细档照样有收尾摘要那一行（它就是"开始解压 + 摘要合成一行"里的那一行）；
            // 详细档里别的行也会提"解压成功"，所以这里钉的是**合成的那一行同时带着两个事实**。
            Assert.Contains(
                mine,
                line => line.Contains("解压 100%", StringComparison.Ordinal)
                    && line.Contains(StatusText.ExtractSuccess, StringComparison.Ordinal));
        }

        /// <summary>
        /// ② **导出文件加头**：时间范围 / 任务数 / 成功失败 / 引擎 / 输出根 + "细节在哪看"。
        ///
        /// <para>这份 txt 的用途就是"发给我排查" —— 没有头，读的人得先自己找"这是哪一批、跑到几点、
        /// 成不成、用的哪个引擎"。⛔ 头部只许有机器事实，不许出现密码相关内容。</para>
        /// </summary>
        [Fact]
        public async Task 导出头部_时间范围任务数引擎输出根与细节在哪看()
        {
            RequireSevenZip();

            string package = BuildPackage("header.7z", 32 * 1024);
            Harness harness = CreateHarness();

            await harness.AddTaskAsync(package);
            await harness.Coordinator.StartExtractAsync();

            string text = string.Join("\n", harness.Vm.BuildLogExportHeader());

            Assert.Contains("时间范围：", text);
            Assert.Contains("任务数：1（成功 1 / 失败 0 / 跳过 0）", text);
            Assert.Contains("引擎：", text);
            Assert.Contains(harness.OutputRoot, text);
            Assert.Contains("细节在哪看", text);

            // 头里要告诉他"要更细的怎么办"（与 ⑥ 那个开关是同一件事）。
            Assert.Contains("详细日志（排查用）", text);

            // ⛔ 隐私红线：导出的头部不许出现密码相关内容。
            Assert.DoesNotContain("密码", text);
        }

        /// <summary>
        /// ② 的边界：**一个任务都没有**时点「导出日志」也不能崩
        /// （启动后随手点一下是常事 —— 那时候任务表是空的，时间范围取不到）。
        /// </summary>
        [Fact]
        public void 导出头部_没有任何任务时也能生成()
        {
            Harness harness = CreateHarness();

            string text = string.Join("\n", harness.Vm.BuildLogExportHeader());

            Assert.Contains("任务数：0（成功 0 / 失败 0 / 跳过 0）", text);
            Assert.Contains("导出时间：", text);   // 取不到时间范围就写导出时间
            Assert.Contains("细节在哪看", text);
        }

        /// <summary>
        /// ② 的落地细节：头部**写在正文之前**，而且**不算进"日志多少行"那个数字**里
        /// （否则他会以为日志突然变长了）。
        /// </summary>
        [Fact]
        public void 导出头部_写在正文之前且不计入日志行数()
        {
            string dataRoot = Path.Combine(_root, "export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var logService = new LogService(pathService);

            logService.Initialize(enableFileLog: true);
            logService.MarkOperationStart("单元测试");
            logService.WriteInfo("正文第一行");

            string withHeader = Path.Combine(_root, "with-header.txt");
            string withoutHeader = Path.Combine(_root, "without-header.txt");

            (int linesWith, bool markerWith) = logService.ExportOperationLog(
                withHeader,
                new[] { "HEAD-1", "HEAD-2" });
            (int linesWithout, bool markerWithout) = logService.ExportOperationLog(withoutHeader);

            Assert.True(markerWith && markerWithout, "两份导出都应该是「本次操作」的范围");

            // 头部不算行数：带不带头的**日志行数必须一样**。
            Assert.Equal(linesWithout, linesWith);

            string text = File.ReadAllText(withHeader);

            Assert.Contains("HEAD-1", text);
            Assert.Contains("正文第一行", text);
            Assert.True(
                text.IndexOf("HEAD-1", StringComparison.Ordinal) < text.IndexOf("正文第一行", StringComparison.Ordinal),
                "头部必须写在正文之前");

            // 不带头的导出里⛔ 不许出现头部那一行。
            Assert.DoesNotContain("HEAD-1", File.ReadAllText(withoutHeader));
        }

        // ================================================================ 详细日志"到底多出什么"（用户 2026-09-27）

        /// <summary>
        /// **开了详细日志到底多出什么**（用户 2026-09-27：「开了更详细的日志选项怎么还是这么简单」）。
        ///
        /// <para>这一条把"两档的差别"钉死在**具体行**上：关着时日志里既没有 `引擎调用：`（参数摘要）
        /// 也没有 `原话：`（引擎原话）；打开之后**两样都必须出现**，而成功那一行摘要照旧在。</para>
        ///
        /// <para><b>红检</b>：把 <c>VerboseTaskLogEnabled</c> 那两处 <c>EngineOutputLog.LogVerbose</c>
        /// 撤掉 → 本用例第二段当场红（这正是用户报的那个症状：勾了跟没勾一样）。</para>
        /// </summary>
        [Fact]
        public async Task 详细日志_多出引擎调用参数与引擎原话()
        {
            RequireSevenZip();

            string package = BuildPackage("verbose-diff.7z", 64 * 1024);

            // ---- 默认档：这两类行一条都不许有 ----
            Harness plain = CreateHarness();
            await plain.AddTaskAsync(package);
            await plain.Coordinator.StartExtractAsync();

            Assert.DoesNotContain(
                plain.LogTexts,
                line => line.Contains(StatusText.EngineCommandSummaryPrefix, StringComparison.Ordinal));
            Assert.DoesNotContain(
                plain.LogTexts,
                line => line.Contains("原话：", StringComparison.Ordinal));

            // ---- 详细档：参数摘要 + 引擎原话都要有，而摘要那一行**照旧在** ----
            Harness verbose = CreateHarness(settings => settings.VerboseLog = true);
            await verbose.AddTaskAsync(package);
            await verbose.Coordinator.StartExtractAsync();

            string commandLine = Assert.Single(
                verbose.LogTexts,
                line => line.Contains(StatusText.EngineCommandSummaryPrefix, StringComparison.Ordinal));

            // 参数摘要里要有命令字与归档名；密码只以 `-p******` 出现（⛔ 明文绝不进日志）
            Assert.Contains("7z x", commandLine, StringComparison.Ordinal);
            Assert.Contains("verbose-diff.7z", commandLine, StringComparison.Ordinal);
            Assert.Contains("-p******", commandLine, StringComparison.Ordinal);

            /*
             * 成功的任务照样会走"引擎原话"那个出口，只是 `Everything is Ok` 一行关键字都不含
             * ⇒ 那一行本身是空的（不写）。所以这里钉的是**参数摘要**这条硬差别；
             * "失败时一定有原话"由下面那条用例钉 —— 用户的真实抱怨正是"失败了也看不到原话"。
             */
            Assert.Contains(
                verbose.LogTexts,
                line => line.Contains("] verbose-diff.7z：", StringComparison.Ordinal)
                    && line.Contains("解压 100%", StringComparison.Ordinal));
        }

        /// <summary>
        /// **失败的任务一定有引擎原话**（⛔ 不受详细日志开关影响）——
        /// 真机那次 13 分钟白跑，日志里连一句 7-Zip 原话都没有。
        ///
        /// <para>造法：真 7z 造一个带口令的包，再塞一个**错**的口令进密码列表 ——
        /// 引擎真的会被调一次并失败，于是"引擎原话那一行"必须出现在日志里
        /// （若一个候选都没有，程序在预检就判「整包已加密、没有可用密码」，
        /// 引擎一次都不调，那种情况下本来就没有原话可写）。</para>
        ///
        /// <para>红检：把 <c>ExtractionCoordinator</c> 结论那一处 <c>EngineOutputLog.LogFailure</c>
        /// 撤掉 → 本用例当场红。</para>
        /// </summary>
        [Fact]
        public async Task 失败的任务_日志里一定有引擎原话()
        {
            RequireSevenZip();

            // 真 7z 造一个"口令不对"的包：结论必须是密码错误，而日志里必须带 7-Zip 原话。
            string stage = Path.Combine(_root, "stage-enc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, "secret.txt"), "引擎原话用例\n");

            string packages = Path.Combine(_root, "packages");
            Directory.CreateDirectory(packages);
            string encrypted = Path.Combine(packages, "enc-verbose.7z");

            Run7z(stage, "a", "-t7z", "-mx0", "-p" + "RightPassword123", encrypted, "secret.txt");

            Harness harness = CreateHarness(settings => settings.TryEmptyPasswordFirst = true, passwordService =>
            {
                // 错口令（本文档与仓库里一律用占位符，⛔ 不写真密码）。
                passwordService.AddPassword("PlaceholderWrongPassword");
            });

            /*
             * 加密包会撞上「解压前提醒」那个确认框。本用例要的是**解压失败现场**，
             * 所以按一键档的口径把那个框压掉（判定与日志一条都不少，少的只是弹窗）。
             */
            harness.Coordinator.SuppressBatchReminderDialogForThisRun();

            await harness.AddTaskAsync(encrypted);
            await harness.Coordinator.StartExtractAsync();

            Assert.True(
                harness.LogTexts.Any(line => line.Contains("7-Zip 原话", StringComparison.Ordinal)),
                "日志里没有引擎原话那一行：\n" + string.Join("\n", harness.LogTexts));

            // ⛔ 任何一行都不许出现明文密码。
            Assert.DoesNotContain(
                harness.LogTexts,
                line => line.Contains("RightPassword123", StringComparison.Ordinal)
                    || line.Contains("PlaceholderWrongPassword", StringComparison.Ordinal));
        }

        // ================================================================ 工具

        private string BuildPackage(string fileName, int payloadBytes)
        {
            string stage = Path.Combine(_root, "stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);

            var payload = new byte[payloadBytes];
            new Random(4242).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(stage, "payload.bin"), payload);

            string packages = Path.Combine(_root, "packages");
            Directory.CreateDirectory(packages);

            Run7z(stage, "a", "-t7z", "-mx0", Path.Combine(packages, fileName), "payload.bin");

            return Path.Combine(packages, fileName);
        }

        private Harness CreateHarness(
            Action<AppSettings>? configure = null,
            Action<PasswordService>? configurePasswords = null)
        {
            string dataRoot = Path.Combine(_root, "data-" + Guid.NewGuid().ToString("N"));
            string outputRoot = Path.Combine(_root, "out-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.CacheRootDirectory = dataRoot;
            settings.CustomOutputDirectory = outputRoot;
            settings.ExtractToOriginalDirectory = false;
            settings.KeepArchiveNameFolder = true;
            settings.RecursionMode = "SingleLayer";
            settings.AutoScanAfterDrop = false;
            settings.MaxParallelExtractCount = 1;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.RestHandlingAfterVerify = RestHandlingModes.Keep;

            configure?.Invoke(settings);
            settingsService.Save(settings);

            var engine = new Engines.SevenZip.SevenZipEngine();
            var logService = new LogService(pathService);

            /*
             * ⚠ 一份密码服务给两处用（与 MainViewModel 里真实接线一致）：
             * 以前这里 new 了两个，于是"给密码列表塞一条候选"只会塞进 VM 那一份，
             * 协调器那份照旧是空的 —— 想造"引擎真的被调了一次并失败"的现场就造不出来。
             */
            var passwordService = new PasswordService();
            configurePasswords?.Invoke(passwordService);

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

            var coordinator = new ExtractionCoordinator(
                vm,
                engine,
                passwordService,
                pathService,
                new DialogService());

            return new Harness(vm, coordinator, logService, outputRoot);
        }

        private void Run7z(string workingDirectory, params string[] args)
        {
            RequireSevenZip();

            var psi = new ProcessStartInfo(_sevenZip!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };

            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 7z.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            Assert.True(process.WaitForExit(120_000), "7z 超时");
            Assert.True(process.ExitCode == 0, $"7z 失败：{stdout}{stderr}");
        }

        private void RequireSevenZip()
        {
            if (string.IsNullOrEmpty(_sevenZip))
            {
                throw new InvalidOperationException("测试机上没有 7z.exe（ArchiveFixer/tools/7zip/7z.exe）");
            }
        }

        private static string? LocateSevenZip()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    string candidate = Path.Combine(directory.FullName, "src", "ArchiveFixer", "tools", "7zip", "7z.exe");
                    return File.Exists(candidate) ? candidate : null;
                }

                directory = directory.Parent;
            }

            return null;
        }

        private sealed class Harness
        {
            public Harness(MainViewModel vm, ExtractionCoordinator coordinator, LogService log, string outputRoot)
            {
                Vm = vm;
                Coordinator = coordinator;
                Log = log;
                OutputRoot = outputRoot;
            }

            public MainViewModel Vm { get; }

            public ExtractionCoordinator Coordinator { get; }

            public LogService Log { get; }

            public string OutputRoot { get; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);

            public async Task AddTaskAsync(string path)
            {
                var task = new ArchiveTask(path, Vm.Tasks.Count + 1) { IsSelected = true };

                await new ArchiveDetectService().ApplyDetectResultAsync(task);
                task.CaptureSourceSnapshot();
                Vm.Tasks.Add(task);
            }
        }
    }
}
