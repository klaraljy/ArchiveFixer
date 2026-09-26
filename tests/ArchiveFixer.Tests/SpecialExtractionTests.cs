using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「特定解压」（用户 2026-09-24 拍板：①页一键处理旁一个开关 + ②页**可扩展**的一栏）
    /// 的**纯逻辑**那一半：规则注册表、设置层、定稿规划里的塌缩边界、界面按注册表渲染。
    ///
    /// <para>真 7z 端到端那一半在 <c>SpecialExtractionPipelineTests</c>（真样本、逐字比目录树）。</para>
    ///
    /// <para>
    /// 本类声明进 <c>ArchiveFixerGlobalState</c> 集合（不与其他集合并行）：其中一条用例会
    /// **临时往注册表里塞一条假规则**来证明"加规则不用动界面骨架"，并行跑会让别的用例读到它。
    /// </para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class SpecialExtractionTests : IDisposable
    {
        private readonly string _root;

        public SpecialExtractionTests()
        {
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
                // 临时目录清不掉不影响结论。
            }
        }

        // ================================================================ ① 注册表

        [Fact]
        public void 注册表_第一条规则是每个包只留一层内容_默认勾上()
        {
            SpecialExtractionRule rule = Assert.Single(
                SpecialExtractionRules.All,
                item => item.Id == SpecialExtractionRules.SingleContentLayer);

            Assert.Equal("每个包只留一层内容", rule.Name);
            Assert.False(string.IsNullOrWhiteSpace(rule.Description), "规则必须带一句说明（它进界面与文档）");
            Assert.True(rule.EnabledByDefault, "第一条规则默认勾上：开了总开关就该按它跑");

            Assert.Equal(
                new[] { SpecialExtractionRules.SingleContentLayer },
                SpecialExtractionRules.DefaultEnabledIds);
        }

        [Fact]
        public void 注册表按Id分派_认不出的Id不会变成任何效果()
        {
            Assert.Equal(
                SpecialExtractionEffect.SingleContentLayer,
                SpecialExtractionRules.ResolveEffect(new[] { SpecialExtractionRules.SingleContentLayer }));

            // 大小写不同仍然认得出（设置文件是人可改的）。
            Assert.Equal(
                SpecialExtractionEffect.SingleContentLayer,
                SpecialExtractionRules.ResolveEffect(new[] { "singlecontentlayer" }));

            // 认不出的 Id **丢掉**，绝不"猜一条来跑"。
            Assert.Equal(
                SpecialExtractionEffect.None,
                SpecialExtractionRules.ResolveEffect(new[] { "以后才有的规则", "随便写的" }));

            Assert.Empty(SpecialExtractionRules.ResolveEnabled(new[] { "以后才有的规则" }));

            // "认不认得"这个判断也只有一处实现（界面与设置层都不许自己比字符串）。
            Assert.True(SpecialExtractionRules.IsKnown("singlecontentlayer"));
            Assert.False(SpecialExtractionRules.IsKnown("以后才有的规则"));
            Assert.False(SpecialExtractionRules.IsKnown(null));
        }

        // ================================================================ ② 设置层

        [Fact]
        public void 设置归一化_认不出的Id丢掉_认得的统一写法并保持稳定()
        {
            List<string> normalized = SpecialExtractionRules.Normalize(new[]
            {
                "  不存在的规则 ", "singlecontentlayer", SpecialExtractionRules.SingleContentLayer, string.Empty
            });

            Assert.Equal(new[] { SpecialExtractionRules.SingleContentLayer }, normalized);

            // 幂等：归一化过的东西再归一化一次结果不变（"保持稳定"）。
            Assert.Equal(normalized, SpecialExtractionRules.Normalize(normalized));
        }

        [Fact]
        public void 设置归一化_null取默认集_空列表保持空()
        {
            // 旧配置里没有这个字段（反序列化后是 null）→ 取默认集。
            Assert.Equal(
                SpecialExtractionRules.DefaultEnabledIds,
                SpecialExtractionRules.Normalize(null));

            // 用户把规则**全部关掉**（空列表）→ 必须保持空，否则"关掉全部 = 与现在完全一样"存不住。
            Assert.Empty(SpecialExtractionRules.Normalize(Array.Empty<string>()));
        }

        [Fact]
        public void 新配置_总开关默认关_规则清单预置默认集()
        {
            AppSettings defaults = AppSettings.CreateDefault();

            Assert.False(defaults.UseSpecialExtraction, "默认关：关掉时行为必须与现在逐字相同");
            Assert.Equal(
                SpecialExtractionRules.DefaultEnabledIds,
                defaults.SpecialExtractionRules);
        }

        [Fact]
        public void 设置落盘读回_总开关与规则清单都在()
        {
            var pathService = new PathService { DataRootDirectory = _root };
            var service = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();
            settings.UseSpecialExtraction = true;
            settings.SpecialExtractionRules = new List<string> { SpecialExtractionRules.SingleContentLayer };

            service.Save(settings);

            AppSettings loaded = service.Load();

            Assert.True(loaded.UseSpecialExtraction);
            Assert.Equal(
                new[] { SpecialExtractionRules.SingleContentLayer },
                loaded.SpecialExtractionRules);

            // 关掉全部规则之后也要读得回来（空列表 ≠ 缺字段）。
            loaded.SpecialExtractionRules = new List<string>();
            service.Save(loaded);

            Assert.Empty(service.Load().SpecialExtractionRules!);
        }

        [Fact]
        public void 旧配置缺这两个字段_不炸且取到默认值()
        {
            var pathService = new PathService { DataRootDirectory = _root };
            var service = new SettingsService(pathService);

            // 旧版 appsettings.json：既没有 UseSpecialExtraction，也没有 SpecialExtractionRules。
            File.WriteAllText(
                pathService.SettingsFilePath,
                "{ \"RecursionMode\": \"SingleChain\", \"MaxRecursionDepth\": 4 }",
                new UTF8Encoding(false));

            AppSettings loaded = service.Load();

            Assert.Equal("SingleChain", loaded.RecursionMode);
            Assert.False(loaded.UseSpecialExtraction);
            Assert.Equal(SpecialExtractionRules.DefaultEnabledIds, loaded.SpecialExtractionRules);
        }

        [Fact]
        public void 旧配置里写了认不出的规则Id_读一次就丢掉()
        {
            var pathService = new PathService { DataRootDirectory = _root };
            var service = new SettingsService(pathService);

            File.WriteAllText(
                pathService.SettingsFilePath,
                "{ \"UseSpecialExtraction\": true, \"SpecialExtractionRules\": [\"上次版本的规则\", \"SingleContentLayer\"] }",
                new UTF8Encoding(false));

            AppSettings loaded = service.Load();

            Assert.True(loaded.UseSpecialExtraction);
            Assert.Equal(new[] { SpecialExtractionRules.SingleContentLayer }, loaded.SpecialExtractionRules);
        }

        [Fact]
        public void 关掉总开关时_规则清单里写着什么都不生效()
        {
            var settings = new AppSettings
            {
                UseSpecialExtraction = false,
                SpecialExtractionRules = new List<string> { SpecialExtractionRules.SingleContentLayer }
            };

            SpecialExtractionPlan plan = SpecialExtractionPlan.FromSettings(settings);

            Assert.False(plan.IsActive);
            Assert.Equal(SpecialExtractionEffect.None, plan.Effect);
            Assert.Empty(plan.Rules);
            Assert.Equal(string.Empty, plan.Describe());

            // 打开总开关之后才真的生效。
            settings.UseSpecialExtraction = true;

            SpecialExtractionPlan on = SpecialExtractionPlan.FromSettings(settings);

            Assert.True(on.IsActive);
            Assert.Equal(SpecialExtractionEffect.SingleContentLayer, on.Effect);
            Assert.Contains("每个包只留一层内容", on.RuleNames, StringComparison.Ordinal);
            Assert.Contains(StatusText.SpecialExtractionNoteFormat.Replace("{0}", string.Empty), on.Describe(), StringComparison.Ordinal);
        }

        // ================================================================ ③ 定稿规划（判定表的例外档）

        /// <summary>
        /// 单链多层包：<c>stage\A\内容文件夹\payload.txt</c>。
        ///
        /// <list type="bullet">
        /// <item><description>规则**关** → 判定表 4：套出来的那一层是"最深层那个文件夹名"，
        /// 产物 <c>222\1111\内容文件夹\payload.txt</c>；</description></item>
        /// <item><description>规则**开** → 那一层不套，内容物直接落成品目录：<c>222\1111\payload.txt</c>。</description></item>
        /// </list>
        /// </summary>
        [Fact]
        public void 定稿规划_单链多层包_规则开时不再套那一层()
        {
            string stage = CreateStage(("A\\内容文件夹\\payload.txt", "payload"));
            string destination = Path.Combine(_root, "222", "1111");

            ExtractionCoordinator.FinalLayoutPlan off = ExtractionCoordinator.PlanFinalLayout(
                stage, destination, sharedOutputRoot: false, "1111", TerminalLayoutMode.KeepLastFolder, Off());

            ExtractionCoordinator.FinalLayoutPlan on = ExtractionCoordinator.PlanFinalLayout(
                stage, destination, sharedOutputRoot: false, "1111", TerminalLayoutMode.KeepLastFolder, On());

            // 关：判定表 4（单链塌缩到最深层那个文件夹名），那一层是 内容文件夹。
            Assert.Equal(FinalizeLayoutKind.CollapseSingleChain, off.Layout);
            Assert.False(off.SpecialExtractionApplied);
            Assert.Contains(off.Moves, move => move.To == Path.Combine(destination, "内容文件夹"));

            // 开：那一层不套，内容物直接落在成品目录里 —— 用户要的那一句 222\1111\内容物。
            Assert.True(on.SpecialExtractionApplied);
            Assert.Contains(on.Moves, move => move.To == Path.Combine(destination, "payload.txt"));
            Assert.DoesNotContain(on.Moves, move => move.To == Path.Combine(destination, "内容文件夹"));

            // 反向断言：**包名那一层永远保留**，内容物绝不许落到 222\ 去。
            string packageLayer = Path.GetDirectoryName(destination)!;

            foreach (PlannedMove move in on.Moves)
            {
                Assert.StartsWith(destination + Path.DirectorySeparatorChar, move.To, StringComparison.OrdinalIgnoreCase);
                Assert.False(
                    string.Equals(Path.GetDirectoryName(move.To), packageLayer, StringComparison.OrdinalIgnoreCase),
                    $"内容物落到包名层之外了：{move.To}");
            }
        }

        /// <summary>
        /// 多分支包：<c>stage\A\X\…</c> 与 <c>stage\A\Y\…</c>（两个并列文件夹）。
        ///
        /// <para>
        /// 规则开着也**不塌**（去掉一层就分不清哪个才是内容物），按原判定表保守套一层；
        /// 而且必须写一条 WARN 说清"为什么这条没按特定解压走"（**绝不静默**）。
        /// 产物与关掉时**逐字相同**。
        /// </para>
        /// </summary>
        [Fact]
        public void 定稿规划_多个并列文件夹_不塌且有WARN说明原因()
        {
            string stage = CreateStage(
                ("A\\X\\a.txt", "a"),
                ("A\\Y\\b.txt", "b"));

            string destination = Path.Combine(_root, "222", "1111");

            ExtractionCoordinator.FinalLayoutPlan off = ExtractionCoordinator.PlanFinalLayout(
                stage, destination, sharedOutputRoot: false, "1111", TerminalLayoutMode.KeepLastFolder, Off());

            ExtractionCoordinator.FinalLayoutPlan on = ExtractionCoordinator.PlanFinalLayout(
                stage, destination, sharedOutputRoot: false, "1111", TerminalLayoutMode.KeepLastFolder, On());

            Assert.False(on.SpecialExtractionApplied);
            Assert.Contains(on.Moves, move => move.To == Path.Combine(destination, "A"));

            // 产物与关掉时逐字相同。
            Assert.Equal(Targets(off), Targets(on));

            // 绝不静默：WARN 要说清是哪条规则、为什么没按它走、包里有几个并列文件夹。
            string warning = Assert.Single(on.Warnings, line => line.Contains("特定解压", StringComparison.Ordinal));

            Assert.Contains("每个包只留一层内容", warning, StringComparison.Ordinal);
            Assert.Contains("并列", warning, StringComparison.Ordinal);
            Assert.Contains("2", warning, StringComparison.Ordinal);
        }

        /// <summary>
        /// 几个包**共用同一个成品目录**（"添加文件夹 + 指定位置"那一档）时同样不塌：
        /// 那一层就是"包名那一层"，去掉它会把几个包的内容物倒进同一个目录。
        /// </summary>
        [Fact]
        public void 定稿规划_共用成品目录_不塌且有WARN()
        {
            string stage = CreateStage(("A\\内容文件夹\\payload.txt", "payload"));
            string destination = Path.Combine(_root, "BBB", "222");

            ExtractionCoordinator.FinalLayoutPlan on = ExtractionCoordinator.PlanFinalLayout(
                stage, destination, sharedOutputRoot: true, "1111", TerminalLayoutMode.KeepLastFolder, On());

            Assert.False(on.SpecialExtractionApplied);
            Assert.Contains(on.Moves, move => move.To == Path.Combine(destination, "内容文件夹"));

            string warning = Assert.Single(on.Warnings, line => line.Contains("特定解压", StringComparison.Ordinal));

            Assert.Contains("共用", warning, StringComparison.Ordinal);
        }

        /// <summary>
        /// 只有内容文件、没有并列文件夹时也塌（规格 §3.5 明写的那一种），
        /// 而**其余物位置一个字都不变**：仍在 <c>成品目录\其余物\</c>。
        /// </summary>
        [Fact]
        public void 定稿规划_其余物位置开与关都不变()
        {
            string stage = CreateStage(
                ("A\\内容文件夹\\payload.txt", "payload"),
                ("inner.7z", "not a real archive, just a marked artifact"));

            string destination = Path.Combine(_root, "222", "1111");
            string expectedArtifactRoot = Path.Combine(destination, ProcessArtifactLayout.ArtifactDirectoryName);

            ExtractionCoordinator.FinalLayoutPlan off = ExtractionCoordinator.PlanFinalLayout(
                stage, destination, sharedOutputRoot: false, "1111", TerminalLayoutMode.KeepLastFolder, Off());

            ExtractionCoordinator.FinalLayoutPlan on = ExtractionCoordinator.PlanFinalLayout(
                stage, destination, sharedOutputRoot: false, "1111", TerminalLayoutMode.KeepLastFolder, On());

            Assert.Equal(expectedArtifactRoot, off.ProcessArtifactDirectory);
            Assert.Equal(expectedArtifactRoot, on.ProcessArtifactDirectory);

            Assert.Contains(on.Moves, move => move.To == Path.Combine(expectedArtifactRoot, "inner.7z"));
            Assert.Contains(off.Moves, move => move.To == Path.Combine(expectedArtifactRoot, "inner.7z"));

            // 内容物那一层不会把"其余物"顶掉（重名兜底仍走既有实现）。
            Assert.DoesNotContain(on.Moves, move => move.To == expectedArtifactRoot);
        }

        [Fact]
        public void 定稿规划_规则关着时与不传特定解压逐字相同()
        {
            string stage = CreateStage(("A\\内容文件夹\\payload.txt", "payload"));
            string destination = Path.Combine(_root, "222", "1111");

            ExtractionCoordinator.FinalLayoutPlan none = ExtractionCoordinator.PlanFinalLayout(
                stage, destination, sharedOutputRoot: false, "1111");

            ExtractionCoordinator.FinalLayoutPlan offPlan = ExtractionCoordinator.PlanFinalLayout(
                stage, destination, sharedOutputRoot: false, "1111", TerminalLayoutMode.KeepLastFolder, Off());

            Assert.Equal(Targets(none), Targets(offPlan));
            Assert.Equal(none.Summary, offPlan.Summary);
            Assert.False(offPlan.SpecialExtractionApplied);
        }

        // ================================================================ ④ 界面按注册表渲染

        /// <summary>
        /// **可扩展性**（用户 2026-09-24 的重点："以后可能会经常加的东西"）：
        /// 临时往注册表里塞一条假描述 → ②页那一栏的列表与①页开关的 ToolTip **自动多一条**，
        /// 界面骨架（XAML）一个字都不用改。
        /// </summary>
        [Fact]
        public void 塞一条假规则_两页的列表里都自动多一条_界面骨架不动()
        {
            int baseline = new SettingsViewModel().SpecialExtractionRules.Count;

            var fake = new SpecialExtractionRule
            {
                Id = "FutureRuleForTest",
                Name = "以后才有的规则",
                Description = "这条只存在于测试里：它证明加规则不用改界面骨架。",
                EnabledByDefault = false
            };

            using (SpecialExtractionRules.RegisterForTesting(fake))
            {
                var viewModel = new SettingsViewModel(
                    AppSettings.CreateDefault(),
                    new SettingsService(new PathService { DataRootDirectory = _root }));

                // ②页那一栏：自动多一条，名称与说明都来自注册表。
                Assert.Equal(baseline + 1, viewModel.SpecialExtractionRules.Count);

                SettingsViewModel.SpecialExtractionRuleItem added = Assert.Single(
                    viewModel.SpecialExtractionRules,
                    item => item.Id == fake.Id);

                Assert.Equal(fake.Name, added.Name);
                Assert.Equal(fake.Description, added.Description);
                Assert.Equal(fake.Description, added.ToolTip);
                Assert.False(added.IsEnabled, "默认不勾的规则不该被自动勾上");

                // ①页开关的 ToolTip：勾上之后必须能列出它（用户要的"当前启用的规则"）。
                added.IsEnabled = true;

                Assert.Contains(fake.Name, viewModel.SpecialExtractionToolTip, StringComparison.Ordinal);
                Assert.Contains(fake.Id, viewModel.Settings.SpecialExtractionRules!, StringComparer.Ordinal);

                // 再关掉：ToolTip 里不再有它，设置里也不再留它。
                added.IsEnabled = false;

                Assert.DoesNotContain(fake.Name, viewModel.SpecialExtractionToolTip, StringComparison.Ordinal);
                Assert.DoesNotContain(fake.Id, viewModel.Settings.SpecialExtractionRules!);
            }

            // 令牌释放后注册表回到原样（不能把假规则留给别的用例）。
            Assert.DoesNotContain(SpecialExtractionRules.All, item => item.Id == fake.Id);
        }

        [Fact]
        public void 规则开关写回设置清单_并按注册表顺序归一化()
        {
            var settings = AppSettings.CreateDefault();
            settings.SpecialExtractionRules = new List<string> { "认不出的规则" };

            var viewModel = new SettingsViewModel(settings, new SettingsService(new PathService { DataRootDirectory = _root }));

            SettingsViewModel.SpecialExtractionRuleItem item = Assert.Single(viewModel.SpecialExtractionRules);
            item.IsEnabled = true;

            Assert.Equal(new[] { SpecialExtractionRules.SingleContentLayer }, viewModel.Settings.SpecialExtractionRules);

            item.IsEnabled = false;

            Assert.Empty(viewModel.Settings.SpecialExtractionRules!);
            Assert.Equal(StatusText.SpecialExtractionNoRuleHint, viewModel.SpecialExtractionToolTip);
        }

        [Fact]
        public void 总开关绑的是设置里那一格_清单里的规则决定ToolTip()
        {
            var viewModel = new SettingsViewModel(
                AppSettings.CreateDefault(),
                new SettingsService(new PathService { DataRootDirectory = _root }));

            // ①页那个开关直接绑设置对象（SettingsEditor.Settings.UseSpecialExtraction）——
            // 这里钉住"它读写的确实是同一个设置项"。
            Assert.False(viewModel.Settings.UseSpecialExtraction);

            viewModel.Settings.UseSpecialExtraction = true;

            Assert.True(viewModel.Settings.UseSpecialExtraction);

            // ToolTip 列的是**清单里勾上的规则**（与总开关无关）：用户要能一眼看出打开开关会发生什么。
            Assert.Contains("每个包只留一层内容", viewModel.SpecialExtractionToolTip, StringComparison.Ordinal);

            SettingsViewModel.SpecialExtractionRuleItem item = Assert.Single(viewModel.SpecialExtractionRules);
            item.IsEnabled = false;

            Assert.Equal(StatusText.SpecialExtractionNoRuleHint, viewModel.SpecialExtractionToolTip);
        }

        /// <summary>
        /// 界面骨架的**证据**：两页都是"绑一个集合 / 绑一个 ToolTip"，没有写死任何一条规则 ——
        /// 所以上面那条"塞一条假规则就自动多一条"才成立。
        /// </summary>
        [Fact]
        public void 两页的界面骨架按注册表渲染_没有写死规则()
        {
            string extraction = Read("Views", "Tabs", "ExtractionTab.xaml");
            string task = Read("Views", "Tabs", "TaskTab.xaml");

            // ②页：规则列表绑集合，行模板里的名称/说明/开关都来自条目本身。
            Assert.Contains(
                "ItemsSource=\"{Binding SettingsEditor.SpecialExtractionRules}\"",
                extraction,
                StringComparison.Ordinal);

            Assert.Contains("IsChecked=\"{Binding IsEnabled, Mode=TwoWay}\"", extraction, StringComparison.Ordinal);
            Assert.Contains("{Binding Name, Mode=OneWay}", extraction, StringComparison.Ordinal);
            Assert.Contains("{Binding Description, Mode=OneWay}", extraction, StringComparison.Ordinal);

            // ①页：一键处理旁边的开关 + 列出当前启用规则的 ToolTip。
            Assert.Contains("SettingsEditor.Settings.UseSpecialExtraction", task, StringComparison.Ordinal);
            Assert.Contains("SettingsEditor.SpecialExtractionToolTip", task, StringComparison.Ordinal);

            // 「关掉全部 = 与现在完全一样」这句必须写在界面上（②页那一栏的开场白里）。
            Assert.Contains("StatusText.SpecialExtractionRulesIntro", extraction, StringComparison.Ordinal);

            // 那条规则的**名字**不许出现在 XAML 里（写死了就谈不上"可加"）。
            Assert.DoesNotContain("每个包只留一层内容", extraction, StringComparison.Ordinal);
        }

        // ================================================================ 工具

        private static SpecialExtractionPlan Off() => SpecialExtractionPlan.Off;

        private static SpecialExtractionPlan On() => SpecialExtractionPlan.FromSettings(new AppSettings
        {
            UseSpecialExtraction = true,
            SpecialExtractionRules = new List<string> { SpecialExtractionRules.SingleContentLayer }
        });

        private static List<string> Targets(ExtractionCoordinator.FinalLayoutPlan plan) =>
            plan.Moves.Select(move => move.To).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();

        private string CreateStage(params (string RelativePath, string Content)[] files)
        {
            string stage = Path.Combine(_root, "stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);

            foreach ((string relativePath, string content) in files)
            {
                string path = Path.Combine(stage, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content, new UTF8Encoding(false));
            }

            return stage;
        }

        private static string Read(params string[] segments)
        {
            string root = XamlBindingScan.RepositoryRoot;
            string path = Path.Combine(new[] { root, "src", "ArchiveFixer" }.Concat(segments).ToArray());

            Assert.True(File.Exists(path), $"读不到文件：{path}");

            return File.ReadAllText(path);
        }
    }
}
