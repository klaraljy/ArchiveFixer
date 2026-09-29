using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「界面在撒谎」那个缺陷的回归测试：设置里 <c>ConflictAction = "Ask"</c>（同名冲突处理 = 询问）
    /// 以前**根本不询问** —— <c>Services/PathService.cs</c> 把它和 <c>AutoRename</c> 并到同一个分支，
    /// 用户选了询问、程序静默自动重命名。
    ///
    /// 现在这一档真的会问：第一次撞上"目标已存在"时暂停该任务，弹一次聚合询问
    /// （覆盖 / 跳过 / 自动重命名 × 这一个 / 整批），本批只问一次（决策 D-4：50–200+ 个包逐个问等于不可用），
    /// 覆盖走"先挪到临时名 → 落位 → 再删"两阶段（不变量 3），无界面宿主降级为保守档（绝不覆盖）。
    ///
    /// 全部走**真 MainViewModel + 真解压管线**，只把归档引擎与对话框换成可控替身：
    /// 这样"到底问没问、问了几次、问在动手之前还是之后"都是可断言的事实。
    /// 与 ExtractionPipelineFixTests 同一组（MainViewModel 构造会写进程级静态，必须串行）。
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class ConflictAskTests : IDisposable
    {
        private readonly string _root;

        public ConflictAskTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerConflictAsk", Guid.NewGuid().ToString("N"));
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

        // ================================================================ 1. Ask 档真的会问

        /// <summary>
        /// Ask 档第一次撞上"目标已存在"时必须**真的弹询问**，而且是**先暂停任务、再动手**。
        ///
        /// 判据用事件顺序（ask → extract）而不是"结果对不对"：旧实现也能跑出一个"看起来正常"的结果
        /// （静默改名），只有顺序能证明"程序停下来问过"。
        /// 同一批里非 Ask 档一次都不许问 —— 那是默认路径，不该被打断。
        /// </summary>
        [Fact]
        public async Task Ask档_第一次冲突真的会询问_并且问在动手之前()
        {
            var order = new List<string>();

            var dialog = new RecordingDialogService
            {
                OnConflictAsync = (_, _) =>
                {
                    lock (order)
                    {
                        order.Add("ask");
                    }

                    return Task.FromResult<ConflictDecision?>(ConflictDecision.Once(ConflictChoice.AutoRename));
                }
            };

            Harness harness = CreateHarness(dialog, settings =>
            {
                settings.ConflictAction = ConflictActions.Ask;
                settings.ExtractToOriginalDirectory = false;
                settings.CustomOutputDirectory = Path.Combine(_root, "out");
                settings.KeepArchiveNameFolder = true;
            });

            ArchiveTask task = AddTask(harness, CreateSourceFile("pack.7z"));

            string requested = harness.PathService.BuildOutputPath(task, DefaultOptions(harness));

            // 现场：目标目录已存在且非空（重跑一次、上次失败留下的目录都会这样）。
            Directory.CreateDirectory(requested);
            File.WriteAllText(Path.Combine(requested, "上次留下的旧文件.txt"), "old");

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                lock (order)
                {
                    order.Add("extract");
                }

                WriteContent(request.OutputPath!, "content.txt", "new");
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult("content.txt"));

            await harness.Coordinator.StartExtractAsync().WaitAsync(TimeSpan.FromSeconds(120));

            Assert.Single(dialog.Prompts);

            // 冲突时**确实调用了询问**，而且是在"还没开始解压"之前（任务真的被暂停过）。
            Assert.Equal(new[] { "ask", "extract" }, order);

            // 问的必须是这次真正的冲突现场，不能是一句泛泛的提示。
            Assert.Contains("输出目录已存在且非空", dialog.Prompts[0].Message, StringComparison.Ordinal);
            Assert.Contains(requested, dialog.Prompts[0].Detail, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // 用户选的是"这一个自动重命名"：落点换成了 名字(1)，旧目录一个字节都没动。
            Assert.NotEqual(requested, task.OutputPath);
            Assert.True(File.Exists(Path.Combine(requested, "上次留下的旧文件.txt")), "旧目录里的文件不许被清掉");
            Assert.Equal("old", File.ReadAllText(Path.Combine(requested, "上次留下的旧文件.txt")));
        }

        /// <summary>
        /// **一键处理档 + 设置里选了「询问」**：批中间照样一次都不许问 —— 用户 2026-09-27 两次拍板的红线
        /// （"一键处理期间零弹窗，唯一例外是批末汇总"）。
        ///
        /// <para>为什么单列一条（2026-09-29 复核逮到）：<c>SuppressDecisionPromptsForOneClickRun()</c>
        /// 原本排在 <c>ResetBatchConflictState()</c> **之前**，而后者会把 <c>_conflictPromptUnavailable</c>
        /// 置回 false —— 先抑制后清零等于没抑制，选「询问」的人一键跑批时仍会撞上那个聚合询问框。
        /// 这一条钉住顺序：抑制必须活过本批的状态清零。</para>
        /// </summary>
        [Fact]
        public async Task 一键档_设置里选了询问_批中间也一次都不问()
        {
            var dialog = new RecordingDialogService();

            Harness harness = CreateHarness(dialog, settings =>
            {
                settings.ConflictAction = ConflictActions.Ask;
                settings.ExtractToOriginalDirectory = false;
                settings.CustomOutputDirectory = Path.Combine(_root, "out-oneclick");
                settings.KeepArchiveNameFolder = true;
            });

            ArchiveTask task = AddTask(harness, CreateSourceFile("pack-oneclick.7z"));

            string requested = harness.PathService.BuildOutputPath(task, DefaultOptions(harness));

            Directory.CreateDirectory(requested);
            File.WriteAllText(Path.Combine(requested, "上次留下的旧文件.txt"), "old");

            harness.Engine.OnExtractAsync = _ => Task.Run(() =>
            {
                WriteContent(_.OutputPath!, "content.txt", "new");
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult("content.txt"));

            await harness.Coordinator.StartExtractForOneClickAsync().WaitAsync(TimeSpan.FromSeconds(120));

            // ① 一个字都没问
            Assert.Empty(dialog.Prompts);

            // ② 该问的事按保守档办了，而且**写进日志**（一键档的规矩是"少弹窗、不少判定"）
            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.NotEqual(requested, task.OutputPath);
            Assert.True(File.Exists(Path.Combine(requested, "上次留下的旧文件.txt")), "旧目录里的文件不许被清掉");

            Assert.Contains(
                harness.Log.Logs.Select(item => item.DisplayText),
                text => text.Contains("本次不弹任何确认框", StringComparison.Ordinal));
        }

        /// <summary>
        /// 非 Ask 档（默认 AutoRename）一个字都不许问：默认路径绝不能被打断，
        /// 而且行为必须与改动前一致（自动改用 <c>名字(1)</c>）。
        /// </summary>
        [Fact]
        public async Task 默认档_一次都不询问_而且行为与改动前一致()
        {
            var dialog = new RecordingDialogService();

            Harness harness = CreateHarness(dialog, settings =>
            {
                settings.ConflictAction = ConflictActions.AutoRename;
                settings.ExtractToOriginalDirectory = false;
                settings.CustomOutputDirectory = Path.Combine(_root, "out");
                settings.KeepArchiveNameFolder = true;
            });

            ArchiveTask task = AddTask(harness, CreateSourceFile("pack.7z"));

            string requested = harness.PathService.BuildOutputPath(task, DefaultOptions(harness));

            Directory.CreateDirectory(requested);
            File.WriteAllText(Path.Combine(requested, "上次留下的旧文件.txt"), "old");

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                WriteContent(request.OutputPath!, "content.txt", "new");
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult("content.txt"));

            await harness.Coordinator.StartExtractAsync().WaitAsync(TimeSpan.FromSeconds(120));

            Assert.Empty(dialog.Prompts);
            Assert.Equal(StatusText.ExtractSuccess, task.Status);
            Assert.NotEqual(requested, task.OutputPath);
            Assert.True(File.Exists(Path.Combine(requested, "上次留下的旧文件.txt")));
        }

        // ================================================================ 2. 选了"全部 X"就不再问第二次

        /// <summary>
        /// 用户答「覆盖全部」之后，**本批内不得再弹第二次**（决策 D-4：50–200+ 个包逐个问等于不可用），
        /// 而且覆盖必须真的发生、必须留痕（哪个落点被顶掉要能在日志里查出来）。
        /// </summary>
        [Fact]
        public async Task 选了覆盖全部之后_本批后续冲突不再询问_且覆盖有日志留痕()
        {
            var dialog = new RecordingDialogService
            {
                OnConflictAsync = (_, _) => Task.FromResult<ConflictDecision?>(ConflictDecision.ForAll(ConflictChoice.Overwrite))
            };

            Harness harness = CreateHarness(dialog, settings =>
            {
                settings.ConflictAction = ConflictActions.Ask;
                settings.ExtractToOriginalDirectory = false;
                settings.CustomOutputDirectory = Path.Combine(_root, "out");
                settings.KeepArchiveNameFolder = true;
            });

            ArchiveTask first = AddTask(harness, CreateSourceFile("111.7z"));
            ArchiveTask second = AddTask(harness, CreateSourceFile("222.7z"));

            var destinations = new List<string>();

            foreach (ArchiveTask task in new[] { first, second })
            {
                string destination = harness.PathService.BuildOutputPath(task, DefaultOptions(harness));

                destinations.Add(destination);

                // 两个任务都撞上"目标已存在且非空"：第一个问一次，第二个不许再问。
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(destination, "content.txt"), "old");
            }

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                WriteContent(request.OutputPath!, "content.txt", "new");
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult("content.txt"));

            await harness.Coordinator.StartExtractAsync().WaitAsync(TimeSpan.FromSeconds(120));

            // ① 两个包、两个冲突，**只弹了一次**。
            Assert.Single(dialog.Prompts);

            Assert.Equal(StatusText.ExtractSuccess, first.Status);
            Assert.Equal(StatusText.ExtractSuccess, second.Status);

            // ② 覆盖真的发生了：内容被顶掉，且没有多出"content(1).txt"这种改名产物。
            foreach (string destination in destinations)
            {
                Assert.Equal("new", File.ReadAllText(Path.Combine(destination, "content.txt")));
                Assert.False(File.Exists(Path.Combine(destination, "content(1).txt")));
            }

            // ③ 覆盖留痕：日志里查得出"按你的选择覆盖 + 具体落点"，并写明旧文件挪到过临时名
            //    （两阶段落位，不是先删后移）；任务结论里也要能看到覆盖了哪几项。
            Assert.Contains("覆盖了 1 项", first.VerifyMessage, StringComparison.Ordinal);

            Assert.Contains(
                harness.Log.Logs.Select(x => x.Message),
                message => message.Contains("按你的选择覆盖", StringComparison.Ordinal) &&
                           message.Contains("content.txt", StringComparison.Ordinal));

            Assert.Contains(
                harness.Log.Logs.Select(x => x.Message),
                message => message.Contains(".af-vacating", StringComparison.Ordinal));

            Assert.Contains(
                harness.Log.Logs.Select(x => x.Message),
                message => message.Contains("不再询问", StringComparison.Ordinal));
        }

        // ================================================================ 3. 两阶段覆盖（绝不先删后移）

        /// <summary>
        /// <b>落位那一步失败时，旧文件必须还在</b> —— 这是"先移到临时名 → 再删 → 再落位"
        /// 与"先 File.Delete 再 Move"的分水岭（不变量 3）。
        ///
        /// 先删后移的话，走到这里旧文件已经没了（新文件又没搬成），用户丢掉的是他本来想保留的那一份。
        /// </summary>
        [Fact]
        public void 两阶段覆盖落位_落位失败时旧文件挪回原位_绝不是先删后移()
        {
            var pathService = new PathService { DataRootDirectory = _root };

            string directory = Path.Combine(_root, "landing-fail");
            Directory.CreateDirectory(directory);

            string target = Path.Combine(directory, "content.txt");
            File.WriteAllText(target, "old");

            // 源故意不存在：模拟"腾位成功、落位这一步失败"（跨卷失败 / 被占用 / 权限）。
            string source = Path.Combine(directory, "missing-source.txt");

            bool landed = pathService.TryOverwriteLanding(source, target, isDirectory: false, out string error, out _);

            Assert.False(landed);
            Assert.Contains("落位失败", error, StringComparison.Ordinal);
            Assert.True(File.Exists(target), "旧文件必须还在（先删后移的实现到这里已经把它删了）");
            Assert.Equal("old", File.ReadAllText(target));
            Assert.Empty(Directory.GetFiles(directory, "*.af-vacating*"));
        }

        /// <summary>两阶段覆盖的成功路径：旧文件在**落位成功之后**才被删，临时名收干净，且说明里留了痕。</summary>
        [Fact]
        public void 两阶段覆盖落位_成功路径_旧文件在落位之后才删且临时名收干净()
        {
            var pathService = new PathService { DataRootDirectory = _root };

            string directory = Path.Combine(_root, "landing-ok");
            Directory.CreateDirectory(directory);

            string target = Path.Combine(directory, "content.txt");
            File.WriteAllText(target, "old");

            string source = Path.Combine(directory, "new-content.txt");
            File.WriteAllText(source, "new");

            bool landed = pathService.TryOverwriteLanding(source, target, isDirectory: false, out string error, out string note);

            Assert.True(landed, error);
            Assert.Equal("new", File.ReadAllText(target));
            Assert.False(File.Exists(source));
            Assert.Contains(".af-vacating", note, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(directory, "*.af-vacating*"));
        }

        /// <summary>类型对不上（文件撞目录）时拒绝覆盖：绝不为了给一个文件让位去删一棵目录树。</summary>
        [Fact]
        public void 覆盖不碰类型不同的同名目标()
        {
            var pathService = new PathService { DataRootDirectory = _root };

            string directory = Path.Combine(_root, "landing-kind");
            Directory.CreateDirectory(directory);

            string target = Path.Combine(directory, "content");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "里面还有东西.txt"), "must survive");

            string source = Path.Combine(directory, "content-file.txt");
            File.WriteAllText(source, "new");

            bool landed = pathService.TryOverwriteLanding(source, target, isDirectory: false, out string error, out _);

            Assert.False(landed);
            Assert.Contains("类型不同", error, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(target, "里面还有东西.txt")), "同名目录里的东西不许被删");
            Assert.True(File.Exists(source), "源条目也不许丢");
        }

        /// <summary>
        /// 定稿搬运时的同名冲突同样会问（不只是"输出目录已存在"那一次），
        /// 选了覆盖就走两阶段：目标换成新内容、不产生 <c>名字(1)</c>、日志里能看到覆盖留痕。
        /// </summary>
        [Fact]
        public async Task Ask档_定稿同名冲突也会问_选覆盖时走两阶段并留痕()
        {
            var dialog = new RecordingDialogService
            {
                OnConflictAsync = (_, _) => Task.FromResult<ConflictDecision?>(ConflictDecision.Once(ConflictChoice.Overwrite))
            };

            // 落点就是源目录（**手动档「解压到当前文件夹」**：src\pack\pack.7z → src\pack），
            // 所以上面那次"输出目录已存在"不成立（那条规则对源目录让开，见 ExtractionCoordinator 的说明），
            // 冲突要留到定稿那一步才出现。
            //
            // ⚠ 2026-09-27 改：以前这里用**场景 B 塌缩**来造"落点 == 源目录"，那一档已经退役
            // （一律不塌缩）—— 现在唯一会落到源目录的路径就是那颗手动按钮，
            // 于是这些用例改成显式走 manual flatten（`extractIntoSourceFolder: true`）。
            Harness harness = CreateHarness(dialog, settings =>
            {
                settings.ConflictAction = ConflictActions.Ask;
                settings.ExtractToOriginalDirectory = true;
                settings.KeepArchiveNameFolder = true;
            });

            string source = CreateFlattenSourceFile("pack");
            string sourceDirectory = Path.GetDirectoryName(source)!;

            // 源目录里已经有一个同名文件（重跑一次、上次留下的产物都会这样）。
            string existing = Path.Combine(sourceDirectory, "content.txt");
            File.WriteAllText(existing, "old");

            ArchiveTask task = AddTask(harness, source);

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                WriteContent(request.OutputPath!, "content.txt", "new");
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult("content.txt"));

            await harness.Coordinator.StartExtractAsync(extractIntoSourceFolder: true).WaitAsync(TimeSpan.FromSeconds(120));

            Assert.Single(dialog.Prompts);

            // 聚合提示必须说清"有几个是内容物"（其余物撞名不该让用户误以为内容被顶掉）。
            Assert.Contains("内容物", dialog.Prompts[0].Message, StringComparison.Ordinal);
            Assert.Contains("content.txt", dialog.Prompts[0].Detail, StringComparison.Ordinal);

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // 覆盖生效：新内容顶掉旧内容，且没有多出一份"content(1).txt"。
            Assert.Equal("new", File.ReadAllText(existing));
            Assert.False(File.Exists(Path.Combine(sourceDirectory, "content(1).txt")));

            // 两阶段留痕：旧文件先被挪到 .af-vacating，落位成功后才删。
            Assert.Contains(
                harness.Log.Logs.Select(x => x.Message),
                message => message.Contains(".af-vacating", StringComparison.Ordinal));
        }

        // ================================================================ 4. 无 UI 宿主降级

        /// <summary>
        /// <c>Application.Current == null</c>（单元测试 / 控制台宿主）时：
        /// **不弹窗、不死等**，结果必须是保守档 —— 自动重命名落位，已存在的文件一个字节都不动，
        /// **绝不默认覆盖**（不变量 3），并且降级原因要写进日志。
        /// </summary>
        [Fact]
        public async Task 无UI宿主_不弹窗不死等_按保守档落位而不是覆盖()
        {
            DialogService.ClearFallbackLog();

            // 前提取自运行环境：本测试进程里没有 WPF 应用（与 UiV2Tests 的前置断言同一口径）。
            Assert.Null(System.Windows.Application.Current);

            var dialog = new DialogService();

            Harness harness = CreateHarness(dialog, settings =>
            {
                settings.ConflictAction = ConflictActions.Ask;
                settings.ExtractToOriginalDirectory = true;
                settings.KeepArchiveNameFolder = true;
            });

            string source = CreateFlattenSourceFile("pack");
            string sourceDirectory = Path.GetDirectoryName(source)!;
            string existing = Path.Combine(sourceDirectory, "content.txt");

            File.WriteAllText(existing, "old");

            ArchiveTask task = AddTask(harness, source);

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                WriteContent(request.OutputPath!, "content.txt", "new");
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult("content.txt"));

            // 有界等待：真出现"死等"这条断言会红，而不是把整个测试挂住。
            await harness.Coordinator.StartExtractAsync(extractIntoSourceFolder: true).WaitAsync(TimeSpan.FromSeconds(120));

            // 保守 = 自动重命名：内容照样落地（不丢产物），旧文件一个字节都不动。
            Assert.Equal("old", File.ReadAllText(existing));
            Assert.Equal("new", File.ReadAllText(Path.Combine(sourceDirectory, "content(1).txt")));

            Assert.Equal(StatusText.ExtractSuccess, task.Status);

            // 降级必须留痕：对话框服务写一条，应用日志也写一条（用户事后能看出"这里为什么没问"）。
            Assert.Contains(
                DialogService.FallbackLog,
                entry => entry.Contains("ShowConflictDecision", StringComparison.Ordinal));

            Assert.Contains(
                harness.Log.Logs.Select(x => x.Message),
                message => message.Contains("问不到答案", StringComparison.Ordinal));
        }

        // ================================================================ 5. 询问期间取消

        /// <summary>
        /// 询问还开着的时候用户按了「取消当前」：**不覆盖、不落位**，任务必须落成「已取消」，
        /// 绝不许显示成功（不变量 6）。这条最容易写错：把取消当成"没问到答案"继续往下跑。
        /// </summary>
        [Fact]
        public async Task 询问期间取消_不覆盖且任务标已取消()
        {
            var dialog = new RecordingDialogService
            {
                OnConflictAsync = async (_, cancellationToken) =>
                {
                    // 模拟"框还开着、用户什么都没点"：挂在取消令牌上，永不返回答案。
                    try
                    {
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // 被取消是预期路径。
                    }

                    return null;
                }
            };

            Harness harness = CreateHarness(dialog, settings =>
            {
                settings.ConflictAction = ConflictActions.Ask;
                settings.ExtractToOriginalDirectory = true;
                settings.KeepArchiveNameFolder = true;
            });

            string source = CreateFlattenSourceFile("pack");
            string sourceDirectory = Path.GetDirectoryName(source)!;
            string existing = Path.Combine(sourceDirectory, "content.txt");

            File.WriteAllText(existing, "old");

            ArchiveTask task = AddTask(harness, source);

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                WriteContent(request.OutputPath!, "content.txt", "new");
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult("content.txt"));

            Task pipeline = harness.Coordinator.StartExtractAsync(extractIntoSourceFolder: true);

            await WaitForAsync(() => dialog.Prompts.Count > 0, TimeSpan.FromSeconds(60), "询问没有被调用");

            // 用户在询问还开着的时候点了「取消当前」。
            harness.Coordinator.CancelCurrentTask();

            await pipeline.WaitAsync(TimeSpan.FromSeconds(120));

            Assert.Equal(StatusText.Cancelled, task.Status);
            Assert.NotEqual(StatusText.ExtractSuccess, task.Status);

            // 取消之后一个字节都不许落位：旧文件原样，也没有多出改名产物。
            Assert.Equal("old", File.ReadAllText(existing));
            Assert.False(File.Exists(Path.Combine(sourceDirectory, "content(1).txt")));
        }

        // ================================================================ 5.6 「取消本批」

        /// <summary>
        /// 询问里选「取消本批」= 既有取消语义（停止后续 + 取消当前）：一个包都不许再解，
        /// 当前任务落成「已取消」，已有的同名文件一个字节都不动，绝不显示成功。
        /// </summary>
        [Fact]
        public async Task 选取消本批_不再解任何包且任务标已取消()
        {
            var dialog = new RecordingDialogService
            {
                OnConflictAsync = (_, _) => Task.FromResult<ConflictDecision?>(ConflictDecision.Once(ConflictChoice.CancelBatch))
            };

            Harness harness = CreateHarness(dialog, settings =>
            {
                settings.ConflictAction = ConflictActions.Ask;
                settings.ExtractToOriginalDirectory = false;
                settings.CustomOutputDirectory = Path.Combine(_root, "out");
                settings.KeepArchiveNameFolder = true;
            });

            ArchiveTask first = AddTask(harness, CreateSourceFile("111.7z"));
            ArchiveTask second = AddTask(harness, CreateSourceFile("222.7z"));

            var destinations = new List<string>();

            foreach (ArchiveTask task in new[] { first, second })
            {
                string destination = harness.PathService.BuildOutputPath(task, DefaultOptions(harness));

                destinations.Add(destination);

                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(destination, "content.txt"), "old");
            }

            harness.Engine.OnExtractAsync = request => Task.Run(() =>
            {
                WriteContent(request.OutputPath!, "content.txt", "new");
                return Succeeded();
            });

            harness.Engine.OnListAsync = _ => Task.FromResult(ListResult("content.txt"));

            await harness.Coordinator.StartExtractAsync().WaitAsync(TimeSpan.FromSeconds(120));

            Assert.Single(dialog.Prompts);

            // 取消本批 = 连当前这个包都不解（Engine.ExtractAsync 是"动手"的判据）。
            Assert.Empty(harness.Engine.ExtractCalls);

            Assert.Equal(StatusText.Cancelled, first.Status);
            Assert.NotEqual(StatusText.ExtractSuccess, second.Status);

            foreach (string destination in destinations)
            {
                Assert.Equal("old", File.ReadAllText(Path.Combine(destination, "content.txt")));
            }
        }

        // ================================================================ 5.5 六档选项 → 决定 的映射
        /// <summary>
        /// 询问框的六个选项必须一一映射到正确的决定（界面说一套、代码做一套的另一种形态就是这里接错线）：
        /// 覆盖 / 跳过 / 自动重命名 × 「只这一个 / 整批都照此办理」。
        /// 映射是 <see cref="DialogService.MapConflictAnswer"/> 里的纯函数，可以脱离窗口单测。
        /// </summary>
        [Theory]
        [InlineData(System.Windows.MessageBoxResult.Yes, false, ConflictChoice.Overwrite, false)]
        [InlineData(System.Windows.MessageBoxResult.Yes, true, ConflictChoice.Overwrite, true)]
        [InlineData(System.Windows.MessageBoxResult.No, false, ConflictChoice.Skip, false)]
        [InlineData(System.Windows.MessageBoxResult.No, true, ConflictChoice.Skip, true)]
        [InlineData(System.Windows.MessageBoxResult.Cancel, false, ConflictChoice.AutoRename, false)]
        [InlineData(System.Windows.MessageBoxResult.Cancel, true, ConflictChoice.AutoRename, true)]
        public void 六档询问_按钮与勾选框映射到正确的决定(
            System.Windows.MessageBoxResult result,
            bool applyToAll,
            ConflictChoice expected,
            bool expectedApplyToAll)
        {
            // DialogResult = false 是"按钮点的"（三条按钮路径都会显式写它）。
            ConflictDecision decision = DialogService.MapConflictAnswer(result, dialogResult: false, applyToAll);

            Assert.Equal(expected, decision.Choice);
            Assert.Equal(expectedApplyToAll, decision.ApplyToAll);
        }

        /// <summary>
        /// ✕ 关窗 = 取消本批（按钮从没被点过，所以 DialogResult 一直是 null）。
        /// 它与"自动重命名"共用 Cancel 这个返回值，靠 DialogResult 区分 —— 这条区分一旦写错，
        /// 用户想取消整批却会得到"静默自动重命名"。
        /// </summary>
        [Fact]
        public void 六档询问_关窗是取消本批而不是自动重命名()
        {
            ConflictDecision decision = DialogService.MapConflictAnswer(
                System.Windows.MessageBoxResult.Cancel,
                dialogResult: null,
                applyToAll: false);

            Assert.Equal(ConflictChoice.CancelBatch, decision.Choice);

            // 勾了"整批"再关窗也还是取消本批：不能因为勾了框就把取消解读成"全部自动重命名"。
            ConflictDecision decisionWithOption = DialogService.MapConflictAnswer(
                System.Windows.MessageBoxResult.Cancel,
                dialogResult: null,
                applyToAll: true);

            Assert.Equal(ConflictChoice.CancelBatch, decisionWithOption.Choice);
        }

        // ================================================================ 6. 钉死"Ask 不再落进 AutoRename 分支"        /// <summary>
        /// 一条就钉住的回归：<c>Ask</c> 档**不再落进 AutoRename 分支**。
        ///
        /// 旧实现（Services/PathService.cs:356/385，两个 switch 里的 <c>"Ask" =&gt; AutoRename</c>）
        /// 让"询问"与"自动重命名"产出**完全相同**的结果，这就是"界面说一套、代码做一套"。
        /// 现在：没有答案的 Ask 绝不静默改名（保守到"跳过"，原路径返回），有答案的 Ask 才按答案走。
        /// </summary>
        [Fact]
        public void Ask档不再落进AutoRename分支()
        {
            var pathService = new PathService { DataRootDirectory = _root };

            string directory = Path.Combine(_root, "ask-branch");
            Directory.CreateDirectory(directory);

            string existing = Path.Combine(directory, "content.txt");
            File.WriteAllText(existing, "old");

            string autoRenamed = SafePathHelper.AutoRenameFilePath(existing);

            // ① 没拿到答案的 Ask：不是 AutoRename 的产物（旧实现这里返回的正是 content(1).txt）。
            Assert.NotEqual(autoRenamed, pathService.ResolveFileConflict(existing, ConflictActions.Ask));

            ConflictResolution unanswered = pathService.ResolveConflict(
                existing,
                ConflictTargetKind.File,
                ConflictActions.Ask);

            Assert.NotEqual(ConflictChoice.AutoRename, unanswered.Choice);
            Assert.Equal(ConflictChoice.Skip, unanswered.Choice);
            Assert.Equal(existing, unanswered.TargetPath);
            Assert.False(unanswered.DecidedByUser);

            // ② 拿到答案的 Ask：严格按用户的答案走（覆盖 / 跳过都不改路径，自动重命名才改名）。
            Assert.Equal(
                ConflictChoice.Overwrite,
                pathService.ResolveConflict(existing, ConflictTargetKind.File, ConflictActions.Ask, ConflictDecision.Once(ConflictChoice.Overwrite)).Choice);

            Assert.Equal(
                existing,
                pathService.ResolveConflict(existing, ConflictTargetKind.File, ConflictActions.Ask, ConflictDecision.Once(ConflictChoice.Skip)).TargetPath);

            Assert.Equal(
                autoRenamed,
                pathService.ResolveConflict(existing, ConflictTargetKind.File, ConflictActions.Ask, ConflictDecision.Once(ConflictChoice.AutoRename)).TargetPath);

            // ③ 其余三档的行为一个字都没变（默认档仍然自动改名，绝不覆盖）。
            Assert.Equal(autoRenamed, pathService.ResolveFileConflict(existing, ConflictActions.AutoRename));
            Assert.Equal(existing, pathService.ResolveFileConflict(existing, ConflictActions.Skip));
            Assert.Equal(existing, pathService.ResolveFileConflict(existing, ConflictActions.Overwrite));

            // ④ 空 / 非法值仍然回落默认档（不变量 3：默认不得覆盖）。
            Assert.Equal(autoRenamed, pathService.ResolveFileConflict(existing, string.Empty));
            Assert.Equal(autoRenamed, pathService.ResolveFileConflict(existing, "看不懂的档位"));
            Assert.False(ConflictActions.IsAsk("看不懂的档位"));
        }

        // ================================================================ 装配

        private sealed class Harness
        {
            public Harness(
                MainViewModel vm,
                FakeEngine engine,
                ExtractionCoordinator coordinator,
                LogService log,
                PathService pathService)
            {
                Vm = vm;
                Engine = engine;
                Coordinator = coordinator;
                Log = log;
                PathService = pathService;
            }

            public MainViewModel Vm { get; }

            public FakeEngine Engine { get; }

            public ExtractionCoordinator Coordinator { get; }

            public LogService Log { get; }

            public PathService PathService { get; }
        }

        /// <summary>
        /// 可记录的对话框替身：把"到底问没问"变成可断言的事实。
        /// 只覆盖冲突询问这一个方法，其余提示走真实实现（本进程无 UI 宿主，只会写降级日志）。
        /// </summary>
        private sealed class RecordingDialogService : DialogService
        {
            public List<ConflictPrompt> Prompts { get; } = new();

            public Func<ConflictPrompt, CancellationToken, Task<ConflictDecision?>>? OnConflictAsync { get; set; }

            public override Task<ConflictDecision?> ShowConflictDecisionAsync(
                ConflictPrompt prompt,
                CancellationToken cancellationToken = default)
            {
                lock (Prompts)
                {
                    Prompts.Add(prompt);
                }

                return OnConflictAsync != null
                    ? OnConflictAsync(prompt, cancellationToken)
                    : Task.FromResult<ConflictDecision?>(ConflictDecision.Conservative);
            }
        }

        private Harness CreateHarness(DialogService dialogService, Action<AppSettings>? configure = null)
        {
            string dataRoot = Path.Combine(_root, "data");
            string sourceRoot = Path.Combine(_root, "src");

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(sourceRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();

            settings.CacheRootDirectory = dataRoot;
            settings.RecursionMode = "SingleLayer";
            settings.AutoScanAfterDrop = false;

            // 源包档位固定"留在原地"：这组测的是同名冲突，源包搬运会把目录搅浑（另有专测）。
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);

            configure?.Invoke(settings);
            settingsService.Save(settings);

            var engine = new FakeEngine();
            var passwordService = new PasswordService();
            var logService = new LogService(pathService);

            // MainViewModel 的构造会顺手写两个进程级静态（7z 路径、递归工作区根目录）：先存后还原。
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

            var coordinator = new ExtractionCoordinator(vm, engine, passwordService, pathService, dialogService);

            return new Harness(vm, engine, coordinator, logService, pathService);
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

        private string CreateSourceFile(string fileName)
        {
            string directory = Path.Combine(_root, "src");
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, fileName);
            File.WriteAllText(path, "not a real archive - the engine is faked in these tests");
            return path;
        }

        /// <summary>
        /// 造一个**落点就是它自己所在目录**的源包：<c>src\pack\pack.7z</c>，
        /// 跑的时候走**手动档「解压到当前文件夹」**（`extractIntoSourceFolder: true`）。
        /// 定稿阶段的同名冲突只在这种形状下才露面 ——
        /// "输出目录已存在且非空就改名"那条规则对源目录让开（见 ExtractionCoordinator 的说明）。
        ///
        /// <para>
        /// ⚠ 2026-09-24 改一次：以前这里用 <c>ExtractToOriginalDirectory=true + KeepArchiveNameFolder=false</c>
        /// （"解压到压缩包所在目录"）来制造"落点 == 源目录"，用户第 13 条把那两档删掉之后改用**塌缩**。
        /// </para>
        /// <para>
        /// ⚠ 2026-09-27 再改一次：**塌缩也退役了**（一律不塌缩），现在唯一会落到源目录的路径
        /// 就是①页那颗「解压到当前文件夹」→ 这里改成那个入口（用户 2026-09-27 新增的手动档）。
        /// </para>
        /// </summary>
        private string CreateFlattenSourceFile(string packageName)
        {
            string directory = Path.Combine(_root, "src", packageName);
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, packageName + ".7z");
            File.WriteAllText(path, "not a real archive - the engine is faked in these tests");
            return path;
        }

        private static ExtractOptions DefaultOptions(Harness harness)
        {
            var options = new ExtractOptions
            {
                ExtractToOriginalDirectory = harness.Vm.Settings.ExtractToOriginalDirectory,
                CustomOutputDirectory = harness.Vm.Settings.CustomOutputDirectory,
                KeepArchiveNameFolder = harness.Vm.Settings.KeepArchiveNameFolder
            };

            options.Normalize();
            return options;
        }

        /// <summary>把内容"解"到暂存目录（假引擎的替身动作，真实 7z 也是在后台线程写盘）。</summary>
        private static void WriteContent(string directory, string fileName, string content)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, fileName), content);
        }

        /// <summary>
        /// 假清单：只声明"有哪些条目"，**不声明字节数**（0 = 清单没给出确定大小 ——
        /// 真实 7z 对 `-mhe` / 读不出大小的清单就是这么报的，用户日志里出现过「清单 0 个文件 / 解压后 0 字节」）。
        ///
        /// <para>
        /// ⚠ 2026-09-24 改：以前这里拿**文件名长度**当字节数（<c>"content.txt".Length</c> = 11），
        /// 而 <see cref="WriteContent"/> 真写出去的是 3 个字节 —— 两者对不上，产物校验判否。
        /// 判否本身没错，但**校验判否现在会直接顶掉「解压成功」**（用户 2026-09-24 的铁证修复），
        /// 于是这一组"同名冲突要不要问、覆盖要不要留痕"的用例全都会挂在与被测行为无关的地方。
        /// 声明"不知道大小"才是这批假引擎真正想表达的东西。
        /// </para>
        /// </summary>
        private static ArchiveListResult ListResult(params string[] fileNames)
        {
            return new ArchiveListResult
            {
                Success = true,
                FileCount = fileNames.Length,
                TotalUncompressedSize = 0,
                Entries = fileNames
                    .Select(name => new ArchiveEntry { Path = name, Size = 0 })
                    .ToList(),
                EngineId = "fake",
                EngineVersion = "1.0"
            };
        }

        private static ArchiveOperationResult Succeeded()
        {
            return new ArchiveOperationResult
            {
                Success = true,
                Status = StatusText.ExtractSuccess,
                Message = "解压成功",
                DetectedErrorType = "None"
            };
        }

        /// <summary>异步等待一个条件成立（不阻塞测试线程：阻塞会让管线的 await 续体没机会跑）。</summary>
        private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string failureMessage)
        {
            DateTime deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(25);
            }

            Assert.Fail($"{failureMessage}（等了 {timeout.TotalSeconds:F0} 秒）");
        }

        /// <summary>可控的假引擎：调用记录 + 每个方法都能被测试替换实现。</summary>
        private sealed class FakeEngine : IArchiveEngine
        {
            /// <summary>真正被要求解压过的源文件（"有没有动手"的判据）。</summary>
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
            {
                return Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });
            }

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return OnListAsync != null
                    ? OnListAsync(request)
                    : Task.FromResult(ListResult("content.txt"));
            }

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(Succeeded());
            }

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                lock (ExtractCalls)
                {
                    ExtractCalls.Add(request.ArchivePath);
                }

                return OnExtractAsync != null
                    ? OnExtractAsync(request)
                    : Task.FromResult(Succeeded());
            }
        }
    }
}
