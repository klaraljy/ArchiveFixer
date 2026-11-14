using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Models;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 2026-10-29 真机 444：别名清理把用户原始 nali.pdf.001/.002 当成多余片名删掉，
    /// 只留下 H:\.ArchiveFixer.work 里的硬链接。这里钉住“用户源路径绝不许进入删除集合”。
    /// </summary>
    public sealed class PieceAliasSourceGuardTests : IDisposable
    {
        private readonly string _root;

        public PieceAliasSourceGuardTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerAliasGuard", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }

        [Fact]
        public void 用户原始路径和分卷各卷都在保护集合里()
        {
            string first = Path.Combine(_root, "nali.pdf.001");
            string second = Path.Combine(_root, "nali.pdf.002");
            File.WriteAllText(first, "a");
            File.WriteAllText(second, "b");

            var task = new ArchiveTask(first)
            {
                CurrentPath = first,
                RootSourcePath = first
            };
            task.VolumePaths.Add(first);
            task.VolumePaths.Add(second);

            HashSet<string> sources = ExtractionCoordinator.BuildUserSourcePathSet(new[] { task });

            Assert.Contains(Path.GetFullPath(first), sources);
            Assert.Contains(Path.GetFullPath(second), sources);
            Assert.DoesNotContain(Path.Combine(_root, "其余物", "nali.zip.001"), sources);
        }
    }
}
