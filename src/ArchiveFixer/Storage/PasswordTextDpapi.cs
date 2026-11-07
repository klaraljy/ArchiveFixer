using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ArchiveFixer.Storage
{
    /// <summary>
    /// 内置密码清单的 DPAPI 加密（当前 Windows 用户范围，不弹 UI）。
    /// 与 <see cref="PasswordListStore"/> 的机器范围不同：密码本属于当前用户，换 Windows 用户
    /// 不应能读；代价是换机器/重装系统也解不开，必须靠显式明文导出。
    /// </summary>
    internal static class PasswordTextDpapi
    {
        internal const string ApplicationEntropy = "ArchiveFixer.PasswordText.v1";

        private const int CryptProtectUiForbidden = 0x1;
        private const string Crypt32 = "crypt32.dll";
        private const string Kernel32 = "kernel32.dll";

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport(Crypt32, SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(
            ref DataBlob pDataIn,
            string? szDataDescr,
            ref DataBlob pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            out DataBlob pDataOut);

        [DllImport(Crypt32, SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(
            ref DataBlob pDataIn,
            out IntPtr ppszDataDescr,
            ref DataBlob pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            out DataBlob pDataOut);

        [DllImport(Kernel32)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        internal static byte[] Protect(byte[] plain)
        {
            if (!TryProtect(plain, out byte[] cipher, out int error))
            {
                throw new InvalidOperationException("密码本加密失败（DPAPI 错误码 " + error + "）");
            }

            return cipher;
        }

        internal static byte[] Unprotect(byte[] cipher)
        {
            if (!TryUnprotect(cipher, out byte[] plain, out int error))
            {
                throw new System.Security.Cryptography.CryptographicException(
                    "密码本解密失败（DPAPI 错误码 " + error + "）");
            }

            return plain;
        }

        private static bool TryProtect(byte[] plain, out byte[] cipher, out int win32Error)
        {
            cipher = Array.Empty<byte>();
            win32Error = 0;
            byte[] entropyBytes = Encoding.UTF8.GetBytes(ApplicationEntropy);
            IntPtr dataPtr = IntPtr.Zero;
            IntPtr entropyPtr = IntPtr.Zero;
            DataBlob outBlob = default;

            try
            {
                dataPtr = Marshal.AllocHGlobal(Math.Max(1, plain.Length));
                if (plain.Length > 0)
                {
                    Marshal.Copy(plain, 0, dataPtr, plain.Length);
                }

                entropyPtr = Marshal.AllocHGlobal(entropyBytes.Length);
                Marshal.Copy(entropyBytes, 0, entropyPtr, entropyBytes.Length);

                var dataIn = new DataBlob { cbData = plain.Length, pbData = dataPtr };
                var entropy = new DataBlob { cbData = entropyBytes.Length, pbData = entropyPtr };

                bool ok = CryptProtectData(
                    ref dataIn,
                    null,
                    ref entropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outBlob);

                if (!ok)
                {
                    win32Error = Marshal.GetLastWin32Error();
                    return false;
                }

                cipher = new byte[outBlob.cbData];
                if (outBlob.cbData > 0)
                {
                    Marshal.Copy(outBlob.pbData, cipher, 0, outBlob.cbData);
                }

                return true;
            }
            catch
            {
                win32Error = Marshal.GetLastWin32Error();
                return false;
            }
            finally
            {
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
                if (dataPtr != IntPtr.Zero) Marshal.FreeHGlobal(dataPtr);
                if (entropyPtr != IntPtr.Zero) Marshal.FreeHGlobal(entropyPtr);
            }
        }

        private static bool TryUnprotect(byte[] cipher, out byte[] plain, out int win32Error)
        {
            plain = Array.Empty<byte>();
            win32Error = 0;
            byte[] entropyBytes = Encoding.UTF8.GetBytes(ApplicationEntropy);
            IntPtr cipherPtr = IntPtr.Zero;
            IntPtr entropyPtr = IntPtr.Zero;
            IntPtr descriptionPtr = IntPtr.Zero;
            DataBlob outBlob = default;

            try
            {
                cipherPtr = Marshal.AllocHGlobal(Math.Max(1, cipher.Length));
                if (cipher.Length > 0)
                {
                    Marshal.Copy(cipher, 0, cipherPtr, cipher.Length);
                }

                entropyPtr = Marshal.AllocHGlobal(entropyBytes.Length);
                Marshal.Copy(entropyBytes, 0, entropyPtr, entropyBytes.Length);

                var dataIn = new DataBlob { cbData = cipher.Length, pbData = cipherPtr };
                var entropy = new DataBlob { cbData = entropyBytes.Length, pbData = entropyPtr };

                bool ok = CryptUnprotectData(
                    ref dataIn,
                    out descriptionPtr,
                    ref entropy,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outBlob);

                if (!ok)
                {
                    win32Error = Marshal.GetLastWin32Error();
                    return false;
                }

                plain = new byte[outBlob.cbData];
                if (outBlob.cbData > 0)
                {
                    Marshal.Copy(outBlob.pbData, plain, 0, outBlob.cbData);
                }

                return true;
            }
            catch
            {
                win32Error = Marshal.GetLastWin32Error();
                return false;
            }
            finally
            {
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
                if (descriptionPtr != IntPtr.Zero) LocalFree(descriptionPtr);
                if (cipherPtr != IntPtr.Zero) Marshal.FreeHGlobal(cipherPtr);
                if (entropyPtr != IntPtr.Zero) Marshal.FreeHGlobal(entropyPtr);
            }
        }
    }
}
