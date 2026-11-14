using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using Xunit;
using ArchiveFixer.Extraction.VolumeStep;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 第二大步底层事实的守门用例（2026-11-10 真机：21 GB 被整组试开两遍、组命名早于任务导入、
    /// 暂停对试开无效）。
    ///
    /// <para>这里只钉判据本身：候选层/代表路径唯一出口、快通道覆盖判据、非等大写名有序不试开、
    /// 入口根的"生产者任务"事实链、停止令牌与试开进度接线。⛔ 不按后缀/样本名写用例。</para>
    /// </summary>
    public sealed class SecondStepBoundaryFactTests
    {
        private const string BaseName = "nidekikibabe-2026p6";

        private const string PieceDir = @"C:\out\222\111\nidekikibabe-2026p6";

        private const string SourceDir = @"C:\src\2222\111";

        // ------------------------------------------------------------ ① 候选层 / 代表路径

        [Fact]
        public void 同基名外层已消费包_不能当代表层或代表路径()
        {
            FirstStepFileLedger ledger = BuildLedgerWithConsumedTar();

            Assert.Equal(PieceDir, ledger.CandidateSourceLayer(BaseName));

            string representative = ledger.CandidateRepresentativePath(BaseName);

            Assert.StartsWith(PieceDir + Path.DirectorySeparatorChar, representative, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".tar", representative, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------ ② 快通道覆盖判据

        [Fact]
        public async Task 候选片全被快通道覆盖_同基名有已消费外层包也不调resolver()
        {
            FirstStepFileLedger ledger = BuildLedgerWithConsumedTar(includeAllPieces: true);

            var pipeline = new VolumeBoundaryPipeline();
            var resolver = new CountingResolver();
            pipeline.Register(resolver);

            IReadOnlyList<PreparedVolumeGroup> groups = await pipeline.ResolveAllAsync(
                ledger,
                baseName => new VolumeBoundaryContext { BaseName = baseName, Ledger = ledger });

            Assert.Empty(groups);
            Assert.Equal(0, resolver.Calls);
        }

        [Fact]
        public async Task 有候选片不在快通道组里_必须交给resolver()
        {
            FirstStepFileLedger ledger = BuildLedgerWithConsumedTar(includeAllPieces: true);

            ledger.AddOrReplace(Piece(
                Path.Combine(@"C:\out\other", BaseName + ".7z.006"),
                size: 10,
                first: false,
                slot: 6,
                layer: @"C:\out\other"));

            var pipeline = new VolumeBoundaryPipeline();
            var resolver = new CountingResolver();
            pipeline.Register(resolver);

            IReadOnlyList<PreparedVolumeGroup> groups = await pipeline.ResolveAllAsync(
                ledger,
                baseName => new VolumeBoundaryContext { BaseName = baseName, Ledger = ledger });

            Assert.NotEmpty(groups);
            Assert.Equal(1, resolver.Calls);
        }

        // ------------------------------------------------------------ ③ 非等大 7z 不整组试开

        [Fact]
        public async Task 七z非等大_名字槽齐全且字节和相等_直接Complete不试开()
        {
            FirstStepFileLedger ledger = new();

            long[] sizes = { 100, 10, 10, 10, 10 };
            long declared = 140;

            for (int index = 0; index < sizes.Length; index++)
            {
                ledger.AddOrReplace(Piece(
                    Path.Combine(PieceDir, $"{BaseName}.7z.{index + 1:000}"),
                    sizes[index],
                    first: index == 0,
                    slot: index + 1,
                    declaredTotalBytes: declared));
            }

            PreparedVolumeGroup? group = await new SevenZipBoundaryResolver().ResolveAsync(
                new VolumeBoundaryContext
                {
                    Ledger = ledger,
                    BaseName = BaseName,

                    // ⛔ 故意不给引擎/工作区：这条判据成立时不需要读整组数据；要试开才算 Complete 的实现会在这里返回 null。
                    Engine = null,
                    WorkspaceRoot = null
                });

            Assert.NotNull(group);
            Assert.Equal(PreparedGroupState.Complete, group!.State);
            Assert.Contains("不整组试开", group.Evidence, StringComparison.Ordinal);
            Assert.Equal(sizes.Length, group.Pieces.Count);
        }

        // ------------------------------------------------------------ ④ 入口根：生产者任务

        [Fact]
        public void 入口任务还没导入_用生产者任务的落点当工作区根()
        {
            var producer = new ArchiveTask(Path.Combine(SourceDir, BaseName + ".tar"))
            {
                OutputPath = PieceDir
            };

            string entryPath = Path.Combine(PieceDir, BaseName + ".7z.001");
            var entryTask = new ArchiveTask(entryPath);

            FirstStepFileLedger ledger = new();
            ledger.AddOrReplace(Piece(entryPath, 100, first: true, slot: 1, producedByTaskId: producer.TaskId));

            var group = new PreparedVolumeGroup
            {
                BaseName = BaseName,
                Family = VolumeNamingFamily.SevenZipNumbered,
                OriginalEntryPath = entryPath,
                EntryCanonicalName = BaseName + ".7z.001",
                State = PreparedGroupState.Complete,
                Pieces = new[]
                {
                    new PreparedVolumePiece
                    {
                        CanonicalName = BaseName + ".7z.001",
                        OriginalPath = entryPath,
                        Size = 100
                    }
                }
            };

            Assert.True(ledger.TryGetByPath(entryPath, out FirstStepFileLedgerEntry lookup), "账本里查不到入口片");
            Assert.Equal(producer.TaskId, lookup.ProducedByTaskId);
            Assert.Equal(PieceDir, producer.OutputPath);

            string? root = SecondStepEntryRootResolver.Resolve(
                group,
                new[] { producer, entryTask },
                ledger);

            Assert.Equal(Path.Combine(PieceDir, ".ArchiveFixer.work"), root);
        }

        [Fact]
        public void 入口任务和生产者任务都没有_不许猜工作区根()
        {
            FirstStepFileLedger ledger = new();
            ledger.AddOrReplace(Piece(Path.Combine(PieceDir, BaseName + ".7z.001"), 100, first: true, slot: 1));

            var group = new PreparedVolumeGroup
            {
                BaseName = BaseName,
                OriginalEntryPath = Path.Combine(PieceDir, BaseName + ".7z.001"),
                State = PreparedGroupState.Complete
            };

            Assert.Null(SecondStepEntryRootResolver.Resolve(group, Array.Empty<ArchiveTask>(), ledger));
        }

        // ------------------------------------------------------------ ⑤ 停止令牌 / 试开可取消 / 进度接线

        [Fact]
        public void 停止令牌_RequestStop同时置位并取消Token()
        {
            using var token = new RunStopToken();

            Assert.False(token.IsStopRequested);
            Assert.False(token.Token.IsCancellationRequested);

            token.RequestStop();

            Assert.True(token.IsStopRequested);
            Assert.True(token.Token.IsCancellationRequested);
        }

        [Fact]
        public async Task 试开_令牌已取消时立刻抛出_不读整组()
        {
            string root = CreateTempDir();

            try
            {
                string first = Path.Combine(root, "111.7z.001");
                File.WriteAllBytes(first, new byte[16]);

                using var cts = new CancellationTokenSource();
                cts.Cancel();

                var engine = new TrialEngine();
                var verifier = new VolumeProbeVerifier(engine);

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verifier.VerifyAsync(
                    first,
                    new List<IReadOnlyList<VolumeCandidate>> { new List<VolumeCandidate>() },
                    cts.Token,
                    root,
                    VolumeContentFormat.SevenZip,
                    testVolumes: true));

                Assert.Equal(0, engine.TestCalls);
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public async Task 试开_TestAsync的请求必须带进度接收端()
        {
            string root = CreateTempDir();

            try
            {
                string first = Path.Combine(root, "111.7z.001");
                string second = Path.Combine(root, "111.7z.002");
                File.WriteAllBytes(first, new byte[16]);
                File.WriteAllBytes(second, new byte[16]);

                var engine = new TrialEngine { FailList = true };
                var verifier = new VolumeProbeVerifier(engine);

                var reported = new List<int>();
                var progress = new Progress<ArchiveProgress>(item => reported.Add(item.Percent));

                VolumeProbeOutcome outcome = await verifier.VerifyAsync(
                    first,
                    new List<IReadOnlyList<VolumeCandidate>>
                    {
                        new List<VolumeCandidate> { new() { Path = second, Size = 16 } }
                    },
                    CancellationToken.None,
                    root,
                    VolumeContentFormat.SevenZip,
                    testVolumes: true,
                    passwordCandidates: new[] { string.Empty },
                    progress: progress,
                    stalled: _ => { });

                Assert.True(outcome.Confirmed);
                Assert.True(engine.TestCalls > 0);
                Assert.True(engine.LastTestHadProgress, "试开请求没有带 Progress：界面/日志会再次出现零输出长窗");

                await Task.Delay(50);
                Assert.True(reported.Count > 0, "试开进度没有送到上层");
            }
            finally
            {
                TryDelete(root);
            }
        }

        [Fact]
        public void 第二大步当前工作_取消后令牌立即取消_再开始是全新令牌()
        {
            var tracker = new ArchiveFixer.ViewModels.SecondStepWorkTracker();

            ArchiveFixer.ViewModels.SecondStepWorkScope first = tracker.Begin();

            Assert.False(first.Token.IsCancellationRequested);
            Assert.True(tracker.Cancel());
            Assert.True(first.Token.IsCancellationRequested);
            Assert.False(tracker.Cancel());

            first.Dispose();

            ArchiveFixer.ViewModels.SecondStepWorkScope second = tracker.Begin();

            Assert.False(second.Token.IsCancellationRequested);
            second.Dispose();
        }
        [Fact]
        public async Task 第二大步当前工作_取消会穿透管线并让resolver立刻停()
        {
            var ledger = new FirstStepFileLedger();
            ledger.AddOrReplace(new FirstStepFileLedgerEntry
            {
                Path = @"C:\in\111.7z.bad",
                BaseName = "111",
                Size = 10,
                Format = VolumeContentFormat.SevenZip,
                BodyKind = ArchiveBodyKind.VolumeFirstPiece,
                SourceLayer = @"C:\in",
                IsGatePassed = true
            });

            var tracker = new ArchiveFixer.ViewModels.SecondStepWorkTracker();
            ArchiveFixer.ViewModels.SecondStepWorkScope work = tracker.Begin();

            var blocking = new BlockingResolver();
            var pipeline = new VolumeBoundaryPipeline();
            pipeline.Register(blocking);

            Task<IReadOnlyList<PreparedVolumeGroup>> running = pipeline.ResolveAllAsync(
                ledger,
                baseName => new VolumeBoundaryContext { BaseName = baseName, Ledger = ledger },
                work.Token);

            await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(tracker.Cancel());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            work.Dispose();
        }

        // ------------------------------------------------------------ helpers

        private static FirstStepFileLedger BuildLedgerWithConsumedTar(bool includeAllPieces = true)
        {
            var ledger = new FirstStepFileLedger();

            ledger.AddOrReplace(new FirstStepFileLedgerEntry
            {
                Path = Path.Combine(SourceDir, BaseName + ".tar"),
                BaseName = BaseName,
                Size = 1_000,
                Format = VolumeContentFormat.Unknown,
                SourceLayer = SourceDir,
                IsGatePassed = true,
                IsConsumedSource = true
            });

            if (!includeAllPieces)
            {
                return ledger;
            }

            for (int index = 1; index <= 5; index++)
            {
                ledger.AddOrReplace(Piece(
                    Path.Combine(PieceDir, $"{BaseName}.7z.{index:000}"),
                    size: 100,
                    first: index == 1,
                    slot: index,
                    declaredTotalBytes: 500));
            }

            return ledger;
        }

        private static FirstStepFileLedgerEntry Piece(
            string path,
            long size,
            bool first,
            int? slot,
            string layer = PieceDir,
            long declaredTotalBytes = 0,
            string producedByTaskId = "")
        {
            return new FirstStepFileLedgerEntry
            {
                Path = path,
                BaseName = BaseName,
                Size = size,
                Format = VolumeContentFormat.SevenZip,
                BodyKind = first ? ArchiveBodyKind.VolumeFirstPiece : ArchiveBodyKind.Unknown,
                NameSlot = slot,
                DeclaredTotalBytes = declaredTotalBytes,
                DeclaredVolumeCount = 0,
                SourceLayer = layer,
                IsGatePassed = true,
                IsConsumedSource = false,
                ProducedByTaskId = producedByTaskId
            };
        }

        private static string CreateTempDir()
        {
            string root = Path.Combine(Path.GetTempPath(), "af-secondstep-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static void TryDelete(string root)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch
            {
            }
        }

        private sealed class CountingResolver : IVolumeBoundaryResolver
        {
            public int Calls { get; private set; }

            public VolumeNamingFamily Family => VolumeNamingFamily.SevenZipNumbered;

            public Task<PreparedVolumeGroup?> ResolveAsync(
                VolumeBoundaryContext context,
                CancellationToken cancellationToken = default)
            {
                Calls++;

                return Task.FromResult<PreparedVolumeGroup?>(new PreparedVolumeGroup
                {
                    BaseName = context.BaseName,
                    Family = Family,
                    State = PreparedGroupState.Complete,
                    Evidence = "counting"
                });
            }
        }

        private sealed class BlockingResolver : IVolumeBoundaryResolver
        {
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public VolumeNamingFamily Family => VolumeNamingFamily.SevenZipNumbered;

            public async Task<PreparedVolumeGroup?> ResolveAsync(
                VolumeBoundaryContext context,
                CancellationToken cancellationToken = default)
            {
                Started.TrySetResult();

                await Task.Delay(Timeout.Infinite, cancellationToken);

                return null;
            }
        }

        private sealed class TrialEngine : IArchiveEngine
        {
            public bool FailList { get; init; }

            public int TestCalls { get; private set; }

            public bool LastTestHadProgress { get; private set; }

            public string Id => "trial";

            public string DisplayName => "试开假引擎";

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

            public Task<ArchiveProbeResult> ProbeAsync(
                ArchiveRequest request,
                CancellationToken cancellationToken = default)
                => Task.FromResult(new ArchiveProbeResult { IsArchive = true, Format = "7Z" });

            public Task<ArchiveListResult> ListAsync(
                ArchiveRequest request,
                CancellationToken cancellationToken = default)
                => Task.FromResult(FailList
                    ? new ArchiveListResult { Success = false, Message = "not a list" }
                    : new ArchiveListResult
                    {
                        Success = true,
                        Entries = new[] { new ArchiveEntry { Path = "payload.bin", Size = 1 } },
                        TotalUncompressedSize = 1
                    });

            public Task<ArchiveOperationResult> TestAsync(
                ArchiveRequest request,
                CancellationToken cancellationToken = default)
            {
                TestCalls++;
                LastTestHadProgress = request.Progress != null;
                request.Progress?.Report(new ArchiveProgress { Percent = 42 });

                return Task.FromResult(new ArchiveOperationResult
                {
                    Success = true,
                    Status = "测试通过",
                    Message = "测试通过",
                    DetectedErrorType = "None"
                });
            }

            public Task<ArchiveOperationResult> ExtractAsync(
                ArchiveRequest request,
                ExtractOptions options,
                CancellationToken cancellationToken = default)
                => Task.FromResult(new ArchiveOperationResult
                {
                    Success = true,
                    Status = "解压成功",
                    Message = "解压成功",
                    DetectedErrorType = "None"
                });
        }
    }
}
