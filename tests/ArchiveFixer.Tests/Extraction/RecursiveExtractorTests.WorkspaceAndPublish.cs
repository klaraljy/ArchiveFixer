using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    public partial class RecursiveExtractorTests
    {
        // ────────────────────────────── 工作区 ──────────────────────────────

        [Fact]
        public void 工作区_层目录按层号递进()
        {
            var workspace = new ExtractionWorkspace(_root, "task-a");

            WorkspaceLayer first = workspace.CreateNextLayer(Path.Combine(_root, "a.zip"));
            WorkspaceLayer second = workspace.CreateNextLayer(Path.Combine(_root, "b.7z"));

            Assert.Equal(0, first.Depth);
            Assert.Equal(1, second.Depth);
            Assert.EndsWith("layer-000", first.DirectoryPath, StringComparison.Ordinal);
            Assert.EndsWith("layer-001", second.DirectoryPath, StringComparison.Ordinal);
            Assert.EndsWith("output", first.OutputPath, StringComparison.Ordinal);
            Assert.True(Directory.Exists(first.OutputPath));
            Assert.True(Directory.Exists(second.OutputPath));
            Assert.Equal(2, workspace.Layers.Count);
        }

        [Fact]
        public void 工作区_taskId带上级目录时被清洗且删不到root之外的东西()
        {
            string root = Path.Combine(_root, "ws-root");
            Directory.CreateDirectory(root);

            // root 的兄弟目录：它就是"root 之外的任何东西"的代表。
            string sibling = Path.Combine(_root, "sibling");
            Directory.CreateDirectory(sibling);
            File.WriteAllText(Path.Combine(sibling, "keep.txt"), "不能被删掉\n", Utf8NoBom);

            // taskId 写成 ".."：如果实现不做文件名清洗、直接 Path.Combine(root, "..")，
            // 工作区就会落在 root 之外，Cleanup 会去删用户的目录 —— 这是本用例要盯住的失效模式。
            var workspace = new ExtractionWorkspace(root, "..");

            // ① 工作区目录必须仍然是 root 的**直接子目录**（清洗后不可能含路径分隔符）。
            string taskDirectoryName = Path.GetFileName(
                workspace.TaskDirectory.TrimEnd(Path.DirectorySeparatorChar));
            Assert.DoesNotContain("..", taskDirectoryName, StringComparison.Ordinal);
            Assert.Equal(
                Path.GetFullPath(root),
                Path.GetFullPath(Path.GetDirectoryName(workspace.TaskDirectory)!));

            workspace.CreateNextLayer(Path.Combine(_root, "x.zip"));
            Assert.True(Directory.Exists(workspace.TaskDirectory));

            workspace.Cleanup();

            // ② root 之外的东西一个都不能少。
            Assert.True(Directory.Exists(sibling), "工作区之外的目录不该被删");
            Assert.True(File.Exists(Path.Combine(sibling, "keep.txt")), "工作区之外的文件不该被删");
            Assert.True(Directory.Exists(root), "工作区根目录不该被删");
        }

        [Fact]
        public void 工作区_Publish同名文件不覆盖且把改名记进Message()
        {
            string source = Path.Combine(_root, "pub-src");
            Directory.CreateDirectory(Path.Combine(source, "inner"));
            File.WriteAllText(Path.Combine(source, "inner", "a.txt"), "新内容", Utf8NoBom);

            var workspace = new ExtractionWorkspace(Path.Combine(_root, "pub-ws"), "task-pub");
            WorkspaceLayer layer = workspace.CreateNextLayer(Path.Combine(_root, "pack.zip"));
            File.Copy(
                Path.Combine(source, "inner", "a.txt"),
                Path.Combine(layer.OutputPath, "a.txt"));

            string target = Path.Combine(_root, "pub-target");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "a.txt"), "旧内容", Utf8NoBom);

            WorkspacePublishResult published = workspace.Publish(target);

            Assert.True(published.Success, published.Message);
            Assert.Equal(1, published.MovedFileCount);

            // 原有文件必须原样保留，新产物换成 a(1).txt，并且这件事要写在 Message 里。
            Assert.Equal("旧内容", File.ReadAllText(Path.Combine(target, "a.txt")));
            Assert.True(File.Exists(Path.Combine(target, "a(1).txt")));
            Assert.Contains("a(1).txt", published.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// ⛔ **发布这一步一个外壳都不摊**（2026-10-02 真机修）：归档自带的文件夹原样保留。
        ///
        /// <para>老口径把"只有一个子目录、没有同级文件"的那一层当"解压器自动加的壳"摊掉 ——
        /// 真机现场 <c>风景01.7z.001</c>（只解一层、内容全在一个 <c>风景\</c> 文件夹里）就这样变成了
        /// <c>&lt;包名&gt;\1.mp4</c>，用户的文件夹被吃过一层、日志里一个字都没提。用户 2026-09-30 的红线：
        /// 「如果是一个文件夹 1111 里面包裹真正的内容物，这个时候你就会把 1111 省略，这是非常大忌」。</para>
        ///
        /// <para>摊不摊"哪一层留"由**唯一出口** <c>ResultFinalizer</c> 决定（同名套娃 <c>pack\pack\</c>
        /// 由它自己的"同名不套层"那一条收掉）。</para>
        /// </summary>
        [Fact]
        public void 工作区_Publish不摊任何外壳_归档自带的文件夹原样保留()
        {
            var workspace = new ExtractionWorkspace(Path.Combine(_root, "pub-ws2"), "task-shell");
            WorkspaceLayer layer = workspace.CreateNextLayer(Path.Combine(_root, "pack.zip"));

            // out\pack\文件：这一层 `pack` 是打包人自己的结构，发布时**原样保留**。
            string shell = Path.Combine(layer.OutputPath, "pack");
            Directory.CreateDirectory(shell);
            File.WriteAllText(Path.Combine(shell, "a.txt"), "内容", Utf8NoBom);

            string target = Path.Combine(_root, "pub-target2");
            WorkspacePublishResult published = workspace.Publish(target);

            Assert.True(published.Success, published.Message);

            Assert.True(
                File.Exists(Path.Combine(target, "pack", "a.txt")),
                "归档自带的文件夹必须原样保留（摊平它是用户的红线）：" + published.Message);

            Assert.False(
                File.Exists(Path.Combine(target, "a.txt")),
                "⛔ 不许把归档自带的文件夹摊平到发布目标根上");
        }

        /// <summary>
        /// ⛔ **展开了内层包时，发布不许摊掉"内层包自己产出的那层文件夹"**（用户 2026-09-30 红线：
        /// 落点最少两层，最里层 = 最后一个压缩包那一层）。
        ///
        /// <para>
        /// 真机现场 <c>26081118.7z</c>：里面只有一个内层包，内层包解出
        /// <c>T 小小绘 推特大合集 330P+454V-9.31G\P|V</c>。丢 <c>T …</c> 的**真凶就是发布这一步** ——
        /// 它把"只有一个子目录、没有同级文件"的那一层当"解压器自动加的壳"摊掉了，
        /// 于是暂存区里只剩 <c>P</c>、<c>V</c>，定稿侧那两条分支只是补刀。
        /// </para>
        ///
        /// <para>
        /// 本用例走**整条递归**（真实 7z 造两个包），因此同时钉住
        /// <c>RecursiveExtractor.BuildResult</c> 有没有把"展开了内层包"这一个事实传给发布 ——
        /// 只测 <see cref="ExtractionWorkspace.Publish"/> 自己的用例（<c>TwoLayerLayoutTests</c>）
        /// 钉不住这根线。
        /// </para>
        /// </summary>
        [SevenZipFact]
        public async Task 递归展开内层包_发布时保留内层包自己产出的那层文件夹()
        {
            RequireSevenZip();

            // 内层包：解出来是一个文件夹 T 内层包产出\P\a.jpg（现场那个形状）。
            string innerSource = Path.Combine(_root, "inner-src");
            string innerFolder = Path.Combine(innerSource, "T 内层包产出", "P");
            Directory.CreateDirectory(innerFolder);
            File.WriteAllText(Path.Combine(innerFolder, "a.jpg"), "内容物", Utf8NoBom);

            string innerArchive = Path.Combine(_root, "inner.7z");
            Run7z("a", "-t7z", innerArchive, Path.Combine(innerSource, "*"));

            // 外层包：里面只有那个内层包。
            string outerSource = Path.Combine(_root, "outer-src");
            Directory.CreateDirectory(outerSource);
            File.Copy(innerArchive, Path.Combine(outerSource, "inner.7z"));

            string outerArchive = Path.Combine(_root, "two-layer-outer.7z");
            Run7z("a", "-t7z", outerArchive, Path.Combine(outerSource, "*"));

            string output = Path.Combine(_root, "out", "two-layer");

            RecursionResult result = await ExtractAsync(outerArchive, output);

            Assert.True(result.Completed, result.Summary);
            Assert.Equal(2, result.Layers.Count);

            // `T 内层包产出` 那一层必须还在（它是内层包自己产出的结构），P 待在它里面；
            // 而它外面还套着**内层包自己那一层**（`inner.7z` → `inner\`，用户 2026-09-30 的就地替换）。
            Assert.True(
                File.Exists(Path.Combine(output, "inner", "T 内层包产出", "P", "a.jpg")),
                "内层包自己产出的那层文件夹被摊掉了。实际目录树："
                + string.Join(" | ", Directory.GetFileSystemEntries(output, "*", SearchOption.AllDirectories))
                + $"\n结论：{result.Summary}");

            // ⛔ 老形状（P 直接躺在发布目标根上，或者 `T 内层包产出` 直接躺在发布目标根上）不许再出现。
            Assert.False(
                Directory.Exists(Path.Combine(output, "P")),
                "P 不许被摊到发布目标根上（那正是真机上 …\\26081118\\P 的形状）");
            Assert.False(
                Directory.Exists(Path.Combine(output, "T 内层包产出")),
                "内层包自己那一层（`inner\\`）不许被摊掉");
        }

        // ─────────────────── 工作区清理（成功后不留 data\work\recursive 垃圾） ───────────────────
        //
        // 这一组盯的是**磁盘泄漏**：递归工作区动辄几百 MB，成功后必须清掉；失败 / 取消 / 部分完成
        // 必须原样保留（那时的中间产物是用户唯一的线索）。清理只许针对**本实例自己持有的那个**工作区，
        // 绝不能按目录名或"最新目录"去扫 —— 那会删掉并发任务正在用的工作区。

        [SevenZipFact]
        public async Task 递归成功_工作区被清理且产物仍在()
        {
            RequireSevenZip();

            string source = BuildSourceDir(("readme.txt", "成功之后不该留工作区\n"));
            string archive = Path.Combine(_root, "cleanup-ok.zip");
            Run7z("a", "-tzip", archive, Path.Combine(source, "*"));

            string output = Path.Combine(_root, "out", "cleanup-ok");
            var log = new List<(string Level, string Message)>();

            var task = new ArchiveTask(archive);
            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicAwareProber(),
                _ => new[] { string.Empty },
                limits: null,
                log: (level, message) => log.Add((level, message)));

            RecursionResult result = await extractor.ExtractAsync(
                task,
                output,
                null,
                CancellationToken.None);

            Assert.True(result.Completed, result.Summary);

            // 产物必须真的在最终目录里：清工作区不能把产物一起清掉。
            Assert.True(File.Exists(Path.Combine(output, "readme.txt")), result.Summary);

            string workspaceDirectory = Assert.IsType<ExtractionWorkspace>(extractor.CurrentWorkspace).TaskDirectory;

            Assert.False(Directory.Exists(workspaceDirectory), $"成功之后工作区不该还在：{workspaceDirectory}");

            // 删什么、为什么：删前一条 INFO（带路径）、删后一条 INFO；本次不该有 WARN。
            Assert.Contains(
                log,
                x => x.Level == "INFO" &&
                     x.Message.Contains("清理本次任务的工作区", StringComparison.Ordinal) &&
                     x.Message.Contains(workspaceDirectory, StringComparison.Ordinal));
            Assert.Contains(
                log,
                x => x.Level == "INFO" &&
                     x.Message.Contains("工作区已清理", StringComparison.Ordinal) &&
                     x.Message.Contains(workspaceDirectory, StringComparison.Ordinal));
            Assert.DoesNotContain(log, x => x.Level == "WARN");
        }

        [SevenZipFact]
        public async Task 递归失败_工作区保留()
        {
            RequireSevenZip();

            // 外层没加密能解开、内层加密（-mhe）只给错密码 → 停在第 1 层，这一份工作区必须留着。
            string outer = BuildEncryptedInnerPackage();

            var task = new ArchiveTask(outer);
            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicAwareProber(),
                _ => new[] { string.Empty, "wrong-pass" });

            RecursionResult result = await extractor.ExtractAsync(
                task,
                Path.Combine(_root, "out", "keep-on-failure"),
                null,
                CancellationToken.None);

            /*
             * ⚠ 2026-10-05：这个内层包是 `-mhe`（连文件名一起加密）⇒ 每个候选都列不出清单、
             * 引擎给的结构化错误类型是"加密头" ⇒ 收尾落「文件名已加密」那一档
             * （与单层路径对齐；以前这里落的是「密码错误」）。
             * 本用例钉的是**工作区保留**，与结论落在哪一档无关 —— 所以只跟着改这一个期望值。
             */
            Assert.Equal(RecursionStopReason.EncryptedHeaders, result.StopReason);
            Assert.False(result.Completed);

            string workspaceDirectory = Assert.IsType<ExtractionWorkspace>(extractor.CurrentWorkspace).TaskDirectory;

            Assert.True(Directory.Exists(workspaceDirectory), "失败之后工作区必须保留：中间产物是用户唯一的线索");
            Assert.True(
                File.Exists(Path.Combine(workspaceDirectory, "report.json")),
                "失败之后崩溃恢复报告也该留着");
        }

        [Fact]
        public async Task 递归取消_工作区保留()
        {
            string archive = Path.Combine(_root, "cancel-mid.zip");
            File.WriteAllBytes(archive, new byte[200]);

            using var cts = new CancellationTokenSource();

            var engine = new FakeEngine();
            engine.OnExtractAsync = (request, _) =>
            {
                // 解压途中用户按下取消：引擎这一单以"已取消"收场（真实 7z 被杀掉也是这样）。
                cts.Cancel();

                WritePayload(request.OutputPath);

                return Task.FromResult(new ArchiveOperationResult
                {
                    Success = false,
                    Status = StatusText.Cancelled,
                    Message = "已取消",
                    DetectedErrorType = "Cancelled"
                });
            };

            var task = new ArchiveTask(archive);
            var extractor = new RecursiveExtractor(engine, new MagicAwareProber(), _ => new[] { string.Empty });

            RecursionResult result = await extractor.ExtractAsync(
                task,
                Path.Combine(_root, "out", "cancel-mid"),
                null,
                cts.Token);

            Assert.Equal(RecursionStopReason.UserCancelled, result.StopReason);
            Assert.False(result.Completed);

            string workspaceDirectory = Assert.IsType<ExtractionWorkspace>(extractor.CurrentWorkspace).TaskDirectory;

            Assert.True(Directory.Exists(workspaceDirectory), "取消之后工作区必须保留（半截产物是用户唯一的线索）");
        }

        /// <summary>
        /// 工作区删不掉（被占用 / 权限）时：只写一条 WARN，**绝不让已经成功的任务变成失败**
        /// （与单层路径的 CleanupTaskWorkspaceDirectory 同一口径）。产物该发布的照样发布。
        /// </summary>
        [Fact]
        public async Task 工作区删不掉_只写WARN且任务仍然成功()
        {
            string archivePath = Path.Combine(_root, "locked-ws.zip");
            File.WriteAllBytes(archivePath, new byte[200]);

            string output = Path.Combine(_root, "out", "locked-ws");
            var log = new List<(string Level, string Message)>();

            /*
             * 一定要让"解压"先停住：假引擎返回的是**已完成**的 Task，整条递归会同步跑到底，
             * 那时工作区已经被删掉了，测试根本没机会往里面放东西（第一次写这条用例就是这么红的）。
             */
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var engine = new FakeEngine();
            engine.OnExtractAsync = async (request, _) =>
            {
                await gate.Task.ConfigureAwait(false);

                WritePayload(request.OutputPath);

                return Succeeded();
            };

            var extractor = new RecursiveExtractor(
                engine,
                new MagicAwareProber(),
                _ => new[] { string.Empty },
                limits: null,
                log: (level, message) => log.Add((level, message)));

            Task<RecursionResult> run = extractor.ExtractAsync(
                new ArchiveTask(archivePath),
                output,
                null,
                CancellationToken.None);

            string workspaceDirectory = Assert.IsType<ExtractionWorkspace>(extractor.CurrentWorkspace).TaskDirectory;

            /*
             * 在本次任务自己的工作区根上放一个**被独占打开**的文件（发布只搬各层产物，不碰它），
             * 于是收尾那次 Directory.Delete(recursive: true) 必然失败。
             */
            string lockedPath = Path.Combine(workspaceDirectory, "locked.bin");
            File.WriteAllBytes(lockedPath, new byte[16]);

            RecursionResult result;

            using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // 放好之后才放行：收尾那次删除必然撞上这个句柄。
                gate.TrySetResult(true);

                result = await run;
            }

            Assert.True(result.Completed, result.Summary);
            Assert.True(Directory.Exists(workspaceDirectory), "删不掉时目录应当原样保留，不能假装删了");
            Assert.Contains(
                log,
                x => x.Level == "WARN" && x.Message.Contains("清理工作区失败", StringComparison.Ordinal));

            // 删不掉工作区不影响任务结论与产物发布。
            Assert.True(File.Exists(Path.Combine(output, "payload.bin")), result.Summary);

            // 断言做完了，顺手把这条用例自己占出来的目录收掉（句柄已释放，现在删得掉）。
            try
            {
                Directory.Delete(workspaceDirectory, recursive: true);
            }
            catch
            {
                // 收不掉就留给临时目录，不影响结论。
            }
        }

        /// <summary>
        /// 并发这条是**本组的重点**：清理只认"本实例自己建的那个目录"，
        /// 不许按目录名 / "最新的那个"去扫工作区根 —— 那种实现会把正在跑的那个任务的工作区端掉。
        /// </summary>
        [Fact]
        public async Task 并发两个递归任务_完成的那一个不会删掉另一个的工作区()
        {
            string slowArchive = Path.Combine(_root, "concurrent-slow.zip");
            string fastArchive = Path.Combine(_root, "concurrent-fast.zip");
            File.WriteAllBytes(slowArchive, new byte[200]);
            File.WriteAllBytes(fastArchive, new byte[200]);

            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var slowEngine = new FakeEngine();
            slowEngine.OnExtractAsync = async (request, _) =>
            {
                started.TrySetResult(true);

                // 卡在"解压中"：此刻它的工作区正在被写，另一个任务收尾时的清理绝不能碰它。
                await release.Task.ConfigureAwait(false);

                WritePayload(request.OutputPath);

                return Succeeded();
            };

            var slowExtractor = new RecursiveExtractor(slowEngine, new MagicAwareProber(), _ => new[] { string.Empty });
            var fastExtractor = new RecursiveExtractor(new FakeEngine(), new MagicAwareProber(), _ => new[] { string.Empty });

            string slowOutput = Path.Combine(_root, "out", "concurrent-slow");
            string fastOutput = Path.Combine(_root, "out", "concurrent-fast");

            Task<RecursionResult> slowRun = slowExtractor.ExtractAsync(
                new ArchiveTask(slowArchive),
                slowOutput,
                null,
                CancellationToken.None);

            await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

            string slowWorkspace = Assert.IsType<ExtractionWorkspace>(slowExtractor.CurrentWorkspace).TaskDirectory;
            File.WriteAllText(Path.Combine(slowWorkspace, "still-mine.txt"), "另一个任务不许删我", Utf8NoBom);

            // 第二个任务（同一个工作区根目录、另一个 taskId）完整跑完 → 它会清掉**自己**的工作区。
            RecursionResult fastResult = await fastExtractor.ExtractAsync(
                new ArchiveTask(fastArchive),
                fastOutput,
                null,
                CancellationToken.None);

            Assert.True(fastResult.Completed, fastResult.Summary);
            Assert.False(
                Directory.Exists(Assert.IsType<ExtractionWorkspace>(fastExtractor.CurrentWorkspace).TaskDirectory),
                "完成的任务应当清掉自己的工作区");

            // 关键断言：清理只动自己那一个目录 —— 正在跑的那个任务的工作区（含刚写进去的文件）必须原封不动。
            Assert.True(Directory.Exists(slowWorkspace), "并发任务的清理把对方正在用的工作区删掉了");
            Assert.True(File.Exists(Path.Combine(slowWorkspace, "still-mine.txt")), "对方工作区里的文件被删了");

            release.TrySetResult(true);

            RecursionResult slowResult = await slowRun;

            Assert.True(slowResult.Completed, slowResult.Summary);
            Assert.True(File.Exists(Path.Combine(slowOutput, "payload.bin")), slowResult.Summary);
            Assert.False(Directory.Exists(slowWorkspace), "自己成功之后也没清掉工作区");
        }
        /// <summary>
        /// 同一个实例被拿去跑**另一个**归档时（不是续跑），上一个任务的工作区不能被连带清掉 ——
        /// 那个任务没跑成、工作区按规矩留着，它的中间产物是用户唯一的线索。
        /// 判据是归档路径，不是"实例上有没有记着东西"。
        ///
        /// <para>⚠ 2026-10-21：第一趟的收尾原因从「多分支询问」换成「文件名已加密」——
        /// 多分支不再问用户（档位已删，`NeedsDecision` 现在不可达），而"没跑成 ⇒ 工作区留着"
        /// 这条没变（见 <c>递归失败_工作区保留</c>）。本条钉的是**换归档**那一半，与停在哪一档无关。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 同一个实例换归档_不清理上一个任务的工作区()
        {
            RequireSevenZip();

            string firstOuter = BuildEncryptedInnerPackage();

            string otherSource = BuildSourceDir(new[] { ("data.txt", "另一个包\n") }, "_other");
            string secondArchive = Path.Combine(_root, "other.zip");
            Run7z("a", "-tzip", secondArchive, Path.Combine(otherSource, "*"));

            var extractor = new RecursiveExtractor(
                new SevenZipEngine(),
                new MagicAwareProber(),
                _ => new[] { string.Empty, "wrong-pass" });

            RecursionResult first = await extractor.ExtractAsync(
                new ArchiveTask(firstOuter),
                Path.Combine(_root, "out", "swap-first"),
                null,
                CancellationToken.None);

            Assert.Equal(RecursionStopReason.EncryptedHeaders, first.StopReason);

            string firstWorkspace = Assert.IsType<ExtractionWorkspace>(extractor.CurrentWorkspace).TaskDirectory;

            // 换成另一个包（不是续跑）：这一次真的成功了，也只许清它自己的那一份。
            RecursionResult second = await extractor.ExtractAsync(
                new ArchiveTask(secondArchive),
                Path.Combine(_root, "out", "swap-second"),
                null,
                CancellationToken.None);

            Assert.True(second.Completed, second.Summary);
            Assert.False(
                Directory.Exists(Assert.IsType<ExtractionWorkspace>(extractor.CurrentWorkspace).TaskDirectory),
                "本次运行的工作区应当被清理");
            Assert.True(
                Directory.Exists(firstWorkspace),
                "上一个任务（没跑成、工作区留着）的工作区被别的任务连带清掉了");
        }

        /// <summary>
        /// **每一层开头都要把进度基准拉回本层**（用户 2026-10-02：「解压完成到真正的解释有个 8 分钟的差距」）。
        ///
        /// <para>现场：`P.7z.001` 第 0 层 09:47:18 到 100%，09:47:23 才开始解内层 `P.ra`，
        /// 09:51:57 才结束 —— 中间 **4 分 34 秒**界面上停在 100%、日志里一条进度都没有。
        /// 成因：进度条记的是"引擎最后一次报的百分比"，第 1 层从头开始时没有任何一帧把基准拉回来。</para>
        ///
        /// <para><b>红检</b>：把 `RecursiveExtractor.ExtractLayerAsync` 里那一帧（`progress?.Report(...)`）
        /// 撤掉 ⇒ 本用例立刻变红（找不到"第 1 层 + 0%"那一帧）。</para>
        /// </summary>
        [Fact]
        public async Task 每一层开头_进度基准都要拉回本层()
        {
            string source = Path.Combine(_root, "layer-progress-source.7z");
            File.WriteAllText(source, "not a real archive; the engine is fake");

            /*
             * 内层候选现在过的是「内容证据」闸门（FileNature.BodyKind == SingleComplete），
             * 不再看后缀/测试 prober ⇒ 这里必须放一份**真 7z**，否则第 1 层根本不会入队，
             * 用例测到的就不是"进度基准"，而是"假包没通过内容闸门"。
             */
            string innerSource = Path.Combine(_root, "_inner-progress");
            Directory.CreateDirectory(innerSource);
            File.WriteAllText(Path.Combine(innerSource, "leaf.txt"), "leaf");
            string realInner = Path.Combine(_root, "real-inner-progress.7z");
            Run7z("a", "-t7z", realInner, Path.Combine(innerSource, "*"));
            byte[] realInnerBytes = File.ReadAllBytes(realInner);

            var engine = new FakeEngine();
            var recorded = new List<ArchiveProgress>();
            int calls = 0;

            engine.OnExtractAsync = (request, options) =>
            {
                calls++;
                Directory.CreateDirectory(request.OutputPath!);

                if (calls == 1)
                {
                    // 第 0 层：解出一个真 7z（内容闸门才会把它收进第 1 层），
                    // 并把进度推到 100% —— 真机上第 0 层结束时就是这样。
                    File.WriteAllBytes(Path.Combine(request.OutputPath!, "inner.7z"), realInnerBytes);
                    request.Progress?.Report(new ArchiveProgress { Percent = 100, CurrentEntry = "第 0 层的最后一个文件" });
                }
                else
                {
                    File.WriteAllText(Path.Combine(request.OutputPath!, "leaf.bin"), "叶子内容");
                }

                return Task.FromResult(Succeeded());
            };

            var task = new ArchiveTask(source);
            var extractor = new RecursiveExtractor(
                engine,
                new MagicAwareProber(),
                _ => new[] { string.Empty });

            await extractor.ExtractAsync(
                task,
                Path.Combine(_root, "out-layer-progress"),
                null,
                CancellationToken.None,
                new RecordingProgress(recorded));

            Assert.Equal(2, calls);   // 内外两层都真的解了

            int layerZeroDone = recorded.FindIndex(p => p.Percent == 100);
            int layerOneStart = recorded.FindIndex(
                p => p.Percent == 0 && p.CurrentEntry.Contains("第 1 层", StringComparison.Ordinal));

            Assert.True(layerZeroDone >= 0, "第 0 层的引擎进度必须透传出来");
            Assert.True(
                layerOneStart > layerZeroDone,
                "第 1 层开头必须报一帧 0%（当前条目点名第 1 层），而且必须排在第 0 层的 100% 之后 —— "
                + "少了它，界面就会停在 100% 一动不动。实际收到的帧："
                + string.Join("；", recorded.Select(p => $"{p.Percent}%/{p.CurrentEntry}")));
        }
    }
}
