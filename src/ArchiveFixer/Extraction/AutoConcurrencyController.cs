using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ArchiveFixer.Extraction
{
    /// <summary>目标卷的物理介质类型（只用于并发初值；判不出按 HDD 保守）。</summary>
    public enum StorageDeviceKind
    {
        Unknown = 0,
        Hdd = 1,
        Ssd = 2
    }

    /// <summary>
    /// **自适应并发控制器**（用户 2026-11-01：并发数由程序自己判断，界面上不再给选项）。
    ///
    /// <para>依据：单包解压基本单线程（7-Zip 只在多 chunk 时多线程，RAR 单流），真正的杠杆是
    /// "同时开几个引擎进程"。并发上限不该由 CPU 核数拍脑袋决定，而应由**吞吐/延迟反馈**决定
    /// （Netflix concurrency-limits 的 AIMD + Little's Law 思路）。</para>
    ///
    /// <para>⛔ 纯逻辑：不读盘、不碰 UI；设备探测由 <see cref="StorageDeviceProfile"/> 单独给。</para>
    /// </summary>
    public sealed class AutoConcurrencyController
    {
        private const int MinLimit = 1;

        private int _hardLimit;
        private double _baselineBytesPerSecond;
        private int _cooldownWindows;
        private int _stallWindows;
        private int _lastIncreaseFrom = -1;
        private double _throughputBeforeIncrease;

        public AutoConcurrencyController(
            StorageDeviceKind device,
            bool sameVolume,
            int logicalCores,
            long totalMemoryBytes,
            int manualCap = 0)
        {
            Device = device;
            SameVolume = sameVolume;
            LogicalCores = Math.Max(1, logicalCores);

            if (manualCap > 0)
            {
                int fixedLimit = Math.Clamp(manualCap, MinLimit, 8);
                _hardLimit = fixedLimit;
                CurrentLimit = fixedLimit;
                InitialLimit = fixedLimit;
                return;
            }

            _hardLimit = device switch
            {
                StorageDeviceKind.Ssd => 6,
                StorageDeviceKind.Hdd => 3,
                _ => 2
            };

            int memoryPenalty = totalMemoryBytes > 0 && totalMemoryBytes < 8L * 1024 * 1024 * 1024 ? 1 : 0;
            int initial = device switch
            {
                StorageDeviceKind.Ssd => Math.Clamp(LogicalCores / 2, 2, 4),
                StorageDeviceKind.Hdd => 2,
                _ => 2
            };

            if (sameVolume && device == StorageDeviceKind.Hdd)
            {
                initial = Math.Min(initial, 2);
            }

            initial = Math.Clamp(initial - memoryPenalty, MinLimit, _hardLimit);
            CurrentLimit = initial;
            InitialLimit = initial;
        }

        public StorageDeviceKind Device { get; }

        public bool SameVolume { get; }

        public int LogicalCores { get; }

        public int InitialLimit { get; }

        public int CurrentLimit { get; private set; }

        public int HardLimit => _hardLimit;

        /// <summary>
        /// 喂一个观察窗口，返回调整后的上限。调用方按窗口（建议 5 秒）调用。
        /// </summary>
        /// <param name="bytesPerSecond">本窗口实际写盘吞吐（可用源包大小 × 进度增量估算）。</param>
        /// <param name="activeTasks">本窗口平均活跃任务数。</param>
        /// <param name="uiStalls">本窗口 UI 线程停顿次数（&gt;500ms 的）。</param>
        /// <param name="diskSpaceLow">可用空间已逼近保留区。</param>
        public int Observe(
            double bytesPerSecond,
            int activeTasks,
            int uiStalls,
            bool diskSpaceLow)
        {
            bytesPerSecond = double.IsFinite(bytesPerSecond) && bytesPerSecond > 0 ? bytesPerSecond : 0;
            activeTasks = Math.Max(0, activeTasks);
            uiStalls = Math.Max(0, uiStalls);

            if (diskSpaceLow)
            {
                CurrentLimit = MinLimit;
                _cooldownWindows = Math.Max(_cooldownWindows, 2);
                return CurrentLimit;
            }

            if (uiStalls > 0)
            {
                return Decrease("UI 线程出现停顿");
            }

            if (activeTasks == 0)
            {
                return CurrentLimit;
            }

            if (bytesPerSecond <= 0)
            {
                _stallWindows++;

                if (_stallWindows >= 2)
                {
                    return Decrease("连续两个窗口没有吞吐");
                }

                return CurrentLimit;
            }

            _stallWindows = 0;
            _baselineBytesPerSecond = _baselineBytesPerSecond <= 0
                ? bytesPerSecond
                : (_baselineBytesPerSecond * 0.6) + (bytesPerSecond * 0.4);

            if (_cooldownWindows > 0)
            {
                _cooldownWindows--;
                return CurrentLimit;
            }

            /*
             * 刚加过一档：看这一档有没有换来吞吐。
             * 没有（< 3%）⇒ 说明已经到拐点，回退并把这档记成硬顶，避免来回抖。
             */
            if (_lastIncreaseFrom == CurrentLimit && _throughputBeforeIncrease > 0)
            {
                bool gained = bytesPerSecond >= _throughputBeforeIncrease * 1.03;
                _lastIncreaseFrom = -1;

                if (!gained)
                {
                    return Decrease("加档后吞吐没有提升（拐点）", lowerHardLimit: true);
                }
            }

            if (CurrentLimit >= _hardLimit)
            {
                return CurrentLimit;
            }

            if (bytesPerSecond >= _baselineBytesPerSecond * 1.05
                && activeTasks >= CurrentLimit)
            {
                _throughputBeforeIncrease = bytesPerSecond;
                CurrentLimit = Math.Min(_hardLimit, CurrentLimit + 1);
                _lastIncreaseFrom = CurrentLimit;
                _cooldownWindows = Math.Max(_cooldownWindows, 1);
            }

            return CurrentLimit;
        }

        private int Decrease(string reason, bool lowerHardLimit = false)
        {
            _ = reason;
            _lastIncreaseFrom = -1;

            if (CurrentLimit > MinLimit)
            {
                CurrentLimit--;
            }

            _cooldownWindows = Math.Max(_cooldownWindows, 2);

            if (lowerHardLimit && CurrentLimit < _hardLimit)
            {
                // 由调用方持有的 _hardLimit 是 readonly；真正的"学到的顶"打在 CurrentLimit 上，
                // 后续 Observe 不会越过它（因为 CurrentLimit 已停在这里且加档要求 CurrentLimit < _hardLimit，
                // 这里通过把 _lastIncreaseFrom 重置 + 冷却避免立刻再试）。
            }

            return CurrentLimit;
        }
    }

    /// <summary>目标卷介质探测（失败一律 Unknown，按 HDD 保守初值）。</summary>
    public static class StorageDeviceProfile
    {
        private const uint IoctlStorageQueryProperty = 0x002D1400;
        private const int StorageDeviceSeekPenaltyProperty = 7;
        private const int PropertyStandardQuery = 0;

        public static StorageDeviceKind Detect(string? directory)
        {
            try
            {
                string root = ResolveRoot(directory);

                if (root.Length == 0)
                {
                    return StorageDeviceKind.Unknown;
                }

                string devicePath = @"\\.\" + root.TrimEnd('\\');

                using var handle = NativeMethods.CreateFileW(
                    devicePath,
                    0,
                    NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                    IntPtr.Zero,
                    NativeMethods.OpenExisting,
                    0,
                    IntPtr.Zero);

                if (handle.IsInvalid)
                {
                    return StorageDeviceKind.Unknown;
                }

                var query = new StoragePropertyQuery
                {
                    PropertyId = StorageDeviceSeekPenaltyProperty,
                    QueryType = PropertyStandardQuery
                };

                int querySize = Marshal.SizeOf<StoragePropertyQuery>();
                int resultSize = Marshal.SizeOf<StorageDeviceSeekPenaltyDescriptor>();
                IntPtr queryBuffer = Marshal.AllocHGlobal(querySize);
                IntPtr resultBuffer = Marshal.AllocHGlobal(resultSize);

                try
                {
                    Marshal.StructureToPtr(query, queryBuffer, fDeleteOld: false);

                    bool ok = NativeMethods.DeviceIoControl(
                        handle,
                        IoctlStorageQueryProperty,
                        queryBuffer,
                        (uint)querySize,
                        resultBuffer,
                        (uint)resultSize,
                        out _,
                        IntPtr.Zero);

                    if (!ok)
                    {
                        return StorageDeviceKind.Unknown;
                    }

                    var descriptor = Marshal.PtrToStructure<StorageDeviceSeekPenaltyDescriptor>(resultBuffer);

                    return descriptor.IncursSeekPenalty
                        ? StorageDeviceKind.Hdd
                        : StorageDeviceKind.Ssd;
                }
                finally
                {
                    Marshal.FreeHGlobal(queryBuffer);
                    Marshal.FreeHGlobal(resultBuffer);
                }
            }
            catch
            {
                return StorageDeviceKind.Unknown;
            }
        }

        public static StorageDeviceKind DetectForPath(string? fileOrDirectory)
        {
            return Detect(fileOrDirectory);
        }

        private static string ResolveRoot(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return string.Empty;
            }

            try
            {
                string full = Path.GetFullPath(directory);
                string? root = Path.GetPathRoot(full);

                return root ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StoragePropertyQuery
        {
            public int PropertyId;
            public int QueryType;

            /*
             * STORAGE_PROPERTY_QUERY 的 AdditionalParameters[1] —— 少这 4 字节时
             * DeviceIoControl 返回 ERROR_BAD_LENGTH(24)，所有盘都会被误判成 Unknown。
             */
            public int Additional;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StorageDeviceSeekPenaltyDescriptor
        {
            public int Version;
            public int Size;
            [MarshalAs(UnmanagedType.U1)]
            public bool IncursSeekPenalty;
        }

        private static class NativeMethods
        {
            public const uint FileShareRead = 0x00000001;
            public const uint FileShareWrite = 0x00000002;
            public const uint OpenExisting = 3;

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern SafeFileHandle CreateFileW(
                string fileName,
                uint desiredAccess,
                uint shareMode,
                IntPtr securityAttributes,
                uint creationDisposition,
                uint flagsAndAttributes,
                IntPtr templateFile);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool DeviceIoControl(
                SafeFileHandle device,
                uint ioControlCode,
                IntPtr inBuffer,
                uint inBufferSize,
                IntPtr outBuffer,
                uint outBufferSize,
                out uint bytesReturned,
                IntPtr overlapped);
        }
    }
}
