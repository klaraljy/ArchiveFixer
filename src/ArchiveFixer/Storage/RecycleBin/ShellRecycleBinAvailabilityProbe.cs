using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using ArchiveFixer.Helpers;
using ArchiveFixer.Security;
using Microsoft.Win32;

namespace ArchiveFixer.Storage
{    /// <summary>
     /// 用系统策略 / 卷类型 / 回收站查询判断可用性。
     ///
     /// 为什么要**先判断**再调 Shell：<c>SHFileOperation</c> 的 <c>FOF_ALLOWUNDO</c> 语义是
     /// "能回收就回收" —— 回收不了的时候它会直接永久删除。那正是我们绝不允许的降级，
     /// 所以判定必须发生在调用之前，而不是事后看结果。
     ///
     /// 判定为不可用时**一律拒绝**（fail closed）：读不到策略也算不可用，
     /// 宁可让用户显式选择"彻底删除"，也不替他猜"应该能回收吧"。
     /// </summary>
    public sealed class ShellRecycleBinAvailabilityProbe : IRecycleBinAvailabilityProbe
    {
        private const string ExplorerPolicySubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

        /// <summary>"不将文件移入回收站，而是直接删除"策略值（资源管理器 → 回收站属性）。</summary>
        private const string NoRecycleFilesValueName = "NoRecycleFiles";

        /// <summary>x64 下 SHQUERYRBINFO 的大小（DWORD + 4 字节填充 + 两个 __int64）。</summary>
        private const int ShQueryRbInfoSizeX64 = 24;

        public bool IsRecycleBinUsable(string fullPath, out string reason)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                reason = "路径为空，无法判断回收站是否可用";
                return false;
            }

            /*
             * Shell 的 SHQUERYRBINFO 在 32 位下是 pack(1)（shellapi.h：
             * `#if !defined(_WIN64) #include <pshpack1.h>`），与 C# 的默认布局不同。
             * 与其在 32 位进程里赌一个结构布局，不如直接判定"回收站档不可用"（fail closed）：
             * 本产品按 64 位分发，32 位进程下用户仍可显式选择彻底删除。
             */
            if (IntPtr.Size != 8)
            {
                reason = "当前不是 64 位进程，回收站接口不可用（保守拒绝，可改用彻底删除）";
                return false;
            }

            // UNC 路径（\\server\share）一定没有回收站；网络盘即使映射成盘符也一样。
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                reason = "目标是网络共享路径（UNC），系统不会为它保留回收站，已拒绝（不会改为永久删除）";
                return false;
            }

            string? volumeRoot;

            try
            {
                volumeRoot = Path.GetPathRoot(Path.GetFullPath(fullPath));
            }
            catch (Exception ex)
            {
                ArchiveFixer.Helpers.AppDiagnostics.Problem("回收站", "降级处理", ex);
                reason = $"路径无法解析，无法判断回收站是否可用：{ex.Message}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(volumeRoot))
            {
                reason = "取不到目标所在卷，无法判断回收站是否可用";
                return false;
            }

            try
            {
                var drive = new DriveInfo(volumeRoot);

                if (!drive.IsReady)
                {
                    reason = $"卷未就绪（{volumeRoot}），回收站不可用";
                    return false;
                }

                if (drive.DriveType == DriveType.Network)
                {
                    reason = $"目标是网络盘（{volumeRoot}），系统不会为它保留回收站，已拒绝（不会改为永久删除）";
                    return false;
                }

                if (drive.DriveType == DriveType.CDRom || drive.DriveType == DriveType.NoRootDirectory)
                {
                    reason = $"卷类型（{drive.DriveType}）不支持回收站";
                    return false;
                }
            }
            catch (Exception ex)
            {
                ArchiveFixer.Helpers.AppDiagnostics.Problem("回收站", "降级处理", ex);
                reason = $"无法确定目标所在卷（{ex.Message}），不能确认回收站可用";
                return false;
            }

            if (!IsPolicyAllowingRecycleBin(out string policyReason))
            {
                reason = policyReason;
                return false;
            }

            if (!TryQueryRecycleBin(volumeRoot, out string queryReason))
            {
                reason = queryReason;
                return false;
            }

            reason = "回收站可用";
            return true;
        }

        /// <summary>
        /// "不经过回收站"策略是否被打开。读不到注册表时返回 false（不可用）并说明原因 ——
        /// 属于 fail closed：无法确认"文件会被回收"时，就不做这次回收站删除。
        /// </summary>
        private static bool IsPolicyAllowingRecycleBin(out string reason)
        {
            // 两个位置都查（用户策略 + 机器策略），任意一个说"不经过回收站"就按不可用处理。
            // 连 Registry.CurrentUser / LocalMachine 这两个属性访问都放在 try 里：
            // 受限环境下取注册表根本身就可能抛，而那也必须收敛成"不可用"，不能漏出去变成一次删除。
            foreach (bool machineScope in new[] { false, true })
            {
                try
                {
                    RegistryKey root = machineScope ? Registry.LocalMachine : Registry.CurrentUser;

                    using RegistryKey? key = root.OpenSubKey(ExplorerPolicySubKey);
                    object? value = key?.GetValue(NoRecycleFilesValueName);

                    if (value is int flag && flag != 0)
                    {
                        reason = $"系统策略已设置\"删除时不将文件移入回收站\"（{NoRecycleFilesValueName}=1），" +
                                 "回收站删除不可用，已拒绝（不会改为永久删除）";
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    ArchiveFixer.Helpers.AppDiagnostics.Problem("回收站", "降级处理", ex);
                    reason = $"无法读取系统策略（{ex.Message}），不能确认回收站可用，已拒绝（不会改为永久删除）";
                    return false;
                }
            }

            reason = "没有禁止使用回收站的策略";
            return true;
        }

        /// <summary>问系统"这个卷有没有回收站"。失败 = 没有（不猜成有）。</summary>
        private static bool TryQueryRecycleBin(string volumeRoot, out string reason)
        {
            int size = Marshal.SizeOf<SHQUERYRBINFO>();

            if (size != ShQueryRbInfoSizeX64)
            {
                // 结构布局与预期不符（例如有人把它改成 Pack=1）：不拿一个可疑的结构去调系统。
                reason = "回收站信息结构与预期不符，无法查询回收站状态，已拒绝";
                return false;
            }

            try
            {
                var info = new SHQUERYRBINFO { cbSize = size };
                int hr = SHQueryRecycleBinW(volumeRoot, ref info);

                if (hr != 0)
                {
                    reason = $"系统报告该卷没有可用的回收站（结果 0x{hr:X8}），已拒绝（不会改为永久删除）";
                    return false;
                }
            }
            catch (Exception ex)
            {
                ArchiveFixer.Helpers.AppDiagnostics.Problem("回收站", "降级处理", ex);
                reason = $"查询回收站状态失败（{ex.Message}），不能确认回收站可用";
                return false;
            }

            reason = "该卷有可用的回收站";
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBinW(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);
    }

}
