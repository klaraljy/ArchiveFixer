using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **「修正后缀」不许把分卷名改坏**（2026-09-29 真机事故，用户当场报）。
    ///
    /// <para>现场（他的日志 `ArchiveFixer-本次操作_20260929_192257.txt` 逐行对得上）：
    /// 同一个目录里放着 <c>amb909.7.01</c>（2 GiB，内容带 7z 魔数）与 <c>amb909.z.2</c>（1.89 GB，认不出格式），
    /// 本来是一组**名字被改坏的分卷**。程序给两个都做了"修正后缀"：</para>
    /// <list type="bullet">
    /// <item><description><c>amb909.7.01</c> → <c>amb909.7.7z</c>：**卷号 <c>.01</c> 被吃掉**；</description></item>
    /// <item><description><c>amb909.z.2</c> → <c>amb909.z.7z</c>：它连格式都没认出来，
    /// 只是"建议后缀为空 → 兜底成设置里的默认后缀 <c>.7z</c>"，等于替它瞎认了一个格式。</description></item>
    /// </list>
    /// <para>后果：7-Zip 再也找不到后续卷，整个包从"还能救"变成「文件损坏」。</para>
    /// </summary>
    public class VolumeNameProtectionTests : IDisposable
    {
        private static readonly byte[] SevenZipMagic = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };

        private readonly string _root;

        public VolumeNameProtectionTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerVolumeNameProtection", Guid.NewGuid().ToString("N"));
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
                // 清不掉只是脏一点。
            }
        }

        /// <summary>认不出格式的文件**一个字都不许改**：默认后缀不是证据。</summary>
        [Fact]
        public async Task 认不出格式的文件_不许被修正后缀改名()
        {
            string path = WriteFile("amb909.z.2", randomBytes: 4096);

            ArchiveTask task = await DetectAsync(path);

            Assert.NotEqual("7Z", task.DetectedFormat, StringComparer.OrdinalIgnoreCase);

            RenamePreviewItem item = Preview(task);

            Assert.Equal("amb909.z.2", item.NewFileName);
            Assert.False(item.CanRename, $"认不出格式的文件不该可改名，实际：{item.Status}");
        }

        /// <summary>
        /// 末尾是**纯数字**（卷号）的文件不许被后缀修正改名 —— 老逻辑会把它改成 <c>amb909.7.7z</c>，
        /// 卷号一没，7-Zip 就再也拼不起整组。
        /// </summary>
        [Fact]
        public async Task 数字尾巴的卷号_不许被后缀修正吃掉()
        {
            string path = WriteFile("amb909.7.01", sevenZipHead: true);

            ArchiveTask task = await DetectAsync(path);

            // 它确实被认成了 7z（内容有魔数），所以老逻辑会"顺手"把后缀改成 .7z。
            Assert.Equal("7Z", task.DetectedFormat, StringComparer.OrdinalIgnoreCase);

            RenamePreviewItem item = Preview(task);

            Assert.Equal("amb909.7.01", item.NewFileName);
            Assert.False(item.CanRename, $"卷号不许被改掉，实际：{item.Status}");
        }

        /// <summary>反向对照：**普通**的伪装后缀照旧要修（别把这一档一起关掉）。</summary>
        [Fact]
        public async Task 普通伪装后缀_照旧会被修正()
        {
            string path = WriteFile("普通包.mp4", sevenZipHead: true);

            ArchiveTask task = await DetectAsync(path);

            RenamePreviewItem item = Preview(task);

            Assert.Equal("普通包.7z", item.NewFileName);
            Assert.True(item.CanRename, $"普通伪装后缀应该可修，实际：{item.Status}");
        }

        private string WriteFile(string fileName, bool sevenZipHead = false, int randomBytes = 0)
        {
            string path = Path.Combine(_root, fileName);
            var bytes = new List<byte>();

            if (sevenZipHead)
            {
                bytes.AddRange(SevenZipMagic);
            }

            if (randomBytes > 0)
            {
                var payload = new byte[randomBytes];
                new Random(20260929).NextBytes(payload);
                bytes.AddRange(payload);
            }

            File.WriteAllBytes(path, bytes.ToArray());

            return path;
        }

        private static async Task<ArchiveTask> DetectAsync(string path)
        {
            var task = new ArchiveTask(path);

            await new ArchiveDetectService().ApplyDetectResultAsync(task, CancellationToken.None);

            return task;
        }

        private static RenamePreviewItem Preview(ArchiveTask task)
        {
            List<RenamePreviewItem> preview = new RenameService().BuildPreview(
                new[] { task },
                RenameOptions.CreateFixByDetectedFormat(AppSettings.CreateDefault()));

            return Assert.Single(preview);
        }
    }
}
