using ArchiveFixer.Extraction;
using System.Runtime.CompilerServices;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 测试宿主的**一次性初始化**（xunit 起来之前跑，每个测试程序集一次）。
    ///
    /// <para>这里只做一件事：显式打开"没人配过工作区根时回落到 <c>%TEMP%</c>"那一档。
    /// 生产默认是**关**的（不变量 12：⛔ 绝不回落程序目录 / C 盘 / <c>%TEMP%</c> ——
    /// 判据与理由见 <see cref="RecursiveExtractor.AllowSystemTempWorkspaceFallback"/>），
    /// 而绝大多数用例只关心"递归能跑起来"、并不跑批首、也就没人配根 ——
    /// 那一档正是为它们存在的（给测试用，不是给生产用）。</para>
    ///
    /// <para>⚠ 顺手把**打开之前**的值记下来（<see cref="ProductDefault"/>）：
    /// 只有这样"产品默认必须是关着的"才是一条**可断言**的事实，
    /// 而不是一句只能靠读代码相信的话（守门用例 <c>WorkspaceTempFallbackGateTests</c>）。</para>
    /// </summary>
    internal static class TestAssemblyInitialize
    {
        /// <summary>产品里那个属性的默认值 —— 在本初始化**打开开关之前**读到的那个。</summary>
        internal static bool ProductDefault { get; private set; }

        [ModuleInitializer]
        internal static void Initialize()
        {
            ProductDefault = RecursiveExtractor.AllowSystemTempWorkspaceFallback;

            RecursiveExtractor.AllowSystemTempWorkspaceFallback = true;
        }
    }
}
