using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Extraction;
using ArchiveFixer.Storage;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 空文件夹清理（原 <c>删除空文件夹.bat</c> 的规则）测试。
    ///
    /// 判据来自 docs/输出与整理模型.md §5 与 bat 原文：
    /// "任意层级都没有文件"的第一层子目录（只剩空目录树的整棵树）才删，任意层级有文件就保留。
    ///
    /// 与 <see cref="RecycleBinServiceTests"/> 同样的自我约束：不碰系统回收站（注入假执行器），
    /// 只在临时目录里动真实文件系统，链接 / 读失败用假探针模拟，Dispose 里清干净。
    /// </summary>
    public class EmptyFolderCleanerTests : IDisposable
    {
        private readonly string _root;

        public EmptyFolderCleanerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerEmptyFolderTests", Guid.NewGuid().ToString("N"));
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
                // 临时目录清不掉不影响测试结论。
            }
        }

        // ---------- 测试辅助 ----------

        private string PathOf(string relativePath)
        {
            return Path.Combine(_root, relativePath);
        }

        private string MakeDirectory(string relativePath)
        {
            string path = PathOf(relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        private string WriteFile(string relativePath, string content)
        {
            string path = PathOf(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        /// <summary>建一个"只有空目录"的树，返回最外层目录路径。</summary>
        private string MakeEmptyTree(string relativePath)
        {
            string path = MakeDirectory(relativePath);
            MakeDirectory(Path.Combine(relativePath, "a"));
            MakeDirectory(Path.Combine(relativePath, "a", "b"));
            MakeDirectory(Path.Combine(relativePath, "c"));
            return path;
        }

        private static EmptyFolderCleanOptions Confirmed(string root, DeleteMode mode = DeleteMode.RecycleBin)
        {
            return new EmptyFolderCleanOptions
            {
                RootDirectory = root,
                UserConfirmed = true,
                Mode = mode
            };
        }

        private static EmptyFolderCleaner CreateCleaner(
            out FakeDeleteExecutor executor,
            out RecordingLogSink sink,
            FakeProbe? probe = null)
        {
            executor = new FakeDeleteExecutor();
            sink = new RecordingLogSink();
            probe ??= new FakeProbe();

            var deleteService = new RecycleBinService(executor, sink, probe);

            return new EmptyFolderCleaner(deleteService, probe);
        }

        private sealed class FakeDeleteExecutor : IDeleteExecutor
        {
            public RecycleAttemptResult RecycleResult { get; set; } = RecycleAttemptResult.Recycled;

            public string RecycleMessage { get; set; } = "已移入回收站（假执行器）";

            public bool SimulateRemovalOnRecycle { get; set; } = true;

            public List<string> RecycleCalls { get; } = new();

            public List<string> PermanentCalls { get; } = new();

            public RecycleAttemptResult TryMoveToRecycleBin(string path, bool isDirectory, out string message)
            {
                RecycleCalls.Add(path);
                message = RecycleMessage;

                if (RecycleResult == RecycleAttemptResult.Recycled && SimulateRemovalOnRecycle)
                {
                    Directory.Delete(path, recursive: true);
                }

                return RecycleResult;
            }

            public void DeletePermanently(string path, bool isDirectory)
            {
                PermanentCalls.Add(path);
                Directory.Delete(path, recursive: true);
            }
        }

        private sealed class RecordingLogSink : IDeleteLogSink
        {
            public List<DeleteLogEntry> Entries { get; } = new();

            public void Write(DeleteLogEntry entry)
            {
                Entries.Add(entry);
            }
        }

        private sealed class FakeProbe : IDeleteFileSystemProbe
        {
            private readonly Dictionary<string, FileAttributes> _forcedAttributes =
                new(StringComparer.OrdinalIgnoreCase);

            private readonly HashSet<string> _unreadableDirectories = new(StringComparer.OrdinalIgnoreCase);

            private readonly List<string>? _forcedChildDirectories;

            public FakeProbe(List<string>? forcedChildDirectories = null)
            {
                _forcedChildDirectories = forcedChildDirectories;
            }

            public void ForceReparsePoint(string path)
            {
                _forcedAttributes[Full(path)] = FileAttributes.Directory | FileAttributes.ReparsePoint;
            }

            public void ForceUnreadableDirectory(string path)
            {
                _unreadableDirectories.Add(Full(path));
            }

            public FileAttributes? TryGetAttributes(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return null;
                }

                string full = Full(path);

                if (_forcedAttributes.TryGetValue(full, out FileAttributes forced))
                {
                    return forced;
                }

                return WindowsDeleteFileSystemProbe.Instance.TryGetAttributes(full);
            }

            public bool TryListChildren(string? directory, out IReadOnlyList<string> children)
            {
                if (!string.IsNullOrWhiteSpace(directory) && _unreadableDirectories.Contains(Full(directory)))
                {
                    children = Array.Empty<string>();
                    return false;
                }

                return WindowsDeleteFileSystemProbe.Instance.TryListChildren(directory, out children);
            }

            public bool TryListChildDirectories(string? directory, out IReadOnlyList<string> directories)
            {
                if (_forcedChildDirectories != null)
                {
                    directories = _forcedChildDirectories;
                    return true;
                }

                return WindowsDeleteFileSystemProbe.Instance.TryListChildDirectories(directory, out directories);
            }

            private static string Full(string path)
            {
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
        }

        // ---------- 判定规则 ----------

        [Fact]
        public void 清理_空目录树被删()
        {
            string emptyTree = MakeEmptyTree("empty-tree");
            EmptyFolderCleaner cleaner = CreateCleaner(out FakeDeleteExecutor executor, out _);

            EmptyFolderCleanResult result = cleaner.Clean(Confirmed(_root));

            Assert.True(result.Scanned);
            Assert.True(result.Attempted);
            Assert.Equal(emptyTree, Assert.Single(result.DeletedDirectories));
            Assert.Equal(emptyTree, Assert.Single(result.EmptyTrees));
            Assert.False(Directory.Exists(emptyTree), "只剩空目录树的目录应当整棵删掉");
            Assert.Equal(emptyTree, Assert.Single(executor.RecycleCalls));
            Assert.Empty(executor.PermanentCalls);
        }

        [Fact]
        public void 清理_含文件的目录保留()
        {
            MakeDirectory("has-file");
            string file = WriteFile(@"has-file\a.txt", "A");
            EmptyFolderCleaner cleaner = CreateCleaner(out FakeDeleteExecutor executor, out _);

            EmptyFolderCleanResult result = cleaner.Clean(Confirmed(_root));

            Assert.Empty(result.DeletedDirectories);
            Assert.Empty(result.EmptyTrees);
            Assert.True(File.Exists(file));
            Assert.Empty(executor.RecycleCalls);

            EmptyFolderSkip skip = Assert.Single(result.Skipped);
            Assert.Equal(EmptyFolderSkipReason.HasFiles, skip.Reason);
        }

        [Fact]
        public void 清理_任意层级有文件就保留()
        {
            string deepFile = WriteFile(@"deep\a\b\c\d.txt", "D");
            EmptyFolderCleaner cleaner = CreateCleaner(out FakeDeleteExecutor executor, out _);

            EmptyFolderCleanResult result = cleaner.Clean(Confirmed(_root));

            Assert.Empty(result.DeletedDirectories);
            Assert.True(File.Exists(deepFile));
            Assert.Empty(executor.RecycleCalls);
            Assert.Equal(EmptyFolderSkipReason.HasFiles, Assert.Single(result.Skipped).Reason);
        }

        [Fact]
        public void 清理_空文件也算文件()
        {
            // 与 bat 的 `dir /a-d` 一致：只看是不是文件，不看大小。
            string emptyFile = WriteFile(@"only-empty-file\zero.txt", string.Empty);
            EmptyFolderCleaner cleaner = CreateCleaner(out FakeDeleteExecutor executor, out _);

            EmptyFolderCleanResult result = cleaner.Clean(Confirmed(_root));

            Assert.Empty(result.DeletedDirectories);
            Assert.True(File.Exists(emptyFile));
            Assert.Empty(executor.RecycleCalls);
            Assert.Equal(EmptyFolderSkipReason.HasFiles, Assert.Single(result.Skipped).Reason);
        }

        [Fact]
        public void 清理_只处理第一层子目录()
        {
            // keep 自己有文件 → 保留；它里面的空目录属于"更深一层"，不在本功能的作用范围内。
            string nested = MakeEmptyTree(@"keep\empty-nested");
            string file = WriteFile(@"keep\x.txt", "X");
            EmptyFolderCleaner cleaner = CreateCleaner(out FakeDeleteExecutor executor, out _);

            EmptyFolderCleanResult result = cleaner.Clean(Confirmed(_root));

            Assert.Empty(result.DeletedDirectories);
            Assert.True(Directory.Exists(nested));
            Assert.True(File.Exists(file));
            Assert.Empty(executor.RecycleCalls);
        }

        [Fact]
        public void 清理_根目录下的文件不受影响()
        {
            string looseFile = WriteFile("loose.txt", "L");
            string emptyTree = MakeEmptyTree("empty-tree");
            EmptyFolderCleaner cleaner = CreateCleaner(out _, out _);

            EmptyFolderCleanResult result = cleaner.Clean(Confirmed(_root));

            Assert.Equal(emptyTree, Assert.Single(result.DeletedDirectories));
            Assert.True(File.Exists(looseFile), "根目录下的文件不属于空文件夹清理的对象，不能被删");
            Assert.True(Directory.Exists(_root));
        }

        [Fact]
        public void 清理_空目录与有文件目录混合时只删空的()
        {
            string emptyTree = MakeEmptyTree("empty");
            string keepTree = MakeDirectory("keep");
            WriteFile(@"keep\a.txt", "A");
            EmptyFolderCleaner cleaner = CreateCleaner(out _, out _);

            EmptyFolderCleanResult result = cleaner.Clean(Confirmed(_root));

            Assert.Equal(emptyTree, Assert.Single(result.DeletedDirectories));
            Assert.False(Directory.Exists(emptyTree));
            Assert.True(Directory.Exists(keepTree));
        }

        // ---------- 默认不删 ----------

        [Fact]
        public void 清理_未确认时一个都不删并列出候选()
        {
            string emptyTree = MakeEmptyTree("empty-tree");
            EmptyFolderCleaner cleaner = CreateCleaner(out FakeDeleteExecutor executor, out _);

            EmptyFolderCleanResult result = cleaner.Clean(new EmptyFolderCleanOptions
            {
                RootDirectory = _root,
                UserConfirmed = false
            });

            Assert.True(result.Scanned);
            Assert.False(result.Attempted);
            Assert.Empty(result.DeletedDirectories);
            Assert.Equal(emptyTree, Assert.Single(result.EmptyTrees));
            Assert.True(Directory.Exists(emptyTree), "没有确认标志时一个字节都不能动");
            Assert.Empty(executor.RecycleCalls);
            Assert.Equal(EmptyFolderSkipReason.NotConfirmed, Assert.Single(result.Skipped).Reason);
            Assert.Contains("未获用户确认", result.Message, StringComparison.Ordinal);
        }

        // ---------- 越界 / 读不到 / 链接 ----------

        [Fact]
        public void 清理_根目录之外的目录不动()
        {
            // 根目录的兄弟目录：同样是空目录树，但不在本次作用范围内。
            string sibling = Path.Combine(Path.GetDirectoryName(_root)!, "sibling-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(sibling, "inner"));

            try
            {
                string emptyTree = MakeEmptyTree("empty-tree");
                EmptyFolderCleaner cleaner = CreateCleaner(out _, out _);

                EmptyFolderCleanResult result = cleaner.Clean(Confirmed(_root));

                Assert.Equal(emptyTree, Assert.Single(result.DeletedDirectories));
                Assert.True(Directory.Exists(sibling), "作用范围之外的目录绝不能被删");
                Assert.True(Directory.Exists(Path.Combine(sibling, "inner")));
            }
            finally
            {
                try
                {
                    Directory.Delete(sibling, recursive: true);
                }
                catch
                {
                    // 清理失败不影响结论。
                }
            }
        }

        [Fact]
        public void 清理_子目录越出根时跳过()
        {
            string outside = Path.Combine(Path.GetDirectoryName(_root)!, "outside-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outside);
            string emptyTree = MakeEmptyTree("empty-tree");

            try
            {
                // 假探针硬塞一个根外的"子目录"：容器内校验必须拦住它。
                var probe = new FakeProbe(new List<string> { emptyTree, outside });
                EmptyFolderCleaner cleaner = CreateCleaner(out FakeDeleteExecutor executor, out _, probe);

                EmptyFolderCleanResult result = cleaner.Clean(Confirmed(_root));

                EmptyFolderSkip outsideSkip = Assert.Single(result.Skipped, skip => skip.Path == outside);
                Assert.Equal(EmptyFolderSkipReason.OutsideRoot, outsideSkip.Reason);
                Assert.True(Directory.Exists(outside));
                Assert.Equal(emptyTree, Assert.Single(result.DeletedDirectories));
                Assert.Equal(emptyTree, Assert.Single(executor.RecycleCalls));
            }
            finally
            {
                try
                {
                    Directory.Delete(outside, recursive: true);
                }
                catch
                {
                    // 清理失败不影响结论。
                }
            }
        }

        [Fact]
        public void 清理_第一层是链接时跳过()
        {
            string linked = MakeDirectory("linked");
            var probe = new FakeProbe();
            probe.ForceReparsePoint(linked);
            EmptyFolderCleaner cleaner = CreateCleaner(out FakeDeleteExecutor executor, out _, probe);

            EmptyFolderCleanResult result = cleaner.Clean(Confirmed(_root));

            Assert.Empty(result.DeletedDirectories);
            Assert.Equal(EmptyFolderSkipReason.IsReparsePoint, Assert.Single(result.Skipped).Reason);
            Assert.Empty(executor.RecycleCalls);
            Assert.True(Directory.Exists(linked));
        }

        [Fact]
        public void 清理_树内有链接时跳过()
        {
            string tree = MakeDirectory("tree");
            string linked = MakeDirectory(@"tree\linked");
            var probe = new FakeProbe();
            probe.ForceReparsePoint(linked);
            EmptyFolderCleaner cleaner = CreateCleaner(out FakeDeleteExecutor executor, out _, probe);

            EmptyFolderCleanResult result = cleaner.Clean(Confirmed(_root));

            Assert.Empty(result.DeletedDirectories);
            Assert.Equal(EmptyFolderSkipReason.IsReparsePoint, Assert.Single(result.Skipped).Reason);
            Assert.Empty(executor.RecycleCalls);
            Assert.True(Directory.Exists(linked));
        }

        [Fact]
        public void 清理_读不到目录内容时保留()
        {
            string locked = MakeDirectory("locked");
            var probe = new FakeProbe();
            probe.ForceUnreadableDirectory(locked);
            EmptyFolderCleaner cleaner = CreateCleaner(out FakeDeleteExecutor executor, out _, probe);

            EmptyFolderCleanResult result = cleaner.Clean(Confirmed(_root));

            Assert.Empty(result.DeletedDirectories);
            Assert.Equal(EmptyFolderSkipReason.NoPermission, Assert.Single(result.Skipped).Reason);
            Assert.Empty(executor.RecycleCalls);
            Assert.True(Directory.Exists(locked));
        }

        // ---------- 两档删除 ----------

        [Fact]
        public void 清理_彻底删除档真的删掉空目录树()
        {
            string emptyTree = MakeEmptyTree("empty-tree");

            // 用真实执行器：彻底删除是真删（只在临时目录里）。
            var deleteService = new RecycleBinService();
            var cleaner = new EmptyFolderCleaner(deleteService);

            EmptyFolderCleanResult result = cleaner.Clean(Confirmed(_root, DeleteMode.Permanent));

            Assert.Equal(emptyTree, Assert.Single(result.DeletedDirectories));
            Assert.False(Directory.Exists(emptyTree));
            Assert.Contains("彻底删除", result.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 清理_回收站不可用时保留并报原因()
        {
            string emptyTree = MakeEmptyTree("empty-tree");
            EmptyFolderCleaner cleaner = CreateCleaner(out FakeDeleteExecutor executor, out _);
            executor.RecycleResult = RecycleAttemptResult.Unavailable;
            executor.RecycleMessage = "系统策略已禁止使用回收站";
            executor.SimulateRemovalOnRecycle = false;

            EmptyFolderCleanResult result = cleaner.Clean(Confirmed(_root));

            Assert.Empty(result.DeletedDirectories);
            Assert.True(Directory.Exists(emptyTree), "回收站不可用时不得删除（更不得改成永久删除）");
            Assert.Empty(executor.PermanentCalls);

            EmptyFolderSkip skip = Assert.Single(result.Skipped);
            Assert.Equal(EmptyFolderSkipReason.DeleteFailed, skip.Reason);
            Assert.Contains("回收站", skip.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 清理_删除动作留下日志()
        {
            string first = MakeEmptyTree("empty-1");
            string second = MakeEmptyTree("empty-2");
            EmptyFolderCleaner cleaner = CreateCleaner(out _, out RecordingLogSink sink);

            EmptyFolderCleanResult result = cleaner.Clean(Confirmed(_root));

            Assert.Equal(2, result.DeletedDirectories.Count);
            Assert.Equal(2, sink.Entries.Count);
            Assert.Equal(2, result.DeleteResult!.LogEntries.Count);
            Assert.All(sink.Entries, entry =>
            {
                Assert.True(entry.Success);
                Assert.Equal("空文件夹清理", entry.Reason);
                Assert.True(entry.EntryCount > 0, "空目录树也有条目（空子目录）");
            });
            Assert.Contains(sink.Entries, entry => entry.Path == first);
            Assert.Contains(sink.Entries, entry => entry.Path == second);
        }

        // ---------- 参数与根目录异常 ----------

        [Fact]
        public void 清理_没有选项时不动手()
        {
            MakeEmptyTree("empty-tree");
            EmptyFolderCleaner cleaner = CreateCleaner(out FakeDeleteExecutor executor, out _);

            EmptyFolderCleanResult result = cleaner.Clean(null);

            Assert.False(result.Scanned);
            Assert.False(result.Attempted);
            Assert.Empty(executor.RecycleCalls);
        }

        [Fact]
        public void 清理_根目录不存在时不动手()
        {
            EmptyFolderCleaner cleaner = CreateCleaner(out FakeDeleteExecutor executor, out _);

            EmptyFolderCleanResult result = cleaner.Clean(Confirmed(PathOf("not-exists")));

            Assert.False(result.Scanned);
            Assert.Contains("根目录", result.Message, StringComparison.Ordinal);
            Assert.Empty(executor.RecycleCalls);
        }

        [Fact]
        public void 清理_根目录下没有子目录时不报错()
        {
            WriteFile("loose.txt", "L");
            EmptyFolderCleaner cleaner = CreateCleaner(out FakeDeleteExecutor executor, out _);

            EmptyFolderCleanResult result = cleaner.Clean(Confirmed(_root));

            Assert.True(result.Scanned);
            Assert.Empty(result.DeletedDirectories);
            Assert.Empty(result.Skipped);
            Assert.Contains("没有子目录", result.Message, StringComparison.Ordinal);
            Assert.Empty(executor.RecycleCalls);
        }

        [Fact]
        public void 清理_根路径是文件时不动手()
        {
            string file = WriteFile("not-a-directory.txt", "F");
            EmptyFolderCleaner cleaner = CreateCleaner(out FakeDeleteExecutor executor, out _);

            EmptyFolderCleanResult result = cleaner.Clean(Confirmed(file));

            Assert.False(result.Scanned);
            Assert.True(File.Exists(file));
            Assert.Empty(executor.RecycleCalls);
        }
    }
}
