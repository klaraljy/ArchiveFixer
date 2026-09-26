using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 死锁回归：在"单线程同步上下文 + 该线程被同步阻塞"下，引擎调用与递归解压必须能跑完。
    ///
    /// 为什么这个用例必须存在（历史"卡死"的真凶）：
    /// 管线是从 WPF 的 UI 线程开始的，而 <c>RecursiveExtractor</c> 判断"展开比"时会调用引擎列目录。
    /// 旧写法是 <c>ListAsync(...).GetAwaiter().GetResult()</c> —— UI 线程被占住，
    /// 而 7z 进程退出后的续体要回到**同一个**被阻塞的线程，两边互等：
    /// 界面完全无响应、没有弹窗、没有 7z 子进程、CPU 不忙。默认配置（SingleLayer）下不触发，
    /// 一旦打开递归就必现，所以它长期没被复现。
    ///
    /// 做法：自造一个"只有一条线程"的 <see cref="SynchronizationContext"/>，
    /// 在这条线程上同步等待异步操作（这正是 WPF 上 <c>AsyncRelayCommand</c> 之外那些
    /// "同步点按钮"的效果），并对等待设上限 —— 死锁时**用例失败而不是把测试进程挂死**。
    /// </summary>
    public class AsyncDeadlockGuardTests : IDisposable
    {
        private readonly string _root;

        public AsyncDeadlockGuardTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerDeadlock", Guid.NewGuid().ToString("N"));
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
                // 临时目录删不掉不影响结论，下次跑换一个 GUID 目录。
            }
        }

        [Fact]
        public void 引擎列目录_在单线程同步上下文里同步等待_不会死锁()
        {
            string sevenZip = LocateSevenZip();
            string archive = BuildZip(sevenZip, "中文条目.txt", "内容\n");

            var runner = new SevenZipProcessRunner(new ToolLocator());

            bool completed = RunBlockedOnSingleThreadedContext(() =>
            {
                // 旧写法（引擎层没有 ConfigureAwait(false)）在这里与续体互等。
                ArchiveOperationResult result = runner
                    .RunSevenZipAsync(new[] { "l", "-slt", "-sccUTF-8", archive })
                    .GetAwaiter()
                    .GetResult();

                return result.Success;
            }, TimeSpan.FromSeconds(30), out bool success);

            Assert.True(completed, "引擎调用在单线程同步上下文里同步等待时没有返回（sync-over-async 死锁）");
            Assert.True(success, "7z l 应当成功");
        }

        [Fact]
        public void 递归解压_在单线程同步上下文里同步等待_不会死锁()
        {
            string sevenZip = LocateSevenZip();
            string archive = BuildZip(sevenZip, "readme.txt", "单层包\n");

            var engine = new SevenZipEngine(new SevenZipProcessRunner(new ToolLocator()));
            var extractor = new ArchiveFixer.Extraction.RecursiveExtractor(
                engine,
                new ArchiveFixer.Services.MagicArchiveProber(),
                _ => new[] { string.Empty });

            string output = Path.Combine(_root, "out");

            bool completed = RunBlockedOnSingleThreadedContext(() =>
            {
                // 旧写法：CheckLimitsBeforeLayer → IsExpansionRatioExceeded 里同步等 ListAsync。
                ArchiveFixer.Extraction.RecursionResult result = extractor
                    .ExtractAsync(new ArchiveTask(archive), output, ArchiveFixer.Extraction.RecursionMode.SingleChain)
                    .GetAwaiter()
                    .GetResult();

                return result.Completed;
            }, TimeSpan.FromSeconds(30), out bool success);

            Assert.True(completed, "递归解压在单线程同步上下文里同步等待时没有返回（sync-over-async 死锁）");
            Assert.True(success, "单层包应当解压完成");
            Assert.True(File.Exists(Path.Combine(output, "readme.txt")));
        }

        /// <summary>在一条装了单线程同步上下文的专用线程上跑一个同步阻塞的委托；超时即判定没有返回。</summary>
        private static bool RunBlockedOnSingleThreadedContext(
            Func<bool> action,
            TimeSpan timeout,
            out bool result)
        {
            bool completed = false;
            bool actionResult = false;
            Exception? failure = null;

            var context = new SingleThreadSynchronizationContext();
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var worker = new Thread(() =>
            {
                SynchronizationContext? previous = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(context);

                try
                {
                    actionResult = action();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previous);
                    context.Complete();
                    done.TrySetResult(true);
                }
            })
            {
                IsBackground = true,
                Name = "single-threaded-sync-context"
            };

            worker.Start();

            // 消息泵运行在这条被阻塞的线程上：同步上下文把续体排到这里，而调用方正等着 action 返回。
            completed = context.RunMessageLoop(done.Task, timeout);

            result = actionResult;

            if (failure != null)
            {
                throw new InvalidOperationException("单线程上下文里的操作抛异常了：" + failure, failure);
            }

            return completed;
        }

        private string BuildZip(string sevenZip, string entryName, string content)
        {
            string source = Path.Combine(_root, "src_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, entryName), content, new UTF8Encoding(false));

            string archive = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");

            Run7z(sevenZip, "a", "-tzip", archive, Path.Combine(source, entryName));

            return archive;
        }

        private static string LocateSevenZip()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    string candidate = Path.Combine(
                        directory.FullName,
                        "src", "ArchiveFixer",
                        "tools",
                        "7zip",
                        "7z.exe");

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                directory = directory.Parent;
            }

            string local = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe");

            return File.Exists(local) ? local : string.Empty;
        }

        private static void Run7z(string sevenZip, params string[] args)
        {
            if (string.IsNullOrEmpty(sevenZip) || !File.Exists(sevenZip))
            {
                throw new InvalidOperationException(
                    "测试机上没有 7z.exe（ArchiveFixer/tools/7zip/7z.exe），本用例无法运行。");
            }

            var psi = new ProcessStartInfo(sevenZip)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (string arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 7z.exe");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(120_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 已经退了就无所谓。
                }

                throw new InvalidOperationException("7z 造样本超时：" + string.Join(' ', args));
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"7z 失败（exit {process.ExitCode}）：{string.Join(' ', args)}\n{stdout}\n{stderr}");
            }
        }

        /// <summary>
        /// 只有一条线程的同步上下文：<see cref="Post"/> 只把回调排进队列，
        /// 而泵消息的 <see cref="RunMessageLoop"/> 正好运行在那条被阻塞的线程上 —— 与 WPF 的
        /// <c>Dispatcher</c> 同构（UI 线程被 <c>GetResult()</c> 占住时，排队的续体无人执行）。
        /// </summary>
        private sealed class SingleThreadSynchronizationContext : SynchronizationContext
        {
            private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)>
                _queue = new();

            public override void Post(SendOrPostCallback d, object? state)
            {
                try
                {
                    _queue.Add((d, state));
                }
                catch (InvalidOperationException)
                {
                    // 队列已经 Complete：丢弃即可。
                }
            }

            public override void Send(SendOrPostCallback d, object? state)
            {
                // 本用例不需要跨线程 Send；真被调到就同步执行，别把线程卡住。
                d(state);
            }

            /// <summary>
            /// 在当前线程上泵消息，直到 <paramref name="finished"/> 完成或超时。返回"是否正常结束"。
            ///
            /// 结束条件用"任务完成 + 队列已空"而不是哨兵值：
            /// <see cref="Post"/> 的续体可能排在"任务完成"之后（<c>RunContinuationsAsynchronously</c>），
            /// 见哨兵就退出会把最后几个续体丢掉。
            /// </summary>
            public bool RunMessageLoop(Task finished, TimeSpan timeout)
            {
                var deadline = DateTime.UtcNow + timeout;

                while (DateTime.UtcNow < deadline)
                {
                    if (_queue.TryTake(out (SendOrPostCallback Callback, object? State) item, 50))
                    {
                        item.Callback(item.State);
                        continue;
                    }

                    if (finished.IsCompleted && _queue.Count == 0)
                    {
                        return true;
                    }
                }

                return false;
            }

            public void Complete()
            {
                try
                {
                    _queue.CompleteAdding();
                }
                catch (ObjectDisposedException)
                {
                    // 已经关了。
                }
            }
        }
    }
}
