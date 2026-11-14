using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ArchiveFixer.Storage
{    /// <summary>
     /// 用 Shell API（<c>SHFileOperationW</c>）把目标移入回收站；彻底删除走 .NET 的
     /// <see cref="File.Delete(string)"/> / <see cref="Directory.Delete(string, bool)"/>。
     ///
     /// 三个实现要点，都是踩过的坑，别改回去：
     /// ① **先判可用性再调用**：<c>FOF_ALLOWUNDO</c> 是"能回收就回收"，回收不了会**直接永久删除**；
     /// ② 默认带 <c>FOF_WANTNUKEWARNING</c>：万一系统仍要永久删除，它会弹系统警告让用户确认 ——
     ///    宁可多一次系统提示，也不允许静默永久删除（代价：无人值守时会等用户点一下）；
     /// ③ 长路径：<c>SHFileOperation</c> 不接受 <c>\\?\</c> 前缀，超过 MAX_PATH 时退到 8.3 短名，
     ///    短名也拿不到就**失败**（不改成永久删除）。
     ///
     /// ⚠ 已知限制（不假装解决）：若系统无法回收而用户在 <c>FOF_WANTNUKEWARNING</c> 的警告里
     /// 选择了永久删除，这次删除同样是"成功"，本方法无法把它与真正进回收站区分开 ——
     /// 但那已经是用户在系统提示下的显式选择，不属于"静默降级"。
     /// </summary>
    public sealed class ShellDeleteExecutor : IDeleteExecutor
    {
        /// <summary>MAX_PATH。超过它的路径 SHFileOperation 处理不了。</summary>
        private const int MaxShellPathLength = 260;

        private const uint FoDelete = 0x0003;

        private const ushort FofSilent = 0x0004;
        private const ushort FofNoConfirmation = 0x0010;
        private const ushort FofAllowUndo = 0x0040;
        private const ushort FofNoErrorUi = 0x0400;
        private const ushort FofWantNukeWarning = 0x4000;

        /// <summary>ERROR_CANCELLED：用户在系统提示里点了取消。</summary>
        private const int ErrorCancelled = 1223;

        private const uint CoInitApartmentThreaded = 0x0002;

        private readonly IRecycleBinAvailabilityProbe _availability;
        private readonly bool _warnBeforePermanentDelete;

        public ShellDeleteExecutor(
            IRecycleBinAvailabilityProbe? availability = null,
            bool warnBeforePermanentDelete = true)
        {
            _availability = availability ?? new ShellRecycleBinAvailabilityProbe();
            _warnBeforePermanentDelete = warnBeforePermanentDelete;
        }

        public RecycleAttemptResult TryMoveToRecycleBin(string path, bool isDirectory, out string message)
        {
            string fullPath = SafePathHelper.GetFullPathSafe(path);

            if (string.IsNullOrWhiteSpace(fullPath))
            {
                message = "路径为空，无法移入回收站";
                return RecycleAttemptResult.Failed;
            }

            if (!_availability.IsRecycleBinUsable(fullPath, out string unavailableReason))
            {
                // 这里**只**返回"不可用"，绝不回落到 DeletePermanently（§3.2 清理表）。
                message = unavailableReason;
                return RecycleAttemptResult.Unavailable;
            }

            if (!TryGetShellCompatiblePath(fullPath, out string shellPath, out string pathReason))
            {
                message = pathReason;
                return RecycleAttemptResult.Failed;
            }

            int result = InvokeShellDelete(shellPath, out bool aborted);

            if (result != 0)
            {
                message = $"移入回收站失败：{DescribeShellError(result)}";
                return RecycleAttemptResult.Failed;
            }

            if (aborted)
            {
                message = "删除被取消（可能是系统弹出的\"永久删除\"警告被拒绝），目标未删除";
                return RecycleAttemptResult.Failed;
            }

            if (SafePathHelper.FileExists(fullPath) || SafePathHelper.DirectoryExists(fullPath))
            {
                message = "系统报告成功，但目标仍然存在，按失败处理（不假装删掉了）";
                return RecycleAttemptResult.Failed;
            }

            message = "已移入回收站";
            return RecycleAttemptResult.Recycled;
        }

        public void DeletePermanently(string path, bool isDirectory)
        {
            string fullPath = SafePathHelper.GetFullPathSafe(path);

            if (string.IsNullOrWhiteSpace(fullPath))
            {
                throw new ArgumentException("路径为空，无法彻底删除", nameof(path));
            }

            if (isDirectory)
            {
                // 递归删除。目标子树里的联接点已在 DeleteSafetyGuard 里被拦掉，所以这里不会跟着链接跑出去。
                Directory.Delete(fullPath, recursive: true);
                return;
            }

            File.Delete(fullPath);
        }

        /// <summary>
        /// SHFileOperation 只认 MAX_PATH 以内的常规路径（<c>\\?\</c> 前缀它反而处理不了），
        /// 超长时退到 8.3 短名；拿不到短名就失败，**不**改成永久删除。
        /// </summary>
        private static bool TryGetShellCompatiblePath(string fullPath, out string shellPath, out string reason)
        {
            shellPath = fullPath;
            reason = string.Empty;

            if (fullPath.Length < MaxShellPathLength)
            {
                return true;
            }

            string shortPath = TryGetShortPathName(fullPath);

            if (!string.IsNullOrWhiteSpace(shortPath) && shortPath.Length < MaxShellPathLength)
            {
                shellPath = shortPath;
                return true;
            }

            reason = $"路径过长（{fullPath.Length} 个字符），回收站接口不支持该路径；本次未执行删除，" +
                     "未改用彻底删除（如需删除请显式选择彻底删除，或先把路径改短）";
            return false;
        }

        private static string TryGetShortPathName(string fullPath)
        {
            try
            {
                var buffer = new StringBuilder(MaxShellPathLength + 1);
                uint length = GetShortPathNameW(fullPath, buffer, (uint)buffer.Capacity);

                if (length == 0 || length > buffer.Capacity)
                {
                    return string.Empty;
                }

                return buffer.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private int InvokeShellDelete(string shellPath, out bool aborted)
        {
            aborted = false;

            /*
             * SHFileOperation 属于 Shell API，需要 COM 已经初始化。
             * S_OK / S_FALSE（本线程已初始化过）都表示这次调用"成功占用了引用计数"，
             * 必须配对 CoUninitialize；RPC_E_CHANGED_MODE（负值）表示本线程已按别的模式初始化过，
             * 此时不配对释放，调用照常进行（Shell 会自行处理）。
             */
            int comResult = CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded);
            bool releaseCom = comResult >= 0;

            try
            {
                ushort flags = (ushort)(FofSilent | FofNoConfirmation | FofAllowUndo | FofNoErrorUi);

                if (_warnBeforePermanentDelete)
                {
                    flags |= FofWantNukeWarning;
                }

                var operation = new SHFILEOPSTRUCTW
                {
                    hwnd = IntPtr.Zero,
                    wFunc = FoDelete,

                    // pFrom 是"双重 null 结尾"的路径列表：字符串尾部再加一个 \0，
                    // 加上封送器自己补的结尾 null，正好两个。少一个会让 Shell 读到越界内存。
                    pFrom = shellPath + "\0",
                    pTo = null,
                    fFlags = flags,
                    hNameMappings = IntPtr.Zero,
                    lpszProgressTitle = null
                };

                int result = SHFileOperationW(ref operation);
                aborted = operation.fAnyOperationsAborted;
                return result;
            }
            finally
            {
                if (releaseCom)
                {
                    CoUninitialize();
                }
            }
        }

        /// <summary>把 Shell 的返回码说成人话。返回值可能是 DE_* 码、Win32 错误码或 HRESULT。</summary>
        private static string DescribeShellError(int code)
        {
            if (code == ErrorCancelled)
            {
                return "操作被取消";
            }

            string? named = code switch
            {
                0x71 => "源与目标相同",
                0x72 => "多个源对应一个目标",
                0x73 => "不能把目录移动到其子目录",
                0x74 => "源是根目录",
                0x75 => "操作被取消",
                0x76 => "目标位于源的子树中",
                0x78 => "源被拒绝访问（权限不足或被占用）",
                0x79 => "路径过深",
                0x7A => "目标过多",
                0x7C => "源文件无效或不存在",
                0x7D => "目标与源在同一目录树",
                0x7E => "目标目录中已有同名文件",
                0x80 => "目标是目录，源是文件",
                0x81 => "文件名过长",
                0x82 => "目标是只读介质",
                0x83 => "目标是只读介质",
                0x84 => "目标目录已存在",
                0x85 => "源是只读介质",
                0x86 => "源是只读介质",
                0xB7 => "目标中存在同名文件",
                _ => null
            };

            return named != null
                ? $"{named}（0x{code:X8}）"
                : $"系统错误码 0x{code:X8}";
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCTW
        {
            public IntPtr hwnd;
            public uint wFunc;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string? pFrom;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string? pTo;

            public ushort fFlags;

            [MarshalAs(UnmanagedType.Bool)]
            public bool fAnyOperationsAborted;

            public IntPtr hNameMappings;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetShortPathNameW(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();
    }

}
