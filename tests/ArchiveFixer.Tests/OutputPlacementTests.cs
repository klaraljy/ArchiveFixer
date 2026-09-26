using System;
using System.IO;
using ArchiveFixer.Extraction;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 输出落点解析的纯逻辑测试（规格 <c>docs/输出与整理模型.md</c> §1 + §3.3）。
    ///
    /// <para>
    /// 全部只比**路径字符串与枚举**，不比中文文案 —— 文案会改，路径不能改。
    /// 盘符存在性一律用注入的探针，只有一条测试故意走默认探针（确认默认接线没漏）。
    /// </para>
    /// </summary>
    public class OutputPlacementTests
    {
        private static readonly Func<string, bool> DriveExists = _ => true;
        private static readonly Func<string, bool> DriveMissing = _ => false;

        // ── 用户 2026-09-24 第 13 条：落点四条规则 ─────────────────────────────
        //
        // （选中的是文件还是文件夹）×（有没有指定位置），四条组合各钉一条**完整路径**。
        // 用户原话："我们解压绝对不能将原来的文件夹给弄混乱"。

        [Fact]
        public void 规则一_选中文件_未指定位置_落同目录同名子文件夹()
        {
            OutputPlacementResult result = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222.rar", OutputPlacementMode.PerArchiveSubfolder);

            Assert.True(result.Success);
            Assert.Equal(@"C:\111", result.DestinationRoot);
            Assert.Equal(@"C:\111\222", result.DestinationDirectory);
            Assert.Equal("222", result.ArchiveBaseName);
            Assert.Equal("222", result.PackageFolderName);
            Assert.False(result.UsesSelectedFolderName);
            Assert.False(result.SharesDestinationWithOtherPackages);
            Assert.False(result.CollapsedRepeatedFolderLayer);
        }

        [Fact]
        public void 规则二_选中文件夹_未指定位置_产物全部留在该文件夹里面()
        {
            // 用户在「添加文件夹」里选了 C:\111\222，这个文件夹里有好几个包。
            // 产物必须落在 222 **里面**：既不凭空多出 111\222\222 这一层，也绝不扔到 111\ 去。
            OutputPlacementResult first = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222\a.rar",
                OutputPlacementMode.PerArchiveSubfolder,
                selectionKind: SourceSelectionKind.Folder,
                selectionRoot: @"C:\111\222");

            OutputPlacementResult second = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222\b.7z.001",
                OutputPlacementMode.PerArchiveSubfolder,
                selectionKind: SourceSelectionKind.Folder,
                selectionRoot: @"C:\111\222");

            Assert.Equal(@"C:\111\222\a", first.DestinationDirectory);
            Assert.Equal(@"C:\111\222\b", second.DestinationDirectory);
            Assert.StartsWith(@"C:\111\222\", first.DestinationDirectory, StringComparison.Ordinal);
            Assert.DoesNotContain(@"C:\111\222\222", first.DestinationDirectory, StringComparison.Ordinal);
            Assert.False(first.UsesSelectedFolderName);

            /*
             * 所选文件夹里只有与它同名的那个包（111\222\222.7z.001）时，塌缩规则让产物直接落进 222 本身
             * —— 这正是用户说的"我们就在 222 文件夹里面操作"。
             */
            OutputPlacementResult onlyOne = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222\222.7z.001",
                OutputPlacementMode.PerArchiveSubfolder,
                collapseRepeatedFolderLayer: true,
                sourceDirectoryContainsOnlyThisArchive: true,
                selectionKind: SourceSelectionKind.Folder,
                selectionRoot: @"C:\111\222");

            Assert.True(onlyOne.CollapsedRepeatedFolderLayer);
            Assert.Equal(@"C:\111\222", onlyOne.DestinationDirectory);
        }

        [Fact]
        public void 规则三_选中文件_指定位置_名字是去掉后缀的包名()
        {
            OutputPlacementResult result = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222.rar",
                OutputPlacementMode.CustomRootPerArchive,
                @"D:\BBB",
                driveExists: DriveExists);

            Assert.True(result.Success);
            Assert.Equal(@"D:\BBB", result.DestinationRoot);
            Assert.Equal(@"D:\BBB\222", result.DestinationDirectory);
            Assert.Equal("222", result.PackageFolderName);
            Assert.False(result.UsesSelectedFolderName);
            Assert.False(result.SharesDestinationWithOtherPackages);
        }

        [Fact]
        public void 规则四_选中文件夹_指定位置_名字是选中文件夹的名字()
        {
            // 用户在「添加文件夹」里选了 C:\111\222，指定位置 D:\BBB →
            // 先在 BBB 里建一个和选中文件夹同名的子文件夹，再在里面操作。
            OutputPlacementResult first = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222\a.rar",
                OutputPlacementMode.CustomRootPerArchive,
                @"D:\BBB",
                driveExists: DriveExists,
                selectionKind: SourceSelectionKind.Folder,
                selectionRoot: @"C:\111\222");

            OutputPlacementResult second = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222\b.7z.001",
                OutputPlacementMode.CustomRootPerArchive,
                @"D:\BBB",
                driveExists: DriveExists,
                selectionKind: SourceSelectionKind.Folder,
                selectionRoot: @"C:\111\222");

            Assert.Equal(@"D:\BBB\222", first.DestinationDirectory);
            Assert.Equal("222", first.PackageFolderName);
            Assert.True(first.UsesSelectedFolderName);

            // 同一个文件夹里的包都落进同一层（共用根）：管线靠这条事实让开"目录已存在就改名"。
            Assert.True(first.SharesDestinationWithOtherPackages);
            Assert.Equal(first.DestinationDirectory, second.DestinationDirectory);

            // 选中文件夹但根没记下（缺信息）→ 回落包基名，绝不摊在 BBB 根上。
            OutputPlacementResult noRoot = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222\a.rar",
                OutputPlacementMode.CustomRootPerArchive,
                @"D:\BBB",
                driveExists: DriveExists,
                selectionKind: SourceSelectionKind.Folder,
                selectionRoot: null);

            Assert.Equal(@"D:\BBB\a", noRoot.DestinationDirectory);
            Assert.False(noRoot.UsesSelectedFolderName);
            Assert.False(noRoot.SharesDestinationWithOtherPackages);
        }

        [Fact]
        public void 规则命名_分卷组与内嵌归档都要取对名字()
        {
            // 分卷组：222.7z.001 → 222（不是 222.7z），组里每一卷算出同一条落点。
            Assert.Equal(
                @"C:\111\222",
                OutputPlacement.ResolveDestinationDirectory(
                    @"C:\111\222.7z.001", OutputPlacementMode.PerArchiveSubfolder).DestinationDirectory);

            Assert.Equal(
                @"D:\BBB\222",
                OutputPlacement.ResolveDestinationDirectory(
                    @"C:\111\222.7z.001",
                    OutputPlacementMode.CustomRootPerArchive,
                    @"D:\BBB",
                    driveExists: DriveExists).DestinationDirectory);

            // 内嵌归档（双面文件，伪装成视频后缀）：222.mp4 → 222。
            Assert.Equal(
                @"C:\111\222",
                OutputPlacement.ResolveDestinationDirectory(
                    @"C:\111\222.mp4", OutputPlacementMode.PerArchiveSubfolder).DestinationDirectory);

            Assert.Equal(
                @"D:\BBB\222",
                OutputPlacement.ResolveDestinationDirectory(
                    @"C:\111\222.mp4",
                    OutputPlacementMode.CustomRootPerArchive,
                    @"D:\BBB",
                    driveExists: DriveExists).DestinationDirectory);

            // 选中文件夹时那一层是文件夹名，跟文件夹里是分卷还是双面文件无关。
            Assert.Equal(
                @"D:\BBB\222",
                OutputPlacement.ResolveDestinationDirectory(
                    @"C:\111\222\222.7z.001",
                    OutputPlacementMode.CustomRootPerArchive,
                    @"D:\BBB",
                    driveExists: DriveExists,
                    selectionKind: SourceSelectionKind.Folder,
                    selectionRoot: @"C:\111\222").DestinationDirectory);
        }

        // "解压到当前目录（摊平）"与"直接解到指定目录（不建子文件夹）"两档已被用户 2026-09-24 第 13 条删除：
        // 旧值（枚举编号 1 / 3、旧名字）读进来必须**迁移到剩下的两档**，而且不抛异常。
        [Theory]
        [InlineData(1, OutputPlacementMode.PerArchiveSubfolder)]
        [InlineData(3, OutputPlacementMode.CustomRootPerArchive)]
        [InlineData(0, OutputPlacementMode.PerArchiveSubfolder)]
        [InlineData(2, OutputPlacementMode.CustomRootPerArchive)]
        [InlineData(99, OutputPlacementMode.PerArchiveSubfolder)]
        public void 旧枚举值迁到新档(int legacyValue, OutputPlacementMode expected)
        {
            Assert.Equal(expected, OutputPlacement.NormalizeLegacyMode((OutputPlacementMode)legacyValue));
        }

        [Theory]
        [InlineData("SourceDirectoryFlat", OutputPlacementMode.PerArchiveSubfolder)]
        [InlineData("CustomRootFlat", OutputPlacementMode.CustomRootPerArchive)]
        [InlineData("PerArchiveSubfolder", OutputPlacementMode.PerArchiveSubfolder)]
        [InlineData("CustomRootPerArchive", OutputPlacementMode.CustomRootPerArchive)]
        [InlineData("1", OutputPlacementMode.PerArchiveSubfolder)]
        [InlineData("3", OutputPlacementMode.CustomRootPerArchive)]
        [InlineData("", OutputPlacementMode.PerArchiveSubfolder)]
        [InlineData("   ", OutputPlacementMode.PerArchiveSubfolder)]
        [InlineData("胡说八道", OutputPlacementMode.PerArchiveSubfolder)]
        [InlineData(null, OutputPlacementMode.PerArchiveSubfolder)]
        public void 旧枚举名迁到新档(string? legacyName, OutputPlacementMode expected)
        {
            Assert.Equal(expected, OutputPlacement.ParsePlacementMode(legacyName));
        }

        [Fact]
        public void 旧枚举值传进落点解析_也按新档算_不抛异常()
        {
            // 摊平那两档的编号即使漏到落点解析里，也必须落到"建同名子文件夹"上，
            // 绝不落成 111\ 或 D:\out 根上（那正是用户删掉它们的理由）。
            OutputPlacementResult legacyFlat = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222.rar", (OutputPlacementMode)1);

            Assert.Equal(@"C:\111\222", legacyFlat.DestinationDirectory);

            OutputPlacementResult legacyCustomFlat = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222.rar", (OutputPlacementMode)3, @"D:\out", driveExists: DriveExists);

            Assert.Equal(@"D:\out\222", legacyCustomFlat.DestinationDirectory);
        }

        // ── 包基名：分卷组取整个组，不是 222.7z ────────────────────────────────

        [Theory]
        [InlineData(@"C:\111\222.rar", "222")]
        [InlineData(@"C:\111\222.7z", "222")]
        [InlineData(@"C:\111\222.7z.001", "222")]
        [InlineData(@"C:\111\222.7z.002", "222")]
        [InlineData(@"C:\111\222.zip.001", "222")]
        [InlineData(@"C:\111\222.part1.rar", "222")]
        [InlineData(@"C:\111\222.part01.rar", "222")]
        [InlineData(@"C:\111\222.001", "222")]
        [InlineData(@"C:\111\222.001.rar", "222")]
        [InlineData(@"C:\111\222.zip", "222")]
        [InlineData(@"C:\111\222.z01", "222")]
        [InlineData(@"C:\111\222.r00", "222")]
        [InlineData(@"C:\111\222.r01", "222")]
        [InlineData(@"C:\111\222.tar.gz", "222")]
        [InlineData(@"C:\111\222.tar.bz2", "222")]
        [InlineData(@"C:\111\222.tar.xz", "222")]
        [InlineData(@"C:\111\222.tgz", "222")]
        [InlineData(@"C:\111\222.tbz2", "222")]
        [InlineData(@"C:\111\222.txz", "222")]
        [InlineData(@"C:\111\222.rar.jpg", "222")]
        [InlineData(@"C:\111\222.mkv", "222")]
        [InlineData(@"C:\111\movie.2024.rar", "movie.2024")]
        [InlineData(@"C:\111\222...rar", "222")]
        public void ResolveArchiveBaseName_StripsVolumeMarkersAndArchiveExtensions(string path, string expected)
        {
            Assert.Equal(expected, OutputPlacement.ResolveArchiveBaseName(path));
        }

        // 分卷组基名要取到"分卷标记之前"，不是只去最后一个后缀（FileNameHelper.GetArchiveBaseName 的老坑：
        // 222.7z.001 会留成 222.7z，真实产物里出现过 17274362.7z\ 这种目录）。
        [Theory]
        [InlineData(@"C:\111\222.7z.001", "222")]
        [InlineData(@"C:\111\222.zip.001", "222")]
        [InlineData(@"C:\111\222.part1.rar", "222")]
        [InlineData(@"C:\111\222.part01.rar", "222")]
        [InlineData(@"C:\111\222.r00", "222")]
        [InlineData(@"C:\111\222.r01", "222")]
        [InlineData(@"C:\111\222.z01", "222")]
        [InlineData(@"C:\111\222.001", "222")]
        public void ResolveArchiveBaseName_VolumeGroups_NeverKeepInnerFormatSegment(string path, string expected)
        {
            string baseName = OutputPlacement.ResolveArchiveBaseName(path);

            Assert.Equal(expected, baseName);
            Assert.DoesNotContain(".7z", baseName);
            Assert.DoesNotContain(".zip", baseName);
            Assert.DoesNotContain(".rar", baseName);
        }

        [Fact]
        public void ResolveDestinationDirectory_VolumeGroup_DoesNotCreatePerVolumeFolders()
        {
            // 分卷组里每一卷都必须算出同一个落点，否则 222.7z.001/.002 会各建一个目录。
            string first = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222.7z.001", OutputPlacementMode.PerArchiveSubfolder).DestinationDirectory;

            string second = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222.7z.002", OutputPlacementMode.PerArchiveSubfolder).DestinationDirectory;

            Assert.Equal(@"C:\111\222", first);
            Assert.Equal(first, second);
        }

        // 上游已经算过分卷组基名（VolumeGroupDetector.BaseName 形如 222.7z）就别再算一遍。
        [Theory]
        [InlineData(@"C:\111\222.7z.001", "222.7z", "222")]
        [InlineData(@"C:\111\222.part1.rar", "222", "222")]
        [InlineData(@"C:\111\222.zip", "222", "222")]
        public void ResolveArchiveBaseName_UsesVolumeGroupBaseNameWhenGiven(
            string path,
            string volumeGroupBaseName,
            string expected)
        {
            Assert.Equal(expected, OutputPlacement.ResolveArchiveBaseName(path, volumeGroupBaseName));
        }

        [Fact]
        public void ResolveArchiveBaseName_GroupNameWinsOverRawFileName()
        {
            // 传进来的分卷组基名与文件名不一致时，以组基名为准（它才是"整组"的答案）。
            Assert.Equal("组名", OutputPlacement.ResolveArchiveBaseName(@"C:\111\000.7z.001", "组名.7z"));
        }

        [Theory]
        [InlineData(@"C:\111\CON.rar", "_CON")]
        [InlineData(@"C:\111\PRN.7z.001", "_PRN")]
        [InlineData(@"C:\111\222 .rar", "222")]
        public void ResolveArchiveBaseName_SanitizesWindowsUnsafeNames(string path, string expected)
        {
            Assert.Equal(expected, OutputPlacement.ResolveArchiveBaseName(path));
        }

        [Fact]
        public void ResolveDestinationDirectory_UsesCleanBaseNameForFolder()
        {
            OutputPlacementResult result = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\CON.rar", OutputPlacementMode.PerArchiveSubfolder);

            Assert.True(result.Success);
            Assert.Equal(@"_CON", result.ArchiveBaseName);
            Assert.Equal(@"C:\111\_CON", result.DestinationDirectory);
        }

        // ── 非法自定义根：返回明确原因，不抛异常、不静默落别处 ─────────────────

        [Theory]
        [InlineData(null, OutputPlacementError.EmptyCustomRoot)]
        [InlineData("", OutputPlacementError.EmptyCustomRoot)]
        [InlineData("   ", OutputPlacementError.EmptyCustomRoot)]
        [InlineData(@"out\sub", OutputPlacementError.CustomRootNotAbsolute)]
        [InlineData(@"out", OutputPlacementError.CustomRootNotAbsolute)]
        [InlineData(@"D:\", OutputPlacementError.CustomRootIsRootDirectory)]
        [InlineData(@"D:", OutputPlacementError.CustomRootIsRootDirectory)]
        public void ResolveDestinationDirectory_InvalidCustomRoot_ReportsReason(
            string? customRoot,
            OutputPlacementError expected)
        {
            OutputPlacementResult result = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222.rar", OutputPlacementMode.CustomRootPerArchive, customRoot, driveExists: DriveExists);

            Assert.False(result.Success);
            Assert.Equal(expected, result.Error);
            Assert.Equal(string.Empty, result.DestinationDirectory);
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
        }

        [Fact]
        public void ResolveDestinationDirectory_MissingDrive_ReportsMissingDrive()
        {
            OutputPlacementResult result = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222.rar", OutputPlacementMode.CustomRootPerArchive, @"Z:\out", driveExists: DriveMissing);

            Assert.False(result.Success);
            Assert.Equal(OutputPlacementError.CustomRootDriveMissing, result.Error);
            Assert.Equal(string.Empty, result.DestinationDirectory);
        }

        [Fact]
        public void ResolveDestinationDirectory_CustomRoot_DefaultProbeAcceptsExistingDrive()
        {
            // 唯一一条走默认探针的测试：确认"没注入探针"时也真的去查了盘符，而不是一律当失败。
            string driveRoot = Path.GetPathRoot(AppContext.BaseDirectory) ?? @"C:\";
            string customRoot = Path.Combine(driveRoot, "ArchiveFixerOutputPlacementProbe");

            OutputPlacementResult result = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222.rar", OutputPlacementMode.CustomRootPerArchive, customRoot);

            Assert.True(result.Success);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ResolveDestinationDirectory_EmptySourcePath_ReportsReason(string? sourcePath)
        {
            OutputPlacementResult result = OutputPlacement.ResolveDestinationDirectory(
                sourcePath, OutputPlacementMode.PerArchiveSubfolder);

            Assert.False(result.Success);
            Assert.Equal(OutputPlacementError.EmptySourcePath, result.Error);
        }

        [Fact]
        public void ResolveDestinationDirectory_SourcePathWithoutDirectory_ReportsReason()
        {
            OutputPlacementResult result = OutputPlacement.ResolveDestinationDirectory(
                "222.rar", OutputPlacementMode.PerArchiveSubfolder);

            Assert.False(result.Success);
            Assert.Equal(OutputPlacementError.SourceDirectoryUnavailable, result.Error);
        }

        [Fact]
        public void ResolveDestinationRoot_FillsRootOnly()
        {
            OutputPlacementResult result = OutputPlacement.ResolveDestinationRoot(
                @"C:\111\222.rar", OutputPlacementMode.PerArchiveSubfolder);

            Assert.True(result.Success);
            Assert.Equal(@"C:\111", result.DestinationRoot);
            Assert.Equal(string.Empty, result.DestinationDirectory);
        }

        // ── §3.3 场景 B：包名与所在目录同名 → 塌缩重复的一层 ────────────────────

        [Fact]
        public void ResolveDestinationDirectory_ScenarioB_CollapseOn_LandsInArchiveOwnFolder()
        {
            // 111\222\名字\名字.rar → 产物落 111\222\名字\内容物
            OutputPlacementResult result = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222\名字\名字.rar",
                OutputPlacementMode.PerArchiveSubfolder,
                collapseRepeatedFolderLayer: true,
                sourceDirectoryContainsOnlyThisArchive: true);

            Assert.True(result.Success);
            Assert.True(result.CollapsedRepeatedFolderLayer);
            Assert.Equal(@"C:\111\222\名字", result.DestinationDirectory);
        }

        [Fact]
        public void ResolveDestinationDirectory_ScenarioB_CollapseOff_KeepsRepeatedLayer()
        {
            OutputPlacementResult result = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222\名字\名字.rar",
                OutputPlacementMode.PerArchiveSubfolder,
                collapseRepeatedFolderLayer: false,
                sourceDirectoryContainsOnlyThisArchive: true);

            Assert.True(result.Success);
            Assert.False(result.CollapsedRepeatedFolderLayer);
            Assert.Equal(@"C:\111\222\名字\名字", result.DestinationDirectory);
        }

        [Fact]
        public void ResolveDestinationDirectory_ScenarioB_NotOnlyArchiveInFolder_DoesNotCollapse()
        {
            // "只有这一个包"由调用方告知：目录里还有别的包时绝不能塌缩，否则两个包的产物会并在一起。
            OutputPlacementResult result = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222\名字\名字.rar",
                OutputPlacementMode.PerArchiveSubfolder,
                collapseRepeatedFolderLayer: true,
                sourceDirectoryContainsOnlyThisArchive: false);

            Assert.True(result.Success);
            Assert.False(result.CollapsedRepeatedFolderLayer);
            Assert.Equal(@"C:\111\222\名字\名字", result.DestinationDirectory);
        }

        [Fact]
        public void ResolveDestinationDirectory_ScenarioA_NamesDiffer_DoesNotCollapse()
        {
            // 场景 A：111\222\ 下几十上百个包，包名(333) 与目录名(222) 不同 → 各建各的文件夹。
            OutputPlacementResult result = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222\333.rar",
                OutputPlacementMode.PerArchiveSubfolder,
                collapseRepeatedFolderLayer: true,
                sourceDirectoryContainsOnlyThisArchive: true);

            Assert.True(result.Success);
            Assert.False(result.CollapsedRepeatedFolderLayer);
            Assert.Equal(@"C:\111\222\333", result.DestinationDirectory);
        }

        [Fact]
        public void ResolveDestinationDirectory_ScenarioB_CollapseIsCaseInsensitive()
        {
            OutputPlacementResult result = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222\Name\name.rar",
                OutputPlacementMode.PerArchiveSubfolder,
                collapseRepeatedFolderLayer: true,
                sourceDirectoryContainsOnlyThisArchive: true);

            Assert.True(result.CollapsedRepeatedFolderLayer);
            Assert.Equal(@"C:\111\222\Name", result.DestinationDirectory);
        }

        [Fact]
        public void ResolveDestinationDirectory_ScenarioB_CollapseOnlyAppliesToPerArchiveSubfolder()
        {
            // 自定义根模式下"所在目录名"根本不参与运算，没有重复层可塌缩。
            OutputPlacementResult result = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222\名字\名字.rar",
                OutputPlacementMode.CustomRootPerArchive,
                @"D:\out",
                collapseRepeatedFolderLayer: true,
                sourceDirectoryContainsOnlyThisArchive: true,
                driveExists: DriveExists);

            Assert.True(result.Success);
            Assert.False(result.CollapsedRepeatedFolderLayer);
            Assert.Equal(@"D:\out\名字", result.DestinationDirectory);
        }

        [Fact]
        public void ResolveDestinationDirectory_ScenarioB_CollapseWithVolumeGroup_UsesGroupBaseName()
        {
            // 分卷组：名字.7z.001 的基名是"名字"（不是"名字.7z"），所以同样能命中同名目录。
            OutputPlacementResult result = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222\名字\名字.7z.001",
                OutputPlacementMode.PerArchiveSubfolder,
                collapseRepeatedFolderLayer: true,
                sourceDirectoryContainsOnlyThisArchive: true);

            Assert.True(result.Success);
            Assert.Equal("名字", result.ArchiveBaseName);
            Assert.True(result.CollapsedRepeatedFolderLayer);
            Assert.Equal(@"C:\111\222\名字", result.DestinationDirectory);
        }

        // ── 旧布尔 ↔ 新枚举（旧配置迁移，规格 §1.3） ───────────────────────────

        [Theory]
        [InlineData(OutputPlacementMode.PerArchiveSubfolder, true, true)]
        [InlineData(OutputPlacementMode.CustomRootPerArchive, false, true)]
        public void ToLegacyFlags_MapsRetainedModes(
            OutputPlacementMode mode,
            bool expectedExtractToOriginalDirectory,
            bool expectedKeepArchiveNameFolder)
        {
            (bool extractToOriginalDirectory, bool keepArchiveNameFolder) = OutputPlacement.ToLegacyFlags(mode);

            Assert.Equal(expectedExtractToOriginalDirectory, extractToOriginalDirectory);
            Assert.Equal(expectedKeepArchiveNameFolder, keepArchiveNameFolder);
        }

        [Theory]
        [InlineData(true, true, @"D:\out", OutputPlacementMode.PerArchiveSubfolder)]
        [InlineData(false, true, @"D:\out", OutputPlacementMode.CustomRootPerArchive)]

        // 被删掉的两档（旧配置里就是这两种组合）→ 迁移到剩下的两档，读旧 appsettings.json 不炸。
        [InlineData(true, false, @"D:\out", OutputPlacementMode.PerArchiveSubfolder)]
        [InlineData(false, false, @"D:\out", OutputPlacementMode.CustomRootPerArchive)]

        // 空自定义根 → 源目录家族（绝不把旧代码"落到程序目录"的兜底当成一种落点）。
        [InlineData(false, true, null, OutputPlacementMode.PerArchiveSubfolder)]
        [InlineData(false, true, "", OutputPlacementMode.PerArchiveSubfolder)]
        [InlineData(false, true, "   ", OutputPlacementMode.PerArchiveSubfolder)]
        [InlineData(false, false, null, OutputPlacementMode.PerArchiveSubfolder)]
        [InlineData(false, false, "", OutputPlacementMode.PerArchiveSubfolder)]
        public void FromLegacyFlags_MapsOldConfiguration(
            bool extractToOriginalDirectory,
            bool keepArchiveNameFolder,
            string? customRoot,
            OutputPlacementMode expected)
        {
            Assert.Equal(
                expected,
                OutputPlacement.FromLegacyFlags(extractToOriginalDirectory, keepArchiveNameFolder, customRoot));
        }

        [Theory]
        [InlineData(OutputPlacementMode.PerArchiveSubfolder)]
        [InlineData(OutputPlacementMode.CustomRootPerArchive)]
        public void LegacyFlags_RoundTripIsExactWhenCustomRootIsKept(OutputPlacementMode mode)
        {
            (bool extractToOriginalDirectory, bool keepArchiveNameFolder) = OutputPlacement.ToLegacyFlags(mode);

            OutputPlacementMode back = OutputPlacement.FromLegacyFlags(
                extractToOriginalDirectory, keepArchiveNameFolder, @"D:\out");

            Assert.Equal(mode, back);
        }
    }
}
