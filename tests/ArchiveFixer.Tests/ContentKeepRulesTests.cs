using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Security;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **「内容物保留关键词」判据 + 三个消费点**（用户 2026-10-04 拍板的新功能
    /// 「内容物压缩文件不解压」；端到端那条路见 <c>ContentKeepKeywordPipelineTests</c>）。
    ///
    /// <para><b>用户原话</b>：「用户可以在里面输入像，<c>1_名字里面包含特定字符的压缩文件不解压</c>，
    /// 你甚至不用去检测他是否是压缩文件，只要文件名里面包含着这个字符就不能动，例如 <c>小明</c>，
    /// 只要内容物里面有文件的名称包含了"小明"的这些压缩文件碰都不要碰，例如 <c>小明.zip</c>、
    /// <c>小明.part1.rar</c>，而且不仅仅是相对于的，包含的也同样是，例如 <c>小明和小红.7z</c>
    /// 这种包含的也不要碰」。</para>
    ///
    /// <para><b>红检</b>：把唯一出口 <see cref="ContentKeepRules.FindMatch"/> 改成恒 <c>null</c>
    /// （= 这个功能从不命中）⇒ 本文件里所有"命中"的用例一起变红
    /// （第一条就是 `命中即留_包含即命中_大小写不敏感`）。</para>
    /// </summary>
    public sealed class ContentKeepRulesTests : IDisposable
    {
        private readonly string _root;

        public ContentKeepRulesTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerContentKeep", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // 临时目录清不掉不影响结论。
            }
        }

        // ================================================================ 判据本体（纯函数）

        /// <summary>
        /// 用户点名的那四个形状 + 两个"不该命中"的对照：**包含即命中**，⛔ 不检测它是不是压缩文件。
        /// </summary>
        [Fact]
        public void 命中即留_包含即命中_大小写不敏感()
        {
            ContentKeepRules rules = ContentKeepRules.FromKeywords(new[] { "小明" });

            // 用户点名的四个：压缩包三种 + 一个**非压缩**的（命中与否只看名字）。
            Assert.True(rules.ShouldKeep("小明.zip"));
            Assert.True(rules.ShouldKeep("小明.part1.rar"));
            Assert.True(rules.ShouldKeep("小明和小红.7z"));
            Assert.True(rules.ShouldKeep("小明.txt"));
            Assert.Equal("小明", rules.FindMatch("小明.zip"));

            // 对照：不含关键词的名字一个都不拦。
            Assert.False(rules.ShouldKeep("小红.zip"));
            Assert.False(rules.ShouldKeep("xiaoming.zip"));
            Assert.Null(rules.FindMatch("小红和小刚.7z"));

            // 大小写不敏感（OrdinalIgnoreCase）。
            ContentKeepRules latin = ContentKeepRules.FromKeywords(new[] { "Ming" });

            Assert.True(latin.ShouldKeep("ming.zip"));
            Assert.True(latin.ShouldKeep("X-MING-01.7z"));
            Assert.False(latin.ShouldKeep("mingo.zip".Replace("mingo", "hong", StringComparison.Ordinal)));
        }

        /// <summary>判据**只吃文件名**：目录名里有关键词不算命中（用户说的是"内容物里的文件"）。</summary>
        [Fact]
        public void 只吃文件名_目录名不算()
        {
            ContentKeepRules rules = ContentKeepRules.FromKeywords(new[] { "小明" });

            Assert.False(rules.ShouldKeep(@"C:\out\小明\正常.zip"));
            Assert.True(rules.ShouldKeep(@"C:\out\小红\小明.zip"));
        }

        /// <summary>
        /// 空白行 / 空关键词忽略；关键词本身带空白会被 Trim；重复项去重（保持用户顺序）。
        /// </summary>
        [Fact]
        public void 空白行与空关键词忽略_重复去重()
        {
            List<string> normalized = ContentKeepRules.NormalizeKeywords(
                new[] { "小明", string.Empty, "   ", " 小明 ", "小红", "小红" });

            Assert.Equal(new[] { "小明", "小红" }, normalized);

            ContentKeepRules rules = ContentKeepRules.FromKeywords(normalized);

            Assert.Equal(2, rules.Keywords.Count);
            Assert.False(rules.ShouldKeep("   "));
            Assert.Null(rules.FindMatch(null));
        }

        /// <summary>⛔ **空列表 = 这个功能不生效**（回归命根：行为与加这条功能之前逐字相同）。</summary>
        [Fact]
        public void 空列表_一个都不拦()
        {
            foreach (ContentKeepRules rules in new[]
                     {
                         ContentKeepRules.Empty,
                         ContentKeepRules.FromKeywords(null),
                         ContentKeepRules.FromKeywords(new[] { string.Empty, "  " }),
                         ContentKeepRules.FromSettings(new AppSettings())
                     })
            {
                Assert.True(rules.IsEmpty);
                Assert.False(rules.ShouldKeep("小明.zip"));
                Assert.Null(rules.FindMatch("小明.zip"));
            }
        }

        /// <summary>
        /// ⛔ **不做通配 / 正则**：关键词是按字面包含比的（用户没要求，"<c>*</c>"就是普通字符）。
        /// </summary>
        [Fact]
        public void 不做通配也不做正则()
        {
            ContentKeepRules rules = ContentKeepRules.FromKeywords(new[] { "*.zip" });

            Assert.True(rules.ShouldKeep("a*.zip"));
            Assert.False(rules.ShouldKeep("a.zip"));
            Assert.False(rules.ShouldKeep("abc.zip"));

            /*
             * 正则里的元字符同样只是普通字符（⛔ 不许把关键词当正则编译）。
             *
             * ⚠ 这里刻意**不用反斜杠**（`\d`）：`\` 是路径分隔符，`FindMatch` 只吃**文件名**
             * （`Path.GetFileName`），带反斜杠的"文件名"本来就不是一个合法文件名 —— 那是另一个话题，
             * 别把两件事混在一条用例里。
             */
            ContentKeepRules regexLike = ContentKeepRules.FromKeywords(new[] { "小明[0-9]+" });

            Assert.True(regexLike.ShouldKeep("小明[0-9]+1.zip"));
            Assert.False(regexLike.ShouldKeep("小明1.zip"));

            // 点号在正则里"匹配任意字符"，这里必须是逐字比：`小明.` 命中 `小明.zip`（字面包含），
            // 但**不**命中 `小明Xzip`（点号不是通配）。
            ContentKeepRules dot = ContentKeepRules.FromKeywords(new[] { "小明." });

            Assert.True(dot.ShouldKeep("小明.zip"));
            Assert.False(dot.ShouldKeep("小明Xzip"));
        }

        /// <summary>设置层归一化（唯一实现转调 <see cref="ContentKeepRules.NormalizeKeywords"/>）。</summary>
        [Fact]
        public void 设置归一化_去空白去重()
        {
            var settings = new AppSettings { ContentKeepKeywords = new List<string> { " 小明 ", string.Empty, "小明", "小红" } };

            settings.Normalize();

            Assert.Equal(new[] { "小明", "小红" }, settings.ContentKeepKeywords);

            // 旧配置里没有这个字段（null）⇒ 空列表，不报错。
            var legacy = new AppSettings { ContentKeepKeywords = null };

            legacy.Normalize();

            Assert.Empty(legacy.ContentKeepKeywords!);
        }

        /// <summary>
        /// **「要有记忆」**（用户原话）：关键词列表跟着 <c>appsettings.json</c> 一起落盘、重启之后还在。
        /// </summary>
        [Fact]
        public void 设置落盘再读回来_关键词还在()
        {
            string dataRoot = Path.Combine(_root, "data");

            Directory.CreateDirectory(dataRoot);

            var pathService = new PathService { DataRootDirectory = dataRoot };
            var service = new SettingsService(pathService);

            AppSettings settings = AppSettings.CreateDefault();

            settings.ContentKeepKeywords = new List<string> { "小明", " 小红 ", "小明" };
            settings.Normalize();

            Assert.True(service.Save(settings), "设置必须能落盘");

            AppSettings reloaded = service.Load();

            Assert.Equal(new[] { "小明", "小红" }, reloaded.ContentKeepKeywords);
            Assert.True(ContentKeepRules.FromSettings(reloaded).ShouldKeep("小明.part1.rar"));
        }

        // ================================================================ 消费点 ②：链尾收内层包不搬

        /// <summary>
        /// 链尾把内层包收进其余物那一步（<see cref="ExtractionCoordinator.PlanChainInnerPackageMoves"/>）：
        /// 命中关键词的**不搬**（"碰都不碰"），而且要如实点名；没命中的照旧搬。
        /// </summary>
        [Fact]
        public void 链尾收内层包_命中的不搬_没命中的照旧搬()
        {
            string output = Path.Combine(_root, "out", "outer");
            string rest = Path.Combine(output, ProcessArtifactLayout.ArtifactDirectoryName);

            Directory.CreateDirectory(rest);

            var root = new ArchiveTask(Path.Combine(_root, "outer.7z"), 1)
            {
                FileName = "outer.7z",
                OutputPath = output,
                ContentDirectoryPath = output
            };

            string kept = Path.Combine(output, "小明.zip");
            string moved = Path.Combine(output, "小红.zip");

            File.WriteAllText(kept, "留着");
            File.WriteAllText(moved, "搬走");

            var keptTask = new ArchiveTask(kept, 2)
            {
                FileName = "小明.zip",
                Outcome = TaskOutcome.Succeeded,
                IsOutputVerified = true,
                IsArchive = true,
                DetectedFormat = "ZIP",
                ParentOutputDirectory = output,
                ParentTaskName = "outer"
            };

            var movedTask = new ArchiveTask(moved, 3)
            {
                FileName = "小红.zip",
                Outcome = TaskOutcome.Succeeded,
                IsOutputVerified = true,
                IsArchive = true,
                DetectedFormat = "ZIP",
                ParentOutputDirectory = output,
                ParentTaskName = "outer"
            };

            Assert.True(keptTask.IsContinuationTask);
            Assert.True(movedTask.IsContinuationTask);

            List<(string From, string To)> moves = ExtractionCoordinator.PlanChainInnerPackageMoves(
                root,
                new[] { root, keptTask, movedTask },
                rest,
                output,
                ContentKeepRules.FromKeywords(new[] { "小明" }),
                out List<string> warnings);

            Assert.Single(moves);
            Assert.Equal(moved, moves[0].From);
            Assert.Contains(warnings, line => line.Contains("小明.zip", StringComparison.Ordinal)
                                             && line.Contains("内容物保留关键词", StringComparison.Ordinal));

            // 空判据（默认档）= 老行为：两个都搬（回归命根）。
            List<(string From, string To)> all = ExtractionCoordinator.PlanChainInnerPackageMoves(
                root,
                new[] { root, keptTask, movedTask },
                rest,
                output,
                ContentKeepRules.Empty,
                out _);

            Assert.Equal(2, all.Count);
        }

        // ================================================================ 消费点 ③：其余物删除不许删到它

        /// <summary>
        /// **整目录删与"里面有命中项"冲突 ⇒ 整份不删 + 一行点名**（用户要求"碰都不碰"；
        /// 兜底永远落在"什么都不做"那一档）。
        /// </summary>
        [Fact]
        public void 其余物删除_里面有命中项就整份不删并点名()
        {
            (ArchiveTask task, _, string rest) = CreateRest("source.7z", "小明.zip", "内容物.bin");

            RestPurgeOutcome outcome = Purge(task, ContentKeepRules.FromKeywords(new[] { "小明" }));

            Assert.False(outcome.Attempted);
            Assert.False(outcome.Succeeded);
            Assert.Contains("小明.zip", outcome.Message, StringComparison.Ordinal);
            Assert.Contains("内容物保留关键词", outcome.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(rest), "有命中项时其余物必须原封不动");
            Assert.True(File.Exists(Path.Combine(rest, "小明.zip")));
        }

        /// <summary>对照：同一个其余物、判据为空（默认档）⇒ 照旧整份删掉（回归命根）。</summary>
        [Fact]
        public void 其余物删除_空判据照旧删_没命中的也照旧删()
        {
            (ArchiveTask emptyRulesTask, _, string emptyRest) = CreateRest("source.7z", "小明.zip", "内容物.bin");

            RestPurgeOutcome degraded = Purge(emptyRulesTask, ContentKeepRules.Empty);

            Assert.True(degraded.Succeeded);
            Assert.False(Directory.Exists(emptyRest));

            // 有判据、但这一份其余物里没有命中项 ⇒ 也照旧删。
            (ArchiveTask noHitTask, _, string noHitRest) = CreateRest("source.7z", "小红.zip", "内容物.bin");

            RestPurgeOutcome noHit = Purge(noHitTask, ContentKeepRules.FromKeywords(new[] { "小明" }));

            Assert.True(noHit.Succeeded);
            Assert.False(Directory.Exists(noHitRest));
        }

        /// <summary>
        /// ⚠ **作用范围只在"内容物"**：用户导入的**源包**不套用这条 —— 真机里 <c>小明.zip</c>
        /// 是别人给的包时要照常处理（这里 = 其余物里那一份源包照旧按设置删掉）。
        /// </summary>
        [Fact]
        public void 其余物删除_源包不套用关键词_照常按设置删()
        {
            (ArchiveTask task, string sourcePath, string rest) = CreateRest("小明.zip");

            Assert.Equal("小明.zip", Path.GetFileName(sourcePath));

            RestPurgeOutcome outcome = Purge(task, ContentKeepRules.FromKeywords(new[] { "小明" }));

            Assert.True(outcome.Succeeded, outcome.Message);
            Assert.False(Directory.Exists(rest));
        }

        /// <summary>部分完成那一档的"半份清理"同样不许删到命中项（判据在同一个执行体里）。</summary>
        [Fact]
        public void 半份清理_命中项也一个字节都不删()
        {
            (ArchiveTask task, string sourcePath, string rest) = CreateRest("source.7z", "小明.zip");

            RestPurgeOutcome outcome = new RestItemPurger(
                    null,
                    null,
                    ContentKeepRules.FromKeywords(new[] { "小明" }))
                .PurgeExcept(task, new[] { sourcePath }, DeleteMode.Permanent);

            Assert.False(outcome.Attempted);
            Assert.True(Directory.Exists(rest));
        }

        private static RestPurgeOutcome Purge(ArchiveTask task, ContentKeepRules rules) =>
            new RestItemPurger(null, null, rules).Purge(task, cancelled: false, DeleteMode.Permanent);

        /// <summary>造一个"该动手的其余物"：源包叫 <paramref name="sourceName"/>，其余物里另有 <paramref name="restEntries"/>。</summary>
        private (ArchiveTask Task, string SourcePath, string RestDirectory) CreateRest(
            string sourceName,
            params string[] restEntries)
        {
            string output = Path.Combine(_root, "case-" + Guid.NewGuid().ToString("N"));
            string rest = Path.Combine(output, ProcessArtifactLayout.ArtifactDirectoryName);
            string sourcePath = Path.Combine(rest, sourceName);

            Directory.CreateDirectory(rest);

            File.WriteAllText(sourcePath, "源包");
            File.WriteAllText(Path.Combine(output, "content.bin"), "内容物");

            foreach (string entry in restEntries)
            {
                File.WriteAllText(Path.Combine(rest, entry), "过程物");
            }

            var task = new ArchiveTask(sourcePath, 1)
            {
                FileName = Path.GetFileName(sourcePath),
                CurrentPath = sourcePath,
                OutputPath = output,
                RestDirectoryPath = rest,
                Outcome = TaskOutcome.Succeeded,
                IsOutputVerified = true,
                OutputManifestCrossChecked = true
            };

            return (task, sourcePath, rest);
        }
    }
}
