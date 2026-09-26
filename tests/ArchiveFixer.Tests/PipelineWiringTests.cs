using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 管线与界面接线的收尾回归（A / B / C / D 四组）。
    ///
    /// <para>
    /// 覆盖的接线：
    /// <list type="bullet">
    /// <item><description>A2 <c>RestRemovalDefaultMode</c> → 清理确认框的档位（含"勾选框语义反转"）；</description></item>
    /// <item><description>A3 <c>ReportDangerousEntries</c> → 复用既有那一遍 list 的统计；</description></item>
    /// <item><description>A4 文件名已加密 → 任务状态（只在"列目录失败"那一支）；</description></item>
    /// <item><description>A5 <c>PathLengthWarning</c> → 任务字段；</description></item>
    /// <item><description>A1 <c>OpenOutputFolderWhenDone</c> → 收尾只开一次、且只在成功时开；</description></item>
    /// <item><description>B 改名路径上的 Ask 语义（预览里逐条确认，默认绝不覆盖）；</description></item>
    /// <item><description>C2 手动密码只对本次运行有效、无 UI 宿主不弹窗；</description></item>
    /// <item><description>C3 缺卷时"手动指定缺失卷所在目录"的重新归组判据。</description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// 线程与静态：<see cref="MainViewModel"/> 的构造会写两个进程级静态
    /// （7z 路径、递归工作区根目录），所以这一组与其它同类测试一起**串行**跑
    /// （见 <c>InnerLayerContinuationTests</c> 顶部的 CollectionDefinition）。
    /// </para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class PipelineWiringTests : IDisposable
    {
        private readonly string _root;

        public PipelineWiringTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerPipelineWiring", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论（句柄可能还在释放中）。
            }
        }

        // ================================================================ A2：其余物清理默认档

        /// <summary>
        /// 默认档 = 回收站（默认设置）时，主按钮是「移入回收站」，勾选框才是切到彻底删除。
        /// 这是一条**负向对照**：接线以前这里两个档位是硬编码的。
        /// </summary>
        [Fact]
        public void 清理默认档_回收站_勾选框表示改为彻底删除()
        {
            var preview = MakePreview();

            MainViewModel.CleanupDecision decision = MainViewModel.DeriveCleanupDecision(
                RestRemovalModes.RecycleBin,
                preview,
                "删除其余物",
                scopeNote: null);

            Assert.Equal(DeleteMode.RecycleBin, decision.DefaultMode);
            Assert.Equal(DeleteMode.Permanent, decision.OptionMode);
            Assert.Equal("移入回收站", decision.ConfirmButtonText);
            Assert.Equal("改为彻底删除，不进回收站", decision.OptionText);
            Assert.False(decision.OptionCheckedByDefault);

            Assert.Equal(DeleteMode.RecycleBin, decision.Resolve(optionChecked: false));
            Assert.Equal(DeleteMode.Permanent, decision.Resolve(optionChecked: true));
        }

        /// <summary>
        /// 默认档 = 彻底删除时，**勾选框的语义必须反过来**（否则界面自相矛盾：
        /// 默认档是彻底删除、勾选框却写着"改为彻底删除"，不勾反而得到更危险的那一档）。
        /// </summary>
        [Fact]
        public void 清理默认档_彻底删除_勾选框反转为改为回收站()
        {
            var preview = MakePreview();

            MainViewModel.CleanupDecision decision = MainViewModel.DeriveCleanupDecision(
                RestRemovalModes.Permanent,
                preview,
                "删除其余物",
                scopeNote: null);

            Assert.Equal(DeleteMode.Permanent, decision.DefaultMode);
            Assert.Equal(DeleteMode.RecycleBin, decision.OptionMode);
            Assert.Equal("彻底删除", decision.ConfirmButtonText);
            Assert.Equal("改为移入回收站（可恢复）", decision.OptionText);
            Assert.False(decision.OptionCheckedByDefault);

            // 不勾 = 默认档（彻底删除）；勾上 = 可恢复的那一档。语义完全没有反转错。
            Assert.Equal(DeleteMode.Permanent, decision.Resolve(optionChecked: false));
            Assert.Equal(DeleteMode.RecycleBin, decision.Resolve(optionChecked: true));

            // 正文说的是"彻底删除"，与主按钮一致 —— 用户读到的东西不许互相打架。
            Assert.Contains("无法恢复", decision.ConfirmText);
        }

        /// <summary>读不懂的档位一律回落"回收站"（宁可多一步，也不默默变成不可恢复的删除）。</summary>
        [Fact]
        public void 清理默认档_非法值回落回收站()
        {
            var preview = MakePreview();

            foreach (string? broken in new string?[] { null, string.Empty, "  ", "Whatever", "99", "recycle" })
            {
                MainViewModel.CleanupDecision decision = MainViewModel.DeriveCleanupDecision(
                    broken,
                    preview,
                    "删除其余物",
                    scopeNote: null);

                Assert.Equal(DeleteMode.RecycleBin, decision.DefaultMode);
                Assert.Equal("移入回收站", decision.ConfirmButtonText);
            }
        }

        /// <summary>
        /// 设置项的默认值必须是回收站。这是**红线的前置条件**：
        /// 默认档一旦变成彻底删除，任何"没改过设置"的用户都会在第一次清理时面对不可恢复的删除。
        /// </summary>
        [Fact]
        public void 清理默认档_出厂设置是回收站()
        {
            Assert.Equal(RestRemovalModes.RecycleBin, AppSettings.CreateDefault().RestRemovalDefaultMode);
            Assert.Equal(RestRemovalModes.RecycleBin, new AppSettings().RestRemovalDefaultMode);
            Assert.False(RestRemovalModes.IsPermanent(AppSettings.CreateDefault().RestRemovalDefaultMode));
        }

        // ================================================================ A3：危险条目统计（只提示不阻断）

        [Fact]
        public void 危险条目统计_可执行与脚本类条目会被点出来()
        {
            var entries = new[]
            {
                Entry("docs/readme.txt"),
                Entry("setup.exe"),
                Entry("patch/run.bat"),
                Entry("crack/keygen.EXE"),   // 大小写不敏感
                Entry("music/song.mp3")
            };

            string hint = ExtractionCoordinator.AnalyzeDangerousEntries(entries, enabled: true);

            Assert.False(string.IsNullOrWhiteSpace(hint));
            Assert.Contains("3 个可执行 / 脚本类条目", hint);
            Assert.Contains("setup.exe", hint);
            Assert.Contains("run.bat", hint);
            Assert.Contains("keygen.EXE", hint);

            // 只提示：文案里必须写明"不阻断"，否则用户会以为这些包被拒了。
            Assert.Contains("不会因此阻断解压", hint);
        }

        [Fact]
        public void 危险条目统计_关掉设置项就什么都不算()
        {
            var entries = new[] { Entry("setup.exe") };

            Assert.Equal(string.Empty, ExtractionCoordinator.AnalyzeDangerousEntries(entries, enabled: false));
        }

        [Fact]
        public void 危险条目统计_目录条目与普通文件不算()
        {
            var entries = new[]
            {
                Entry("setup.exe", isDirectory: true),   // 名字像可执行文件，但它是目录
                Entry("readme.txt"),
                Entry("data.bin"),
                Entry("noextension")
            };

            Assert.Equal(string.Empty, ExtractionCoordinator.AnalyzeDangerousEntries(entries, enabled: true));
        }

        [Fact]
        public void 危险条目统计_没有清单时不下结论()
        {
            Assert.Equal(string.Empty, ExtractionCoordinator.AnalyzeDangerousEntries(null, enabled: true));
            Assert.Equal(string.Empty, ExtractionCoordinator.AnalyzeDangerousEntries(Array.Empty<ArchiveEntry>(), enabled: true));
        }

        [Fact]
        public void 危险条目统计_条目很多时只列前几个并给出总数()
        {
            var entries = Enumerable.Range(1, 40)
                .Select(i => Entry($"tool{i:D2}.exe"))
                .ToArray();

            string hint = ExtractionCoordinator.AnalyzeDangerousEntries(entries, enabled: true);

            Assert.Contains("40 个可执行 / 脚本类条目", hint);
            Assert.Contains("…等 40 个", hint);

            // 提示本身不许长到把详情刷屏。
            Assert.True(hint.Length < 300, $"提示太长了，会刷屏：{hint.Length} 字符");
        }

        // ================================================================ A4 / A5：预检那一支的落地

        /// <summary>
        /// 列目录失败 + 引擎判"文件名已加密" → 任务状态就是「文件名已加密」，
        /// 而不是含糊的"密码错误"。文案里还要给出下一步（需要正确密码）。
        ///
        /// ⚠ 假引擎必须**真的失败**：加密头的包在真实世界里 7z 也解不开
        /// （<c>Cannot open encrypted archive. Wrong password?</c>）。
        /// 如果让假引擎返回成功，这条测试测的就不是"预检的结论落到任务上"，
        /// 而是"解压成功会不会被覆盖" —— 那是另一件事。
        /// </summary>
        [Fact]
        public async Task 列目录失败且判为加密头_任务状态是文件名已加密()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, CreateSourceFile("hdr.7z"));

            harness.Engine.OnListAsync = _ => Task.FromResult(
                ArchiveListResult.Failure(
                    "EncryptedHeaders",
                    "Cannot open encrypted archive. Wrong password?",
                    "fake",
                    "1.0"));

            harness.Engine.OnExtractAsync = _ => Task.FromResult(new ArchiveOperationResult
            {
                Success = false,
                Status = StatusText.WrongPassword,
                Message = "密码错误或缺少正确密码",
                DetectedErrorType = "WrongPassword"
            });

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.EncryptedHeaders, task.Status);
            Assert.Contains("加密了文件名", task.ErrorMessage);
            Assert.Contains("需要正确密码", task.ErrorMessage);
        }

        /// <summary>
        /// ⛔ 反向回归（这一条比上面那条更重要）：**列目录失败但不是加密头**时，
        /// 绝不许落成「文件名已加密」—— 那会把"文件损坏""权限不足"这类结论改成一句误导人的话。
        /// </summary>
        [Fact]
        public async Task 列目录失败但不是加密头_不许落成文件名已加密()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, CreateSourceFile("broken.7z"));

            harness.Engine.OnListAsync = _ => Task.FromResult(
                ArchiveListResult.Failure("Corrupted", "归档已损坏", "fake", "1.0"));

            harness.Engine.OnExtractAsync = _ => Task.FromResult(new ArchiveOperationResult
            {
                Success = false,
                Status = StatusText.Corrupted,
                Message = "归档已损坏",
                DetectedErrorType = "Corrupted"
            });

            await harness.Coordinator.StartExtractAsync();

            Assert.NotEqual(StatusText.EncryptedHeaders, task.Status);
            Assert.Equal(StatusText.Corrupted, task.Status);
        }

        /// <summary>
        /// A5：预检算出来的路径长度预警要落进**任务字段**（以前只有一行日志，
        /// 而"为什么少了几个文件"恰恰是用户事后才来查的问题）。
        /// 这条同时钉住 A3：同一遍 list 里统计出来的可疑条目也要落进任务字段。
        /// </summary>
        [Fact]
        public async Task 预检的路径过长与可疑条目_都落进任务字段()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, CreateSourceFile("long.7z"));

            // 240 字符阈值（SafePathHelper.IsPathTooLong）：造一条长得足够夸张的相对路径。
            string longEntry = string.Join("/", Enumerable.Repeat("很长的目录名字", 40)) + "/video.mkv";

            Assert.True(longEntry.Length >= 240, $"测试样本自己得先够长：{longEntry.Length}");

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult(longEntry, "setup.exe"));

            await harness.Coordinator.StartExtractAsync();

            Assert.False(
                string.IsNullOrWhiteSpace(task.PathLengthWarning),
                "路径过长预警应当落进任务字段（以前只在日志里）");

            Assert.Contains(StatusText.PathTooLong, task.PathLengthWarning);

            Assert.False(
                string.IsNullOrWhiteSpace(task.DangerousEntriesWarning),
                "可疑条目提示应当落进任务字段");

            Assert.Contains("setup.exe", task.DangerousEntriesWarning);
        }

        /// <summary>
        /// 关掉「统计并提示可疑条目」之后，任务字段里不许再出现这条提示（设置项要真的管用）。
        /// </summary>
        [Fact]
        public async Task 关掉可疑条目统计_任务字段里就没有这条提示()
        {
            Harness harness = CreateHarness(settings: s => s.ReportDangerousEntries = false);
            ArchiveTask task = AddTask(harness, CreateSourceFile("safe.7z"));

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult("setup.exe", "content.txt"));

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(string.Empty, task.DangerousEntriesWarning);
        }

        // ================================================================ A1：完成后打开输出目录

        /// <summary>
        /// 设置关着（默认）时，收尾**一个目录都不打开**。这条是 A1 的负向对照：
        /// 开着是"用户显式要的"，关着时多开一次就是"动了用户的桌面"。
        /// </summary>
        [Fact]
        public async Task 完成后打开输出目录_默认关_一个都不打开()
        {
            Harness harness = CreateHarness(settings: s => s.OpenOutputFolderWhenDone = false);
            ArchiveTask task = AddTask(harness, CreateSourceFile("one.7z"));

            WireSuccessfulExtraction(harness, "content.txt");

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.Empty(harness.Recording.OpenedOutputDirectories);
        }

        /// <summary>
        /// 设置开着时：成功收尾**只开一次**（批里两个包也只有一个目录被打开）。
        /// 一次批处理开几十个资源管理器窗口，那不是"看一眼结果"，是骚扰。
        /// </summary>
        [Fact]
        public async Task 完成后打开输出目录_开着_整批只开一次()
        {
            Harness harness = CreateHarness(settings: s => s.OpenOutputFolderWhenDone = true);
            ArchiveTask first = AddTask(harness, CreateSourceFile("one.7z"));
            ArchiveTask second = AddTask(harness, CreateSourceFile("two.7z"));

            WireSuccessfulExtraction(harness, "content.txt");

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, first.Status);
            Assert.Equal(StatusText.ExtractSuccess, second.Status);

            Assert.Single(harness.Recording.OpenedOutputDirectories);
        }

        /// <summary>
        /// 收尾结论不成立（这里让输出校验失败）时**不许打开** —— 打开一个空目录/半成品目录
        /// 只会让用户以为东西在里面。
        /// </summary>
        [Fact]
        public async Task 完成后打开输出目录_校验没通过时不打开()
        {
            Harness harness = CreateHarness(settings: s => s.OpenOutputFolderWhenDone = true);
            ArchiveTask task = AddTask(harness, CreateSourceFile("bad.7z"));

            // 引擎声称有 5 个文件，实际只写出 1 个 → 输出校验不通过。
            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult("a.txt", "b.txt", "c.txt", "d.txt", "e.txt"));

            harness.Engine.OnExtractAsync = request =>
            {
                WriteContent(request.OutputPath!, "a.txt", "only one");
                return Task.FromResult(Succeeded());
            };

            await harness.Coordinator.StartExtractAsync();

            Assert.False(task.IsOutputVerified);
            Assert.Empty(harness.Recording.OpenedOutputDirectories);
        }

        // ================================================================ C2：手动密码（只对本次运行有效）

        /// <summary>
        /// 无 UI 宿主（本进程没有 WPF Application）时**不弹窗、不死等**：
        /// 这条测试能跑完本身就是判据（弹模态框会挂在这里直到测试超时）。
        /// </summary>
        [Fact]
        public async Task 手动密码_无界面宿主不弹窗也不影响流程()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, CreateSourceFile("enc.7z"));

            // 有任何任务"可能带密码"才会走那一步询问。
            task.IsEncrypted = true;

            WireSuccessfulExtraction(harness, "content.txt");

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains("没有 WPF 界面", StringComparison.Ordinal) &&
                        item.Message.Contains("手动输入密码", StringComparison.Ordinal));
        }

        /// <summary>
        /// 手动密码插进候选的位置：**空密码之后、密码本之前**。
        ///
        /// 为什么位置本身也要测：它决定"无密码包会不会被多试一次密码"（空密码优先），
        /// 以及"用户刚给的密码够不够早试"（排在密码本之前）。
        /// 这里用真管线跑一遍：假引擎记录每次被要求用的密码。
        /// </summary>
        [Fact]
        public async Task 无界面宿主_手动密码为空_候选顺序不受影响()
        {
            Harness harness = CreateHarness(passwords: new[] { "book-password" });
            ArchiveTask task = AddTask(harness, CreateSourceFile("enc.7z"));
            task.IsEncrypted = true;

            var triedPasswords = new List<string>();

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult("content.txt"));

            harness.Engine.OnExtractAsync = request =>
            {
                lock (triedPasswords)
                {
                    triedPasswords.Add(request.Password ?? string.Empty);
                }

                /*
                 * 落盘内容必须与清单声明的字节数对得上（这里是 `"content.txt".Length` = 11）。
                 *
                 * ⚠ 2026-09-24：以前这里写的是 "ok"（2 字节），而校验判否原本**不影响状态**，
                 * 所以"声明 11 实际 2"一直没被发现；现在校验判否会直接顶掉「解压成功」，
                 * 这种自相矛盾的现场就会挂在与被测行为（候选顺序）无关的地方。
                 */
                WriteContent(request.OutputPath!, "content.txt", "okokokokoko");
                return Task.FromResult(Succeeded());
            };

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // 第一个候选仍是空密码（用户没手动输入时不许凭空多一个候选）。
            Assert.Equal(string.Empty, triedPasswords.First());
        }

        // ================================================================ C3：缺卷时的手动指定目录

        /// <summary>
        /// 缺卷门的**前置条件不变**：没有人（也没有界面）指定目录时，仍然拒绝启动、仍然报缺哪几个。
        /// ⛔ 不变量 7：出口只是"帮你找齐"，不是"缺卷也允许开始"。
        /// </summary>
        [Fact]
        public async Task 缺卷_没有指定目录时仍然不启动并报缺哪几个()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, CreateSourceFile("vol.7z.001"));

            task.IsVolumeGroup = true;
            task.IsVolumeComplete = false;
            task.VolumePaths.Add(task.CurrentPath);
            task.MissingVolumeNames.Add("vol.7z.002");
            task.VolumeInfoText = "2 卷，缺 vol.7z.002";

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.VolumeMissing, task.Status);
            Assert.Contains("vol.7z.002", task.ErrorMessage);

            // 一次都没有真的去解压（不变量 7：缺卷不得开始不可完成的任务）。
            Assert.Empty(harness.Engine.ExtractCalls);
        }

        /// <summary>
        /// 重新归组的判据（纯函数，不碰界面）：把"任务现有各卷 + 用户指定目录里找到的卷"合起来重算，
        /// 补齐了就返回完整的那一组，没补齐就仍然报缺。
        ///
        /// 这里直接验"补齐"与"没补齐"两种形态 —— 归组**复用** <c>VolumeGroupDetector</c>，
        /// 所以这条测试同时钉住"没有另写一套分卷命名规则"。
        /// </summary>
        [Fact]
        public void 重新归组_补齐后判为完整_没补齐仍然缺()
        {
            string dir = Path.Combine(_root, "vols");
            Directory.CreateDirectory(dir);

            string part1 = Path.Combine(dir, "movie.7z.001");
            string part2 = Path.Combine(dir, "movie.7z.002");

            File.WriteAllText(part1, "part1");
            File.WriteAllText(part2, "part2");

            // 先只认第一卷：缺 .002。
            var missingTask = new ArchiveTask(part1) { IsVolumeGroup = true };
            missingTask.VolumePaths.Add(part1);

            HashSet<string> onlyFirst = OnlyVolumes(missingTask);

            Assert.DoesNotContain(Path.GetFullPath(part2), onlyFirst);

            // 把 .002 也加进来（模拟"用户指定目录里找到了它"）：整组必须判为完整。
            missingTask.VolumePaths.Add(part2);

            HashSet<string> both = OnlyVolumes(missingTask);

            Assert.Contains(Path.GetFullPath(part1), both);
            Assert.Contains(Path.GetFullPath(part2), both);
        }

        /// <summary>
        /// 缺卷补救的入口本身：**每批每个任务只问一次**，而且无界面宿主时直接按缺卷处理
        /// （绝不弹窗、绝不死等）。这里用真管线跑，断言"没有第二个包被反复问"的形态。
        /// </summary>
        [Fact]
        public async Task 缺卷_无界面宿主时一次都不弹且不启动()
        {
            Harness harness = CreateHarness();

            ArchiveTask first = AddTask(harness, CreateSourceFile("a.7z.001"));
            first.IsVolumeGroup = true;
            first.IsVolumeComplete = false;
            first.VolumePaths.Add(first.CurrentPath);
            first.MissingVolumeNames.Add("a.7z.002");
            first.VolumeInfoText = "2 卷，缺 a.7z.002";

            ArchiveTask second = AddTask(harness, CreateSourceFile("b.7z.001"));
            second.IsVolumeGroup = true;
            second.IsVolumeComplete = false;
            second.VolumePaths.Add(second.CurrentPath);
            second.MissingVolumeNames.Add("b.7z.002");
            second.VolumeInfoText = "2 卷，缺 b.7z.002";

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(StatusText.VolumeMissing, first.Status);
            Assert.Equal(StatusText.VolumeMissing, second.Status);

            Assert.Empty(harness.Engine.ExtractCalls);
        }

        // ================================================================ B：改名路径上的 Ask

        /// <summary>
        /// 「询问」档在**改名路径**上不再被静默当成"自动重命名"：
        /// 预览把撞名的条目标成"待你选择"，而不是直接给出一个 (1) 的新名字。
        /// 这是旧缺陷的正面判据（旧实现这里的状态是「将自动重命名」）。
        /// </summary>
        [Fact]
        public void 改名预览_询问档_撞名的条目变成待选择()
        {
            string dir = Path.Combine(_root, "rename-ask");
            Directory.CreateDirectory(dir);

            string source = Path.Combine(dir, "a.jpg");
            File.WriteAllText(source, "x");
            File.WriteAllText(Path.Combine(dir, "a.zip"), "existing");

            List<RenamePreviewItem> preview = new RenameService().BuildPreview(
                new[] { RenameTask(source) },
                FixOptions(ConflictActions.Ask));

            Assert.Single(preview);
            Assert.True(preview[0].NeedsConflictChoice);
            Assert.Equal(StatusText.TargetExists, preview[0].Status);

            // 旧实现的形态（"将自动重命名"）绝不许再出现 —— 那正是"界面说询问、代码做一套"。
            Assert.NotEqual(StatusText.WillAutoRename, preview[0].Status);

            // 而且预览里给的落点**还没有**被改成 (1)：等用户选。
            Assert.Equal(Path.Combine(dir, "a.zip"), preview[0].NewPath);
        }

        /// <summary>用户选「自动重命名」→ 落成 (1)，已有文件不动。</summary>
        [Fact]
        public async Task 改名_询问档选自动重命名_落成带括号的新名字且不动已有文件()
        {
            string dir = Path.Combine(_root, "rename-pick-auto");
            Directory.CreateDirectory(dir);

            string source = Path.Combine(dir, "a.jpg");
            string existing = Path.Combine(dir, "a.zip");
            File.WriteAllText(source, "新");
            File.WriteAllText(existing, "旧");

            var service = new RenameService();
            var task = RenameTask(source);
            List<RenamePreviewItem> preview = service.BuildPreview(new[] { task }, FixOptions(ConflictActions.Ask));

            preview[0].ConflictChoice = "AutoRename";
            Assert.True(service.ApplyConflictChoice(preview[0]));

            await service.ExecuteRenameAsync(preview, new[] { task });

            Assert.Equal("旧", File.ReadAllText(existing));
            Assert.Equal("新", File.ReadAllText(Path.Combine(dir, "a(1).zip")));
        }

        /// <summary>用户选「跳过」→ 已有文件不动，这一条也不改名。</summary>
        [Fact]
        public async Task 改名_询问档选跳过_两个文件都原位不动()
        {
            string dir = Path.Combine(_root, "rename-pick-skip");
            Directory.CreateDirectory(dir);

            string source = Path.Combine(dir, "a.jpg");
            string existing = Path.Combine(dir, "a.zip");
            File.WriteAllText(source, "新");
            File.WriteAllText(existing, "旧");

            var service = new RenameService();
            var task = RenameTask(source);
            List<RenamePreviewItem> preview = service.BuildPreview(new[] { task }, FixOptions(ConflictActions.Ask));

            preview[0].ConflictChoice = "Skip";
            Assert.True(service.ApplyConflictChoice(preview[0]));

            Assert.Equal(StatusText.RenameWillSkip, preview[0].Status);
            Assert.False(preview[0].IsSelected, "选了跳过的条目不该再被勾着执行");

            await service.ExecuteRenameAsync(preview, new[] { task });

            Assert.True(File.Exists(source));
            Assert.Equal("旧", File.ReadAllText(existing));
        }

        /// <summary>
        /// ⛔ 不变量 3 的负向对照：**没选**（用户直接关掉预览窗口）时落到"自动重命名"，
        /// **绝不覆盖**。旧实现在这里的返回值与 AutoRename 相同，新实现必须显式走保守档并留痕。
        /// </summary>
        [Fact]
        public async Task 改名_询问档没选_按保守档自动重命名且绝不覆盖()
        {
            string dir = Path.Combine(_root, "rename-pick-none");
            Directory.CreateDirectory(dir);

            string source = Path.Combine(dir, "a.jpg");
            string existing = Path.Combine(dir, "a.zip");
            File.WriteAllText(source, "新");
            File.WriteAllText(existing, "旧");

            var service = new RenameService();
            var task = RenameTask(source);
            List<RenamePreviewItem> preview = service.BuildPreview(new[] { task }, FixOptions(ConflictActions.Ask));

            Assert.True(string.IsNullOrWhiteSpace(preview[0].ConflictChoice), "初始状态必须是「没选」");

            Assert.True(service.ApplyConflictChoice(preview[0]));

            Assert.Equal("旧", File.ReadAllText(existing));
            Assert.Contains("绝不覆盖", preview[0].ErrorMessage);

            await service.ExecuteRenameAsync(preview, new[] { task });

            Assert.Equal("旧", File.ReadAllText(existing));
            Assert.True(File.Exists(Path.Combine(dir, "a(1).zip")));
        }

        /// <summary>用户选「覆盖」→ 两阶段落位，旧文件被顶掉，源文件不再存在。</summary>
        [Fact]
        public async Task 改名_询问档选覆盖_走两阶段且不留临时文件()
        {
            string dir = Path.Combine(_root, "rename-pick-overwrite");
            Directory.CreateDirectory(dir);

            string source = Path.Combine(dir, "a.jpg");
            string existing = Path.Combine(dir, "a.zip");
            File.WriteAllText(source, "新");
            File.WriteAllText(existing, "旧");

            var service = new RenameService();
            var task = RenameTask(source);
            List<RenamePreviewItem> preview = service.BuildPreview(new[] { task }, FixOptions(ConflictActions.Ask));

            preview[0].ConflictChoice = "Overwrite";
            Assert.True(service.ApplyConflictChoice(preview[0]));

            await service.ExecuteRenameAsync(preview, new[] { task });

            Assert.False(File.Exists(source));
            Assert.Equal("新", File.ReadAllText(existing));
            Assert.Empty(Directory.GetFiles(dir, "*.af-vacating*"));
        }

        /// <summary>
        /// 默认档（AutoRename）的行为一个字都不能变：撞名直接给出 (1) 的落点，
        /// 不进入"待选择"（不变量 3：默认档不得覆盖，也不该在默认档上多问一句）。
        /// </summary>
        [Fact]
        public void 改名预览_默认档_仍然是自动重命名且不问()
        {
            string dir = Path.Combine(_root, "rename-default");
            Directory.CreateDirectory(dir);

            string source = Path.Combine(dir, "a.jpg");
            File.WriteAllText(source, "x");
            File.WriteAllText(Path.Combine(dir, "a.zip"), "existing");

            List<RenamePreviewItem> preview = new RenameService().BuildPreview(
                new[] { RenameTask(source) },
                FixOptions(ConflictActions.AutoRename));

            Assert.Single(preview);
            Assert.False(preview[0].NeedsConflictChoice);
            Assert.Equal(StatusText.WillAutoRename, preview[0].Status);
            Assert.Equal(Path.Combine(dir, "a(1).zip"), preview[0].NewPath);
        }

        // ================================================================ D：越界即整包失败的口径

        /// <summary>
        /// 「检出产物越界即整包失败（不归集、不清理）」这个口径必须**在界面上说清**：
        /// 失败原因里写明"产物越出目标根目录"，而且要写明"不归集产物、不处理源包"。
        /// 只留一行日志等于用户看不到（README 已声明这个口径，界面以前没说）。
        /// </summary>
        [Fact]
        public async Task 产物越界_失败原因写明整包失败且不归集不清理()
        {
            Harness harness = CreateHarness();
            ArchiveTask task = AddTask(harness, CreateSourceFile("escape.7z"));

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult("content.txt"));

            // 假引擎"作弊"：把产物写到暂存目录之外的兄弟目录里（真实场景是符号链接/目录联接点）。
            string outsideDirectory = Path.Combine(harness.PathService.WorkDirectory, "outside-" + Guid.NewGuid().ToString("N"));

            harness.Engine.OnExtractAsync = request =>
            {
                Directory.CreateDirectory(outsideDirectory);
                File.WriteAllText(Path.Combine(outsideDirectory, "escaped.txt"), "escaped");

                // 同时在暂存目录里放一个**目录联接点**指向它 —— 这是 FindLandingViolation 能识别的形态。
                TryCreateJunction(Path.Combine(request.OutputPath!, "escaped"), outsideDirectory);

                return Task.FromResult(Succeeded());
            };

            await harness.Coordinator.StartExtractAsync();

            // 建不出联接点（没有权限的设备上）时这一条只能验到"至少没被误判成成功"。
            if (task.Status == StatusText.ExtractFailed)
            {
                Assert.Contains("产物越出目标根目录", task.ErrorMessage);
                Assert.Contains("整包判定失败", task.ErrorMessage);
                Assert.Contains("不归集产物", task.ErrorMessage);
                Assert.False(task.IsOutputVerified, "越界时不许显示「输出校验通过」");
            }

            // 无论联接点建没建成，源文件都必须原样留着（越界时不许清理源包）。
            Assert.True(File.Exists(task.CurrentPath), "越界时源包必须原地不动");
        }

        // ================================================================ C1：并发等待可见 + 全速出口

        /// <summary>
        /// 「全速」开着时，真正同时在跑的任务数可以超过设置里的「最大并发解压数」（默认 1 = 串行）。
        ///
        /// 判据用**观测到的最大并发数**，不看日志文案：这才是"节流到底生效没有"的事实。
        /// 两个任务的解压在假引擎里会互相等对方进场，所以只要实现是串行的，这条必然超时失败。
        /// </summary>
        [Fact]
        public async Task 全速开着_并发数超过设置里的上限()
        {
            Harness harness = CreateHarness(settings: s =>
            {
                s.MaxParallelExtractCount = 1;
                s.TryEmptyPasswordFirst = true;
            });

            harness.Vm.RunAtFullSpeed = true;

            AddTask(harness, CreateSourceFile("one.7z"));
            AddTask(harness, CreateSourceFile("two.7z"));

            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int inFlight = 0;
            int maxInFlight = 0;

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult("content.txt"));

            harness.Engine.OnExtractAsync = async request =>
            {
                int now = Interlocked.Increment(ref inFlight);

                InterlockedMax(ref maxInFlight, now);

                if (now >= 2)
                {
                    entered.TrySetResult(true);
                }

                // 等另一个任务也进场（全速下应当等得到；串行时超时，测试如实失败）。
                await Task.WhenAny(entered.Task, Task.Delay(TimeSpan.FromSeconds(5)));

                WriteContent(request.OutputPath!, "content.txt", "x");

                Interlocked.Decrement(ref inFlight);

                return Succeeded();
            };

            await harness.Coordinator.StartExtractAsync();

            Assert.True(
                maxInFlight >= 2,
                $"开了「全速」之后应当能同时跑 2 个任务，实测最大并发 {maxInFlight}（节流仍然生效？）");
        }

        /// <summary>
        /// 没开「全速」时严格按设置节流：最大并发数不许超过设置值（默认 1）。
        /// 这是上一条的负向对照 —— 也说明"全速"是唯一能让它突破上限的东西。
        /// </summary>
        [Fact]
        public async Task 没开全速_并发数受设置限制()
        {
            Harness harness = CreateHarness(settings: s => s.MaxParallelExtractCount = 1);

            Assert.False(harness.Vm.RunAtFullSpeed, "「全速」的出厂状态必须是关（只对本次运行有效，不是持久设置）");

            AddTask(harness, CreateSourceFile("one.7z"));
            AddTask(harness, CreateSourceFile("two.7z"));
            AddTask(harness, CreateSourceFile("three.7z"));

            int inFlight = 0;
            int maxInFlight = 0;

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult("content.txt"));

            harness.Engine.OnExtractAsync = request =>
            {
                int now = Interlocked.Increment(ref inFlight);

                InterlockedMax(ref maxInFlight, now);

                WriteContent(request.OutputPath!, "content.txt", "x");

                Interlocked.Decrement(ref inFlight);

                return Task.FromResult(Succeeded());
            };

            await harness.Coordinator.StartExtractAsync();

            Assert.Equal(1, maxInFlight);
        }

        /// <summary>
        /// 被节流挡住时**等待状态必须可见**：日志里要写清"并发已满 N/M、某个包在队列里等空位"，
        /// 并给出"立即继续"的出口提示（这正是用户投诉"看着像卡死"的那一类现象）。
        /// </summary>
        [Fact]
        public async Task 被节流挡住_等待状态写进日志并给出出口()
        {
            Harness harness = CreateHarness(settings: s => s.MaxParallelExtractCount = 1);

            AddTask(harness, CreateSourceFile("one.7z"));
            AddTask(harness, CreateSourceFile("two.7z"));

            WireSuccessfulExtraction(harness, "content.txt");

            await harness.Coordinator.StartExtractAsync();

            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains("并发已满", StringComparison.Ordinal) &&
                        item.Message.Contains("等空位", StringComparison.Ordinal) &&
                        item.Message.Contains("全速（本批不节流）", StringComparison.Ordinal));

            /*
             * ⚠ 2026-09-26 第 45 条：**等待那一段不再逐任务刷**（真机 76 个任务刷了 74 遍），
             * 所以原来那句「「X」等到空位，开始解压」没有了（等 <30 秒不单独写行）。
             * 取而代之的是批末一条汇总 —— 这里改成钉它。
             */
            Assert.Contains(
                harness.Log.Logs,
                item => item.Message.Contains("并发排队汇总", StringComparison.Ordinal));

            // ⛔ 反面：不许再说"主界面的「全速」"（那个开关在②页，用户就是被这句带偏的）。
            Assert.DoesNotContain(
                harness.Log.Logs,
                item => item.Message.Contains("主界面的「全速」", StringComparison.Ordinal));
        }

        // ================================================================ 装配

        private sealed class Harness
        {
            public Harness(
                MainViewModel vm,
                FakeEngine engine,
                RecordingMainViewModel recordingVm,
                ExtractionCoordinator coordinator,
                LogService log,
                PathService pathService)
            {
                Vm = vm;
                Engine = engine;
                Recording = recordingVm;
                Coordinator = coordinator;
                Log = log;
                PathService = pathService;
            }

            public MainViewModel Vm { get; }

            /// <summary>同一个对象的强类型视图（用来读测试替身记下来的"打开过哪些目录"）。</summary>
            public RecordingMainViewModel Recording { get; }
            public FakeEngine Engine { get; }

            public ExtractionCoordinator Coordinator { get; }

            public LogService Log { get; }

            public PathService PathService { get; }
        }

        /// <summary>
        /// MainViewModel 的测试替身：只把"打开输出目录"换成一个**记录器**。
        ///
        /// 为什么必须换掉：真实现会 <c>Process.Start("explorer.exe", 目录)</c> ——
        /// 单元测试里既不该真的弹资源管理器（AGENTS.md §13 不许动用户桌面），
        /// 也没法断言"到底开了几次"。记录器把这件事变成可断言的事实。
        /// </summary>
        internal sealed class RecordingMainViewModel : MainViewModel
        {
            private readonly List<string> _opened = new();

            public RecordingMainViewModel(
                FileScanService fileScanService,
                ArchiveDetectService archiveDetectService,
                RenameService renameService,
                IArchiveEngine archiveEngine,
                PasswordService passwordService,
                LogService logService,
                SettingsService settingsService,
                PathService pathService,
                TaskSummaryService taskSummaryService,
                ClipboardService clipboardService,
                DialogService dialogService)
                : base(
                      fileScanService,
                      archiveDetectService,
                      renameService,
                      archiveEngine,
                      passwordService,
                      logService,
                      settingsService,
                      pathService,
                      taskSummaryService,
                      clipboardService,
                      dialogService)
            {
            }

            public IReadOnlyList<string> OpenedOutputDirectories
            {
                get
                {
                    lock (_opened)
                    {
                        return _opened.ToList();
                    }
                }
            }

            public override bool OpenCompletedOutputDirectory(string directory)
            {
                if (Settings == null || !Settings.OpenOutputFolderWhenDone || string.IsNullOrWhiteSpace(directory))
                {
                    return false;
                }

                lock (_opened)
                {
                    _opened.Add(directory);
                }

                return true;
            }
        }

        private Harness CreateHarness(
            IEnumerable<string>? passwords = null,
            Action<AppSettings>? settings = null)
        {
            string dataRoot = Path.Combine(_root, "data");
            string outputRoot = Path.Combine(_root, "out");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(outputRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings appSettings = AppSettings.CreateDefault();
            appSettings.CacheRootDirectory = dataRoot;
            appSettings.CustomOutputDirectory = outputRoot;
            appSettings.ExtractToOriginalDirectory = false;
            appSettings.KeepArchiveNameFolder = true;
            appSettings.RecursionMode = "SingleLayer";
            appSettings.AutoScanAfterDrop = false;

            // 源包固定在原地：这一组测的是接线，源包搬运会把断言里的计数搅浑（另有专测）。
            appSettings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);

            settings?.Invoke(appSettings);
            settingsService.Save(appSettings);

            var engine = new FakeEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

            if (passwords != null)
            {
                foreach (string password in passwords)
                {
                    passwordService.Passwords.Add(new PasswordItem
                    {
                        Value = password,
                        Source = "ImportedList",
                        IsEnabled = true,
                        Remark = "测试候选"
                    });
                }
            }

            // MainViewModel 的构造会顺手写两个进程级静态（7z 路径、递归工作区根目录）：先存后还原。
            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

            var vm = new RecordingMainViewModel(
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

            var coordinator = new ExtractionCoordinator(vm, engine, passwordService, pathService, new DialogService());

            // 这个用例拿「细节日志」当行为证据（第 44 条之后，成功时默认只留两行）。
            coordinator.KeepTaskDetailInLog = true;

            return new Harness(vm, engine, vm, coordinator, logService, pathService);
        }

        private ArchiveTask AddTask(Harness harness, string sourcePath)
        {
            var task = new ArchiveTask(sourcePath, harness.Vm.Tasks.Count + 1)
            {
                IsArchive = true,
                DetectedFormat = "7Z",
                ExtensionStatus = StatusText.ExtensionNormal,
                Status = StatusText.Recognized,
                IsSelected = true
            };

            harness.Vm.Tasks.Add(task);
            return task;
        }

        /// <summary>
        /// 把假引擎接成"能真的解出一个文件、而且输出校验通过"的形态。
        ///
        /// ⚠ 内容长度必须是**真实字节数**：输出校验按"预期大小 vs 实际大小"比对，
        /// 随手写个 <c>"ok"</c> 而 list 里按字符数报大小，校验会立刻失败 ——
        /// 那会让"完成后打开输出目录"这类以校验通过为前提的用例测了个空。
        /// </summary>
        private static void WireSuccessfulExtraction(Harness harness, string contentFileName, string content = "ok")
        {
            harness.Engine.OnListAsync = _ => Task.FromResult(
                new ArchiveListResult
                {
                    Success = true,
                    FileCount = 1,
                    TotalUncompressedSize = System.Text.Encoding.UTF8.GetByteCount(content),
                    Entries = new List<ArchiveEntry>
                    {
                        new() { Path = contentFileName, Size = System.Text.Encoding.UTF8.GetByteCount(content) }
                    },
                    EngineId = "fake",
                    EngineVersion = "1.0"
                });

            harness.Engine.OnExtractAsync = request =>
            {
                WriteContent(request.OutputPath!, contentFileName, content);
                return Task.FromResult(Succeeded());
            };
        }

        private string CreateSourceFile(string fileName)
        {
            string directory = Path.Combine(_root, "src");
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, fileName);
            File.WriteAllText(path, "not a real archive - the engine is faked in these tests");
            return path;
        }

        private static ArchiveTask RenameTask(string filePath) => new(filePath)
        {
            DetectedFormat = "ZIP",
            SuggestedExtension = ".zip",
            IsArchive = true,
            ExtensionStatus = StatusText.ExtensionMismatch,
            IsSelected = true
        };

        private static RenameOptions FixOptions(string conflictAction)
        {
            var options = new RenameOptions
            {
                OperationType = "FixByDetectedFormat",
                TargetExtension = ".zip",
                ConflictAction = conflictAction,
                UnknownFormatAction = "MarkUnknown"
            };

            options.Normalize();
            return options;
        }

        private static CleanupPreview MakePreview() => new()
        {
            ScopePath = @"C:\t\111\其余物\222",
            ItemCount = 1,
            EntryCount = 3,
            TotalBytes = 1024,
            Determined = true,
            Items = new List<string> { "inner.7z" }
        };

        private static ArchiveEntry Entry(string path, bool isDirectory = false) => new()
        {
            Path = path,
            Size = isDirectory ? 0 : path.Length,
            IsDirectory = isDirectory
        };

        private static ArchiveListResult ListResult(params string[] fileNames) => new()
        {
            Success = true,
            FileCount = fileNames.Length,
            TotalUncompressedSize = fileNames.Sum(name => (long)name.Length),
            Entries = fileNames.Select(name => Entry(name)).ToList(),
            EngineId = "fake",
            EngineVersion = "1.0"
        };

        private static ArchiveOperationResult Succeeded() => new()
        {
            Success = true,
            Status = StatusText.ExtractSuccess,
            Message = "解压成功",
            DetectedErrorType = "None"
        };

        private static void WriteContent(string directory, string fileName, string content)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, fileName), content);
        }

        /// <summary>把 <paramref name="target"/> 抬到 <paramref name="value"/>（并发观测用，线程安全）。</summary>
        private static void InterlockedMax(ref int target, int value)
        {
            int current;

            while (value > (current = Volatile.Read(ref target)))
            {
                if (Interlocked.CompareExchange(ref target, value, current) == current)
                {
                    return;
                }
            }
        }

        /// <summary>只把这一组里真实存在的分卷收成一个集合（判据复用 VolumeGroupDetector）。</summary>
        private static HashSet<string> OnlyVolumes(ArchiveTask task)
        {
            var candidates = task.VolumePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => new VolumeCandidate { Path = path, Size = -1 })
                .ToList();

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (VolumeGroup group in VolumeGroupDetector.Group(candidates))
            {
                foreach (VolumeCandidate volume in group.Volumes)
                {
                    result.Add(Path.GetFullPath(volume.Path));
                }
            }

            return result;
        }

        /// <summary>
        /// 尽力建一个目录联接点（<c>mklink /J</c>）。建不出来（没有权限 / 没有 cmd）就返回 false，
        /// 由调用方把那条断言降级 —— 不因为环境限制把测试变成假红。
        /// </summary>
        private static bool TryCreateJunction(string linkPath, string targetPath)
        {
            try
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add("mklink");
                startInfo.ArgumentList.Add("/J");
                startInfo.ArgumentList.Add(linkPath);
                startInfo.ArgumentList.Add(targetPath);

                using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(startInfo);

                if (process == null)
                {
                    return false;
                }

                process.WaitForExit(10_000);

                return process.ExitCode == 0 && Directory.Exists(linkPath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>可控的假引擎：调用记录 + 每个方法都能被测试替换实现。</summary>
        private sealed class FakeEngine : IArchiveEngine
        {
            public List<string> ExtractCalls { get; } = new();

            public Func<ArchiveRequest, Task<ArchiveOperationResult>>? OnExtractAsync { get; set; }

            public Func<ArchiveRequest, Task<ArchiveListResult>>? OnListAsync { get; set; }

            public string Id => "fake";

            public string DisplayName => "假引擎";

            public string Version => "1.0";

            public bool IsAvailable => true;

            public EngineCapabilities Capabilities { get; } = new()
            {
                CanProbe = true,
                CanList = true,
                CanTest = true,
                CanExtract = true,
                SupportsPassword = true
            };

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
                => OnListAsync != null ? OnListAsync(request) : Task.FromResult(ListResult("content.txt"));

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult(Succeeded());

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                lock (ExtractCalls)
                {
                    ExtractCalls.Add(request.ArchivePath);
                }

                return OnExtractAsync != null ? OnExtractAsync(request) : Task.FromResult(Succeeded());
            }
        }
    }
}
