using System;
using System.Diagnostics;
using System.Threading;
using ArchiveFixer.Helpers;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 启动期读盘的兜底规则（2026-11-10 真机：坏盘上读一个小 json 把窗口拖死）。
    ///
    /// 这里只钉三件事：快的时候拿真值、慢的时候按默认值放弃、抛异常也按默认值。
    /// ⛔ 它**不承诺中止底层 I/O** —— 线程进了内核等待，谁也叫不回来。
    /// </summary>
    public class SafeIoTests
    {
        [Fact]
        public void 读得快_返回真值()
        {
            int value = SafeIo.Run(() => 42, -1, TimeSpan.FromSeconds(5));

            Assert.Equal(42, value);
        }

        [Fact]
        public void 读得慢_按默认值放弃且不拖住调用方()
        {
            var stopwatch = Stopwatch.StartNew();
            int value = SafeIo.Run(
                () =>
                {
                    Thread.Sleep(2000);
                    return 42;
                },
                -1,
                TimeSpan.FromMilliseconds(80));
            stopwatch.Stop();

            Assert.Equal(-1, value);
            Assert.True(stopwatch.ElapsedMilliseconds < 1500, "超时后必须立刻返回，实际耗时 " + stopwatch.ElapsedMilliseconds + "ms");
        }

        [Fact]
        public void 读盘抛异常_按默认值()
        {
            int value = SafeIo.Run<int>(() => throw new System.IO.IOException("文件或目录损坏且无法读取"), -1);

            Assert.Equal(-1, value);
        }

        [Fact]
        public void 有返回值版本的失败回调会被调用()
        {
            string? message = null;
            SafeIo.Run<int>(() => throw new InvalidOperationException("boom"), 0, null, m => message = m);

            Assert.False(string.IsNullOrEmpty(message));
        }

        [Fact]
        public void 后台任务用线程池线程_不阻塞进程退出()
        {
            bool isBackground = SafeIo.Run(() => Thread.CurrentThread.IsBackground, false, TimeSpan.FromSeconds(5));

            Assert.True(isBackground, "工作线程必须是后台线程，否则主窗口关掉后进程退不出去");
        }
    }
}
