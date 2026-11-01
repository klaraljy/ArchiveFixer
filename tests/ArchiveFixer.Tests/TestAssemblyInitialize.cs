using ArchiveFixer.Extraction;
using ArchiveFixer.Services;
using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 测试宿主的**一次性初始化**（xunit 起来之前跑，每个测试程序集一次）。
    ///
    /// <para>这里做两件事：</para>
    /// <list type="number">
    /// <item><description>显式打开"没人配过工作区根时回落到 <c>%TEMP%</c>"那一档
    /// （生产默认关；判据见 <see cref="RecursiveExtractor.AllowSystemTempWorkspaceFallback"/>）；</description></item>
    /// <item><description><b>本进程的默认数据根改到临时目录</b>：2026-11-01 真机，全量单测里
    /// 92 处裸 <c>new PasswordService()</c>、UI 探针的 <c>new MainWindow()</c> 都在往
    /// 用户真实的 <c>%LOCALAPPDATA%\ArchiveFixer\password-list.dat</c> 写测试密码。
    /// 这里给测试进程设 <c>ARCHIVEFIXER_DATA_ROOT</c>（生产不设），任何漏网之鱼最多写到临时目录。</description></item>
    /// </list>
    /// </summary>
    internal static class TestAssemblyInitialize
    {
        /// <summary>产品里那个属性的默认值 —— 在本初始化**打开开关之前**读到的那个。</summary>
        internal static bool ProductDefault { get; private set; }

        /// <summary>本测试进程专用的临时数据根。</summary>
        internal static string TestDataRoot { get; } = Path.Combine(
            Path.GetTempPath(),
            "ArchiveFixerTestDataRoot",
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        private static bool _previousFallback;

        [ModuleInitializer]
        internal static void Initialize()
        {
            _previousFallback = RecursiveExtractor.AllowSystemTempWorkspaceFallback;
            ProductDefault = _previousFallback;

            RecursiveExtractor.AllowSystemTempWorkspaceFallback = true;

            try
            {
                Directory.CreateDirectory(TestDataRoot);
            }
            catch
            {
                // 临时目录建不出来也不阻断测试；后续写盘自然失败。
            }

            Environment.SetEnvironmentVariable("ARCHIVEFIXER_DATA_ROOT", TestDataRoot);

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    if (Directory.Exists(TestDataRoot))
                    {
                        Directory.Delete(TestDataRoot, recursive: true);
                    }
                }
                catch
                {
                    // 进程退出时清不掉不影响结论。
                }
            };
        }
    }
}
