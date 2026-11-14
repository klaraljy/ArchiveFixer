using System;
using System.Runtime.InteropServices;

namespace ArchiveFixer.Detection;

/// <summary>
/// 文件物理身份（卷序列号 + FileId + 硬链接数）的唯一读取口 —— 只回答"这两个路径是不是同一份数据"。
///
/// <para><b>为什么单独成层</b>：物理同一性是六条证据里唯一需要 <c>kernel32</c> 的一条，
/// 而它要挡的是"同一份数据被数成两卷"（硬链接 / 同一个文件的两个名字）—— 这是组错与不可逆删除的
/// 入口之一，不能混在文案/排序里。句柄只在这一层开、也只在这一层关。</para>
///
/// <para><b>与 <see cref="FileIdentity"/> 的分工</b>：那一位回答"两条路径是不是同一份"，一次只取一件事；
/// 这一条证据要的是"卷序列号 + FileId + 硬链接数"三件**同源**事实（同一句话里既报重复又报链接数），
/// 所以在这里用**一次句柄**读齐 —— ⛔ 不拆成两次打开（那会让每卷多开一次句柄，判定闸门是热路径）。</para>
///
/// <para><b>失败模型</b>：打不开（不存在 / 被占 / 权限 / 路径非法）、读不到
/// <c>ByHandleFileInformation</c>、任何异常 ⇒ 一律 <c>false</c> + 三个 0，**绝不抛**；
/// 调用方据此把"物理同一性"这一条记成"取不到"（⛔ 不是记成"成立"）。</para>
///
/// <para>生命周期：纯静态、无状态；每次调用现开现关（<c>FILE_FLAG_BACKUP_SEMANTICS</c> 允许目录句柄），
/// 不缓存句柄、不缓存结果 —— 判定的那一刻必须与磁盘同步。</para>
/// </summary>
internal static class Win32FileId
{
    /// <summary>读"卷序列号 + FileId + 硬链接数"；读不到 ⇒ <c>false</c>（三个输出都是 0），绝不抛。</summary>
    public static bool TryGet(string path, out uint volumeSerial, out ulong fileIndex, out uint linkCount)
    {
        volumeSerial = 0;
        fileIndex = 0;
        linkCount = 0;

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
            linkCount = info.NumberOfLinks;

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
