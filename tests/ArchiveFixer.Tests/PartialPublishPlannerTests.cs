using ArchiveFixer.Extraction;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **「部分完成」敢不敢发布、发哪些** —— 判据级守门用例（纯函数，不碰盘、不调引擎）。
    ///
    /// <para>现场（用户 2026-10-01 真机 `giu910`）：19 GB 的 7z `-mhe`，解到 98% 时**只有 1 个** mp4
    /// 的 CRC 失败，557 个文件都好 —— 老口径整份丢弃（13 分钟白跑）。
    /// 用户定了口径之后（见 `docs/部分完成发布方案.md`）：把"逐条对过账、确认是好的"那些放进
    /// `<包名>\部分完成\`，⛔ 但**判不出就一个字节都不发**。</para>
    ///
    /// <para>本文件只钉**判据**：逐条对账 + 四道闸门。发布动作本身（子目录、让位、清理）在别处测。</para>
    /// </summary>
    public class PartialPublishPlannerTests
    {
        private readonly ITestOutputHelper _output;

        public PartialPublishPlannerTests(ITestOutputHelper output) => _output = output;

        private static PartialPublishPlanner.SizeProbe Disk(params (string Path, long Size)[] files)
        {
            var map = new Dictionary<string, long>(System.StringComparer.OrdinalIgnoreCase);

            foreach ((string path, long size) in files)
            {
                map[path.Replace('/', '\\')] = size;
            }

            return path => map.TryGetValue(path.Replace('/', '\\'), out long size) ? size : -1;
        }

        // ────────────────────────── 正例：giu910 的形状 ──────────────────────────

        /// <summary>558 个条目，1 个 mp4 坏（引擎点名 + 盘上半截），其余 557 个好 ⇒ 发布那 557 个。</summary>
        [Fact]
        public void 五百五十七个好一个坏_发布好的那批并点名坏的那个()
        {
            var manifest = new List<(string, long)>();
            var disk = new List<(string, long)>();

            for (int i = 0; i < 557; i++)
            {
                manifest.Add(($"content/f{i:000}.bin", 1000));
                disk.Add(($"content/f{i:000}.bin", 1000));
            }

            manifest.Add(("movie/broken.mp4", 5_000_000));
            disk.Add(("movie/broken.mp4", 1_234));   // 半截

            PartialPublishVerdict verdict = PartialPublishPlanner.Plan(
                manifest,
                Disk(disk.ToArray()),
                engineFailedEntries: new[] { "movie/broken.mp4" },
                engineReportedErrorCount: 1);

            _output.WriteLine($"{verdict.ReasonCode}：{verdict.Reason}");

            Assert.True(verdict.CanPublish, verdict.Reason);
            Assert.Equal(PartialPublishPlanner.ReasonOk, verdict.ReasonCode);
            Assert.Equal(557, verdict.Publishable.Count);

            (string Path, PartialEntryState State) rejected = Assert.Single(verdict.Rejected);
            Assert.Equal("movie/broken.mp4", rejected.Path);

            // 半截 + 被点名 ⇒ 按"大小不符"记（两条都成立时取更客观的那一条）。
            Assert.Equal(PartialEntryState.SizeMismatch, rejected.State);
        }

        /// <summary>引擎点名了它、盘上大小也对（CRC 坏但长度没变）⇒ 依旧不发它。</summary>
        [Fact]
        public void 被引擎点名但大小对得上_照样不许发布()
        {
            PartialPublishVerdict verdict = PartialPublishPlanner.Plan(
                new List<(string, long)> { ("a.bin", 100), ("b.bin", 100) },
                Disk(("a.bin", 100), ("b.bin", 100)),
                engineFailedEntries: new[] { "b.bin" },
                engineReportedErrorCount: 1);

            Assert.True(verdict.CanPublish, verdict.Reason);
            Assert.Equal(new[] { "a.bin" }, verdict.Publishable.Select(p => p.Path));

            (string Path, PartialEntryState State) rejected = Assert.Single(verdict.Rejected);
            Assert.Equal(PartialEntryState.EngineReported, rejected.State);
        }

        // ────────────────────────── 四道闸门 ──────────────────────────

        /// <summary>
        /// **最危险的那一档**：引擎自报有错，但我们**一个名字都点不出来**（中文版 UnRAR 的报错是中文）
        /// ⇒ 那个坏文件在盘上可能"大小看着对" ⇒ 一律不发布。
        /// </summary>
        [Fact]
        public void 引擎自报有错却点不出名_一个字节都不发布()
        {
            PartialPublishVerdict verdict = PartialPublishPlanner.Plan(
                new List<(string, long)> { ("a.bin", 100), ("b.bin", 100), ("c.bin", 100) },
                Disk(("a.bin", 100), ("b.bin", 100), ("c.bin", 100)),
                engineFailedEntries: System.Array.Empty<string>(),
                engineReportedErrorCount: 1);

            _output.WriteLine($"{verdict.ReasonCode}：{verdict.Reason}");

            Assert.False(verdict.CanPublish);
            Assert.Equal(PartialPublishPlanner.ReasonUnnamedEngineErrors, verdict.ReasonCode);
            Assert.Empty(verdict.Publishable);
        }

        [Fact]
        public void 清单拿不到_一个字节都不发布()
        {
            PartialPublishVerdict verdict = PartialPublishPlanner.Plan(
                manifestEntries: null,
                sizeProbe: Disk(),
                engineFailedEntries: null,
                engineReportedErrorCount: 0);

            Assert.False(verdict.CanPublish);
            Assert.Equal(PartialPublishPlanner.ReasonNoManifest, verdict.ReasonCode);
        }

        [Fact]
        public void 清单是空的_一个字节都不发布()
        {
            PartialPublishVerdict verdict = PartialPublishPlanner.Plan(
                new List<(string, long)>(),
                Disk(),
                engineFailedEntries: System.Array.Empty<string>(),
                engineReportedErrorCount: 0);

            Assert.False(verdict.CanPublish);
            Assert.Equal(PartialPublishPlanner.ReasonNoManifest, verdict.ReasonCode);
        }

        [Fact]
        public void 一个条目都没解出来_不发布()
        {
            PartialPublishVerdict verdict = PartialPublishPlanner.Plan(
                new List<(string, long)> { ("a.bin", 100), ("b.bin", 100) },
                Disk(),
                engineFailedEntries: System.Array.Empty<string>(),
                engineReportedErrorCount: 0);

            Assert.False(verdict.CanPublish);
            Assert.Equal(PartialPublishPlanner.ReasonNothingPublishable, verdict.ReasonCode);
        }

        /// <summary>缺太多：100 个里缺 50 ⇒ 不发（缺太多通常说明是密码/格式层面的问题）。</summary>
        [Fact]
        public void 缺得太多_不发布()
        {
            var manifest = new List<(string, long)>();
            var disk = new List<(string, long)>();

            for (int i = 0; i < 100; i++)
            {
                manifest.Add(($"f{i:000}.bin", 100));

                if (i < 50)
                {
                    disk.Add(($"f{i:000}.bin", 100));
                }
            }

            PartialPublishVerdict verdict = PartialPublishPlanner.Plan(
                manifest,
                Disk(disk.ToArray()),
                engineFailedEntries: System.Array.Empty<string>(),
                engineReportedErrorCount: 0);

            Assert.False(verdict.CanPublish);
            Assert.Equal(PartialPublishPlanner.ReasonTooMuchMissing, verdict.ReasonCode);
        }

        /// <summary>阈值两条满足其一即可：缺 6 个（超过条数上限）但字节占比 ≥ 95% ⇒ 照发。</summary>
        [Fact]
        public void 缺的条数超了但字节占比够_照发()
        {
            var manifest = new List<(string, long)> { ("big.bin", 1_000_000) };
            var disk = new List<(string, long)> { ("big.bin", 1_000_000) };

            for (int i = 0; i < 6; i++)
            {
                manifest.Add(($"small{i}.bin", 100));
            }

            PartialPublishVerdict verdict = PartialPublishPlanner.Plan(
                manifest,
                Disk(disk.ToArray()),
                engineFailedEntries: System.Array.Empty<string>(),
                engineReportedErrorCount: 0);

            _output.WriteLine($"{verdict.ReasonCode}：{verdict.Reason}");

            Assert.True(verdict.CanPublish, verdict.Reason);
            Assert.Equal("big.bin", Assert.Single(verdict.Publishable).Path);
            Assert.Equal(6, verdict.Rejected.Count);
        }

        /// <summary>缺的条数刚好等于上限（5）⇒ 照发。</summary>
        [Fact]
        public void 缺的条数刚好到上限_照发()
        {
            var manifest = new List<(string, long)>();

            for (int i = 0; i < 10; i++)
            {
                manifest.Add(($"f{i}.bin", 100));
            }

            PartialPublishVerdict verdict = PartialPublishPlanner.Plan(
                manifest,
                Disk(("f0.bin", 100), ("f1.bin", 100), ("f2.bin", 100), ("f3.bin", 100), ("f4.bin", 100)),
                engineFailedEntries: System.Array.Empty<string>(),
                engineReportedErrorCount: 0);

            Assert.True(verdict.CanPublish, verdict.Reason);
            Assert.Equal(5, verdict.Publishable.Count);
            Assert.Equal(PartialPublishPlanner.MaxMissingEntries, verdict.Rejected.Count);
        }

        // ────────────────────────── 对账细节 ──────────────────────────

        /// <summary>清单用 <c>/</c>、盘上那个探针用 <c>\</c>（或反过来）也要对得上：写法不同不算"缺"。</summary>
        [Fact]
        public void 路径分隔符写法不同_不许当成缺()
        {
            PartialPublishVerdict verdict = PartialPublishPlanner.Plan(
                new List<(string, long)> { ("sub/dir/a.bin", 100) },
                Disk(("sub\\dir\\a.bin", 100)),
                engineFailedEntries: System.Array.Empty<string>(),
                engineReportedErrorCount: 0);

            Assert.True(verdict.CanPublish, verdict.Reason);
            Assert.Single(verdict.Publishable);
        }

        /// <summary>引擎点名用的是另一种分隔符/大小写 ⇒ 照样要认出来（⛔ 不许因为写法不同就漏掉一个坏文件）。</summary>
        [Fact]
        public void 引擎点名的写法不同_照样认出来()
        {
            PartialPublishVerdict verdict = PartialPublishPlanner.Plan(
                new List<(string, long)> { ("sub/dir/A.bin", 100), ("ok.bin", 100) },
                Disk(("sub\\dir\\A.bin", 100), ("ok.bin", 100)),
                engineFailedEntries: new[] { "sub\\dir\\a.BIN" },
                engineReportedErrorCount: 1);

            Assert.True(verdict.CanPublish, verdict.Reason);
            Assert.Equal(new[] { "ok.bin" }, verdict.Publishable.Select(p => p.Path));
        }

        /// <summary>盘上多出来的文件（清单里没有）不进"可发布"，也不影响闸门 —— 清单才是唯一账本。</summary>
        [Fact]
        public void 清单外的文件不算可发布()
        {
            PartialPublishVerdict verdict = PartialPublishPlanner.Plan(
                new List<(string, long)> { ("a.bin", 100) },
                Disk(("a.bin", 100), ("engine-stub.tmp", 4096)),
                engineFailedEntries: System.Array.Empty<string>(),
                engineReportedErrorCount: 0);

            Assert.True(verdict.CanPublish, verdict.Reason);
            Assert.Equal(new[] { "a.bin" }, verdict.Publishable.Select(p => p.Path));
        }
    }
}
