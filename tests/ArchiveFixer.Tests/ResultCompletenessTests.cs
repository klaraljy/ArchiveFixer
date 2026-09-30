using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Storage;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **L4 完整性分类**（用户 2026-09-30 定的检验等级；定义见 `docs/检验等级.md`）。
    ///
    /// <para>钉三件事：</para>
    /// <list type="number">
    /// <item><description>三态判据只读机器终态（枚举 / 校验结果 / 分卷名单），⛔ 不比中文文案；</description></item>
    /// <item><description>**"判不出" ⇒ 不删源**：删源那道闸门（<see cref="RestItemPurger"/>）在"校验通过但没核对过清单"
    /// 时必须一个字节都不动；</description></item>
    /// <item><description>底线校验（没有可信清单）**如实记成** <c>ManifestCrossChecked = false</c> ——
    /// 老口径把它和"逐条核对通过"都写成 <c>Verified = true</c>，删源闸门分不开这两件事。</description></item>
    /// </list>
    /// </summary>
    public class ResultCompletenessTests : IDisposable
    {
        private readonly string _root;

        public ResultCompletenessTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerCompleteness", Guid.NewGuid().ToString("N"));
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

        // ================================================================ ① 三态判据

        [Fact]
        public void 可证完整_只有拿清单逐条核对过才算()
        {
            ResultCompletenessVerdict verdict = ResultCompletenessClassifier.Classify(new OutputVerificationResult
            {
                Verified = true,
                ManifestCrossChecked = true,
                ExpectedFileCount = 10,
                ActualFileCount = 10,
                Message = "校验通过"
            });

            Assert.Equal(ResultCompleteness.Complete, verdict.State);
            Assert.True(verdict.AllowsSourceRemoval);
            Assert.Contains("ManifestCrossChecked=True", verdict.Evidence, StringComparison.Ordinal);
        }

        [Fact]
        public void 校验判否_就是可证不完整()
        {
            ResultCompletenessVerdict verdict = ResultCompletenessClassifier.Classify(new OutputVerificationResult
            {
                Verified = false,
                ManifestCrossChecked = true,
                ExpectedFileCount = 10,
                ActualFileCount = 4,
                Message = "校验未通过：预期 10 个文件，实际 4 个"
            });

            Assert.Equal(ResultCompleteness.Incomplete, verdict.State);
            Assert.False(verdict.AllowsSourceRemoval);
        }

        /// <summary>
        /// ⛔ **本条就是"判不出"那一档**：校验写着通过，但那是**底线校验**（拿不到清单）——
        /// 机器手上没有任何证据，所以既不能说完整、也不许动源包。
        /// </summary>
        [Fact]
        public void 只做了非空底线校验_判不出完整性()
        {
            ResultCompletenessVerdict verdict = ResultCompletenessClassifier.Classify(new OutputVerificationResult
            {
                Verified = true,
                ManifestCrossChecked = false,
                ActualFileCount = 3,
                ActualTotalSize = 1024,
                Message = "未取得预期条目数，只做了非空校验"
            });

            Assert.Equal(ResultCompleteness.Undeterminable, verdict.State);
            Assert.False(verdict.AllowsSourceRemoval);
            Assert.Contains("无法确认", verdict.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 没做过校验与没有任务_都判不出()
        {
            Assert.Equal(ResultCompleteness.Undeterminable, ResultCompletenessClassifier.Classify((OutputVerificationResult?)null).State);
            Assert.Equal(ResultCompleteness.Undeterminable, ResultCompletenessClassifier.Classify((ArchiveTask?)null).State);

            var untouched = new ArchiveTask(Path.Combine(_root, "a.7z"), 1) { FileName = "a.7z" };

            Assert.Equal(ResultCompleteness.Undeterminable, ResultCompletenessClassifier.Classify(untouched).State);
            Assert.False(ResultCompletenessClassifier.Classify(untouched).AllowsSourceRemoval);
        }

        [Fact]
        public void 分卷缺失_直接就是可证不完整()
        {
            var task = new ArchiveTask(Path.Combine(_root, "b.7z.001"), 1)
            {
                FileName = "b.7z.001",
                IsOutputVerified = true,
                OutputManifestCrossChecked = true
            };

            task.MissingVolumeNames.Add("b.7z.003");

            ResultCompletenessVerdict verdict = ResultCompletenessClassifier.Classify(task);

            Assert.Equal(ResultCompleteness.Incomplete, verdict.State);
            Assert.False(verdict.AllowsSourceRemoval);
            Assert.Contains("MissingVolumeNames=1", verdict.Evidence, StringComparison.Ordinal);
        }

        [Fact]
        public void 任务上的两个字段合起来才判得出完整()
        {
            var task = new ArchiveTask(Path.Combine(_root, "c.7z"), 1)
            {
                FileName = "c.7z",
                IsOutputVerified = true
            };

            // ① 只写"校验通过"= 判不出（老口径在这里就放行删源了）。
            Assert.Equal(ResultCompleteness.Undeterminable, ResultCompletenessClassifier.Classify(task).State);

            // ② 补上"核对过清单"这个事实 → 可证完整。
            task.OutputManifestCrossChecked = true;

            Assert.Equal(ResultCompleteness.Complete, ResultCompletenessClassifier.Classify(task).State);
        }

        // ================================================================ ② 校验结果里的那个事实

        /// <summary>
        /// <see cref="OutputVerifier.Verify"/> 必须**如实**记下"这一档到底核对了没有"：
        /// 没清单 → false（底线校验）、有清单 → true（逐条核对）。
        /// </summary>
        [Fact]
        public void 校验结果如实记下有没有核对过清单()
        {
            string stage = Path.Combine(_root, "stage");
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, "a.bin"), "内容");

            // ① 拿不到可信清单：只做非空底线校验 → 通过，但**没有核对过**。
            OutputVerificationResult withoutManifest = OutputVerifier.Verify(stage, null);

            Assert.True(withoutManifest.Verified);
            Assert.False(withoutManifest.ManifestCrossChecked);
            Assert.Equal(
                ResultCompleteness.Undeterminable,
                ResultCompletenessClassifier.Classify(withoutManifest).State);

            // ② 有可信清单：逐条核对 → 通过，而且**核对过**。
            OutputVerificationResult withManifest = OutputVerifier.Verify(stage, new ArchiveListResult
            {
                Success = true,
                FileCount = 1,
                TotalUncompressedSize = 6,
                Entries = new List<ArchiveEntry> { new() { Path = "a.bin", Size = 6 } },
                EngineId = "fake",
                EngineVersion = "1.0"
            });

            Assert.True(withManifest.Verified);
            Assert.True(withManifest.ManifestCrossChecked);
            Assert.Equal(
                ResultCompleteness.Complete,
                ResultCompletenessClassifier.Classify(withManifest).State);
        }

        // ================================================================ ③ 判不出 ⇒ 不删源（L6 闸门）

        /// <summary>
        /// ⛔ **"判不出时也删源"的守门用例**：其余物里那份源包在"校验通过但没核对过清单"时
        /// 必须**一个字节都不动**，而且结论文案要如实说"无法确认完整性"。
        /// </summary>
        [Fact]
        public void 判不出完整性_其余物一个字节都不删()
        {
            (ArchiveTask task, string sourcePath, string restDirectory) = CreateRemovableRest();

            // 校验写着"通过"，但那是**底线校验**（没清单可核对）—— 正是 L4 要拦下的那一档。
            task.IsOutputVerified = true;
            task.OutputManifestCrossChecked = false;

            RestPurgeOutcome outcome = new RestItemPurger().Purge(task, cancelled: false, DeleteMode.Permanent);

            Assert.False(outcome.Attempted);
            Assert.False(outcome.Succeeded);
            Assert.Contains("无法确认", outcome.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(restDirectory), "判不出完整性时其余物必须原封不动");
            Assert.True(File.Exists(sourcePath), "判不出完整性时源包必须原封不动");
        }

        /// <summary>对照组：同一个现场，只把"核对过清单"这个事实补上 → 该删就删（红线不是"一律不删"）。</summary>
        [Fact]
        public void 可证完整_其余物照旧删得掉()
        {
            (ArchiveTask task, string sourcePath, string restDirectory) = CreateRemovableRest();

            task.IsOutputVerified = true;
            task.OutputManifestCrossChecked = true;

            RestPurgeOutcome outcome = new RestItemPurger().Purge(task, cancelled: false, DeleteMode.Permanent);

            Assert.True(outcome.Attempted);
            Assert.True(outcome.Succeeded);
            Assert.False(Directory.Exists(restDirectory));
            Assert.False(File.Exists(sourcePath));
        }

        /// <summary>链尾那道门（<see cref="ChainCompletionGate"/>）同样收紧到"可证完整"。</summary>
        [Fact]
        public void 链上有一个判不出的_整条链的其余物都不动()
        {
            var root = new ArchiveTask(Path.Combine(_root, "root.7z"), 1)
            {
                FileName = "root.7z",
                Outcome = TaskOutcome.Succeeded,
                IsOutputVerified = true,
                OutputManifestCrossChecked = true
            };

            var inner = new ArchiveTask(Path.Combine(_root, "inner.7z"), 2)
            {
                FileName = "inner.7z",
                Outcome = TaskOutcome.Succeeded,
                IsOutputVerified = true,
                OutputManifestCrossChecked = false,

                // 续解任务的判据就是这个字段（内层包沿用父任务的落点）。
                ParentOutputDirectory = Path.Combine(_root, "out", "root")
            };

            Assert.True(inner.IsContinuationTask);

            string? blocker = ChainCompletionGate.DescribeBlocker(root, new[] { root, inner });

            Assert.NotNull(blocker);
            Assert.Contains("无法确认", blocker!, StringComparison.Ordinal);

            // 补上那个事实之后这条链就放行了 —— 拦下的原因确实只有"判不出完整性"。
            inner.OutputManifestCrossChecked = true;

            Assert.Null(ChainCompletionGate.DescribeBlocker(root, new[] { root, inner }));
        }

        /// <summary>造一个"该动手的其余物现场"：成功终态 + 其余物里有源包 + 内容物已定稿。</summary>
        private (ArchiveTask Task, string SourcePath, string RestDirectory) CreateRemovableRest()
        {
            string outputPath = Path.Combine(_root, "out", "pack");
            string restDirectory = Path.Combine(outputPath, "其余物");

            Directory.CreateDirectory(restDirectory);

            string sourcePath = Path.Combine(restDirectory, "pack.7z");
            File.WriteAllText(sourcePath, "源包");
            File.WriteAllText(Path.Combine(outputPath, "content.bin"), "内容物");

            var task = new ArchiveTask(Path.Combine(_root, "src", "pack.7z"), 1)
            {
                FileName = "pack.7z",
                OutputPath = outputPath,
                Status = StatusText.ExtractSuccess,
                Outcome = TaskOutcome.Succeeded,
                RestDirectoryPath = restDirectory,
                VolumeGroupKey = "pack"
            };

            return (task, sourcePath, restDirectory);
        }
    }
}
