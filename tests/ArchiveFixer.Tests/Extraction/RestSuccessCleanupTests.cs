using System;
using System.IO;
using ArchiveFixer.Models;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 单一物最终成功时，失败期间留下的“最后一层过程物”必须清掉；
    /// 失败组和源包路径一律不动。
    /// </summary>
    public sealed class RestSuccessCleanupTests : IDisposable
    {
        private readonly string _root;

        public RestSuccessCleanupTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerRestCleanup", Guid.NewGuid().ToString("N"));
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
            }
        }

        [Fact]
        public void 单一物最终成功_清掉最后一层过程物并移除空其余物()
        {
            string source = Path.Combine(_root, "outer.7z");
            File.WriteAllText(source, "source");

            string rest = Path.Combine(_root, "其余物");
            Directory.CreateDirectory(rest);
            string artifact = Path.Combine(rest, "inner.7z");
            File.WriteAllText(artifact, "process");

            var root = new ArchiveTask
            {
                CurrentPath = source,
                OriginalPath = source,
                Outcome = TaskOutcome.Succeeded,
                RestDirectoryPath = rest,
                LastProcessArtifactPath = artifact
            };
            var child = new ArchiveTask
            {
                CurrentPath = Path.Combine(_root, "child.7z"),
                RootSourcePath = source,
                Outcome = TaskOutcome.Succeeded
            };

            int deleted = OneClickCoordinator.CleanupSuccessfulProcessArtifacts(new[] { root, child });

            Assert.Equal(1, deleted);
            Assert.False(File.Exists(artifact));
            Assert.False(Directory.Exists(rest));
        }

        [Fact]
        public void 单一物失败_最后一层过程物原封不动()
        {
            string source = Path.Combine(_root, "outer.7z");
            File.WriteAllText(source, "source");

            string rest = Path.Combine(_root, "其余物");
            Directory.CreateDirectory(rest);
            string artifact = Path.Combine(rest, "inner.7z");
            File.WriteAllText(artifact, "process");

            var root = new ArchiveTask
            {
                CurrentPath = source,
                OriginalPath = source,
                Outcome = TaskOutcome.Succeeded,
                RestDirectoryPath = rest,
                LastProcessArtifactPath = artifact
            };
            var child = new ArchiveTask
            {
                CurrentPath = Path.Combine(_root, "child.7z"),
                RootSourcePath = source,
                Outcome = TaskOutcome.Failed
            };

            int deleted = OneClickCoordinator.CleanupSuccessfulProcessArtifacts(new[] { root, child });

            Assert.Equal(0, deleted);
            Assert.True(File.Exists(artifact));
        }

        [Fact]
        public void 最后一层过程物指向源包时_一个字节都不删()
        {
            string source = Path.Combine(_root, "outer.7z");
            File.WriteAllText(source, "source");

            string rest = Path.Combine(_root, "其余物");
            Directory.CreateDirectory(rest);

            var root = new ArchiveTask
            {
                CurrentPath = source,
                OriginalPath = source,
                Outcome = TaskOutcome.Succeeded,
                RestDirectoryPath = rest,
                LastProcessArtifactPath = source
            };

            int deleted = OneClickCoordinator.CleanupSuccessfulProcessArtifacts(new[] { root });

            Assert.Equal(0, deleted);
            Assert.True(File.Exists(source));
        }
    }
}
