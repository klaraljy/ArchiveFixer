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
             * 所选文件夹里只有与它同名的那个包（111\222\222.7z.001）时：**不塌缩**（2026-09-27 用户指示）——
             * 产物落在 C:\111\222\222\，"111\222\ 这层里还有很多别的东西"时绝不会被搅在一起。
             * ⚠ 未指定位置这一档下包所在目录是"源目录"，与「选中文件夹」无关。
             */
            OutputPlacementResult onlyOne = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222\222.7z.001",
                OutputPlacementMode.PerArchiveSubfolder,
                selectionKind: SourceSelectionKind.Folder,
                selectionRoot: @"C:\111\222");

            Assert.Equal(@"C:\111\222\222", onlyOne.DestinationDirectory);
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
            // 在 BBB 里建一个与选中文件夹同名的子文件夹，**每个包再占自己那一层**
            //（2026-09-27 用户指示方案 A：这一档不再"所有包共用一层"）。
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

            Assert.Equal(@"D:\BBB\222\a", first.DestinationDirectory);
            Assert.Equal(@"D:\BBB\222\b", second.DestinationDirectory);
            Assert.Equal("a", first.PackageFolderName);
            Assert.True(first.UsesSelectedFolderName);
            Assert.Equal(string.Empty, first.RelativeSubPath);

            /*
             * ⛔ 2026-09-27 起**没有"共用根"这一档了**：每个包各自一层目录，
             * 于是管线里"让开目录已存在"和"其余物按包名分层"两条特例都不再触发
             * （真机现场：13 个包全倒进同一层 → 子文件夹的名字与归属全丢、同名文件撞成 (1)…(6)）。
             */
            Assert.False(first.SharesDestinationWithOtherPackages);
            Assert.False(second.SharesDestinationWithOtherPackages);
            Assert.NotEqual(first.DestinationDirectory, second.DestinationDirectory);

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

        /// <summary>
        /// **保住源目录的分层**（2026-09-27 用户指示方案 A，真机现场）：
        /// 选中文件夹里的子文件夹名字必须原样出现在产物里，包在子文件夹里就落进那一层。
        ///
        /// <para>用户原话："我选中了这个 AAA……里面有五个子文件夹，而且每个都有确切的名字，
        /// 你解压的情况就是将这五个都删掉，而后再将这里面的东西全部拿出来了，
        /// 这不就导致了文件夹混乱了吗，你应该在各自的子文件夹里面操作"。</para>
        /// </summary>
        [Fact]
        public void 规则四追加_选中文件夹里的子文件夹分层要原样保住()
        {
            // 直接躺在选中文件夹里 → 只有包名那一层。
            Assert.Equal(
                @"D:\BBB\AAA\a",
                OutputPlacement.ResolveDestinationDirectory(
                    @"C:\AAA\a.rar",
                    OutputPlacementMode.CustomRootPerArchive,
                    @"D:\BBB",
                    driveExists: DriveExists,
                    selectionKind: SourceSelectionKind.Folder,
                    selectionRoot: @"C:\AAA").DestinationDirectory);

            // 子文件夹里的包 → 子文件夹那一层原样保留。
            OutputPlacementResult nested = OutputPlacement.ResolveDestinationDirectory(
                @"C:\AAA\新建文件夹_20260916_152637\b.mp4",
                OutputPlacementMode.CustomRootPerArchive,
                @"D:\BBB",
                driveExists: DriveExists,
                selectionKind: SourceSelectionKind.Folder,
                selectionRoot: @"C:\AAA");

            Assert.Equal(@"D:\BBB\AAA\新建文件夹_20260916_152637\b", nested.DestinationDirectory);
            Assert.Equal(@"新建文件夹_20260916_152637", nested.RelativeSubPath);

            // 再深一层（真机里的 解压软件\）也照样保留 —— 用户选的是"整段相对路径原样保留"。
            OutputPlacementResult deeper = OutputPlacement.ResolveDestinationDirectory(
                @"C:\AAA\新建文件夹_20260916_152637\解压软件\rar-android-722.132.apk",
                OutputPlacementMode.CustomRootPerArchive,
                @"D:\BBB",
                driveExists: DriveExists,
                selectionKind: SourceSelectionKind.Folder,
                selectionRoot: @"C:\AAA");

            Assert.Equal(
                @"D:\BBB\AAA\新建文件夹_20260916_152637\解压软件\rar-android-722.132.apk",
                deeper.DestinationDirectory);
            Assert.Equal(@"新建文件夹_20260916_152637\解压软件", deeper.RelativeSubPath);
        }

        /// <summary>包**不在**选中文件夹之下（理论上不该发生）时不许凭空拼出子路径，也不许跑出该层之外。</summary>
        [Fact]
        public void 规则四追加_包不在选中文件夹下时退回只有包名那一层()
        {
            OutputPlacementResult result = OutputPlacement.ResolveDestinationDirectory(
                @"C:\Other\x.rar",
                OutputPlacementMode.CustomRootPerArchive,
                @"D:\BBB",
                driveExists: DriveExists,
                selectionKind: SourceSelectionKind.Folder,
                selectionRoot: @"C:\AAA");

            Assert.Equal(@"D:\BBB\AAA\x", result.DestinationDirectory);
            Assert.Equal(string.Empty, result.RelativeSubPath);
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

            // 选中文件夹时：选中文件夹名那一层照旧，**再加上包自己那一层**（2026-09-27 方案 A）。
            Assert.Equal(
                @"D:\BBB\222\222",
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

        // ── 场景 B 塌缩**已退役**（2026-09-27 用户指示：一律不塌缩） ──────────────

        /// <summary>
        /// <c>111\222\名字\名字.rar</c>（包名与所在目录同名）**不再塌缩**：
        /// 产物是 <c>111\222\名字\名字\内容物</c>。
        ///
        /// <para>用户 2026-09-27 原话："不行，如果 <c>111\222\</c> 这层文件夹里面还有很多的东西呢，
        /// 这不就是将文件夹弄混乱了吗" —— 旧规则只在"那层里只有这一个**包**"时才塌缩，
        /// 可那层里完全可能还有几十个小文件、说明、别的素材，一塌就全混在一起，
        /// 而且"什么时候会塌"对用户不可预期。现在**一律不塌缩**。</para>
        /// </summary>
        [Fact]
        public void 同名同目录_一律不塌缩_产物多一层包名目录()
        {
            OutputPlacementResult result = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222\名字\名字.rar",
                OutputPlacementMode.PerArchiveSubfolder);

            Assert.True(result.Success);
            Assert.Equal(@"C:\111\222\名字\名字", result.DestinationDirectory);
        }

        /// <summary>包名与目录名不同（<c>111\222\333.rar</c>）：本来就各建各的，不受影响。</summary>
        [Fact]
        public void 包名与目录名不同_各建各的文件夹()
        {
            OutputPlacementResult result = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222\333.rar",
                OutputPlacementMode.PerArchiveSubfolder);

            Assert.Equal(@"C:\111\222\333", result.DestinationDirectory);
        }

        /// <summary>分卷组：基名照旧取整组基名（<c>名字.7z.001</c> → <c>名字</c>），同样不塌缩。</summary>
        [Fact]
        public void 分卷组_基名照旧_也不塌缩()
        {
            OutputPlacementResult result = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222\名字\名字.7z.001",
                OutputPlacementMode.PerArchiveSubfolder);

            Assert.Equal("名字", result.ArchiveBaseName);
            Assert.Equal(@"C:\111\222\名字\名字", result.DestinationDirectory);
        }

        /// <summary>指定位置那一档：包名与所在目录同名与否都无关，照旧 <c>指定位置\包名</c>。</summary>
        [Fact]
        public void 指定位置_同不同名都一样()
        {
            OutputPlacementResult result = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222\名字\名字.rar",
                OutputPlacementMode.CustomRootPerArchive,
                @"D:\out",
                driveExists: DriveExists);

            Assert.Equal(@"D:\out\名字", result.DestinationDirectory);
        }

        /// <summary>
        /// 手动档「**解压到当前文件夹**」（2026-09-27 新增，语义 = WinRAR 的那个）：
        /// 内容物直接落在**压缩包所在的那一层**，⛔ 不套包名目录；盘根要拦住。
        /// </summary>
        [Fact]
        public void 手动档_解压到当前文件夹_不套包名目录()
        {
            OutputPlacementResult result = OutputPlacement.ResolveDestinationDirectory(
                @"C:\111\222.rar",
                OutputPlacementMode.PerArchiveSubfolder,
                flattenIntoSourceFolder: true);

            Assert.True(result.Success);
            Assert.Equal(@"C:\111", result.DestinationDirectory);
            Assert.Equal("222", result.ArchiveBaseName);

            // ⛔ 不许直接摊在盘根上（那比多一层糟得多）。
            OutputPlacementResult atRoot = OutputPlacement.ResolveDestinationDirectory(
                @"C:\222.rar",
                OutputPlacementMode.PerArchiveSubfolder,
                flattenIntoSourceFolder: true);

            Assert.False(atRoot.Success);
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
