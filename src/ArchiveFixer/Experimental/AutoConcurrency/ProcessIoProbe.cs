// STATUS: 未完成 / 暂停（2026-11-05）。自动并发实验代码，不接入产品路径。
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ArchiveFixer.Experimental.AutoConcurrency
{
    /// <summary>
    /// 当前进程的 I/O 吞吐代理：用 GetProcessIoCounters 的读写传输字节差值估算 MB/s。
    /// 它不是磁盘队列长度，也不判断 HDD/SSD；只给 CapacityPolicy 一个“读写是不是停了”的信号。
    /// </summary>
    public static class ProcessIoProbe
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        private static readonly object Gate = new();
        private static ulong _lastBytes;
        private static long _lastTimestamp;
        private static bool _hasSample;

        public static bool TryGetMegabytesPerSecond(out double megabytesPerSecond)
        {
            megabytesPerSecond = 0;

            if (!GetProcessIoCounters(GetCurrentProcess(), out IoCounters counters))
            {
                return false;
            }

            ulong bytes = counters.ReadTransferCount + counters.WriteTransferCount;
            long now = Stopwatch.GetTimestamp();

            lock (Gate)
            {
                if (!_hasSample || _lastTimestamp <= 0 || now <= _lastTimestamp)
                {
                    _lastBytes = bytes;
                    _lastTimestamp = now;
                    _hasSample = true;
                    return false;
                }

                double seconds = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
                ulong delta = bytes >= _lastBytes ? bytes - _lastBytes : 0;
                _lastBytes = bytes;
                _lastTimestamp = now;

                if (seconds <= 0)
                {
                    return false;
                }

                megabytesPerSecond = delta / (1024d * 1024d) / seconds;
                return true;
            }
        }

        internal static void ResetForTests()
        {
            lock (Gate)
            {
                _lastBytes = 0;
                _lastTimestamp = 0;
                _hasSample = false;
            }
        }
    }
}
