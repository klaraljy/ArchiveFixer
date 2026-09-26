using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// "本身就是 ZIP 容器"的合法后缀不该被判成伪装后缀。
    ///
    /// 背景（真实踩到）：`rar-android-722.132.apk` 内容是 ZIP，被判「后缀不匹配」、建议后缀 `.zip`。
    /// 如果用户点「智能修正后缀」，`.apk` 会被改成 `.zip` —— 安装包变成一个打不开的压缩包。
    /// 同一类还有 .jar / .docx / .xlsx / .pptx / .epub / .vsix / .nupkg …
    /// </summary>
    public class ZipContainerExtensionTests : IDisposable
    {
        private readonly string _root;

        public ZipContainerExtensionTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerZipContainer", Guid.NewGuid().ToString("N"));
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
                // 清不掉不影响结论。
            }
        }

        /// <summary>造一个最小但合法的 ZIP（用 BCL，不依赖 7z）。</summary>
        private string CreateZip(string fileName)
        {
            string path = Path.Combine(_root, fileName);

            using FileStream stream = File.Create(path);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            ZipArchiveEntry entry = archive.CreateEntry("inside.txt");
            using StreamWriter writer = new(entry.Open());
            writer.Write("content");

            return path;
        }

        private async Task<ArchiveTask> ScanAsync(string fileName)
        {
            string path = CreateZip(fileName);
            var task = new ArchiveTask(path);

            await new ArchiveDetectService().ApplyDetectResultAsync(task, CancellationToken.None);

            return task;
        }

        [Theory]
        [InlineData("app.apk")]
        [InlineData("tool.jar")]
        [InlineData("report.docx")]
        [InlineData("sheet.xlsx")]
        [InlineData("slides.pptx")]
        [InlineData("book.epub")]
        [InlineData("ext.vsix")]
        [InlineData("pkg.nupkg")]
        [InlineData("app.ipa")]
        public async Task ZIP容器型后缀应视为后缀正常(string fileName)
        {
            ArchiveTask task = await ScanAsync(fileName);

            Assert.Equal("ZIP", task.DetectedFormat);
            Assert.Equal(StatusText.ExtensionNormal, task.ExtensionStatus);
        }

        [Theory]
        [InlineData("app.apk")]
        [InlineData("report.docx")]
        public async Task ZIP容器型后缀不该被智能修正改名(string fileName)
        {
            ArchiveTask task = await ScanAsync(fileName);

            System.Collections.Generic.List<RenamePreviewItem> preview = new RenameService().BuildPreview(
                new[] { task },
                RenameOptions.CreateFixByDetectedFormat(AppSettings.CreateDefault()));

            RenamePreviewItem item = Assert.Single(preview);

            Assert.False(item.CanRename, $"后缀本来就对的容器不该改名，实际：{item.NewFileName}");
            Assert.Equal(fileName, item.NewFileName);
        }

        [Theory]
        [InlineData("photo.jpg")]
        [InlineData("movie.mp4")]
        [InlineData("伪装.7z.pdf.jpg")]
        public async Task 真正的伪装后缀仍然要被抓出来(string fileName)
        {
            ArchiveTask task = await ScanAsync(fileName);

            Assert.Equal("ZIP", task.DetectedFormat);
            Assert.NotEqual(StatusText.ExtensionNormal, task.ExtensionStatus);
        }

        [Fact]
        public void 伪装后缀表里不该再有Office文档后缀()
        {
            // .docx/.xlsx/.pptx 本身就是 ZIP 容器，把它们列为"常见伪装后缀"
            // 会让"疑似伪装文件"扫描模式把每一份 Office 文档都当成可疑文件。
            Assert.False(ExtensionHelperBridge.IsSuspiciousFakeExtension(".docx"));
            Assert.False(ExtensionHelperBridge.IsSuspiciousFakeExtension(".xlsx"));
            Assert.False(ExtensionHelperBridge.IsSuspiciousFakeExtension(".pptx"));

            // 图片/视频这些仍然算伪装目标
            Assert.True(ExtensionHelperBridge.IsSuspiciousFakeExtension(".jpg"));
            Assert.True(ExtensionHelperBridge.IsSuspiciousFakeExtension(".mp4"));
        }
    }

    /// <summary>薄封装：ExtensionHelper 是静态类，测试里读起来清楚一点。</summary>
    internal static class ExtensionHelperBridge
    {
        public static bool IsSuspiciousFakeExtension(string extension)
        {
            return Helpers.ExtensionHelper.IsSuspiciousFakeExtension(extension);
        }
    }
}
