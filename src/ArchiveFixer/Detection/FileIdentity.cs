using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ArchiveFixer.Detection
{
    /// <summary>
    /// **"这是不是同一份文件"的唯一判据**（卷序列号 + 文件索引；硬链接双胞胎也算同一份）。
    ///
    /// <para><b>为什么要它</b>（真机 CCCC 2026-10-06 20:55，用户原话：「为什么文件真正的名字不看」）：
    /// 接片那一档会在"这一组该在的那一层"建一个**零字节硬链接**（`…\111\111\111.z03`），
    /// 而用户自己那一份还在他放的那个目录里（`…\111(4)\111.z03`）—— **两条不同的路径指着同一份字节**。
    /// 账（"哪一片借给了谁"）里记的是其中一条路径，回查时拿另一条去比
    /// ⇒ **按路径字符串永远对不上** ⇒ 那一单按红线「判不出 ⇒ 什么都不做」把源片一直留着。
    /// ⛔ 判据必须落在"同一份文件"上，⛔ 不是路径相等、⛔ 不是文件名、⛔ 不是（名字+体积+时间）。</para>
    ///
    /// <para>⛔ 只读、不写、不改名、不删；读不到（文件不在 / 没权限）⇒ 返回 false（判不出就不认）。</para>
    /// </summary>
    internal static class FileIdentity
    {
        /// <summary>两条路径**指向同一份文件**吗（含硬链接双胞胎）。任一条读不到 ⇒ false。</summary>
        public static bool IsSamePhysicalFile(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            if (!TryGetIdentity(left!, out ulong leftVolume, out ulong leftIndex))
            {
                return false;
            }

            return TryGetIdentity(right!, out ulong rightVolume, out ulong rightIndex)
                && leftVolume == rightVolume
                && leftIndex == rightIndex;
        }

        /// <summary>取"卷序列号 + 文件索引"；读不到 ⇒ false。</summary>
        public static bool TryGetIdentity(string path, out ulong volumeSerial, out ulong fileIndex)
        {
            volumeSerial = 0;
            fileIndex = 0;

            IntPtr handle = IntPtr.Zero;

            try
            {
                handle = CreateFileW(
                    path,
                    FileReadAttributes,
                    FileShareRead | FileShareWrite | FileShareDelete,
                    IntPtr.Zero,
                    OpenExisting,
                    FileFlagBackupSemantics,
                    IntPtr.Zero);

                if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                {
                    return false;
                }

                if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info))
                {
                    return false;
                }

                volumeSerial = info.VolumeSerialNumber;
                fileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (handle != IntPtr.Zero && handle != new IntPtr(-1))
                {
                    CloseHandle(handle);
                }
            }
        }

        private const uint FileReadAttributes = 0x0080;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint FileShareDelete = 0x00000004;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(IntPtr hFile, out ByHandleFileInformation lpFileInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
