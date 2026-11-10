using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「特定解压 · 每个包只留一层内容」（用户 2026-09-24 拍板）的**真样本端到端**验收。
    ///
    /// <para>
    /// 跑的是**真链路**：真 7z.exe 造的样本、真 <see cref="MainViewModel"/> + 各 Coordinator +
    /// 真单层解压管线（与 <c>InnerLayerContinuationTests</c> 同一套造样本与装配做法）。
    /// 断言的是**逐字比对最终目录树**，不是 Contains —— 用户要的正是
    /// "<c>222\1111\内容物</c>"这一个形状，差一层就是没做到。
    /// </para>
    ///
    /// <para>
    /// 本类声明进 <c>ArchiveFixerGlobalState</c> 集合（不与其他集合并行）：
    /// <see cref="MainViewModel"/> 的构造会写两个进程级静态（内置 7z 路径、递归工作区根），
    /// 与别的真 7z 用例并行会互相踩（见该集合的说明）。
    /// </para>
    ///
    /// <para>样本全部自己造（临时目录 + 项目内置 7z.exe），不读用户目录里的任何文件（AGENTS.md §8）。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class SpecialExtractionPipelineTests : IDisposable
    {
        private const string PayloadText = "最里面那一层的内容物\n";
        private const string OuterPayloadText = "外层包自己的 payload\n";
        private const string InnerPayloadText = "内层包自己的 payload\n";


        private readonly string _root;
        private readonly string _sevenZip;

        public SpecialExtractionPipelineTests()
        {
            _sevenZip = LocateSevenZip();
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerSpecialExtraction", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响结论（句柄可能还被 7z 释放中）。
            }
        }

        // ================================================================ ① 规则关：与现在完全一样


        // ================================================================ ② 规则开：用户要的那一句

        /// <summary>
        /// 特定解压的输出根跟着压缩包自己的父目录走：
        /// <c>合集\333\3333.7z</c>（包内只有 <c>33333\</c>）→ <c>合集\333\33333\</c>。
        /// </summary>
        [SevenZipFact]
        public async Task 特定解压_嵌套包_输出到压缩包父目录()
        {
            string collection = Path.Combine(_root, "合集");
            Directory.CreateDirectory(Path.Combine(collection, "333"));

            string build = Path.Combine(_root, "build-specific-root");
            Directory.CreateDirectory(build);
            WriteText(Path.Combine(build, "33333", "01.jpg"), "x");

            string package = Path.Combine(collection, "333", "3333.7z");
            Run7z(build, "a", "-t7z", package, @"33333\01.jpg");

            Harness harness = CreateHarness("specific-root", settings =>
            {
                EnableSpecialExtraction(settings);
            });

            await harness.AddPathsAsync(collection);

            ArchiveTask task = Assert.Single(harness.Vm.Tasks);
            Assert.Equal(collection, task.SourceSelectionRoot);

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(120));

            Assert.True(
                File.Exists(Path.Combine(collection, "333", "33333", "01.jpg")),
                "输出必须留在源包自己的父目录里；实际目录树：" + string.Join(" | ", Tree(collection)));
            Assert.False(
                Directory.Exists(Path.Combine(collection, "33333")),
                "输出不能再堆到导入的合集根");
            Assert.False(
                Directory.Exists(Path.Combine(collection, "333", "3333")),
                "不能多套一层源包名目录");
        }



        [SevenZipFact]
        public async Task 特定解压_空间不足模式_失败不删源包()
        {
            string source = Path.Combine(_root, "bad.7z");
            File.WriteAllText(source, "这不是压缩包");

            Harness harness = CreateHarness("specific-space-fail", settings =>
            {
                EnableSpecialExtraction(settings);
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            });

            harness.Vm.SpaceTightMode = true;

            await harness.AddPathsAsync(source);
            Assert.Single(harness.Vm.Tasks);

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(120));

            Assert.True(
                File.Exists(source),
                "空间不足模式也不能用“用户接受风险”替代本单成功/校验证据；失败必须保留源包");
        }

        [SevenZipFact]
        public async Task 特定解压_入口就是标准分卷_整组只解一次()
        {
            string collection = Path.Combine(_root, "合集-volume");
            Directory.CreateDirectory(collection);

            string build = Path.Combine(_root, "build-volume");
            Directory.CreateDirectory(build);
            var payload = new byte[100_000];
            new Random(12345).NextBytes(payload);
            File.WriteAllBytes(Path.Combine(build, "payload.bin"), payload);

            Run7z(build, "a", "-t7z", "-v1k", Path.Combine(collection, "111.7z"), "payload.bin");

            string[] parts = Directory.GetFiles(collection, "111.7z.*");
            Assert.True(parts.Length >= 2, "样本本身必须真的分成多卷");

            Harness harness = CreateHarness("specific-volume", settings =>
            {
                EnableSpecialExtraction(settings);
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
                settings.ConcurrencyOverride = 1;
            });

            await harness.AddPathsAsync(collection);
            Assert.Equal(parts.Length, harness.Vm.Tasks.Count);

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(120));

            Assert.True(
                File.Exists(Path.Combine(collection, "111", "payload.bin")),
                "标准分卷整组应只由入口卷解一次；实际目录树：" + string.Join(" | ", Tree(collection)));
            Assert.True(
                harness.Vm.Tasks.Count(task => task.IsVolumeGroupFollower) == parts.Length - 1,
                "同组其余卷必须标成跟班卷，不能各自再解一遍");
        }

        [SevenZipFact]
        public async Task 特定解压_空间不足_用户2_最多2()
        {
            string collection = Path.Combine(_root, "合集-concurrent");
            Directory.CreateDirectory(collection);

            string build = Path.Combine(_root, "build-concurrent");
            Directory.CreateDirectory(build);
            WriteText(Path.Combine(build, "01.jpg"), "x");

            Run7z(build, "a", "-t7z", Path.Combine(collection, "a.7z"), "01.jpg");
            Run7z(build, "a", "-t7z", Path.Combine(collection, "b.7z"), "01.jpg");

            Harness harness = CreateHarness("specific-concurrent", settings =>
            {
                EnableSpecialExtraction(settings);
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
                settings.ConcurrencyOverride = 2;
            });

            harness.Vm.SpaceTightMode = true;

            await harness.AddPathsAsync(collection);
            Assert.Equal(2, harness.Vm.Tasks.Count);

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(120));

            Assert.True(File.Exists(Path.Combine(collection, "a", "01.jpg")));
            Assert.True(File.Exists(Path.Combine(collection, "b", "01.jpg")));
            Assert.Contains(
                harness.LogTexts,
                text => text.Contains("用户上限 2 个", StringComparison.Ordinal)
                        && text.Contains("实际 2 个", StringComparison.Ordinal));
            Assert.DoesNotContain(
                harness.LogTexts,
                text => text.Contains("实际 3 个", StringComparison.Ordinal));
        }

        // ================================================================ ③ 多分支：不塌 + WARN


        // ================================================================ 小体积：暂停后不再补位
        [SevenZipFact]
        public async Task 真机混合盘_两个设备池补位()
        {
            string eDir = @"E:\DeepSeekProjects\_tmp\ArchiveFixer\real-mixed-e";
            string hDir = Path.Combine(TestPaths.TempRoot, "real-mixed-h");
            if (!Directory.Exists(@"H:\"))
            {
                FixtureGate.Skip("SpecialExtractionPipelineTests.cs", "前置条件不满足：夹具或运行环境缺失");
                 return;
            }

            Directory.CreateDirectory(eDir);
            Directory.CreateDirectory(hDir);
            try
            {
                for (int i = 1; i <= 2; i++)
                {
                    string build = Path.Combine(_root, "mixed-build-" + i);
                    Directory.CreateDirectory(build);
                    byte[] payload = new byte[512 * 1024];
                    Random.Shared.NextBytes(payload);
                    File.WriteAllBytes(Path.Combine(build, "a.bin"), payload);
                    Run7z(build, "a", "-t7z", Path.Combine(eDir, "e" + i + ".7z"), "a.bin");
                    Run7z(build, "a", "-t7z", Path.Combine(hDir, "h" + i + ".7z"), "a.bin");

                }

                Harness harness = CreateHarness("real-mixed", settings =>
                {
                    settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
                });

                await harness.AddPathsAsync(eDir, hDir);
                await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(180));

                Assert.True(4 == harness.Vm.Tasks.Count, string.Join(" | ", harness.Vm.Tasks.Select(t => t.CurrentPath)));
                Assert.True(harness.Vm.Tasks.All(t => t.Outcome == TaskOutcome.Succeeded), string.Join(" | ", harness.Vm.Tasks.Select(t => t.CurrentPath + "=" + t.Status + "/" + t.ErrorMessage)));
            }
            finally
            {
                if (Directory.Exists(eDir)) Directory.Delete(eDir, recursive: true);
                if (Directory.Exists(hDir)) Directory.Delete(hDir, recursive: true);
            }
        }


        [SevenZipFact]
        public async Task 特定解压_小包暂停所有操作_不继续启动后续()
        {
            string collection = Path.Combine(_root, "specific-pause-small");
            Directory.CreateDirectory(collection);

            for (int i = 1; i <= 20; i++)
            {
                string build = Path.Combine(_root, "build-pause-" + i.ToString("D2"));
                WriteText(Path.Combine(build, "a.txt"), new string('x', 1024 * 1024));
                Run7z(build, "a", "-t7z", Path.Combine(collection, "p" + i.ToString("D2") + ".7z"), "a.txt");
            }

            Harness harness = CreateHarness("specific-pause-small", settings =>
            {
                EnableSpecialExtraction(settings);
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            });

            await harness.AddPathsAsync(collection);
            Assert.Equal(20, harness.Vm.Tasks.Count);

            Task<OneClickOutcome> run = harness.RunOneClickAsync();
            await Task.Delay(100);

            harness.Vm.PauseAllOperationsCommand.Execute(null);

            await run.WaitAsync(TimeSpan.FromSeconds(120));

            Assert.Contains(
                harness.LogTexts,
                text => text.Contains("暂停所有操作", StringComparison.Ordinal));
            Assert.True(
                harness.Vm.Tasks.Count(task => task.Outcome == TaskOutcome.Succeeded) < 20,
                "暂停后仍有任务被启动完成");
        }

        // ================================================================ 人工抽查：普通/特定解压的成功后源包与过程物
        [SevenZipFact]
        public async Task 特定解压_预计耗时大的先占槽位()
        {
            string collection = Path.Combine(_root, "specific-duration-order");
            Directory.CreateDirectory(collection);

            for (int i = 1; i <= 4; i++)
            {
                string build = Path.Combine(_root, "build-order-" + i.ToString("D2"));
                WriteText(Path.Combine(build, "a.txt"), new string('x', i * 1024 * 1024));
                Run7z(build, "a", "-t7z", Path.Combine(collection, "p" + i.ToString("D2") + ".7z"), "a.txt");
            }

            Harness harness = CreateHarness("specific-duration-order", settings =>
            {
                EnableSpecialExtraction(settings);
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
                settings.ConcurrencyOverride = 2;
            });

            await harness.AddPathsAsync(collection);
            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(120));

            List<string> starts = harness.LogTexts
                .Where(text => text.Contains("第 1 层：解压「p", StringComparison.Ordinal))
                .ToList();

            Assert.True(starts.Count >= 2, string.Join(" ｜ ", harness.LogTexts));
            Assert.Contains("p04.7z", starts[0], StringComparison.Ordinal);
            Assert.Contains("p03.7z", starts[1], StringComparison.Ordinal);
        }


        [SevenZipFact]
        public async Task 特定解压_源包按KeepInPlace时保留()
        {
            string root = Path.Combine(_root, "specific-keep");
            string build = Path.Combine(_root, "specific-keep-build");
            WriteText(Path.Combine(build, "111", "a.txt"), "keep\n");
            string package = Path.Combine(root, "111.7z");
            Directory.CreateDirectory(root);
            Run7z(build, "a", "-t7z", package, @"111\a.txt");

            Harness harness = CreateHarness("specific-keep", settings =>
            {
                settings.UseSpecificExtraction = true;
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
                settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            });
            await harness.AddPathsAsync(root);
            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(120));

            Assert.True(File.Exists(package), "特定解压：KeepInPlace 必须保留源包");
            Assert.True(File.Exists(Path.Combine(root, "111", "a.txt")), "特定解压：内容物仍要落位");
        }

        [SevenZipFact]
        public async Task 特定解压_后缀夹多个中文_全部删掉后仍能认出并输出()
        {
            string root = Path.Combine(_root, "specific-suffix");
            string build = Path.Combine(_root, "specific-suffix-build");
            WriteText(Path.Combine(build, "111", "a.txt"), "suffix\n");
            string package = Path.Combine(root, "111.删7z除中");
            Directory.CreateDirectory(root);
            Run7z(build, "a", "-t7z", package, @"111\a.txt");

            Harness harness = CreateHarness("specific-suffix", settings =>
            {
                settings.UseSpecificExtraction = true;
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
                settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            });
            await harness.AddPathsAsync(root);
            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(120));

            Assert.True(File.Exists(package), "特定解压：KeepInPlace 必须保留源包");
            Assert.True(File.Exists(Path.Combine(root, "111", "a.txt")), "后缀里的中文字符必须全部清掉");
        }

        [SevenZipFact]
        public async Task 人工抽查_普通与特定解压_成功后源包与最后一层过程物()
        {
            string normalRoot = Path.Combine(_root, "manual-normal");
            string normalBuild = Path.Combine(_root, "manual-normal-build");
            WriteText(Path.Combine(normalBuild, "111", "a.txt"), "normal\n");
            string normalPackage = Path.Combine(normalRoot, "111.7z");
            Directory.CreateDirectory(normalRoot);
            Run7z(normalBuild, "a", "-t7z", normalPackage, @"111\a.txt");

            string specificRoot = Path.Combine(_root, "manual-specific", "合集");
            string specificBuild = Path.Combine(_root, "manual-specific-build");
            WriteText(Path.Combine(specificBuild, "111", "a.txt"), "specific\n");
            string specificPackage = Path.Combine(specificRoot, "111.7z");
            Directory.CreateDirectory(specificRoot);
            Run7z(specificBuild, "a", "-t7z", specificPackage, @"111\a.txt");

            string deleteRoot = Path.Combine(_root, "manual-delete");
            string deleteBuild = Path.Combine(_root, "manual-delete-build");
            WriteText(Path.Combine(deleteBuild, "222", "a.txt"), "delete\n");
            string deletePackage = Path.Combine(deleteRoot, "222.7z");
            Directory.CreateDirectory(deleteRoot);
            Run7z(deleteBuild, "a", "-t7z", deletePackage, @"222\a.txt");

            string nestedRoot = Path.Combine(_root, "manual-nested");
            string innerBuild = Path.Combine(_root, "manual-nested-inner");
            WriteText(Path.Combine(innerBuild, "content", "b.txt"), "nested\n");
            string innerPackage = Path.Combine(_root, "manual-nested-inner.7z");
            Run7z(innerBuild, "a", "-t7z", innerPackage, @"content\b.txt");
            string nestedBuild = Path.Combine(_root, "manual-nested-build");
            Directory.CreateDirectory(nestedBuild);
            File.Copy(innerPackage, Path.Combine(nestedBuild, "inner.7z"));
            string nestedPackage = Path.Combine(nestedRoot, "outer.7z");
            Directory.CreateDirectory(nestedRoot);
            Run7z(nestedBuild, "a", "-t7z", nestedPackage, "inner.7z");

            Harness nested = CreateHarness("manual-nested", settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
                settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            });
            await nested.AddPathsAsync(nestedRoot);
            await nested.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(120));

            Harness normal = CreateHarness("manual-normal", settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
                settings.RestHandlingAfterVerify = RestHandlingModes.Keep;
            });
            await normal.AddPathsAsync(normalRoot);
            await normal.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(120));

            Harness delete = CreateHarness("manual-delete", settings =>
            {
                settings.SourceHandling = nameof(SourceHandlingMode.DeleteInPlace);
                settings.RestHandlingAfterVerify = RestHandlingModes.Delete;
            });
            await delete.AddPathsAsync(deleteRoot);
            await delete.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(120));

            Harness specific = CreateHarness("manual-specific", settings =>
            {
                settings.UseSpecificExtraction = true;
                settings.SourceHandling = nameof(SourceHandlingMode.DeleteInPlace);
                settings.RestHandlingAfterVerify = RestHandlingModes.Delete;
            });
            await specific.AddPathsAsync(specificRoot);
            await specific.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(120));

            Assert.True(File.Exists(normalPackage), "普通解压：源包按 KeepInPlace 必须保留");
            Assert.True(File.Exists(Path.Combine(normalRoot, "111", "a.txt")), "普通解压：内容物应落在 111\\a.txt");
            Assert.False(File.Exists(deletePackage), "普通解压：源包按 DeleteInPlace 成功后才删");
            Assert.True(File.Exists(Path.Combine(deleteRoot, "222", "a.txt")), "普通解压：删源后内容物仍在");
            Assert.True(File.Exists(nestedPackage), "普通解压：内层包成功后源包仍按 KeepInPlace 保留");
            Assert.True(File.Exists(Path.Combine(nestedRoot, "outer", "inner", "content", "b.txt")), "普通解压：内层内容物应落位");
            Assert.False(File.Exists(specificPackage), "特定解压：源包按 DeleteInPlace 成功后必须删");
            Assert.True(File.Exists(Path.Combine(specificRoot, "111", "a.txt")), "特定解压：内容物应落在 合集\\111\\a.txt");
        }

        // ================================================================ ④ 同名冲突：改名，绝不覆盖


        // ================================================================ ⑤ 确认框那行


        // ================================================================ 样本构造

        /// <summary>
        /// 单链多层包：包内是 <c>A\内容文件夹\payload.txt</c>（两层文件夹，每层都只有一个子目录）。
        ///
        /// <para>规则关 → 判定表 4 套出最深那层（<c>内容文件夹</c>）；规则开 → 那一层不套。</para>
        /// </summary>
        private string BuildSingleChainPackage(string sourceRoot, string packageName)
        {
            string build = Path.Combine(_root, "build-single-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(build);

            WriteText(Path.Combine(build, @"A\内容文件夹\payload.txt"), PayloadText);

            Directory.CreateDirectory(sourceRoot);

            string package = Path.Combine(sourceRoot, packageName);

            Run7z(build, "a", "-t7z", package, @"A\内容文件夹\payload.txt");

            return package;
        }

        /// <summary>
        /// 多分支包：包内是 <c>A\X\a.txt</c> 与 <c>A\Y\b.txt</c>（<c>A</c> 下面两个并列文件夹）。
        /// 规则开着也**不塌**。
        /// </summary>
        private string BuildBranchPackage(string sourceRoot, string packageName)
        {
            string build = Path.Combine(_root, "build-branch-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(build);

            WriteText(Path.Combine(build, @"A\X\a.txt"), "x");
            WriteText(Path.Combine(build, @"A\Y\b.txt"), "y");

            Directory.CreateDirectory(sourceRoot);

            string package = Path.Combine(sourceRoot, packageName);

            Run7z(build, "a", "-t7z", package, @"A\X\a.txt", @"A\Y\b.txt");

            return package;
        }

        /// <summary>
        /// 塌缩后撞名的现场：外层包里有 <c>payload.txt</c> + 内层包 <c>inner.7z</c>；
        /// 内层包里也有一个 <c>payload.txt</c>（单链，规则开会塌到同一层）。
        /// </summary>
        private string BuildNestedSameNamePackage(string sourceRoot, string packageName)
        {
            string build = Path.Combine(_root, "build-nested-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(build);

            string innerBuild = Path.Combine(_root, "build-inner-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(innerBuild);

            WriteText(Path.Combine(innerBuild, @"X\payload.txt"), InnerPayloadText);

            Run7z(innerBuild, "a", "-t7z", Path.Combine(build, "inner.7z"), @"X\payload.txt");

            WriteText(Path.Combine(build, "payload.txt"), OuterPayloadText);

            Directory.CreateDirectory(sourceRoot);

            string package = Path.Combine(sourceRoot, packageName);

            Run7z(build, "a", "-t7z", package, "payload.txt", "inner.7z");

            return package;
        }

        [SevenZipFact]
        public async Task 特定解压_指定位置_产物落到指定根()
        {
            string collection = Path.Combine(_root, "specific-custom-source");
            Directory.CreateDirectory(collection);

            string build = Path.Combine(_root, "build-specific-custom");
            Directory.CreateDirectory(build);
            WriteText(Path.Combine(build, "payload.txt"), "payload");
            Run7z(build, "a", "-t7z", Path.Combine(collection, "pack.7z"), "payload.txt");

            string target = Path.Combine(_root, "specific-custom-target");
            Directory.CreateDirectory(target);

            Harness harness = CreateHarness("specific-custom", settings =>
            {
                EnableSpecialExtraction(settings);
                settings.ExtractToOriginalDirectory = false;
                settings.CustomOutputDirectory = target;
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            });

            await harness.AddPathsAsync(collection);
            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(120));

            ArchiveTask task = harness.Vm.Tasks.Single();
            Assert.Equal(TaskOutcome.Succeeded, task.Outcome);
            Assert.StartsWith(target, task.OutputPath, StringComparison.OrdinalIgnoreCase);
        }

        [SevenZipFact]
        public async Task 特定解压_加密包成功_密码状态和候选日志都落下来()
        {
            string collection = Path.Combine(_root, "specific-password");
            Directory.CreateDirectory(collection);

            string build = Path.Combine(_root, "build-password");
            Directory.CreateDirectory(build);
            WriteText(Path.Combine(build, "secret.txt"), "secret");

            Run7z(build, "a", "-t7z", "-pTEST", Path.Combine(collection, "secret.7z"), "secret.txt");

            Harness harness = CreateHarness("specific-password", settings =>
            {
                EnableSpecialExtraction(settings);
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            });

            harness.PasswordService.Passwords.Add(new PasswordItem
            {
                Value = "TEST",
                Source = "ImportedList",
                IsEnabled = true
            });

            await harness.AddPathsAsync(collection);
            Assert.Single(harness.Vm.Tasks);

            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(120));

            ArchiveTask task = harness.Vm.Tasks.Single();
            Assert.Equal(TaskOutcome.Succeeded, task.Outcome);
            Assert.Equal(StatusText.PasswordCorrect, task.PasswordStatus);
            Assert.Contains(harness.LogTexts, text => text.Contains("密码候选", StringComparison.Ordinal)
                                                       && text.Contains("secret.7z", StringComparison.Ordinal));
            Assert.Contains(harness.LogTexts, text => text.Contains("密码候选", StringComparison.Ordinal)
                                                       && text.Contains("通过", StringComparison.Ordinal));
        }

        [SevenZipFact]
        public async Task 特定解压_普通日志档_成功细节进内部日志不刷屏()
        {
            string collection = Path.Combine(_root, "specific-logcapture");
            Directory.CreateDirectory(collection);

            string build = Path.Combine(_root, "build-logcapture");
            Directory.CreateDirectory(build);
            WriteText(Path.Combine(build, "payload.txt"), "payload");
            Run7z(build, "a", "-t7z", Path.Combine(collection, "normal.7z"), "payload.txt");

            Harness harness = CreateHarness("specific-logcapture", settings =>
            {
                EnableSpecialExtraction(settings);
                settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            });
            harness.Extraction.KeepTaskDetailInLog = false;

            await harness.AddPathsAsync(collection);
            await harness.RunOneClickAsync().WaitAsync(TimeSpan.FromSeconds(120));

            ArchiveTask task = harness.Vm.Tasks.Single();
            Assert.Equal(TaskOutcome.Succeeded, task.Outcome);

            // 成功任务在屏幕上只留一条摘要；候选/层细节只进内部详细日志。
            Assert.Contains(
                harness.LogTexts,
                text => text.Contains("normal.7z", StringComparison.Ordinal)
                        && text.Contains("解压 100%", StringComparison.Ordinal));
            Assert.DoesNotContain(
                harness.LogTexts,
                text => text.Contains("密码候选", StringComparison.Ordinal));
        }

        // ================================================================ 装配与工具

        /// <summary>打开特定解压独立模式（每个用例都写一遍太啰嗦，抽一处）。</summary>
        private static void EnableSpecialExtraction(AppSettings settings)
        {
            settings.UseSpecificExtraction = true;
        }

        /// <summary>
        /// 一套真实装配：真 MainViewModel + 真各 Coordinator + 真 7z 引擎。
        ///
        /// <para>
        /// <paramref name="runId"/> 决定这一次的**源目录**（<c>&lt;root&gt;\&lt;runId&gt;</c>）——
        /// 跑两遍比对的那几条用例各用一份干净目录，免得第二遍撞上第一遍留下的"输出目录已存在"。
        /// </para>
        /// </summary>
        private Harness CreateHarness(string runId, Action<AppSettings>? configure = null)
        {
            string dataRoot = Path.Combine(_root, "data-" + runId);
            string sourceRoot = Path.Combine(_root, runId);

            Directory.CreateDirectory(dataRoot);
            Directory.CreateDirectory(sourceRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var settingsService = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();


            // 未指定位置：产物落在源包旁边那一层（用户现场就是 222\1111\…）。
            settings.ExtractToOriginalDirectory = true;
            settings.CustomOutputDirectory = string.Empty;
            settings.KeepArchiveNameFolder = true;
            settings.SourceHandling = nameof(SourceHandlingMode.KeepInPlace);
            settings.CustomSevenZipExePath = string.Empty;

            configure?.Invoke(settings);
            settingsService.Save(settings);

            var engine = new SevenZipEngine();
            var passwordService = new PasswordService();
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

            // 这个用例拿「细节日志」当行为证据（第 44 条之后，成功时默认只留两行）。
            extraction.KeepTaskDetailInLog = true;
            var oneClick = new OneClickCoordinator(vm, scan, rename, extraction, new DialogService());

            return new Harness(vm, oneClick, extraction, logService, passwordService, sourceRoot);
        }

        /// <summary>目录树的**逐字**快照（相对 <paramref name="root"/> 的全部条目，排序后比对）。</summary>
        private static List<string> Tree(string root)
        {
            if (!Directory.Exists(root))
            {
                return new List<string>();
            }

            return Directory
                .EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string[] DirectoryNames(string root) =>
            Directory.GetDirectories(root)
                .Select(path => Path.GetFileName(path) ?? string.Empty)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

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

            string local = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe");

            if (File.Exists(local))
            {
                return local;
            }

            throw new InvalidOperationException("找不到内置 7z.exe。");
        }

        private void Run7z(string workingDirectory, params object[] args)
        {
            var psi = new ProcessStartInfo(_sevenZip)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory
            };

            foreach (object a in args)
            {
                psi.ArgumentList.Add(a.ToString() ?? string.Empty);
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

        private static void WriteText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        private sealed class Harness
        {
            private readonly OneClickCoordinator _oneClick;

            public Harness(
                MainViewModel vm,
                OneClickCoordinator oneClick,
                ExtractionCoordinator extraction,
                LogService log,
                PasswordService passwords,
                string sourceRoot)
            {
                Vm = vm;
                _oneClick = oneClick;
                Extraction = extraction;
                Log = log;
                PasswordService = passwords;
                SourceRoot = sourceRoot;
            }

            public MainViewModel Vm { get; }

            public ExtractionCoordinator Extraction { get; }

            public LogService Log { get; }

            public PasswordService PasswordService { get; }

            /// <summary>这一次的源目录（也是"未指定位置"档下的成品根）。</summary>
            public string SourceRoot { get; }

            public IEnumerable<string> LogTexts => Log.Logs.Select(item => item.DisplayText);

            public Task AddPathsAsync(params string[] paths) => Vm.AddPathsAsync(paths);

            /// <summary>跑一键处理的**流程部分**（不弹任何对话框）。</summary>
            public Task<OneClickOutcome> RunOneClickAsync() => _oneClick.RunPipelineAsync(Vm.Tasks.ToList());
        }
    }
}
