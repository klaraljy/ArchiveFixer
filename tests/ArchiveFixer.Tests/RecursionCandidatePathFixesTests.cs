using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using static ArchiveFixer.Tests.RecursionFixHarness;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 2026-10-05 只读审计：**递归那条路（出厂默认档 = 展开所有分支）缺的几道密码侧口径**。
    ///
    /// <para>这一组钉五件事（每一条都有对应的红检：撤掉修复 ⇒ 用例变红）：</para>
    /// <list type="number">
    /// <item>本批手动输入的密码要进递归候选表（否则用户刚输对密码仍报「密码错误」）；</item>
    /// <item>候选表不在这里截断 —— 每层上限由递归核心自己判，分卷试开那一侧自己截；</item>
    /// <item>递归的密码类失败也要登记到本批（批末那句指路与"本批 N 个包没能解开"）；</item>
    /// <item>加密头包（<c>-mhe</c> / <c>-hp</c>）在递归层也跳过空密码；</item>
    /// <item>「停止后续」在递归候选循环里要有落点（正在解的那一次不打断、下一个不再试）。</item>
    /// </list>
    ///
    /// <para>能用假引擎的地方一律用假引擎（结论完全确定、不依赖真 7z 的措辞）；
    /// 只有"批末汇总"那一条用真 7z 造样本 —— 它要验的正是整条管线的接线。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class RecursionCandidatePathFixesTests : IDisposable
    {
        /// <summary>测试专用合成密码；只出现在测试数据里，不是任何真实凭据。</summary>
        private const string RightPassword = "Right-Pass-2026";

        private readonly string _root;

        public RecursionCandidatePathFixesTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerCandidateFix", Guid.NewGuid().ToString("N"));
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
                // 临时目录删不掉不影响结论。
            }
        }

        // ================================================================ ① 手动密码进递归候选表

        /// <summary>
        /// 本批手动输入的密码必须出现在**递归**那条路的候选表里，位置与单层路径一致
        /// （空密码之后、密码本之前）。
        ///
        /// <para><b>红检</b>：撤掉 <c>BuildRecursionPasswordCandidates</c> 里那次
        /// <c>InsertManualPasswordCandidates</c> ⇒ 本用例变红
        /// （<c>Assert.Equal() Failure: Collections differ … Missing: "Manual-2026-10-05"</c>）。</para>
        /// </summary>
        [Fact]
        public void 手动输入的密码也要进递归那条路的候选表()
        {
            RecursionFixHarness harness = RecursionFixHarness.Create(
                _root,
                new[] { "书里的-1", "书里的-2" },
                attemptLimit: 10);

            // 用户在本批输过的密码（单层路径那边由手动密码框写进来，这里直接等价地放进去）。
            harness.Coordinator._manualBatchPasswords.Add("Manual-2026-10-05");

            IReadOnlyList<string> candidates =
                harness.Coordinator.BuildRecursionPasswordCandidates(Path.Combine(_root, "pack.7z"));

            Assert.Equal(
                new[] { string.Empty, "Manual-2026-10-05", "书里的-1", "书里的-2" },
                candidates);
        }

        /// <summary>同一个手动密码**不许出现两次**（去重仍由既有的那个出口负责，这里钉住它没被绕过）。</summary>
        [Fact]
        public void 手动密码与密码本重名时_候选表里只留一条()
        {
            RecursionFixHarness harness = RecursionFixHarness.Create(
                _root,
                new[] { "书里的-1", "重复的密码" },
                attemptLimit: 10);

            harness.Coordinator._manualBatchPasswords.Add("重复的密码");

            IReadOnlyList<string> candidates =
                harness.Coordinator.BuildRecursionPasswordCandidates(Path.Combine(_root, "pack.7z"));

            Assert.Equal(1, candidates.Count(c => string.Equals(c, "重复的密码", StringComparison.Ordinal)));

            /*
             * 位置仍是**密码本给它的那一位**：`InsertManualPasswordCandidates` 的既有口径是
             * "已经在候选表里的值不重复插"（只对新值做"插在空密码之后"），这里一个字都没改。
             */
            Assert.Equal(new[] { string.Empty, "书里的-1", "重复的密码" }, candidates);
        }

        // ================================================================ ② 候选记账要满额

        /// <summary>
        /// 递归候选表**不在这里截断**（递归核心的层循环自己按上限停手），
        /// 而「分卷试开」那一侧必须自己截到同一个上限 —— 否则每种卷序 × 整份候选表都要跑一次试开。
        ///
        /// <para><b>红检</b>：① 把 <c>.Take(MaxPasswordAttemptsPerLayer)</c> 加回
        /// <c>BuildRecursionPasswordCandidates</c> ⇒ 第一条断言变红（13 → 10）；
        /// ② 把 <c>BuildVolumeProbePasswordCandidates</c> 里的 <c>.Take</c> 撤掉 ⇒
        /// 第二条断言变红（10 → 13）。</para>
        /// </summary>
        [Fact]
        public void 递归候选表不截断_分卷试开那一侧自己截到每层上限()
        {
            string[] book = Enumerable.Range(1, 12).Select(i => "书里的-" + i).ToArray();

            RecursionFixHarness harness = RecursionFixHarness.Create(_root, book, attemptLimit: 10);

            string archivePath = Path.Combine(_root, "pack.7z");

            IReadOnlyList<string> full = harness.Coordinator.BuildRecursionPasswordCandidates(archivePath);

            // 空密码 + 12 条密码本 = 13：**一条都不许在这里被吃掉**。
            Assert.Equal(13, full.Count);

            IReadOnlyList<string> probe = harness.Coordinator.BuildVolumeProbePasswordCandidates(archivePath);

            Assert.Equal(10, probe.Count);
            Assert.Equal(full.Take(10), probe);
        }

        /// <summary>
        /// 端到端：每层上限 10、候选 13 条，而**空密码会被"整包已加密"那一档跳过** ——
        /// 真实候选必须仍然是满额 10 个（老写法在截断之后再跳，实际只试到 9 个）。
        ///
        /// <para>用假引擎数"到底调了几次解压"，所以结论与真 7z 的措辞无关。</para>
        ///
        /// <para><b>红检</b>：把 <c>.Take(MaxPasswordAttemptsPerLayer)</c> 加回
        /// <c>BuildRecursionPasswordCandidates</c> ⇒ 解压只被调 9 次，本用例变红。</para>
        /// </summary>
        [Fact]
        public async Task 递归层每层上限是满额_跳过空密码不会让真实候选少一个()
        {
            string[] book = Enumerable.Range(1, 12).Select(i => "书里的-" + i).ToArray();

            RecursionFixHarness harness = RecursionFixHarness.Create(_root, book, attemptLimit: 10);

            var engine = new ScriptedEngine
            {
                // 清单里说有条目加密 ⇒ 空密码那一档会被跳过（这正是"少一个"的来源）。
                OnList = _ => Listing(("data.bin", 1024L)),
                OnExtract = _ => WrongPassword()
            };

            RecursiveExtractor extractor = CreateExtractor(
                engine,
                archivePath => harness.Coordinator.BuildRecursionPasswordCandidates(archivePath),
                new RecursionLimits { MaxPasswordAttemptsPerLayer = 10 },
                out _);

            RecursionResult result = await RunRecursionAsync(extractor, _root, "full-limit.7z");

            Assert.Equal(10, engine.Extracted.Count);
            Assert.DoesNotContain(string.Empty, engine.Extracted);
            Assert.Equal(RecursionStopReason.PasswordAttemptsExceeded, result.StopReason);
        }

        // ================================================================ ③ 密码类失败登记到本批

        /// <summary>
        /// 递归那条路的"密码类失败"必须登记到本批：批末那句「有 1 个包没能解开」在
        /// **出厂默认档**（展开所有分支）下也要出现。
        ///
        /// <para>真 7z 造一个加密包（不给正确密码）走完整条管线 —— 这一条验的正是"接线"，
        /// 假引擎替不了（要的就是 `RunRecursiveAsync` 那一支的收尾）。</para>
        ///
        /// <para><b>红检</b>：撤掉 B 分支收尾那次 <c>RecordPasswordFailure(task)</c> ⇒ 本用例变红
        /// （日志里再没有「有 1 个包没能解开」）。</para>
        /// </summary>
        [SevenZipFact]
        public async Task 递归那条路的密码失败也要登记到本批_批末出现那句汇总()
        {
            RecursionFixHarness.RequireSevenZip();

            string archive = BuildEncryptedPackage();

            RecursionFixHarness harness = RecursionFixHarness.Create(
                _root,
                Array.Empty<string>(),
                attemptLimit: 10,
                recursionMode: "AllBranches");

            await harness.AddTaskAsync(archive);

            await harness.Coordinator.StartExtractAsync();

            ArchiveTask task = Assert.Single(harness.Vm.Tasks);

            // 前提：这一单确实是"密码类"终态（否则这条用例什么都没测到）。
            Assert.Contains(
                task.Status,
                new[]
                {
                    StatusText.WrongPassword,
                    StatusText.PasswordAttemptLimitReached,
                    StatusText.PasswordOrCorrupted
                });

            string all = string.Join("\n", harness.LogTexts);

            Assert.Contains("有 1 个包没能解开", all, StringComparison.Ordinal);
        }

        // ================================================================ ④ 加密头包不试空密码

        /// <summary>
        /// 文件名也加密（<c>-mhe</c> / <c>-hp</c>）的包，在递归层同样**不试空密码** ——
        /// 引擎在列目录那一步就报"加密头"，空密码必然白跑一整包。
        ///
        /// <para><b>红检</b>：撤掉候选循环里那一段（按 <c>EngineErrorTypes.EncryptedHeaders</c> 跳过）
        /// ⇒ 假引擎会先收到一次空密码，本用例变红
        /// （<c>Assert.Equal() Failure: Collections differ … Actual: ["", "Right-Pass-2026"]</c>）。</para>
        /// </summary>
        [Fact]
        public async Task 加密头的包在递归层也不试空密码()
        {
            var engine = new ScriptedEngine
            {
                OnList = _ => ListingFailure(EngineErrorTypes.EncryptedHeaders),
                OnExtract = _ => WrongPassword()
            };

            RecursiveExtractor extractor = CreateExtractor(
                engine,
                _ => new[] { string.Empty, RightPassword },
                limits: null,
                out List<string> logs);

            RecursionResult result = await RunRecursionAsync(extractor, _root, "encrypted-headers.7z");

            // 空密码一次都没解过：唯一的解压调用是那个非空候选。
            Assert.Equal(new[] { RightPassword }, engine.Extracted);

            Assert.Contains(logs, line => line.Contains("跳过「空密码」", StringComparison.Ordinal));

            /*
             * ⚠ 2026-10-05 第二轮：这一档的**最终结论**已经改成「文件名已加密」那一档了
             * （这条假引擎的每一候选都列不出清单、错误类型都是加密头 ⇒ 判据成立）。
             * 本用例钉的仍是上面那句"空密码一次都没解过"；结论那一档由
             * `RecursionGateAndHintFixesTests` 里那几条（真 7z `-mhe` + 对照组 + 边界）专钉。
             *
             * 顺带说明：被跳过的空密码**没试过**，所以收尾比"候选总数 vs 试过几个"时必须把它减掉
             * （同一个意思在单层路径那边是"先 RemoveAll 再重算 maxPasswordAttempts"）——
             * 不减就会把"两个候选都试完了都不对"误报成「达到密码尝试上限（候选还有剩余）」
             * （`RecursiveExtractorTests.密码_候选全试完仍失败报WrongPassword` / `递归失败_工作区保留` 钉着）。
             */
            Assert.Equal(RecursionStopReason.EncryptedHeaders, result.StopReason);
        }

        /// <summary>
        /// 边界与既有那条"整包已加密"完全一致：**一个非空候选都没有**时照旧试空密码 ——
        /// 跳掉它会让收场落到一句更难懂的结论上。
        /// </summary>
        [Fact]
        public async Task 加密头包_一个非空候选都没有时照旧试空密码()
        {
            var engine = new ScriptedEngine
            {
                OnList = _ => ListingFailure(EngineErrorTypes.EncryptedHeaders),
                OnExtract = _ => WrongPassword()
            };

            RecursiveExtractor extractor = CreateExtractor(
                engine,
                _ => new[] { string.Empty },
                limits: null,
                out _);

            await RunRecursionAsync(extractor, _root, "encrypted-headers-only-empty.7z");

            Assert.Equal(new[] { string.Empty }, engine.Extracted);
        }

        // ================================================================ ⑤ 「停止后续」的落点

        /// <summary>
        /// 用户点了「停止后续」之后，递归这一层**剩下的候选一个都不再试** ——
        /// 但正在解的那一次不打断（与单层路径同一语义）。
        ///
        /// <para>收尾必须落在"候选还有、只是不再试"那一档
        /// （<see cref="RecursionStopReason.PasswordAttemptsExceeded"/>），
        /// ⛔ 不许报成密码错误 —— 候选根本没试完。</para>
        ///
        /// <para><b>红检</b>：撤掉候选循环开头那段 <c>StopRequested</c> 判据 ⇒
        /// 三个候选全被试完、停因变回 <see cref="RecursionStopReason.WrongPassword"/>，本用例变红。</para>
        /// </summary>
        [Fact]
        public async Task 停止后续_递归层剩下的候选不再试_也不报成密码错误()
        {
            var engine = new ScriptedEngine
            {
                OnList = _ => PlainListing(("data.bin", 512L)),
                OnExtract = _ => WrongPassword()
            };

            RecursiveExtractor extractor = CreateExtractor(
                engine,
                _ => new[] { "p1", "p2", "p3" },
                limits: null,
                out List<string> logs);

            // 用户在第一个候选解完之后点了「停止后续」。
            extractor.StopRequested = () => engine.Extracted.Count >= 1;

            RecursionResult result = await RunRecursionAsync(extractor, _root, "stop-after-current.7z");

            Assert.Equal(new[] { "p1" }, engine.Extracted);
            Assert.Equal(RecursionStopReason.PasswordAttemptsExceeded, result.StopReason);
            Assert.NotEqual(RecursionStopReason.WrongPassword, result.StopReason);

            Assert.Contains(logs, line => line.Contains("停止后续", StringComparison.Ordinal));
        }

        /// <summary>
        /// 一个候选都还没试就"已停止"时**不许跳过后面的候选**：那会让收场落到
        /// "没有可用密码"（<c>triedAny == false</c>）—— 对用户的误报。
        /// </summary>
        [Fact]
        public async Task 停止信号在第一个候选之前到达时_照旧试完那个候选()
        {
            var engine = new ScriptedEngine
            {
                OnList = _ => PlainListing(("data.bin", 512L)),
                OnExtract = _ => WrongPassword()
            };

            RecursiveExtractor extractor = CreateExtractor(
                engine,
                _ => new[] { "p1", "p2" },
                limits: null,
                out _);

            extractor.StopRequested = () => true;

            await RunRecursionAsync(extractor, _root, "stop-before-first.7z");

            Assert.Equal(new[] { "p1" }, engine.Extracted);
        }

        // ================================================================ 样本

        /// <summary>造一个真加密包（<c>-p</c>，名字不加密 ⇒ 列目录能成功，走的正是候选循环那条路）。</summary>
        private string BuildEncryptedPackage()
        {
            string stage = Path.Combine(_root, "stage-" + Guid.NewGuid().ToString("N"));
            string packages = Path.Combine(_root, "pkg-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(stage);
            Directory.CreateDirectory(packages);

            File.WriteAllText(Path.Combine(stage, "payload.bin"), "candidate-path-payload");

            string archive = Path.Combine(packages, "encrypted.7z");

            RecursionFixHarness.Run7z(
                stage,
                "a",
                "-t7z",
                "-mx0",
                "-p" + RightPassword,
                archive,
                "payload.bin");

            Assert.True(File.Exists(archive), "加密包没造出来：" + archive);

            return archive;
        }
    }
}
