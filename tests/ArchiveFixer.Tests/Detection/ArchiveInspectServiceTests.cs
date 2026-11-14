using ArchiveFixer.Engines;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「看内容 / 试密码」的回归（用户 2026-09-26 批准加的两个高频功能）。
    ///
    /// <para>⛔ 两条纪律必须被钉住：①**一个字节都不写盘**（只调 list，绝不调 extract）；
    /// ②结论里**不许出现密码明文**（只写候选的说明文字）。</para>
    /// </summary>
    public class ArchiveInspectServiceTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "af-inspect-" + Guid.NewGuid().ToString("N"));

        public ArchiveInspectServiceTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, true);
                }
            }
            catch
            {
                // 临时目录清理失败不影响结论。
            }
        }

        [Fact]
        public async Task 看内容_不需要密码的包_给出条目数与总大小()
        {
            string file = Path.Combine(_root, "普通.zip");
            File.WriteAllText(file, "x");

            var engine = new FakeListEngine { Encrypted = false };
            engine.Entries.Add(new ArchiveEntry { Path = "a.txt", Size = 100 });
            engine.Entries.Add(new ArchiveEntry { Path = "b/c.bin", Size = 900 });

            var task = new ArchiveTask(file, 1) { DetectedFormat = "ZIP" };

            ArchiveInspectResult result = await new ArchiveInspectService(engine).InspectAsync(task, null);

            Assert.True(result.Success);
            Assert.False(result.NeedsPassword);
            Assert.Equal(2, result.FileCount);
            Assert.Equal(1000, result.TotalBytes);
            Assert.Equal(2, result.TopEntries.Count);

            // ⛔ 只许 list：任何一次 extract 都是"看一眼"这条路上的越界。
            Assert.All(engine.Calls, call => Assert.Equal("list", call));
        }

        [Fact]
        public async Task 试密码_第二个候选能开_报出它的说明且不含密码明文()
        {
            string file = Path.Combine(_root, "加密.7z");
            File.WriteAllText(file, "x");

            var engine = new FakeListEngine { Encrypted = true, CorrectPassword = "right-pass" };
            engine.Entries.Add(new ArchiveEntry { Path = "只有一条.txt", Size = 42 });

            var task = new ArchiveTask(file, 1) { DetectedFormat = "7Z" };

            var candidates = new List<PasswordItem>
            {
                new("wrong-pass", "ImportedList", true, "密码列表第 1 项"),
                new("right-pass", "ImportedList", true, "密码列表第 2 项")
            };

            ArchiveInspectResult result = await new ArchiveInspectService(engine).InspectAsync(task, candidates);

            Assert.True(result.Success);
            Assert.True(result.NeedsPassword);
            Assert.True(result.PasswordFound);
            Assert.Equal("密码列表第 2 项", result.PasswordLabel);
            Assert.Equal(2, result.CandidatesTried);

            // ⛔ 结论里不许出现密码明文（界面上只显示"第几项/哪来的"）。
            string dump = result.PasswordLabel + "|" + result.Message;
            Assert.DoesNotContain("right-pass", dump, StringComparison.Ordinal);
            Assert.DoesNotContain("wrong-pass", dump, StringComparison.Ordinal);
            Assert.All(engine.Calls, call => Assert.Equal("list", call));
        }

        [Fact]
        public async Task 试密码_一个都不对_如实说试了几个()
        {
            string file = Path.Combine(_root, "开不了.7z");
            File.WriteAllText(file, "x");

            var engine = new FakeListEngine { Encrypted = true, CorrectPassword = "别的密码" };
            engine.Entries.Add(new ArchiveEntry { Path = "x.bin", Size = 1 });

            var task = new ArchiveTask(file, 1) { DetectedFormat = "7Z" };

            var candidates = new List<PasswordItem>
            {
                new("a", "ImportedList", true, "密码列表第 1 项"),
                new("b", "ImportedList", true, "密码列表第 2 项")
            };

            ArchiveInspectResult result = await new ArchiveInspectService(engine).InspectAsync(task, candidates);

            Assert.False(result.PasswordFound);
            Assert.Equal(2, result.CandidatesTried);
            Assert.Equal(2, result.CandidatesTotal);
            Assert.Contains("2", result.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("别的密码", result.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task 损坏的包_不许被说成需要密码()
        {
            // 真机代跑当场逮到（2026-09-26）：一个**不加密、只是坏了**的包第一遍 list 就失败，
            // 于是我也去试了候选，然后张嘴就说"需要密码：试了 10 个候选都没能打开" ——
            // 那会把人引去翻密码本，而真相是引擎那句「无法识别或不支持该格式」。
            string file = Path.Combine(_root, "损坏的.7z");
            File.WriteAllText(file, "x");

            var engine = new FakeListEngine
            {
                AlwaysFail = true,
                FailureErrorType = "CorruptedArchive",
                FailureMessage = "7-Zip 无法识别或不支持该格式"
            };

            var task = new ArchiveTask(file, 1) { DetectedFormat = "7Z" };
            var candidates = new List<PasswordItem> { new("a", "ImportedList", true, "密码列表第 1 项") };

            ArchiveInspectResult result = await new ArchiveInspectService(engine).InspectAsync(task, candidates);

            Assert.False(result.Success);
            Assert.False(result.NeedsPassword);
            Assert.DoesNotContain("密码", result.Message, StringComparison.Ordinal);
            Assert.Contains("无法识别或不支持该格式", result.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// 假引擎：只实现 <c>list</c>（其余的调用会**记一笔**，让"有没有越界"可断言）。
        /// </summary>
        private sealed class FakeListEngine : IArchiveEngine
        {
            public string Id => "fake";

            public string DisplayName => "假引擎";

            public string Version => "1.0";

            public bool IsAvailable => true;

            public EngineCapabilities Capabilities { get; } = new();

            public bool Encrypted { get; init; }

            public string CorrectPassword { get; init; } = string.Empty;
            /// <summary>第一遍（不带密码）失败时用的错误类型与文案；空表示"失败就是密码错误"。</summary>
            public string FailureErrorType { get; init; } = string.Empty;

            public string FailureMessage { get; init; } = "密码错误";

            /// <summary>true = 每一次 list 都失败（用来演"这个包压根打不开"，与密码无关）。</summary>
            public bool AlwaysFail { get; init; }

            public List<ArchiveEntry> Entries { get; } = new();

            public List<string> Calls { get; } = new();

            public Task<ArchiveProbeResult> ProbeAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                Calls.Add("probe");
                return Task.FromResult(new ArchiveProbeResult());
            }

            public Task<ArchiveListResult> ListAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                Calls.Add("list");

                if (AlwaysFail)
                {
                    string failType = string.IsNullOrEmpty(FailureErrorType) ? "CorruptedArchive" : FailureErrorType;
                    string failText = string.IsNullOrEmpty(FailureMessage) ? "无法识别或不支持该格式" : FailureMessage;

                    return Task.FromResult(ArchiveListResult.Failure(failType, failText, Id, Version));
                }

                bool passwordOk = !Encrypted ||
                                  string.Equals(request.Password, CorrectPassword, StringComparison.Ordinal);

                if (!passwordOk)
                {
                    // 「不带密码那一次」的失败类型可以被测试指定（用来验"损坏 ≠ 需要密码"）。
                    bool firstAttempt = request.Password == null;

                    string errorType = firstAttempt && !string.IsNullOrEmpty(FailureErrorType)
                        ? FailureErrorType
                        : "WrongPassword";

                    string message = firstAttempt && !string.IsNullOrEmpty(FailureErrorType)
                        ? FailureMessage
                        : "密码错误";

                    return Task.FromResult(ArchiveListResult.Failure(errorType, message, Id, Version));
                }

                return Task.FromResult(new ArchiveListResult
                {
                    Success = true,
                    Entries = Entries,
                    FileCount = Entries.Count(entry => !entry.IsDirectory),
                    DirectoryCount = Entries.Count(entry => entry.IsDirectory),
                    TotalUncompressedSize = Entries.Sum(entry => entry.Size),
                    IsEncrypted = Encrypted,
                    EngineId = Id,
                    EngineVersion = Version
                });
            }

            public Task<ArchiveOperationResult> TestAsync(ArchiveRequest request, CancellationToken cancellationToken = default)
            {
                Calls.Add("test");
                return Task.FromResult(new ArchiveOperationResult());
            }

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
            {
                Calls.Add("extract");
                return Task.FromResult(new ArchiveOperationResult());
            }
        }
    }
}
