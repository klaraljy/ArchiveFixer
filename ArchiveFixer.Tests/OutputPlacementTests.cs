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

        // ── §1.1 四种模式各自的落点 ─────────────────────────────────────────────

        [Fact]
        public void ResolveDestinationDirectory_PerArchiveSubfolder_PutsContentInSameNameFolder()
        {
            OutputPlacementResult result = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222.rar", OutputPlacementMode.PerArchiveSubfolder);

            Assert.True(result.Success);
            Assert.Equal(OutputPlacementError.None, result.Error);
            Assert.Equal(@"C:\111", result.DestinationRoot);
            Assert.Equal(@"C:\111\222", result.DestinationDirectory);
            Assert.Equal("222", result.ArchiveBaseName);
            Assert.False(result.CollapsedRepeatedFolderLayer);
        }

        [Fact]
        public void ResolveDestinationDirectory_SourceDirectoryFlat_PutsContentInSourceDirectory()
        {
            OutputPlacementResult result = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222.rar", OutputPlacementMode.SourceDirectoryFlat);

            Assert.True(result.Success);
            Assert.Equal(@"C:\111", result.DestinationRoot);
            Assert.Equal(@"C:\111", result.DestinationDirectory);
            Assert.Equal("222", result.ArchiveBaseName);
        }

        [Fact]
        public void ResolveDestinationDirectory_CustomRootPerArchive_AddsArchiveFolder()
        {
            OutputPlacementResult result = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222.rar", OutputPlacementMode.CustomRootPerArchive, @"D:\out", driveExists: DriveExists);

            Assert.True(result.Success);
            Assert.Equal(@"D:\out", result.DestinationRoot);
            Assert.Equal(@"D:\out\222", result.DestinationDirectory);
        }

        [Fact]
        public void ResolveDestinationDirectory_CustomRootFlat_DoesNotAddArchiveFolder()
        {
            OutputPlacementResult result = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222.rar", OutputPlacementMode.CustomRootFlat, @"D:\out", driveExists: DriveExists);

            Assert.True(result.Success);
            Assert.Equal(@"D:\out", result.DestinationRoot);
            Assert.Equal(@"D:\out", result.DestinationDirectory);
        }

        // "单独建文件夹"和"直接解到此目录"必须是两个独立可选项，差别就在这一层。
        [Fact]
        public void ResolveDestinationDirectory_CustomRoot_TwoModesDifferByExactlyOneLayer()
        {
            OutputPlacementResult perArchive = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222.rar", OutputPlacementMode.CustomRootPerArchive, @"D:\out", driveExists: DriveExists);

            OutputPlacementResult flat = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222.rar", OutputPlacementMode.CustomRootFlat, @"D:\out", driveExists: DriveExists);

            Assert.NotEqual(perArchive.DestinationDirectory, flat.DestinationDirectory);
            Assert.Equal(flat.DestinationDirectory + @"\222", perArchive.DestinationDirectory);
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
                @"C:\111\222.rar", OutputPlacementMode.CustomRootFlat, @"Z:\out", driveExists: DriveMissing);

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
                @"C:\111\222.rar", OutputPlacementMode.CustomRootFlat, customRoot);

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
        [InlineData(OutputPlacementMode.SourceDirectoryFlat, true, false)]
        [InlineData(OutputPlacementMode.CustomRootPerArchive, false, true)]
        [InlineData(OutputPlacementMode.CustomRootFlat, false, false)]
        public void ToLegacyFlags_MapsFourModes(
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
        [InlineData(true, false, @"D:\out", OutputPlacementMode.SourceDirectoryFlat)]
        [InlineData(false, true, @"D:\out", OutputPlacementMode.CustomRootPerArchive)]
        [InlineData(false, false, @"D:\out", OutputPlacementMode.CustomRootFlat)]

        // 空自定义根 → 源目录家族（绝不把旧代码"落到程序目录"的兜底当成一种落点）。
        [InlineData(false, true, null, OutputPlacementMode.PerArchiveSubfolder)]
        [InlineData(false, true, "", OutputPlacementMode.PerArchiveSubfolder)]
        [InlineData(false, true, "   ", OutputPlacementMode.PerArchiveSubfolder)]
        [InlineData(false, false, null, OutputPlacementMode.SourceDirectoryFlat)]
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
        [InlineData(OutputPlacementMode.SourceDirectoryFlat)]
        [InlineData(OutputPlacementMode.CustomRootPerArchive)]
        [InlineData(OutputPlacementMode.CustomRootFlat)]
        public void LegacyFlags_RoundTripIsExactWhenCustomRootIsKept(OutputPlacementMode mode)
        {
            (bool extractToOriginalDirectory, bool keepArchiveNameFolder) = OutputPlacement.ToLegacyFlags(mode);

            OutputPlacementMode back = OutputPlacement.FromLegacyFlags(
                extractToOriginalDirectory, keepArchiveNameFolder, @"D:\out");

            Assert.Equal(mode, back);
        }
    }
}
