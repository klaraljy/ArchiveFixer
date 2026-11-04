using System;
using System.Runtime.InteropServices;

namespace ArchiveFixer.Services
{
    /// <summary>
    /// 给自适应并发控制器的低开销系统压力采样：系统 CPU 忙闲 + 可用物理内存。
    /// 不读磁盘、不扫目录、不碰 UI；采样失败时返回“未知/充足”，不阻止任务。
    /// </summary>
    public static class SystemResourceProbe
    {
        private static readonly object Gate = new();
        private static ulong _lastIdle;
        private static ulong _lastKernel;
        private static ulong _lastUser;

        public static ConcurrencyResourceSnapshot Snapshot()
        {
            return new ConcurrencyResourceSnapshot(
                ReadCpuBusyPercent(),
                ReadAvailablePhysicalBytes());
        }

        private static double ReadCpuBusyPercent()
        {
            lock (Gate)
            {
                if (!GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user))
                {
                    return 0;
                }

                ulong idleTicks = idle.ToUInt64();
                ulong kernelTicks = kernel.ToUInt64();
                ulong userTicks = user.ToUInt64();

                if (_lastKernel == 0 && _lastUser == 0)
                {
                    _lastIdle = idleTicks;
                    _lastKernel = kernelTicks;
                    _lastUser = userTicks;
                    return 0;
                }

                ulong idleDelta = idleTicks - _lastIdle;
                ulong totalDelta = (kernelTicks - _lastKernel) + (userTicks - _lastUser);
                _lastIdle = idleTicks;
                _lastKernel = kernelTicks;
                _lastUser = userTicks;

                if (totalDelta == 0)
                {
                    return 0;
                }

                double busy = 100.0 * (totalDelta - idleDelta) / totalDelta;
                return Math.Clamp(busy, 0, 100);
            }
        }

        private static long ReadAvailablePhysicalBytes()
        {
            try
            {
                var status = new MemoryStatusEx();
                if (!GlobalMemoryStatusEx(status))
                {
                    return 0;
                }

                return status.AvailPhys > long.MaxValue
                    ? long.MaxValue
                    : (long)status.AvailPhys;
            }
            catch
            {
                return 0;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime
        {
            public uint Low;
            public uint High;

            public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private sealed class MemoryStatusEx
        {
            public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }
    }

    public readonly record struct ConcurrencyResourceSnapshot(
        double CpuBusyPercent,
        long AvailablePhysicalBytes);
}
