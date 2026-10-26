using System;
using System.IO;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「半套分卷」扫描边界的唯一出口：未指定位置只看选中文件夹/文件所在目录；
    /// 指定位置只看指定根。⛔ 旧地址 CustomOutputDirectory 在未指定位置时不许参与。
    /// </summary>
    public sealed class SplitGateScanBoundaryTests : IDisposable
    {
        private readonly string _root;

        public SplitGateScanBoundaryTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "SplitGateBoundary_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch
            {
            }
        }

        [Fact]
        public void 未指定位置_选中文件夹_边界就是选中的文件夹_不读旧的自定义根()
        {
            string selected = Path.Combine(_root, "666");
            Directory.CreateDirectory(selected);

            var task = new ArchiveTask(Path.Combine(selected, "583900112233.7z"))
            {
                SourceSelectionKind = SourceSelectionKind.Folder,
                SourceSelectionRoot = selected
            };

            var settings = new AppSettings
            {
                ExtractToOriginalDirectory = true,
                CustomOutputDirectory = Path.Combine(_root, "旧测试位置")
            };

            Assert.Equal(
                Path.GetFullPath(selected).TrimEnd(Path.DirectorySeparatorChar),
                ExtractionCoordinator.ResolveSplitGateScanBoundary(task, settings));
        }

        [Fact]
        public void 未指定位置_选中单个文件_边界就是文件所在目录()
        {
            string selected = Path.Combine(_root, "单个");
            Directory.CreateDirectory(selected);

            var task = new ArchiveTask(Path.Combine(selected, "pack.7z"))
            {
                SourceSelectionKind = SourceSelectionKind.File,
                SourceSelectionRoot = Path.Combine(selected, "pack.7z")
            };

            var settings = new AppSettings
            {
                ExtractToOriginalDirectory = true,
                CustomOutputDirectory = Path.Combine(_root, "旧测试位置")
            };

            Assert.Equal(
                Path.GetFullPath(selected).TrimEnd(Path.DirectorySeparatorChar),
                ExtractionCoordinator.ResolveSplitGateScanBoundary(task, settings));
        }

        [Fact]
        public void 指定位置_边界就是用户指定的根()
        {
            string outputRoot = Path.Combine(_root, "指定位置");
            Directory.CreateDirectory(outputRoot);

            var task = new ArchiveTask(Path.Combine(_root, "pack.7z"));
            var settings = new AppSettings
            {
                ExtractToOriginalDirectory = false,
                CustomOutputDirectory = outputRoot
            };

            Assert.Equal(
                Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar),
                ExtractionCoordinator.ResolveSplitGateScanBoundary(task, settings));
        }
    }
}
