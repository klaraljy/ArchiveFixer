using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ArchiveFixer.Engines;
using ArchiveFixer.Security;
using ArchiveFixer.Storage;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// M5「安全与恢复」第一批的测试：路径预检（ArchivePathGuard）/ 资源预算（ResourceBudget）/
    /// 目标空间检查（SpaceChecker）。
    ///
    /// 三条原则：
    /// ① 绝大多数断言不碰真实磁盘 —— 预检与预算都是纯计算，一旦去建目录建文件，
    ///    就变成依赖盘符和可用空间的集成测试了（慢，而且换台机器就红）；
    /// ② 少数必须问磁盘的（SpaceChecker）只用 Path.GetTempPath()，且只断言
    ///    "要么取不到、要么是正数"这类宽松条件，不写死具体数值；
    /// ③ 路径一律用合成值（C:\t\...），不出现任何真实用户目录。
    ///
    /// ⚠ 这些测试证明的是"预检认得出危险条目名"，**不是**"解压一定安全"：
    /// 真正写盘的是外部进程 7z.exe，进程外挡不住它（见 ArchivePathGuard 的类注释）。
    /// </summary>
    public class SecurityGuardTests
    {
        /// <summary>合成目标根目录，全部测试共用（这些路径只做字符串比较，不需要真的存在）。</summary>
        private const string Root = @"C:\t\out";

        private static ArchiveEntry Entry(string path, long size = 10, bool isDirectory = false)
        {
            return new ArchiveEntry
            {
                Path = path,
                Size = size,
                IsDirectory = isDirectory
            };
        }

        /// <summary>按条目清单拼一个"列目录成功"的结果，文件数 / 总大小由条目算出来（不手填，免得两处对不上）。</summary>
        private static ArchiveListResult ArchiveList(params ArchiveEntry[] entries)
        {
            long total = 0;
            int fileCount = 0;

            foreach (ArchiveEntry entry in entries)
            {
                if (entry.IsDirectory)
                {
                    continue;
                }

                total += entry.Size;
                fileCount++;
            }

            return new ArchiveListResult
            {
                Success = true,
                Entries = entries,
                FileCount = fileCount,
                TotalUncompressedSize = total
            };
        }

        // ------------------------------------------------------------------
        // ArchivePathGuard.CheckEntry
        // ------------------------------------------------------------------

        [Theory]
        // 跳出目标目录
        [InlineData("../evil.txt", PathRiskKind.ParentTraversal)]
        [InlineData("..\\evil.txt", PathRiskKind.ParentTraversal)]
        [InlineData("a/../../b", PathRiskKind.ParentTraversal)]
        [InlineData("a\\..\\..\\b.txt", PathRiskKind.ParentTraversal)]
        [InlineData(".. /evil.txt", PathRiskKind.ParentTraversal)]
        // 绝对路径 / 盘符
        [InlineData("/etc/passwd", PathRiskKind.AbsolutePath)]
        [InlineData("C:\\Windows\\x", PathRiskKind.AbsolutePath)]
        [InlineData("C:/Windows/x", PathRiskKind.AbsolutePath)]
        [InlineData("C:x", PathRiskKind.DriveRelative)]
        [InlineData("C:", PathRiskKind.DriveRelative)]
        // UNC 与扩展 / 设备前缀
        [InlineData("\\\\server\\share\\x", PathRiskKind.UncPath)]
        [InlineData("//server/share/x", PathRiskKind.UncPath)]
        [InlineData("\\\\?\\C:\\x", PathRiskKind.ExtendedPrefix)]
        [InlineData("\\\\.\\PhysicalDrive0", PathRiskKind.ExtendedPrefix)]
        // 保留设备名（含带扩展名的形式）
        [InlineData("CON", PathRiskKind.DeviceName)]
        [InlineData("NUL.txt", PathRiskKind.DeviceName)]
        [InlineData("docs/COM1.log", PathRiskKind.DeviceName)]
        [InlineData("a/NUL.foo.bar", PathRiskKind.DeviceName)]
        // 结尾点 / 空格
        [InlineData("name.", PathRiskKind.TrailingDotOrSpace)]
        [InlineData("name ", PathRiskKind.TrailingDotOrSpace)]
        // 备用数据流
        [InlineData("file:stream", PathRiskKind.AlternateDataStream)]
        [InlineData("docs/readme.txt:hidden", PathRiskKind.AlternateDataStream)]
        // 控制字符
        [InlineData("a/\u0001b.txt", PathRiskKind.ControlCharacter)]
        [InlineData("x\u007fy.txt", PathRiskKind.ControlCharacter)]
        // 空路径
        [InlineData("", PathRiskKind.EmptyEntry)]
        [InlineData("   ", PathRiskKind.EmptyEntry)]
        public void CheckEntry_DangerousForms_AreClassified(string entryPath, PathRiskKind expected)
        {
            UnsafeArchiveEntry? risk = ArchivePathGuard.CheckEntry(entryPath);

            Assert.NotNull(risk);
            Assert.Equal(expected, risk!.Kind);
            Assert.False(string.IsNullOrWhiteSpace(risk.Reason));
        }

        [Theory]
        [InlineData("docs/说明.txt")]
        [InlineData("a/b/c.txt")]
        [InlineData("中文目录/中文文件.zip")]
        [InlineData("docs\\sub\\file.txt")]
        [InlineData("sub/dir/")]
        [InlineData("a.b.c")]
        [InlineData("图片/照片 01.jpg")]
        [InlineData(".hidden")]
        // "./" 前缀在 tar 里非常常见，它逃不出目标目录，不该报成危险条目
        // （代价是落点与朴素拼接不一致，所以要靠解压后的落点校验兜住）。
        [InlineData("./a/b.txt")]
        [InlineData("a/./b")]
        public void CheckEntry_NormalPaths_ReturnNull(string entryPath)
        {
            Assert.Null(ArchivePathGuard.CheckEntry(entryPath));
        }

        [Fact]
        public void CheckEntry_Null_IsEmptyEntry()
        {
            UnsafeArchiveEntry? risk = ArchivePathGuard.CheckEntry(null);

            Assert.NotNull(risk);
            Assert.Equal(PathRiskKind.EmptyEntry, risk!.Kind);
        }

        [Theory]
        // 一条路径只报一个最严重的问题，优先级顺序必须稳定。
        [InlineData("\\\\.\\C:\\..\\x", PathRiskKind.ExtendedPrefix)]      // 扩展前缀 > 穿越
        [InlineData("../CON", PathRiskKind.ParentTraversal)]               // 穿越 > 设备名
        [InlineData("a/file:stream/..", PathRiskKind.ParentTraversal)]     // 穿越 > 备用数据流
        [InlineData("C:..\\..\\x", PathRiskKind.DriveRelative)]            // 盘符相对 > 穿越
        [InlineData("a/COM1.log\u0001", PathRiskKind.DeviceName)]          // 设备名 > 控制字符
        [InlineData("x\u0001.", PathRiskKind.ControlCharacter)]            // 控制字符 > 结尾点
        public void CheckEntry_ReportsOnlyTheMostSevereRisk(string entryPath, PathRiskKind expected)
        {
            UnsafeArchiveEntry? risk = ArchivePathGuard.CheckEntry(entryPath);

            Assert.NotNull(risk);
            Assert.Equal(expected, risk!.Kind);
        }

        // ------------------------------------------------------------------
        // ArchivePathGuard.CheckEntries
        // ------------------------------------------------------------------

        [Fact]
        public void CheckEntries_TwelveEntriesWithOneDangerous_ReportsExactlyThatOne()
        {
            var entries = new List<ArchiveEntry>
            {
                Entry("docs/说明.txt"),
                Entry("docs/readme.md"),
                Entry("a/b/c.txt"),
                Entry("中文目录/文件.zip"),
                Entry("sub/dir/"),
                Entry("data/1.bin"),
                Entry("图片/照片 01.jpg"),
                Entry("a.b.c"),
                Entry("x y.txt"),
                Entry("notes.md"),
                Entry("备份/2026/清单.csv"),
                Entry("../evil.txt")
            };

            PathSafetyReport report = ArchivePathGuard.CheckEntries(entries);

            Assert.False(report.IsSafe);
            Assert.Equal(12, report.CheckedEntryCount);

            UnsafeArchiveEntry risk = Assert.Single(report.UnsafeEntries);
            Assert.Equal("../evil.txt", risk.EntryPath);
            Assert.Equal(PathRiskKind.ParentTraversal, risk.Kind);
            Assert.Contains("../evil.txt", report.Summary);
            Assert.Contains("12", report.Summary);
        }

        [Fact]
        public void CheckEntries_AllSafe_IsSafeWithHonestSummary()
        {
            PathSafetyReport report = ArchivePathGuard.CheckEntries(new List<ArchiveEntry>
            {
                Entry("a.txt"),
                Entry("dir/b.txt")
            });

            Assert.True(report.IsSafe);
            Assert.Empty(report.UnsafeEntries);
            Assert.Equal(2, report.CheckedEntryCount);

            // 结论只敢说到"条目名这一层"：写盘的是 7z.exe，进程外挡不住它。
            Assert.Contains("条目名", report.Summary);
        }

        [Fact]
        public void CheckEntries_NullInput_ReportsNothingWasChecked()
        {
            PathSafetyReport report = ArchivePathGuard.CheckEntries(null);

            Assert.True(report.IsSafe);
            Assert.Equal(0, report.CheckedEntryCount);
            Assert.Empty(report.UnsafeEntries);
            Assert.Equal("没有可检查的条目", report.Summary);
        }

        [Fact]
        public void CheckEntries_EmptyList_ReportsNothingWasChecked()
        {
            PathSafetyReport report = ArchivePathGuard.CheckEntries(new List<ArchiveEntry>());

            Assert.True(report.IsSafe);
            Assert.Equal(0, report.CheckedEntryCount);
            Assert.Equal("没有可检查的条目", report.Summary);
        }

        [Fact]
        public void CheckEntries_DirectoryEntries_AreCheckedToo()
        {
            // 目录名决定的是它后面所有文件的落点，只查文件条目挡不住"目录先跳出去"。
            PathSafetyReport report = ArchivePathGuard.CheckEntries(new List<ArchiveEntry>
            {
                Entry("..\\", isDirectory: true),
                Entry("a.txt")
            });

            Assert.False(report.IsSafe);
            Assert.Equal(2, report.CheckedEntryCount);
            Assert.Equal(PathRiskKind.ParentTraversal, Assert.Single(report.UnsafeEntries).Kind);
        }

        [Fact]
        public void CheckEntries_NullElement_TreatedAsEmptyEntry()
        {
            // 清单里混进 null 是调用方数据异常，不能当"没问题"放过去，也不能抛。
            PathSafetyReport report = ArchivePathGuard.CheckEntries(new ArchiveEntry[] { null!, Entry("a.txt") });

            Assert.False(report.IsSafe);
            Assert.Equal(PathRiskKind.EmptyEntry, Assert.Single(report.UnsafeEntries).Kind);
        }

        [Fact]
        public void CheckEntries_ManyDangerous_ListsAtMostThreeSamples()
        {
            PathSafetyReport report = ArchivePathGuard.CheckEntries(new List<ArchiveEntry>
            {
                Entry("../a.txt"),
                Entry("../b.txt"),
                Entry("../c.txt"),
                Entry("../d.txt"),
                Entry("../e.txt")
            });

            Assert.Equal(5, report.UnsafeEntries.Count);
            Assert.Contains("../a.txt", report.Summary);
            Assert.Contains("../c.txt", report.Summary);
            Assert.DoesNotContain("../d.txt", report.Summary);
            Assert.Contains("另有 2 条未列出", report.Summary);
        }

        // ------------------------------------------------------------------
        // ArchivePathGuard.IsInsideRoot（解压后的第二道）
        // ------------------------------------------------------------------

        [Fact]
        public void IsInsideRoot_PathUnderRoot_IsTrue()
        {
            Assert.True(ArchivePathGuard.IsInsideRoot(Root, @"C:\t\out\a\b.txt", out string reason));
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }

        [Fact]
        public void IsInsideRoot_NEVER_TrustsRawStrings()
        {
            // 规范化后比，而不是比字符串：root\..\x 必须判成"在外面"。
            Assert.False(ArchivePathGuard.IsInsideRoot(Root, @"C:\t\out\..\x.txt", out string reason));
            Assert.False(string.IsNullOrWhiteSpace(reason));

            // 而且 reason 里给出的是**展开后**的落点，用户才知道东西到底写到哪去了。
            Assert.Contains(@"C:\t\x.txt", reason);
        }

        [Fact]
        public void IsInsideRoot_SiblingDirectoryWithSamePrefix_IsFalse()
        {
            // C:\t\out2 与 C:\t\out 前缀相同，但根本不是同一个目录。
            // 用 target.StartsWith(root) 判会在这里放过一个越界落点。
            Assert.False(ArchivePathGuard.IsInsideRoot(Root, @"C:\t\out2\a.txt", out string reason));
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }

        [Fact]
        public void IsInsideRoot_DifferentDrive_IsFalse()
        {
            Assert.False(ArchivePathGuard.IsInsideRoot(Root, @"D:\t\out\a.txt", out string reason));
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }

        [Fact]
        public void IsInsideRoot_RelativePath_IsFalse()
        {
            // 相对路径按当前工作目录展开，展开结果不会落在目标根目录下 —— 保守判 false。
            Assert.False(ArchivePathGuard.IsInsideRoot(Root, "a.txt", out string reason));
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }

        [Fact]
        public void IsInsideRoot_EmptyArguments_AreFalseWithReason()
        {
            Assert.False(ArchivePathGuard.IsInsideRoot(null, @"C:\t\out\a.txt", out string reason1));
            Assert.False(string.IsNullOrWhiteSpace(reason1));

            Assert.False(ArchivePathGuard.IsInsideRoot(Root, null, out string reason2));
            Assert.False(string.IsNullOrWhiteSpace(reason2));

            Assert.False(ArchivePathGuard.IsInsideRoot("   ", string.Empty, out string reason3));
            Assert.False(string.IsNullOrWhiteSpace(reason3));
        }

        [Fact]
        public void IsInsideRoot_CaseAndRedundantSeparators_AreNormalized()
        {
            Assert.True(ArchivePathGuard.IsInsideRoot(@"C:\t\Out", @"c:\t\out\a.txt", out _));

            // 目标根目录结尾多一个分隔符不该被当成两个不同的目录。
            Assert.True(ArchivePathGuard.IsInsideRoot(@"C:\t\out\", @"C:\t\out\a.txt", out _));

            // 落点就是目标根目录本身（如条目名 "./"）：没有越界。
            Assert.True(ArchivePathGuard.IsInsideRoot(Root, @"C:\t\out\.", out _));
        }

        // ------------------------------------------------------------------
        // ResourceBudget.CheckBeforeExtract
        // ------------------------------------------------------------------

        [Fact]
        public void CheckBeforeExtract_NullList_AllowsAndSwitchesToRuntimeBudget()
        {
            BudgetCheckResult result = new ResourceBudget().CheckBeforeExtract(null, 1024, null);

            // 不因为"不知道"就放行成"没问题"，也不因为不知道就拒绝：
            // 放行，但必须在 Reason 里写明改用了运行时预算。
            Assert.True(result.Allowed);
            Assert.Contains("运行时预算", result.Reason);
            Assert.Equal(0L, result.EstimatedTotalSize);
            Assert.Equal(0d, result.ExpansionRatio);
            Assert.Null(result.FreeSpaceBytes);
        }

        [Fact]
        public void CheckBeforeExtract_FailedList_AllowsAndSwitchesToRuntimeBudget()
        {
            ArchiveListResult list = ArchiveListResult.Failure("UnsupportedFeature", "引擎拒绝列目录", "7z", "26.01");

            BudgetCheckResult result = new ResourceBudget().CheckBeforeExtract(list, 1024, null);

            Assert.True(result.Allowed);
            Assert.Contains("运行时预算", result.Reason);
        }

        [Fact]
        public void CheckBeforeExtract_SingleFileTooLarge_IsRejectedWithFileName()
        {
            var options = new ResourceBudgetOptions { MaxSingleFileSize = 1000 };
            ArchiveListResult list = ArchiveList(Entry("docs/big.bin", 5000), Entry("docs/small.bin", 10));

            BudgetCheckResult result = new ResourceBudget(options).CheckBeforeExtract(list, 1_000_000, null);

            Assert.False(result.Allowed);
            Assert.Contains("docs/big.bin", result.Reason);
            Assert.Contains("5000", result.Reason);
        }

        [Fact]
        public void CheckBeforeExtract_TooManyFiles_IsRejected()
        {
            var options = new ResourceBudgetOptions { MaxFileCount = 3 };
            ArchiveListResult list = ArchiveList(Entry("a", 10), Entry("b", 10), Entry("c", 10), Entry("d", 10));

            BudgetCheckResult result = new ResourceBudget(options).CheckBeforeExtract(list, 1_000_000, null);

            Assert.False(result.Allowed);
            Assert.Contains("文件数", result.Reason);
        }

        [Fact]
        public void CheckBeforeExtract_TotalSizeTooLarge_IsRejected()
        {
            var options = new ResourceBudgetOptions { MaxTotalSize = 1000 };
            ArchiveListResult list = ArchiveList(Entry("a.bin", 600), Entry("b.bin", 600));

            BudgetCheckResult result = new ResourceBudget(options).CheckBeforeExtract(list, 1_000_000, null);

            Assert.False(result.Allowed);
            Assert.Contains("总大小", result.Reason);
            Assert.Contains("1200", result.Reason);
        }

        [Fact]
        public void CheckBeforeExtract_ExpansionRatioTooHigh_IsRejectedAsBomb()
        {
            // 100 字节的包解压出 1 MiB → 约 10485 倍，远超上限。
            var options = new ResourceBudgetOptions { MaxExpansionRatio = 100d };
            ArchiveListResult list = ArchiveList(Entry("bomb.bin", 1024 * 1024));

            BudgetCheckResult result = new ResourceBudget(options).CheckBeforeExtract(list, 100, null);

            Assert.False(result.Allowed);
            Assert.Contains("压缩炸弹", result.Reason);
        }

        [Fact]
        public void CheckBeforeExtract_UnknownArchiveSize_SkipsRatioCheck()
        {
            // 压缩包体积未知（<=0）时比值记 0，**不做**展开比判断（不知道就别猜）。
            var options = new ResourceBudgetOptions { MaxExpansionRatio = 1d };
            ArchiveListResult list = ArchiveList(Entry("a.bin", 1024));

            BudgetCheckResult result = new ResourceBudget(options).CheckBeforeExtract(list, 0, null);

            Assert.True(result.Allowed);
            Assert.Equal(0d, result.ExpansionRatio);
        }

        [Fact]
        public void CheckBeforeExtract_UsesTheMoreConservativeOfReportedAndSummedNumbers()
        {
            // 引擎自报 FileCount=1 / 总大小 10，清单里实际是 3 个文件 / 3000 字节：
            // 取更保守的一侧，不能让"自报数字偏小"把超限的包放过去。
            var options = new ResourceBudgetOptions { MaxTotalSize = 2000 };
            var list = new ArchiveListResult
            {
                Success = true,
                Entries = new List<ArchiveEntry> { Entry("a", 1000), Entry("b", 1000), Entry("c", 1000) },
                FileCount = 1,
                TotalUncompressedSize = 10
            };

            BudgetCheckResult result = new ResourceBudget(options).CheckBeforeExtract(list, 1_000_000, null);

            Assert.False(result.Allowed);
            Assert.Equal(3000L, result.EstimatedTotalSize);
        }

        [Fact]
        public void CheckBeforeExtract_DirectoryEntries_DoNotConsumeBudget()
        {
            var options = new ResourceBudgetOptions { MaxFileCount = 1, MaxTotalSize = 100 };
            ArchiveListResult list = ArchiveList(
                Entry("dir/", 0, isDirectory: true),
                Entry("a.bin", 10),
                Entry("dir2/", 0, isDirectory: true));

            BudgetCheckResult result = new ResourceBudget(options).CheckBeforeExtract(list, 1_000_000, null);

            Assert.True(result.Allowed);
            Assert.Equal(10L, result.EstimatedTotalSize);
        }

        [Fact]
        public void CheckBeforeExtract_NotEnoughFreeSpace_IsRejectedWithNumbers()
        {
            string tempPath = Path.GetTempPath();
            long? freeSpace = SpaceChecker.GetAvailableFreeSpace(tempPath);

            if (freeSpace == null)
            {
                // 取不到可用空间时不做空间判断（这本身就是被允许的一条路径，也不抛）。
                return;
            }

            // 其它上限全部放开，只留"空间"这一条：估算值正好比当前可用空间多 1 字节。
            var options = new ResourceBudgetOptions
            {
                MaxSingleFileSize = long.MaxValue,
                MaxTotalSize = long.MaxValue,
                MaxFileCount = int.MaxValue,
                MaxExpansionRatio = double.MaxValue,
                MinFreeSpaceReserveBytes = 1
            };

            long required = freeSpace.Value + 1;
            ArchiveListResult list = ArchiveList(Entry("huge.bin", required));

            BudgetCheckResult result = new ResourceBudget(options).CheckBeforeExtract(list, 0, tempPath);

            Assert.False(result.Allowed);
            Assert.Contains("空间", result.Reason);
        }

        [Fact]
        public void CheckBeforeExtract_BlankTargetDirectory_DoesNotQuerySpaceAndStillAllows()
        {
            // 目标目录是空白时就压根不去问盘（等价于"没取到"）：空间这一项不判定，
            // 不能因此拒绝，也不能声称空间充足 —— 只能照实说没取到。
            ArchiveListResult list = ArchiveList(Entry("a.bin", 1024));

            BudgetCheckResult result = new ResourceBudget().CheckBeforeExtract(list, 1_000_000, "   ");

            Assert.True(result.Allowed);
            Assert.Null(result.FreeSpaceBytes);
            Assert.Contains("未取到目标盘可用空间", result.Reason);
        }

        [Fact]
        public void CheckBeforeExtract_UnreachableDrive_DoesNotThrow()
        {
            // Z: 在多数机器上不存在。这条只要求"不抛 + 结论文案自洽"：
            // 万一这台机器真有 Z:，就不能再断言"取不到空间"了。
            ArchiveListResult list = ArchiveList(Entry("a.bin", 1024));

            BudgetCheckResult result = new ResourceBudget().CheckBeforeExtract(list, 1_000_000, @"Z:\t\out");

            if (result.FreeSpaceBytes == null)
            {
                Assert.True(result.Allowed);
                Assert.Contains("未取到目标盘可用空间", result.Reason);
            }

            Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        }

        // ------------------------------------------------------------------
        // BudgetTracker
        // ------------------------------------------------------------------

        [Fact]
        public void BudgetTracker_Record_AccumulatesWhileWithinBudget()
        {
            var options = new ResourceBudgetOptions
            {
                MaxTotalSize = 1000,
                MaxFileCount = 10,
                MaxSingleFileSize = 400
            };

            BudgetTracker tracker = new ResourceBudget(options).CreateTracker();

            Assert.Null(tracker.Record(300));
            Assert.Null(tracker.Record(300));
            Assert.Equal(600L, tracker.TotalBytes);
            Assert.Equal(2, tracker.FileCount);
            Assert.Equal("累计 2 个文件 / 600 字节", tracker.Describe());
        }

        [Fact]
        public void BudgetTracker_Record_OverTotalLimit_ReturnsReasonWithNumbers()
        {
            var options = new ResourceBudgetOptions { MaxTotalSize = 500 };
            BudgetTracker tracker = new ResourceBudget(options).CreateTracker();

            Assert.Null(tracker.Record(400));

            string? reason = tracker.Record(400);

            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.Contains("800", reason!);
            Assert.Contains("500", reason!);
        }

        [Fact]
        public void BudgetTracker_Record_SingleFileOverLimit_ReturnsReason()
        {
            var options = new ResourceBudgetOptions
            {
                MaxSingleFileSize = 100,
                MaxTotalSize = long.MaxValue,
                MaxFileCount = int.MaxValue
            };

            BudgetTracker tracker = new ResourceBudget(options).CreateTracker();

            string? reason = tracker.Record(101);

            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.Contains("单文件", reason!);
        }

        [Fact]
        public void BudgetTracker_Record_FileCountOverLimit_ReturnsReason()
        {
            var options = new ResourceBudgetOptions
            {
                MaxFileCount = 2,
                MaxTotalSize = long.MaxValue,
                MaxSingleFileSize = long.MaxValue
            };

            BudgetTracker tracker = new ResourceBudget(options).CreateTracker();

            Assert.Null(tracker.Record(1));
            Assert.Null(tracker.Record(1));

            string? reason = tracker.Record(1);

            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.Contains("文件数", reason!);
            Assert.Equal(3, tracker.FileCount);
        }

        [Fact]
        public void BudgetTracker_Record_NegativeSize_CountsAsZero()
        {
            BudgetTracker tracker = new ResourceBudget().CreateTracker();

            // 负数按 0 计，但文件本身还是要记一个（数量是要计的）。
            Assert.Null(tracker.Record(-100));
            Assert.Equal(0L, tracker.TotalBytes);
            Assert.Equal(1, tracker.FileCount);
        }

        [Fact]
        public void BudgetTracker_Record_IsThreadSafe()
        {
            // 并发解压会同时调用 Record：用"读-改-写"会少算，而少算正是预算最不能出的错。
            var options = new ResourceBudgetOptions
            {
                MaxTotalSize = long.MaxValue,
                MaxFileCount = int.MaxValue,
                MaxSingleFileSize = long.MaxValue
            };

            BudgetTracker tracker = new ResourceBudget(options).CreateTracker();

            Parallel.For(0, 100, _ => tracker.Record(1));

            Assert.Equal(100, tracker.FileCount);
            Assert.Equal(100L, tracker.TotalBytes);
        }

        // ------------------------------------------------------------------
        // SpaceChecker（要问真实磁盘，所以一律用宽松断言）
        // ------------------------------------------------------------------

        [Fact]
        public void GetAvailableFreeSpace_NullOrEmptyPath_IsNull()
        {
            Assert.Null(SpaceChecker.GetAvailableFreeSpace(null));
            Assert.Null(SpaceChecker.GetAvailableFreeSpace(string.Empty));
            Assert.Null(SpaceChecker.GetAvailableFreeSpace("   "));
        }

        [Fact]
        public void GetAvailableFreeSpace_MissingDirectory_IsNullOrPositive()
        {
            // 目标目录通常要等解压时才创建 —— 取不到就 null，绝不抛。
            // 不断言具体数值：那随机器和磁盘变化。
            long? freeSpace = SpaceChecker.GetAvailableFreeSpace(@"C:\t\不存在的目录\子目录");

            Assert.True(freeSpace == null || freeSpace > 0);
        }

        [Fact]
        public void GetAvailableFreeSpace_TempPath_IsNullOrPositive()
        {
            long? freeSpace = SpaceChecker.GetAvailableFreeSpace(Path.GetTempPath());

            Assert.True(freeSpace == null || freeSpace > 0);
        }

        [Fact]
        public void HasEnoughSpace_HugeRequirement_IsFalseWithReason()
        {
            // long.MaxValue / 2 字节一定超过任何真实磁盘 —— 顺带钉住"减法不会溢出成充足"。
            bool enough = SpaceChecker.HasEnoughSpace(Path.GetTempPath(), long.MaxValue / 2, 0, out string reason);

            Assert.False(enough);
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.Contains("空间", reason);
        }

        [Fact]
        public void HasEnoughSpace_EmptyPath_IsFalseWithReason()
        {
            bool enough = SpaceChecker.HasEnoughSpace(null, 1024, 0, out string reason);

            Assert.False(enough);
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }

        [Fact]
        public void HasEnoughSpace_ZeroRequirement_FollowsAvailableSpace()
        {
            // 不要求写任何字节、也不要求保留时：只有"取不到盘"才该返回 false
            // （取不到就不敢说够，这是有意的保守选择）。
            long? freeSpace = SpaceChecker.GetAvailableFreeSpace(Path.GetTempPath());

            bool enough = SpaceChecker.HasEnoughSpace(Path.GetTempPath(), 0, 0, out string reason);

            Assert.Equal(freeSpace != null, enough);
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }

        [Fact]
        public void IsFat32_NullOrEmptyPath_IsFalse()
        {
            Assert.False(SpaceChecker.IsFat32(null));
            Assert.False(SpaceChecker.IsFat32(string.Empty));
        }

        [Fact]
        public void IsFat32_TempPath_DoesNotThrowAndIsStable()
        {
            // 跑测试的机器多半是 NTFS / exFAT，但**不能**断言 false（真跑在 FAT32 上就翻车），
            // 只钉住：不抛、同样的输入给同样的答案。
            bool first = SpaceChecker.IsFat32(Path.GetTempPath());
            bool second = SpaceChecker.IsFat32(Path.GetTempPath());

            Assert.Equal(first, second);
        }

        [Fact]
        public void IsFat32SingleFileLimitExceeded_LargeFileOnRealVolume_DoesNotThrow()
        {
            long over4GiB = 4L * 1024 * 1024 * 1024 + 1024;
            string tempPath = Path.GetTempPath();

            bool exceeded = SpaceChecker.IsFat32SingleFileLimitExceeded(tempPath, over4GiB, out string reason);

            // 真实盘多半不是 FAT32 → false；万一是 FAT32 → true。两种都合法，
            // 只要求不抛、结果与"这个卷是不是 FAT32"一致、并且给了可直接展示的原因。
            Assert.Equal(SpaceChecker.IsFat32(tempPath), exceeded);
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }

        [Fact]
        public void IsFat32SingleFileLimitExceeded_EmptyPathOrZeroSize_IsFalseWithReason()
        {
            Assert.False(SpaceChecker.IsFat32SingleFileLimitExceeded(
                null, 5L * 1024 * 1024 * 1024, out string emptyPathReason));
            Assert.False(string.IsNullOrWhiteSpace(emptyPathReason));

            Assert.False(SpaceChecker.IsFat32SingleFileLimitExceeded(
                Path.GetTempPath(), 0, out string zeroSizeReason));
            Assert.False(string.IsNullOrWhiteSpace(zeroSizeReason));
        }
    }
}
