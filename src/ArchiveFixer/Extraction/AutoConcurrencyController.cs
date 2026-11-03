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
