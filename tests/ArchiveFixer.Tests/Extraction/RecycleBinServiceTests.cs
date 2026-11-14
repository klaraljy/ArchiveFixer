using System;
using System.Collections.Generic;
using System.IO;
using ArchiveFixer.Storage;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 两档删除（<see cref="RecycleBinService"/>）的测试。
    ///
    /// 三条自我约束（都是硬要求，别改）：
    /// ① **绝不往用户系统回收站里塞东西**：回收站档全部走注入的假执行器（<see cref="FakeDeleteExecutor"/>），
    ///    只验证"服务怎么决策、怎么记账、怎么回报"；
    /// ② 真正"彻底删除"的用例只在 <see cref="Path.GetTempPath"/> 下的独立临时目录里跑，<see cref="Dispose"/> 里清干净；
    /// ③ 造符号链接 / 联接点需要管理员权限或外部命令，在测试里不稳定 —— 改用可注入的假探针
    ///    （<see cref="FakeProbe"/>）来验证"有链接就拒绝"这条判定。
    ///
    /// 测试数据一律是合成值（pack.7z / a.txt / "NEW"），不含任何真实用户数据（AGENTS.md §8）。
    /// </summary>
    public class RecycleBinServiceTests : IDisposable
    {
        private readonly string _root;

        public RecycleBinServiceTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerRecycleTests", Guid.NewGuid().ToString("N"));
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

        private string WriteFile(string relativePath, string content)
        {
            string path = PathOf(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        private string MakeDirectory(string relativePath)
        {
            string path = PathOf(relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        private static DeleteOptions Confirmed(string root, DeleteMode mode = DeleteMode.RecycleBin, string reason = "测试用")
        {
            return new DeleteOptions
            {
                AllowedRoot = root,
                UserConfirmed = true,
                Mode = mode,
                Reason = reason
            };
        }

        /// <summary>记录调用、可脚本化结果的假执行器：全程不碰系统回收站。</summary>
        private sealed class FakeDeleteExecutor : IDeleteExecutor
        {
            public RecycleAttemptResult RecycleResult { get; set; } = RecycleAttemptResult.Recycled;

            public string RecycleMessage { get; set; } = "已移入回收站（假执行器）";

            /// <summary>报"回收站成功"时是否真的把目标从临时目录里移走（模拟"搬进回收站"）。</summary>
            public bool SimulateRemovalOnRecycle { get; set; } = true;

            public Exception? RecycleException { get; set; }

            public List<string> RecycleCalls { get; } = new();

            public List<string> PermanentCalls { get; } = new();

            public RecycleAttemptResult TryMoveToRecycleBin(string path, bool isDirectory, out string message)
            {
                RecycleCalls.Add(path);
                message = RecycleMessage;

                if (RecycleException != null)
                {
                    throw RecycleException;
                }

                if (RecycleResult == RecycleAttemptResult.Recycled && SimulateRemovalOnRecycle)
                {
                    if (isDirectory)
                    {
                        Directory.Delete(path, recursive: true);
                    }
                    else
                    {
                        File.Delete(path);
                    }
                }

                return RecycleResult;
            }

            public void DeletePermanently(string path, bool isDirectory)
            {
                PermanentCalls.Add(path);

                if (isDirectory)
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>在真实文件系统探针外面套一层"可强制的属性 / 可强制的读失败"，用来模拟链接与权限问题。</summary>
        private sealed class FakeProbe : IDeleteFileSystemProbe
        {
            private readonly Dictionary<string, FileAttributes> _forcedAttributes =
                new(StringComparer.OrdinalIgnoreCase);

            private readonly HashSet<string> _unreadableAttributes = new(StringComparer.OrdinalIgnoreCase);
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

            public void ForceUnreadableAttributes(string path)
            {
                _unreadableAttributes.Add(Full(path));
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

                if (_unreadableAttributes.Contains(full))
                {
                    return null;
                }

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

                if (!string.IsNullOrWhiteSpace(directory) && _unreadableDirectories.Contains(Full(directory)))
                {
                    directories = Array.Empty<string>();
                    return false;
                }

                return WindowsDeleteFileSystemProbe.Instance.TryListChildDirectories(directory, out directories);
            }

            private static string Full(string path)
            {
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
        }

        /// <summary>收集删除日志的假落点。</summary>
        private sealed class RecordingLogSink : IDeleteLogSink
        {
            public List<DeleteLogEntry> Entries { get; } = new();

            public bool ThrowOnWrite { get; set; }

            public void Write(DeleteLogEntry entry)
            {
                if (ThrowOnWrite)
                {
                    throw new InvalidOperationException("测试用：日志落点故意抛异常");
                }

                Entries.Add(entry);
            }
        }

        // ---------- 安全前置（纯判定，不碰文件系统） ----------

        [Fact]
        public void 安全判定_未传确认标志一律拒绝()
        {
            string target = PathOf(@"out\a.txt");

            DeleteSafetyDecision decision = DeleteSafetyGuard.EvaluatePath(_root, target, userConfirmed: false);

            Assert.False(decision.IsAllowed);
            Assert.Equal(DeleteBlockReason.NoConfirmation, decision.Reason);
            Assert.Contains("确认", decision.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void 安全判定_没有允许根时拒绝()
        {
            DeleteSafetyDecision decision = DeleteSafetyGuard.EvaluatePath(null, PathOf(@"out\a.txt"), userConfirmed: true);

            Assert.False(decision.IsAllowed);
            Assert.Equal(DeleteBlockReason.RootNotSpecified, decision.Reason);
        }

        [Fact]
        public void 安全判定_目标为空时拒绝()
        {
            DeleteSafetyDecision decision = DeleteSafetyGuard.EvaluatePath(_root, "   ", userConfirmed: true);

            Assert.False(decision.IsAllowed);
            Assert.Equal(DeleteBlockReason.EmptyTarget, decision.Reason);
        }

        [Fact]
        public void 安全判定_根内路径通过()
        {
            DeleteSafetyDecision decision = DeleteSafetyGuard.EvaluatePath(
                _root,
                PathOf(@"out\sub\a.txt"),
                userConfirmed: true);

            Assert.True(decision.IsAllowed);
        }

        [Fact]
        public void 安全判定_大小写与冗余分隔符不影响结论()
        {
            DeleteSafetyDecision decision = DeleteSafetyGuard.EvaluatePath(
                _root.ToUpperInvariant(),
                PathOf(@"out\sub\a.txt").ToUpperInvariant(),
                userConfirmed: true);

            Assert.True(decision.IsAllowed, decision.Message);
        }

        [Fact]
        public void 安全判定_根外路径被拒()
        {
            string outside = Path.Combine(Path.GetTempPath(), "ArchiveFixerOutside", "a.txt");

            DeleteSafetyDecision decision = DeleteSafetyGuard.EvaluatePath(_root, outside, userConfirmed: true);

            Assert.False(decision.IsAllowed);
            Assert.Equal(DeleteBlockReason.OutsideAllowedRoot, decision.Reason);
        }

        [Fact]
        public void 安全判定_同前缀的兄弟目录被拒()
        {
            // C:\t\out2 不在 C:\t\out 里面 —— 只比前缀的写法会把它判成"在里面"。
            DeleteSafetyDecision decision = DeleteSafetyGuard.EvaluatePath(
                PathOf("out"),
                PathOf(@"out2\a.txt"),
                userConfirmed: true);

            Assert.False(decision.IsAllowed);
            Assert.Equal(DeleteBlockReason.OutsideAllowedRoot, decision.Reason);
        }

        [Fact]
        public void 安全判定_允许根本身被拒()
        {
            DeleteSafetyDecision decision = DeleteSafetyGuard.EvaluatePath(_root, _root, userConfirmed: true);

            Assert.False(decision.IsAllowed);
            Assert.Equal(DeleteBlockReason.IsAllowedRootItself, decision.Reason);
        }

        [Fact]
        public void 安全判定_允许根带结尾分隔符时同样被拒()
        {
            DeleteSafetyDecision decision = DeleteSafetyGuard.EvaluatePath(
                _root,
                _root + Path.DirectorySeparatorChar,
                userConfirmed: true);

            Assert.False(decision.IsAllowed);
            Assert.Equal(DeleteBlockReason.IsAllowedRootItself, decision.Reason);
        }

        [Fact]
        public void 安全判定_驱动器根被拒()
        {
            string driveRoot = Path.GetPathRoot(_root)!;

            DeleteSafetyDecision decision = DeleteSafetyGuard.EvaluatePath(_root, driveRoot, userConfirmed: true);

            Assert.False(decision.IsAllowed);
            Assert.Equal(DeleteBlockReason.IsDriveRoot, decision.Reason);
        }

        [Fact]
        public void 安全判定_上跳路径绕回根外时被拒()
        {
            // "root\..\.." 规范化之后是盘根/上层目录，绝不能因为字符串里有 root 就放行。
            DeleteSafetyDecision decision = DeleteSafetyGuard.EvaluatePath(
                _root,
                Path.Combine(_root, "..", ".."),
                userConfirmed: true);

            Assert.False(decision.IsAllowed);
            Assert.True(
                decision.Reason == DeleteBlockReason.IsDriveRoot ||
                decision.Reason == DeleteBlockReason.OutsideAllowedRoot,
                $"实际原因：{decision.Reason}");
        }

        // ---------- 安全前置（符号链接 / 联接点，注入假探针） ----------

        [Fact]
        public void 安全检查_目标是链接时拒绝()
        {
            string target = MakeDirectory("link-self");
            var probe = new FakeProbe();
            probe.ForceReparsePoint(target);

            DeleteSafetyDecision decision = DeleteSafetyGuard.Evaluate(_root, target, userConfirmed: true, probe);

            Assert.False(decision.IsAllowed);
            Assert.Equal(DeleteBlockReason.TargetIsReparsePoint, decision.Reason);
            Assert.True(Directory.Exists(target), "被拒绝的删除不能动文件系统");
        }

        [Fact]
        public void 安全检查_上级目录是链接时拒绝()
        {
            string target = WriteFile(@"hop\sub\a.txt", "A");
            var probe = new FakeProbe();

            // 目标自己干净，但它上面那一级是指向别处的联接点：字符串上还在根内，物理上可能已经在盘外。
            probe.ForceReparsePoint(PathOf("hop"));

            DeleteSafetyDecision decision = DeleteSafetyGuard.Evaluate(_root, target, userConfirmed: true, probe);

            Assert.False(decision.IsAllowed);
            Assert.Equal(DeleteBlockReason.ReparsePointInPath, decision.Reason);
            Assert.True(File.Exists(target));
        }

        [Fact]
        public void 安全检查_子树里有链接时拒绝()
        {
            string target = MakeDirectory("tree");
            string inner = MakeDirectory(@"tree\inner");
            var probe = new FakeProbe();
            probe.ForceReparsePoint(inner);

            DeleteSafetyDecision decision = DeleteSafetyGuard.Evaluate(_root, target, userConfirmed: true, probe);

            Assert.False(decision.IsAllowed);
            Assert.Equal(DeleteBlockReason.ReparsePointInSubtree, decision.Reason);
            Assert.True(Directory.Exists(inner));
        }

        [Fact]
        public void 安全检查_读不到条目属性时保守拒绝()
        {
            string target = MakeDirectory("tree");
            string child = WriteFile(@"tree\a.txt", "A");
            var probe = new FakeProbe();
            probe.ForceUnreadableAttributes(child);

            DeleteSafetyDecision decision = DeleteSafetyGuard.Evaluate(_root, target, userConfirmed: true, probe);

            Assert.False(decision.IsAllowed);
            Assert.Equal(DeleteBlockReason.SafetyProbeUnavailable, decision.Reason);
        }

        [Fact]
        public void 安全检查_子树读不到时保守拒绝()
        {
            string target = MakeDirectory("tree");
            MakeDirectory(@"tree\inner");
            var probe = new FakeProbe();
            probe.ForceUnreadableDirectory(PathOf(@"tree\inner"));

            DeleteSafetyDecision decision = DeleteSafetyGuard.Evaluate(_root, target, userConfirmed: true, probe);

            Assert.False(decision.IsAllowed);
            Assert.Equal(DeleteBlockReason.SafetyProbeUnavailable, decision.Reason);
        }

        [Fact]
        public void 安全检查_目标不存在时拒绝()
        {
            DeleteSafetyDecision decision = DeleteSafetyGuard.Evaluate(
                _root,
                PathOf(@"not-exists\a.txt"),
                userConfirmed: true);

            Assert.False(decision.IsAllowed);
            Assert.Equal(DeleteBlockReason.TargetNotFound, decision.Reason);
        }

        // ---------- 回收站档（假执行器，不碰系统回收站） ----------

        [Fact]
        public void 回收站_未确认时执行器一次都没被调用()
        {
            string target = WriteFile(@"out\a.txt", "0123456789");
            var executor = new FakeDeleteExecutor();
            var service = new RecycleBinService(executor);

            DeleteResult result = service.Delete(
                new DeleteRequest(target, "过程物清理"),
                new DeleteOptions { AllowedRoot = _root, UserConfirmed = false, Mode = DeleteMode.RecycleBin });

            Assert.Empty(executor.RecycleCalls);
            Assert.Empty(executor.PermanentCalls);
            Assert.Equal(0, result.SuccessCount);
            Assert.Equal(1, result.FailureCount);
            Assert.Equal(DeleteBlockReason.NoConfirmation, result.Outcomes[0].BlockReason);
            Assert.True(File.Exists(target), "未确认时源文件必须原样保留");
        }

        [Fact]
        public void 回收站_成功时计数与字节数正确且分开记回收站字节()
        {
            string first = WriteFile(@"out\a.txt", "0123456789");      // 10 字节
            string second = WriteFile(@"out\b.txt", "ABCDEF");         // 6 字节
            var executor = new FakeDeleteExecutor();
            var service = new RecycleBinService(executor);

            DeleteResult result = service.Delete(
                new[] { new DeleteRequest(first, "过程物"), new DeleteRequest(second, "过程物") },
                Confirmed(_root));

            Assert.Equal(2, result.SuccessCount);
            Assert.Equal(0, result.FailureCount);
            Assert.Equal(2, executor.RecycleCalls.Count);
            Assert.Empty(executor.PermanentCalls);

            // 回收站档：文件只是被搬走，空间并没有真正腾出来，所以 FreedBytes 必须是 0。
            Assert.Equal(0L, result.FreedBytes);
            Assert.Equal(16L, result.RecycledBytes);
            Assert.False(File.Exists(first));
            Assert.False(File.Exists(second));
        }

        [Fact]
        public void 回收站_不可用时绝不降级为永久删除()
        {
            string target = WriteFile(@"out\pack.7z", "0123456789");
            var executor = new FakeDeleteExecutor
            {
                RecycleResult = RecycleAttemptResult.Unavailable,
                RecycleMessage = "目标是网络盘，系统不会为它保留回收站",
                SimulateRemovalOnRecycle = false
            };
            var service = new RecycleBinService(executor);

            DeleteResult result = service.Delete(new DeleteRequest(target, "过程物"), Confirmed(_root));

            Assert.Equal(0, result.SuccessCount);
            Assert.Equal(1, result.FailureCount);
            Assert.Equal(DeleteBlockReason.RecycleBinUnavailable, result.Outcomes[0].BlockReason);

            // 关键断言：回收站不可用时**没有**任何永久删除发生，文件还在。
            Assert.Empty(executor.PermanentCalls);
            Assert.True(File.Exists(target));
            Assert.Single(result.FailureReasons);
            Assert.Contains("网络盘", result.FailureReasons[0], StringComparison.Ordinal);
        }

        [Fact]
        public void 回收站_执行器抛异常时文件仍在且不降级()
        {
            string target = WriteFile(@"out\pack.7z", "0123456789");
            var executor = new FakeDeleteExecutor
            {
                RecycleException = new IOException("测试用：回收站写入失败")
            };
            var service = new RecycleBinService(executor);

            DeleteResult result = service.Delete(new DeleteRequest(target, "过程物"), Confirmed(_root));

            Assert.Equal(1, result.FailureCount);
            Assert.Equal(DeleteBlockReason.DeleteFailed, result.Outcomes[0].BlockReason);
            Assert.Empty(executor.PermanentCalls);
            Assert.True(File.Exists(target));
        }

        [Fact]
        public void 回收站_系统报成功但目标还在时不算成功()
        {
            string target = WriteFile(@"out\pack.7z", "0123456789");
            var executor = new FakeDeleteExecutor { SimulateRemovalOnRecycle = false };
            var service = new RecycleBinService(executor);

            DeleteResult result = service.Delete(new DeleteRequest(target, "过程物"), Confirmed(_root));

            Assert.Equal(0, result.SuccessCount);
            Assert.Equal(DeleteBlockReason.TargetStillExists, result.Outcomes[0].BlockReason);
            Assert.True(File.Exists(target));
        }

        [Fact]
        public void 回收站_目录也支持且条目数按子树统计()
        {
            WriteFile(@"out\inner\a.txt", "0123456789");
            WriteFile(@"out\inner\b.txt", "ABCDEF");
            string target = MakeDirectory(@"out\inner");
            var executor = new FakeDeleteExecutor();
            var service = new RecycleBinService(executor);

            DeleteResult result = service.Delete(new DeleteRequest(target, "过程物"), Confirmed(_root));

            Assert.Equal(1, result.SuccessCount);
            Assert.Equal(2, result.Outcomes[0].EntryCount);
            Assert.Equal(16L, result.Outcomes[0].TotalBytes);
            Assert.Equal(16L, result.RecycledBytes);
            Assert.False(Directory.Exists(target));
        }

        // ---------- 彻底删除档 ----------

        [Fact]
        public void 彻底删除_真的删掉文件与目录并计释放字节()
        {
            string file = WriteFile(@"out\a.txt", "0123456789");             // 10 字节
            WriteFile(@"out\tree\inner\b.txt", "1234567890");                // 10 字节
            WriteFile(@"out\tree\inner\c.txt", "abc");                       // 3 字节
            string tree = MakeDirectory(@"out\tree");

            // 用真实执行器（默认构造）：彻底删除是真删，但只删这个临时目录里的东西。
            var service = new RecycleBinService();

            DeleteResult result = service.Delete(
                new[] { new DeleteRequest(file, "过程物"), new DeleteRequest(tree, "过程物") },
                Confirmed(_root, DeleteMode.Permanent));

            Assert.Equal(2, result.SuccessCount);
            Assert.Equal(0, result.FailureCount);
            Assert.Equal(23L, result.FreedBytes);
            Assert.Equal(0L, result.RecycledBytes);
            Assert.False(File.Exists(file));
            Assert.False(Directory.Exists(tree));
        }

        [Fact]
        public void 彻底删除_成功说明写明不可恢复()
        {
            string file = WriteFile(@"out\a.txt", "A");
            var service = new RecycleBinService();

            DeleteResult result = service.Delete(
                new DeleteRequest(file, "过程物"),
                Confirmed(_root, DeleteMode.Permanent));

            Assert.Contains("不可恢复", result.Outcomes[0].Message, StringComparison.Ordinal);
        }

        // ---------- 拒绝路径（一个字节都不能动） ----------

        [Fact]
        public void 拒绝_根外目标不执行且文件保留()
        {
            string outsideRoot = Path.Combine(Path.GetTempPath(), "ArchiveFixerOutside", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outsideRoot);
            string target = Path.Combine(outsideRoot, "a.txt");
            File.WriteAllText(target, "A");

            try
            {
                var executor = new FakeDeleteExecutor();
                var service = new RecycleBinService(executor);

                DeleteResult result = service.Delete(new DeleteRequest(target, "过程物"), Confirmed(_root));

                Assert.Empty(executor.RecycleCalls);
                Assert.Empty(executor.PermanentCalls);
                Assert.Equal(DeleteBlockReason.OutsideAllowedRoot, result.Outcomes[0].BlockReason);
                Assert.True(File.Exists(target));
            }
            finally
            {
                try
                {
                    Directory.Delete(outsideRoot, recursive: true);
                }
                catch
                {
                    // 清理失败不影响结论。
                }
            }
        }

        [Fact]
        public void 拒绝_允许根本身不执行()
        {
            var executor = new FakeDeleteExecutor();
            var service = new RecycleBinService(executor);

            DeleteResult result = service.Delete(new DeleteRequest(_root, "误操作"), Confirmed(_root));

            Assert.Empty(executor.RecycleCalls);
            Assert.Equal(DeleteBlockReason.IsAllowedRootItself, result.Outcomes[0].BlockReason);
            Assert.True(Directory.Exists(_root));
        }

        [Fact]
        public void 拒绝_驱动器根不执行()
        {
            var executor = new FakeDeleteExecutor();
            var service = new RecycleBinService(executor);
            string driveRoot = Path.GetPathRoot(_root)!;

            DeleteResult result = service.Delete(new DeleteRequest(driveRoot, "误操作"), Confirmed(_root));

            Assert.Empty(executor.RecycleCalls);
            Assert.Equal(DeleteBlockReason.IsDriveRoot, result.Outcomes[0].BlockReason);
            Assert.True(Directory.Exists(driveRoot));
        }

        [Fact]
        public void 拒绝_没有删除选项时全部拒绝()
        {
            string target = WriteFile(@"out\a.txt", "A");
            var executor = new FakeDeleteExecutor();
            var service = new RecycleBinService(executor);

            DeleteResult result = service.Delete(new DeleteRequest(target, "过程物"), null);

            Assert.Empty(executor.RecycleCalls);
            Assert.Equal(DeleteBlockReason.InvalidRequest, result.Outcomes[0].BlockReason);
            Assert.True(File.Exists(target));
        }

        [Fact]
        public void 拒绝_目标不存在时不报成功()
        {
            var executor = new FakeDeleteExecutor();
            var service = new RecycleBinService(executor);

            DeleteResult result = service.Delete(new DeleteRequest(PathOf(@"out\ghost.7z"), "过程物"), Confirmed(_root));

            Assert.Equal(0, result.SuccessCount);
            Assert.Equal(DeleteBlockReason.TargetNotFound, result.Outcomes[0].BlockReason);
            Assert.Empty(executor.RecycleCalls);
        }

        [Fact]
        public void 拒绝_目标是链接时执行器不被调用()
        {
            string target = MakeDirectory("link-self");
            var probe = new FakeProbe();
            probe.ForceReparsePoint(target);
            var executor = new FakeDeleteExecutor();
            var service = new RecycleBinService(executor, probe: probe);

            DeleteResult result = service.Delete(new DeleteRequest(target, "过程物"), Confirmed(_root));

            Assert.Empty(executor.RecycleCalls);
            Assert.Equal(DeleteBlockReason.TargetIsReparsePoint, result.Outcomes[0].BlockReason);
            Assert.True(Directory.Exists(target));
        }

        // ---------- 度量预览（不删除） ----------

        [Fact]
        public void 度量_预览只算大小不删除()
        {
            WriteFile(@"out\inner\a.txt", "0123456789");
            WriteFile(@"out\inner\b.txt", "ABCDEF");
            string target = MakeDirectory(@"out\inner");
            var service = new RecycleBinService(new FakeDeleteExecutor());

            DeleteMeasurement measurement = service.MeasureTarget(target);

            Assert.True(measurement.Determined);
            Assert.True(measurement.IsDirectory);
            Assert.Equal(2, measurement.EntryCount);
            Assert.Equal(16L, measurement.TotalBytes);
            Assert.True(File.Exists(Path.Combine(target, "a.txt")), "预览只读，不能动文件");
            Assert.True(Directory.Exists(target));
        }

        [Fact]
        public void 度量_单个文件按一条记录()
        {
            string file = WriteFile(@"out\a.txt", "0123456789");
            var service = new RecycleBinService(new FakeDeleteExecutor());

            DeleteMeasurement measurement = service.MeasureTarget(file);

            Assert.True(measurement.Determined);
            Assert.False(measurement.IsDirectory);
            Assert.Equal(1, measurement.EntryCount);
            Assert.Equal(10L, measurement.TotalBytes);
        }

        [Fact]
        public void 度量_目标不存在时标记为没量到()
        {
            var service = new RecycleBinService(new FakeDeleteExecutor());

            DeleteMeasurement measurement = service.MeasureTarget(PathOf(@"out\ghost.7z"));

            Assert.False(measurement.Determined);
        }

        // ---------- 日志 ----------

        [Fact]
        public void 日志_成功时含路径理由条目数与总大小()
        {
            WriteFile(@"out\inner\a.txt", "0123456789");
            WriteFile(@"out\inner\b.txt", "ABCDEF");
            string target = MakeDirectory(@"out\inner");
            var sink = new RecordingLogSink();
            var service = new RecycleBinService(new FakeDeleteExecutor(), sink);

            DeleteResult result = service.Delete(new DeleteRequest(target, "内层归档与分卷"), Confirmed(_root));

            DeleteLogEntry entry = Assert.Single(sink.Entries);
            Assert.Single(result.LogEntries);
            Assert.True(entry.Success);
            Assert.Equal(target, entry.Path);
            Assert.Equal("内层归档与分卷", entry.Reason);
            Assert.Equal(2, entry.EntryCount);
            Assert.Equal(16L, entry.TotalBytes);
            Assert.Contains(target, entry.ToDisplayText(), StringComparison.Ordinal);
            Assert.Contains("16", entry.ToDisplayText(), StringComparison.Ordinal);
        }

        [Fact]
        public void 日志_被拒绝的目标同样留日志且写明原因()
        {
            string target = WriteFile(@"out\a.txt", "A");
            var sink = new RecordingLogSink();
            var service = new RecycleBinService(new FakeDeleteExecutor(), sink);

            service.Delete(
                new DeleteRequest(target, "过程物"),
                new DeleteOptions { AllowedRoot = _root, UserConfirmed = false });

            DeleteLogEntry entry = Assert.Single(sink.Entries);
            Assert.False(entry.Success);
            Assert.Equal(DeleteBlockReason.NoConfirmation, entry.BlockReason);
            Assert.Contains("确认", entry.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(target));
        }

        [Fact]
        public void 日志_落点抛异常不影响删除结果()
        {
            string target = WriteFile(@"out\a.txt", "A");
            var sink = new RecordingLogSink { ThrowOnWrite = true };
            var service = new RecycleBinService(new FakeDeleteExecutor(), sink);

            DeleteResult result = service.Delete(new DeleteRequest(target, "过程物"), Confirmed(_root));

            Assert.Equal(1, result.SuccessCount);
            Assert.Single(result.LogEntries);
            Assert.False(File.Exists(target));
        }

        // ---------- 回收站执行器：可用性不可用时绝不调用 Shell ----------

        [Fact]
        public void 回收站执行器_可用性探针说不时就只返回失败且文件不动()
        {
            string target = WriteFile(@"out\a.txt", "A");
            var executor = new ShellDeleteExecutor(new FakeAvailabilityProbe("测试用：回收站被策略禁用", usable: false));

            RecycleAttemptResult result = executor.TryMoveToRecycleBin(target, isDirectory: false, out string message);

            Assert.Equal(RecycleAttemptResult.Unavailable, result);
            Assert.Contains("策略", message, StringComparison.Ordinal);
            Assert.True(File.Exists(target), "回收站不可用时绝不能动文件");
        }

        [Fact]
        public void 回收站执行器_路径过长时失败而不是改成永久删除()
        {
            // 超长且不存在的路径：8.3 短名也拿不到 → 必须是"失败 + 说明"，而不是永久删除。
            string tooLong = Path.Combine(_root, new string('x', 200), new string('y', 200), "a.txt");
            var executor = new ShellDeleteExecutor(new FakeAvailabilityProbe("可用", usable: true));

            RecycleAttemptResult result = executor.TryMoveToRecycleBin(tooLong, isDirectory: false, out string message);

            Assert.Equal(RecycleAttemptResult.Failed, result);
            Assert.Contains("路径过长", message, StringComparison.Ordinal);
            Assert.Contains("未改用彻底删除", message, StringComparison.Ordinal);
        }

        /// <summary>可脚本化的可用性探针（真实实现要查盘类型 / 策略 / 回收站，测试里不碰这些）。</summary>
        private sealed class FakeAvailabilityProbe : IRecycleBinAvailabilityProbe
        {
            private readonly string _reason;
            private readonly bool _usable;

            public FakeAvailabilityProbe(string reason, bool usable)
            {
                _reason = reason;
                _usable = usable;
            }

            public bool IsRecycleBinUsable(string fullPath, out string reason)
            {
                reason = _reason;
                return _usable;
            }
        }
    }
}
