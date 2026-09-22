using System;
using System.Collections.Generic;
using System.Linq;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 定稿布局规划的纯逻辑测试（规格 §3.1 判定表 + §3.2 过程物集中 + §3.3 批量场景）。
    ///
    /// <para>
    /// 全部用 <see cref="StagedEntry"/> 假路径构造，**不碰真实文件系统**（规划本来就不该碰）。
    /// 断言一律比路径字符串或枚举，不比中文文案。
    /// </para>
    /// </summary>
    public class ResultFinalizerTests
    {
        private const string Staging = @"C:\work\t1";
        private const string Dest = @"C:\out\222";

        private static StagedEntry File(string relativePath, long size = 100)
        {
            return new StagedEntry { RelativePath = relativePath, Size = size };
        }

        private static StagedEntry Dir(string relativePath)
        {
            return new StagedEntry { RelativePath = relativePath, IsDirectory = true };
        }

        private static StagedEntry Artifact(string relativePath, long size = 100)
        {
            return new StagedEntry { RelativePath = relativePath, Size = size, IsProcessArtifact = true };
        }

        /// <summary>默认：暂存树里 <c>out\</c> 是内容物，别的都是过程物。</summary>
        private static FinalizePlan Plan(params StagedEntry[] entries)
        {
            return ResultFinalizer.Plan(entries, Dest, stagingRoot: Staging, contentRoot: "out");
        }

        // ── 判定表 1：终端是单个文件 ────────────────────────────────────────────

        [Fact]
        public void Plan_TerminalSingleFile_GoesDirectlyUnderDestination()
        {
            FinalizePlan plan = Plan(File(@"out\movie.mp4", 500));

            Assert.Equal(FinalizeLayoutKind.SingleFileToDestination, plan.Layout);
            Assert.Equal(Dest, plan.ContentParentDirectory);
            Assert.Equal(string.Empty, plan.ContentDirectoryName);
            Assert.Single(plan.Moves);
            Assert.Equal((@"C:\work\t1\out\movie.mp4", @"C:\out\222\movie.mp4"), (plan.Moves[0].From, plan.Moves[0].To));
            Assert.Equal(1, plan.ContentFileCount);
            Assert.Equal(500, plan.ContentTotalSize);
        }

        // 单个文件时 TerminalLayoutMode 无所谓：本来就没有文件夹层可改名。
        [Fact]
        public void Plan_TerminalSingleFile_IgnoresTerminalLayoutMode()
        {
            FinalizePlan keep = ResultFinalizer.Plan(
                new[] { File(@"out\movie.mp4") }, Dest, TerminalLayoutMode.KeepLastFolder, "222", "out", Staging);

            FinalizePlan useName = ResultFinalizer.Plan(
                new[] { File(@"out\movie.mp4") }, Dest, TerminalLayoutMode.UseArchiveName, "222", "out", Staging);

            Assert.Equal(FinalizeLayoutKind.SingleFileToDestination, keep.Layout);
            Assert.Equal(FinalizeLayoutKind.SingleFileToDestination, useName.Layout);
            Assert.Equal(keep.Moves[0].To, useName.Moves[0].To);
            Assert.Equal(@"C:\out\222\movie.mp4", useName.Moves[0].To);
        }

        [Fact]
        public void Plan_TerminalSingleDirWithSingleFile_KeepsFolderAsOneLayer()
        {
            // 单个文件被一层文件夹包着 ≠ "终端是单个文件"：那一层是有名字的，要保留（判定表 2）。
            FinalizePlan plan = Plan(File(@"out\666\movie.mp4", 500));

            Assert.Equal(FinalizeLayoutKind.WrapInFolder, plan.Layout);
            Assert.Equal(@"C:\out\222\666", plan.ContentParentDirectory);
            Assert.Single(plan.Moves);
            Assert.Equal((@"C:\work\t1\out\666", @"C:\out\222\666"), (plan.Moves[0].From, plan.Moves[0].To));
        }

        // ── 判定表 2：多个文件 / 多个文件夹 / 自带一层文件夹 → 套一层 ───────────

        [Fact]
        public void Plan_SelfContainedFolder_WrapsWithThatFolderName()
        {
            FinalizePlan plan = Plan(File(@"out\666\a.mp4"), File(@"out\666\b.mp4"));

            Assert.Equal(FinalizeLayoutKind.WrapInFolder, plan.Layout);
            Assert.Equal("666", plan.ContentDirectoryName);
            Assert.Equal(@"C:\out\222\666", plan.ContentParentDirectory);
            Assert.Single(plan.Moves);
            Assert.Equal((@"C:\work\t1\out\666", @"C:\out\222\666"), (plan.Moves[0].From, plan.Moves[0].To));
            Assert.Equal(2, plan.ContentFileCount);
        }

        [Fact]
        public void Plan_MultipleFoldersAtRoot_WrapsWithArchiveBaseName()
        {
            // 归档根上直接摊着多个文件夹、没有"最外层文件夹名"可用 → 用终端归档基名当那一层。
            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { Dir(@"out\a"), Dir(@"out\b"), File(@"out\a\x.mp4"), File(@"out\b\y.mp4") },
                @"C:\111",
                TerminalLayoutMode.KeepLastFolder,
                "222",
                "out",
                Staging);

            Assert.Equal(FinalizeLayoutKind.WrapInFolder, plan.Layout);
            Assert.Equal("222", plan.ContentDirectoryName);
            Assert.Equal(@"C:\111\222", plan.ContentParentDirectory);
            Assert.Equal(@"C:\111\222\a", plan.Moves[0].To);
            Assert.Equal(@"C:\111\222\b", plan.Moves[1].To);
        }

        [Fact]
        public void Plan_MultipleFilesAtRootWithoutArchiveName_DoesNotInventRepeatedLayer()
        {
            // 没有归档基名可用时不硬造一层：目标目录本身就是那一层（同名子文件夹模式下它就叫包名）。
            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { File(@"out\a.mp4"), File(@"out\b.mp4") }, Dest, stagingRoot: Staging, contentRoot: "out");

            Assert.Equal(FinalizeLayoutKind.WrapInFolder, plan.Layout);
            Assert.Equal(Dest, plan.ContentParentDirectory);
            Assert.Equal(@"C:\out\222\a.mp4", plan.Moves[0].To);
            Assert.Equal(@"C:\out\222\b.mp4", plan.Moves[1].To);
            Assert.NotEmpty(plan.Warnings);
        }

        // ── 判定表 3：被多重空目录嵌套包裹 → 提上最后那个有意义的文件夹 ─────────

        [Fact]
        public void Plan_EmptyFolderNesting_PromotesLastMeaningfulFolder()
        {
            // out\{empty1, empty2, 999\{a,b}} → 空壳被跳过，999 提上来当 destDir 的直接子层。
            FinalizePlan plan = Plan(
                Dir(@"out\empty1"),
                Dir(@"out\empty2"),
                Dir(@"out\999"),
                File(@"out\999\a.mp4"),
                File(@"out\999\b.mp4"));

            Assert.Equal(FinalizeLayoutKind.PromoteInnermostFolder, plan.Layout);
            Assert.Equal("999", plan.ContentDirectoryName);
            Assert.Equal(@"C:\out\222\999", plan.ContentParentDirectory);
            Assert.Contains(new PlannedMove(@"C:\work\t1\out\999", @"C:\out\222\999"), plan.Moves);
        }

        [Fact]
        public void Plan_EmptyFolderNesting_ShellsGoToProcessArtifacts()
        {
            FinalizePlan plan = Plan(
                Dir(@"out\empty1"),
                Dir(@"out\empty2"),
                File(@"out\999\a.mp4"),
                File(@"out\999\b.mp4"));

            Assert.Equal(2, plan.ProcessArtifactMoves.Count);
            Assert.Equal(@"C:\out\222\过程物\out\empty1", plan.ProcessArtifactMoves[0].To);
            Assert.Equal(@"C:\out\222\过程物\out\empty2", plan.ProcessArtifactMoves[1].To);

            // 过程物绝不与内容物同层：一个在 过程物\ 下，一个在 999\ 下。
            Assert.Equal(@"C:\out\222\过程物", plan.ProcessArtifactDirectory);
        }

        // ── 判定表 4：单链 → 塌缩为最深层那个文件夹名 ──────────────────────────

        [Fact]
        public void Plan_SingleChain_CollapsesToDeepestFolderName()
        {
            // out\666\777\{a,b}：每层都只有一个文件夹、没有别的文件 → 塌缩到最深的 777。
            FinalizePlan plan = Plan(File(@"out\666\777\a.mp4"), File(@"out\666\777\b.mp4"));

            Assert.Equal(FinalizeLayoutKind.CollapseSingleChain, plan.Layout);
            Assert.Equal("777", plan.ContentDirectoryName);
            Assert.Equal(@"C:\out\222\777", plan.ContentParentDirectory);
            Assert.Single(plan.Moves);
            Assert.Equal((@"C:\work\t1\out\666\777", @"C:\out\222\777"), (plan.Moves[0].From, plan.Moves[0].To));
        }

        [Fact]
        public void Plan_DeepSingleChain_CollapsesToDeepestFolderName()
        {
            FinalizePlan plan = Plan(
                Dir(@"out\a"),
                Dir(@"out\a\b"),
                Dir(@"out\a\b\999"),
                File(@"out\a\b\999\x.mp4"),
                File(@"out\a\b\999\y.mp4"));

            Assert.Equal(FinalizeLayoutKind.CollapseSingleChain, plan.Layout);
            Assert.Equal("999", plan.ContentDirectoryName);
            Assert.Equal((@"C:\work\t1\out\a\b\999", @"C:\out\222\999"), (plan.Moves[0].From, plan.Moves[0].To));
        }

        // 单链里只要有一层同时还放着文件，就不再是单链（判定表 2 优先）。
        [Fact]
        public void Plan_ChainWithExtraFile_StopsAtThatLevel()
        {
            FinalizePlan plan = Plan(File(@"out\666\777\a.mp4"), File(@"out\666\readme.txt"));

            Assert.Equal(FinalizeLayoutKind.WrapInFolder, plan.Layout);
            Assert.Equal("666", plan.ContentDirectoryName);
            Assert.Equal(@"C:\out\222\666", plan.ContentParentDirectory);
        }

        // ── TerminalLayoutMode：KeepLastFolder vs UseArchiveName ───────────────

        [Fact]
        public void Plan_KeepLastFolder_KeepsArchiveInternalFolderName()
        {
            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { File(@"out\666\a.mp4") }, Dest, TerminalLayoutMode.KeepLastFolder, "222", "out", Staging);

            Assert.Equal("666", plan.ContentDirectoryName);
            Assert.Equal(@"C:\out\222\666", plan.ContentParentDirectory);
            Assert.Equal((@"C:\work\t1\out\666", @"C:\out\222\666"), (plan.Moves[0].From, plan.Moves[0].To));
        }

        [Fact]
        public void Plan_UseArchiveName_UsesArchiveBaseNameAsLastFolder()
        {
            // 目标目录是当前目录（C:\111），套的那一层改叫包名 222 → C:\111\222\...
            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { File(@"out\666\a.mp4") }, @"C:\111", TerminalLayoutMode.UseArchiveName, "222", "out", Staging);

            Assert.Equal("222", plan.ContentDirectoryName);
            Assert.Equal(@"C:\111\222", plan.ContentParentDirectory);
            Assert.Equal((@"C:\work\t1\out\666", @"C:\111\222"), (plan.Moves[0].From, plan.Moves[0].To));
        }

        [Fact]
        public void Plan_UseArchiveName_TargetAlreadyNamedAfterArchive_DoesNotRepeatLayer()
        {
            // 目标目录已经叫 222（同名子文件夹模式）→ 规格 §3.1 的"111\222\内容物"，不再套一层。
            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { File(@"out\666\a.mp4") }, Dest, TerminalLayoutMode.UseArchiveName, "222", "out", Staging);

            Assert.Equal(string.Empty, plan.ContentDirectoryName);
            Assert.Equal(Dest, plan.ContentParentDirectory);
            Assert.Equal(@"C:\out\222\a.mp4", plan.Moves[0].To);
        }

        [Fact]
        public void Plan_UseArchiveName_AppliesToSingleChainAndNestingToo()
        {
            FinalizePlan chain = ResultFinalizer.Plan(
                new[] { File(@"out\666\777\a.mp4") }, Dest, TerminalLayoutMode.UseArchiveName, "555", "out", Staging);

            FinalizePlan nesting = ResultFinalizer.Plan(
                new[] { Dir(@"out\empty"), File(@"out\999\a.mp4") },
                Dest,
                TerminalLayoutMode.UseArchiveName,
                "555",
                "out",
                Staging);

            Assert.Equal("555", chain.ContentDirectoryName);
            Assert.Equal(@"C:\out\222\555", chain.ContentParentDirectory);
            Assert.Equal("555", nesting.ContentDirectoryName);
            Assert.Equal(FinalizeLayoutKind.PromoteInnermostFolder, nesting.Layout);
        }

        [Fact]
        public void Plan_ContentRootNestedDeeper_DoesNotSweepItsAncestorsIntoProcessArtifacts()
        {
            // 暂存区常见两级结构：<stage>\out。内容物根指到 out 上时，stage 只是路径容器，
            // 绝不能把 stage 整棵当过程物搬走（那会连内容物一起搬）。
            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { File(@"stage\out\666\a.mp4", 10), File(@"stage\out\666\b.mp4", 20) },
                Dest,
                stagingRoot: Staging,
                contentRoot: @"stage\out");

            Assert.Equal(FinalizeLayoutKind.WrapInFolder, plan.Layout);
            Assert.Equal(@"C:\out\222\666", plan.ContentParentDirectory);
            Assert.Empty(plan.ProcessArtifactMoves);
            Assert.Single(plan.Moves);
            Assert.Equal((@"C:\work\t1\stage\out\666", @"C:\out\222\666"), (plan.Moves[0].From, plan.Moves[0].To));
        }

        [Fact]
        public void Plan_ContentRootPointingAtFile_TreatsItAsTerminalSingleFile()
        {
            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { File(@"out\movie.mp4", 700) },
                Dest,
                stagingRoot: Staging,
                contentRoot: @"out\movie.mp4");

            Assert.Equal(FinalizeLayoutKind.SingleFileToDestination, plan.Layout);
            Assert.Equal(
                (@"C:\work\t1\out\movie.mp4", @"C:\out\222\movie.mp4"),
                (plan.Moves[0].From, plan.Moves[0].To));

            Assert.Empty(plan.ProcessArtifactMoves);
        }

        [Fact]
        public void Plan_MarkedStagingAreaWithContentRootInside_KeepsContentOut()
        {
            // "把 stage 整个标成过程物、又把内容物根指到 stage\out"：内容物根的祖先标记不外溢。
            FinalizePlan plan = ResultFinalizer.Plan(
                new[]
                {
                    new StagedEntry { RelativePath = @"stage", IsDirectory = true, IsProcessArtifact = true },
                    File(@"stage\out\666\a.mp4", 10),
                    File(@"stage\volumes\v.7z.001", 20)
                },
                Dest,
                stagingRoot: Staging,
                contentRoot: @"stage\out");

            Assert.Equal(FinalizeLayoutKind.WrapInFolder, plan.Layout);
            Assert.Equal(@"C:\out\222\666", plan.ContentParentDirectory);
            Assert.Equal(2, plan.Moves.Count);
            Assert.Equal((@"C:\work\t1\stage\out\666", @"C:\out\222\666"), (plan.Moves[0].From, plan.Moves[0].To));
            Assert.Equal(
                (@"C:\work\t1\stage\volumes", @"C:\out\222\过程物\stage\volumes"),
                (plan.ProcessArtifactMoves[0].From, plan.ProcessArtifactMoves[0].To));
        }

        // ── 决策 D-2：模式 B 的过程物要按包基名隔离 ────────────────────────────

        [Fact]
        public void Plan_SourceDirectoryFlat_NestsProcessArtifactsUnderArchiveName()
        {
            // 当前目录模式：一个源目录里几十上百个包共用它 → 过程物必须按包名隔离。
            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { File(@"out\666\a.mp4"), Artifact(@"inner.7z", 10) },
                @"C:\111",
                TerminalLayoutMode.KeepLastFolder,
                "222",
                "out",
                Staging,
                OutputPlacementMode.SourceDirectoryFlat);

            Assert.Equal(@"C:\111\过程物\222", plan.ProcessArtifactDirectory);
            Assert.Equal(
                (@"C:\work\t1\inner.7z", @"C:\111\过程物\222\inner.7z"),
                (plan.ProcessArtifactMoves[0].From, plan.ProcessArtifactMoves[0].To));
        }

        [Fact]
        public void Plan_PerArchiveSubfolder_KeepsProcessArtifactsDirectlyUnderProcessFolder()
        {
            // 同名子文件夹模式：destDir 本身就是"一个包一个目录"，不用再套一层。
            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { File(@"out\666\a.mp4"), Artifact(@"inner.7z", 10) },
                Dest,
                TerminalLayoutMode.KeepLastFolder,
                "222",
                "out",
                Staging,
                OutputPlacementMode.PerArchiveSubfolder);

            Assert.Equal(@"C:\out\222\过程物", plan.ProcessArtifactDirectory);
            Assert.Equal(@"C:\out\222\过程物\inner.7z", plan.ProcessArtifactMoves[0].To);
        }

        [Fact]
        public void Plan_SourceDirectoryFlatWithoutArchiveName_WarnsAboutSharedProcessFolder()
        {
            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { File(@"out\666\a.mp4"), Artifact(@"inner.7z", 10) },
                @"C:\111",
                stagingRoot: Staging,
                contentRoot: "out",
                placementMode: OutputPlacementMode.SourceDirectoryFlat);

            Assert.Equal(@"C:\111\过程物", plan.ProcessArtifactDirectory);
            Assert.Contains(plan.Warnings, warning => warning.Contains("过程物"));
        }

        // ── §3.2 过程物集中 ───────────────────────────────────────────────────

        [Fact]
        public void Plan_ProcessArtifacts_AreCollectedUnderOneFolderKeepingStructure()
        {
            FinalizePlan plan = Plan(
                File(@"out\666\a.mp4"),
                Artifact(@"inner.7z", 10),
                Artifact(@"volumes\222.7z.001", 20),
                Artifact(@"volumes\222.7z.002", 20));

            Assert.Equal(@"C:\out\222\过程物", plan.ProcessArtifactDirectory);
            Assert.Equal(2, plan.ProcessArtifactMoves.Count);
            Assert.Equal(
                (@"C:\work\t1\inner.7z", @"C:\out\222\过程物\inner.7z"),
                (plan.ProcessArtifactMoves[0].From, plan.ProcessArtifactMoves[0].To));

            // 分卷那一组保持自己的相对结构：整目录搬，不在过程物里摊平。
            Assert.Equal(
                (@"C:\work\t1\volumes", @"C:\out\222\过程物\volumes"),
                (plan.ProcessArtifactMoves[1].From, plan.ProcessArtifactMoves[1].To));

            Assert.Equal(50, plan.ProcessArtifactTotalSize);
            Assert.All(plan.ProcessArtifactMoves, m => Assert.StartsWith(@"C:\out\222\过程物", m.To));
        }

        [Fact]
        public void Plan_ContentRootOutsideEntries_AreTreatedAsProcessArtifacts()
        {
            // 不显式标也行：内容物根（out）之外的一切都算过程物。
            FinalizePlan plan = Plan(File(@"out\666\a.mp4"), File(@"carved\inner.7z", 7), File(@"volumes\222.7z.001", 8));

            Assert.Equal(2, plan.ProcessArtifactMoves.Count);
            Assert.Equal(@"C:\out\222\过程物\carved", plan.ProcessArtifactMoves[0].To);
            Assert.Equal(@"C:\out\222\过程物\volumes", plan.ProcessArtifactMoves[1].To);
            Assert.Equal(15, plan.ProcessArtifactTotalSize);
        }

        [Fact]
        public void Plan_ProcessArtifactNameCollision_GetsSequenceNumberInsteadOfOverwriting()
        {
            // 两个空壳目录清洗后同名（e? 与 e* 都会被清成 e_）→ 第二个加序号，绝不覆盖。
            FinalizePlan plan = Plan(File(@"out\666\a.mp4"), Dir(@"out\e?"), Dir(@"out\e*"));

            Assert.Equal(2, plan.ProcessArtifactMoves.Count);

            // 计划内顺序是稳定的（按暂存相对路径序数排序）：'*' < '?'，所以 e* 在前。
            Assert.Equal(
                (@"C:\work\t1\out\e*", @"C:\out\222\过程物\out\e_"),
                (plan.ProcessArtifactMoves[0].From, plan.ProcessArtifactMoves[0].To));

            Assert.Equal(
                (@"C:\work\t1\out\e?", @"C:\out\222\过程物\out\e_(1)"),
                (plan.ProcessArtifactMoves[1].From, plan.ProcessArtifactMoves[1].To));
        }

        [Fact]
        public void Plan_ContentItemsWithSameSanitizedName_GetSequenceNumbersInsteadOfOverwriting()
        {
            FinalizePlan plan = Plan(File(@"out\a?.mp4"), File(@"out\a*.mp4"));

            Assert.Equal(2, plan.Moves.Count);
            Assert.Equal(@"C:\out\222\a_.mp4", plan.Moves[0].To);
            Assert.Equal(@"C:\out\222\a_(1).mp4", plan.Moves[1].To);
        }

        [Fact]
        public void Plan_ContentFolderNamedProcessArtifact_MovesArtifactsAsideInstead()
        {
            FinalizePlan plan = Plan(File(@"out\过程物\a.mp4"), Artifact(@"carved\inner.7z", 10));

            Assert.Equal(@"C:\out\222\过程物", plan.ContentParentDirectory);
            Assert.Equal(@"C:\out\222\过程物(1)", plan.ProcessArtifactDirectory);
            Assert.Equal(@"C:\out\222\过程物(1)\carved", plan.ProcessArtifactMoves[0].To);
        }

        [Fact]
        public void Plan_ProcessArtifactsOnly_ReportsProcessArtifactsOnly()
        {
            FinalizePlan plan = Plan(Artifact(@"carved\inner.7z", 9));

            Assert.Equal(FinalizeLayoutKind.ProcessArtifactsOnly, plan.Layout);
            Assert.Empty(plan.ContentMoves);
            Assert.Single(plan.ProcessArtifactMoves);
            Assert.Equal(string.Empty, plan.ContentParentDirectory);
            Assert.Equal(string.Empty, plan.ContentDirectoryName);
            Assert.Equal(9, plan.ProcessArtifactTotalSize);
        }

        [Fact]
        public void Plan_ShellInsideWholesaleContent_IsNotPlannedTwice()
        {
            // 内容物那一层（777）被整棵搬走时，它里面的空壳是跟着走的，不能再单独规划一次。
            FinalizePlan plan = Plan(
                Dir(@"out\666\777\emptyInside"),
                File(@"out\666\777\a.mp4"));

            Assert.Single(plan.Moves);
            Assert.Empty(plan.ProcessArtifactMoves);
            Assert.Equal((@"C:\work\t1\out\666\777", @"C:\out\222\777"), (plan.Moves[0].From, plan.Moves[0].To));
        }

        // ── 移动计划的基本性质 ────────────────────────────────────────────────

        [Fact]
        public void Plan_MovesNeverContainBothAParentAndItsChild()
        {
            FinalizePlan plan = Plan(
                File(@"out\666\a.mp4"),
                File(@"out\666\sub\b.mp4"),
                Artifact(@"carved\inner.7z"),
                Artifact(@"carved\deeper\other.7z"),
                Dir(@"out\empty"));

            var sources = plan.Moves.Select(m => m.From).ToList();

            foreach (string source in sources)
            {
                Assert.DoesNotContain(
                    sources,
                    other => !string.Equals(other, source, StringComparison.OrdinalIgnoreCase)
                             && source.StartsWith(other + @"\", StringComparison.OrdinalIgnoreCase));
            }
        }

        [Fact]
        public void Plan_MovesPutContentBeforeProcessArtifacts()
        {
            FinalizePlan plan = Plan(File(@"out\666\a.mp4"), Artifact(@"carved\inner.7z"));

            Assert.Equal(2, plan.Moves.Count);
            Assert.Equal(@"C:\work\t1\out\666", plan.Moves[0].From);
            Assert.Equal(@"C:\work\t1\carved", plan.Moves[1].From);
        }

        [Fact]
        public void Plan_WithoutStagingRoot_KeepsRelativeSources()
        {
            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { File(@"out\movie.mp4") }, Dest, contentRoot: "out");

            Assert.Equal(@"out\movie.mp4", plan.Moves[0].From);
            Assert.Equal(@"C:\out\222\movie.mp4", plan.Moves[0].To);
        }

        // ── 边界 ──────────────────────────────────────────────────────────────

        [Fact]
        public void Plan_EmptyInput_IsEmpty()
        {
            FinalizePlan plan = Plan();

            Assert.Equal(FinalizeLayoutKind.Empty, plan.Layout);
            Assert.Empty(plan.Moves);
            Assert.Empty(plan.ContentMoves);
            Assert.Empty(plan.ProcessArtifactMoves);
            Assert.Equal(string.Empty, plan.ContentParentDirectory);
        }

        [Fact]
        public void Plan_NullInput_IsEmpty()
        {
            FinalizePlan plan = ResultFinalizer.Plan(null, Dest, stagingRoot: Staging, contentRoot: "out");

            Assert.Equal(FinalizeLayoutKind.Empty, plan.Layout);
            Assert.Empty(plan.Moves);
        }

        [Fact]
        public void Plan_OnlyEmptyFolders_IsProcessArtifactsOnly()
        {
            // 只有纯空壳：内容物根整棵都算过程物，一次搬走。
            FinalizePlan plan = Plan(Dir(@"out\empty1"), Dir(@"out\empty2"));

            Assert.Equal(FinalizeLayoutKind.ProcessArtifactsOnly, plan.Layout);
            Assert.Empty(plan.ContentMoves);
            Assert.Single(plan.ProcessArtifactMoves);
            Assert.Equal(
                (@"C:\work\t1\out", @"C:\out\222\过程物\out"),
                (plan.ProcessArtifactMoves[0].From, plan.ProcessArtifactMoves[0].To));
        }

        [Fact]
        public void Plan_MissingDestinationDirectory_Fails()
        {
            FinalizePlan plan = ResultFinalizer.Plan(new[] { File(@"out\a.mp4") }, "  ", contentRoot: "out");

            Assert.Equal(FinalizeLayoutKind.Failed, plan.Layout);
            Assert.Empty(plan.Moves);
            Assert.False(string.IsNullOrWhiteSpace(plan.FailureReason));
        }

        [Fact]
        public void Plan_MissingContentRoot_TreatsEverythingAsProcessArtifacts()
        {
            // 内容物根写错时宁可"没有内容物"，也不能把暂存区里的中间件当成用户的东西搬出去。
            FinalizePlan plan = Plan(File(@"other\a.mp4"));

            Assert.Equal(FinalizeLayoutKind.ProcessArtifactsOnly, plan.Layout);
            Assert.Empty(plan.ContentMoves);
            Assert.Single(plan.ProcessArtifactMoves);
            Assert.Equal(@"C:\out\222\过程物\other", plan.ProcessArtifactMoves[0].To);
            Assert.Contains(plan.Warnings, warning => warning.Contains("找不到内容物根"));
        }

        [Fact]
        public void Plan_EntriesEscapingStagingRoot_AreIgnoredWithWarning()
        {
            FinalizePlan plan = Plan(File(@"..\..\evil.txt"), File(@"out\a.mp4"));

            Assert.Equal(FinalizeLayoutKind.SingleFileToDestination, plan.Layout);
            Assert.Single(plan.Moves);
            Assert.Equal(@"C:\out\222\a.mp4", plan.Moves[0].To);
            Assert.Contains(plan.Warnings, warning => warning.Contains("越出暂存根"));
        }

        [Fact]
        public void Plan_AbsoluteAndDriveEntries_AreIgnored()
        {
            FinalizePlan plan = Plan(File(@"C:\outside\x.txt"), File(@"\\server\share\y.txt"), File(@"out\a.mp4"));

            Assert.Single(plan.Moves);
            Assert.Equal(2, plan.Warnings.Count(warning => warning.Contains("越出暂存根")));
        }

        [Fact]
        public void Plan_SlashSeparatedPaths_AreAccepted()
        {
            FinalizePlan plan = Plan(File("out/666/a.mp4"));

            Assert.Equal(FinalizeLayoutKind.WrapInFolder, plan.Layout);
            Assert.Equal(@"C:\out\222\666", plan.Moves[0].To);
        }

        [Fact]
        public void Plan_FileAndDirectoryEntriesForSamePath_ResolveWithoutThrowing()
        {
            FinalizePlan plan = Plan(File(@"out\666\a.mp4"), Dir(@"out\666\a.mp4"));

            Assert.NotEqual(FinalizeLayoutKind.Failed, plan.Layout);
            Assert.Contains(plan.Warnings, warning => warning.Contains("按目录处理"));
        }

        // ── 与落点解析串起来（用户原话的端到端形状） ──────────────────────────

        [Fact]
        public void PlacementAndFinalizer_ScenarioA_ProducesPackageFolderWithContent()
        {
            // 111\222\333.rar（内含 333\ 一层）→ 111\222\333\…
            OutputPlacementResult placement = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222\333.rar",
                OutputPlacementMode.PerArchiveSubfolder,
                collapseRepeatedFolderLayer: true,
                sourceDirectoryContainsOnlyThisArchive: true);

            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { File(@"out\333\a.mp4"), File(@"out\333\b.mp4") },
                placement.DestinationDirectory,
                TerminalLayoutMode.KeepLastFolder,
                placement.ArchiveBaseName,
                "out",
                Staging);

            Assert.Equal(@"C:\111\222\333", plan.ContentParentDirectory);
            Assert.Equal(@"C:\111\222\333\a.mp4", plan.Moves[0].To);
            Assert.Equal(@"C:\111\222\333\b.mp4", plan.Moves[1].To);
        }

        [Fact]
        public void PlacementAndFinalizer_ScenarioB_DoesNotNestTheRepeatedNameAgain()
        {
            // 111\222\名字\名字.rar（内含 名字\ 一层）→ 111\222\名字\…（不再出现"名字\名字"）
            OutputPlacementResult placement = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222\名字\名字.rar",
                OutputPlacementMode.PerArchiveSubfolder,
                collapseRepeatedFolderLayer: true,
                sourceDirectoryContainsOnlyThisArchive: true);

            Assert.Equal(@"C:\111\222\名字", placement.DestinationDirectory);

            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { File(@"out\名字\a.mp4"), File(@"out\名字\b.mp4") },
                placement.DestinationDirectory,
                TerminalLayoutMode.KeepLastFolder,
                placement.ArchiveBaseName,
                "out",
                Staging);

            Assert.Equal(@"C:\111\222\名字", plan.ContentParentDirectory);
            Assert.Equal(@"C:\111\222\名字\a.mp4", plan.Moves[0].To);
        }

        [Fact]
        public void PlacementAndFinalizer_SingleFileArchive_EndsBesidePackageFolder()
        {
            OutputPlacementResult placement = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222.rar", OutputPlacementMode.PerArchiveSubfolder);

            FinalizePlan plan = ResultFinalizer.Plan(
                new[] { File(@"out\666.MP4") },
                placement.DestinationDirectory,
                TerminalLayoutMode.KeepLastFolder,
                placement.ArchiveBaseName,
                "out",
                Staging);

            Assert.Equal(@"C:\111\222\666.MP4", plan.Moves[0].To);
        }
    }
}
