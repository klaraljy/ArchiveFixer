using System;
using System.IO;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 夹具/前置条件缺失时的**显式跳过登记**。
    ///
    /// <para>
    /// 背景（2026-11-10 审计）：19 处测试在前置条件不满足时直接 <c>return</c>，
    /// xUnit 把它记成 **通过** —— 报表上"跳过"和"真的验过"长得一模一样，这是最危险的一种假绿。
    /// </para>
    ///
    /// <para>
    /// xUnit 2.9 没有运行时跳过 API（<c>Assert.Skip</c> 是 v3 才有的），项目又规定不新增 NuGet 依赖，
    /// 所以这里落一条**可统计的台账**：每条跳过写一行到台账文件，跑批脚本据此单独报"跳过 N 条"，
    /// 不再混进通过数。
    /// </para>
    ///
    /// <para>
    /// ⛔ 它只登记，不改变控制流：调用方照旧 <c>return</c>。
    /// </para>
    /// </summary>
    internal static class FixtureGate
    {
        private static readonly object Sync = new();

        /// <summary>台账文件位置（可用环境变量覆盖，默认系统临时目录）。</summary>
        internal static string LedgerPath =>
            Environment.GetEnvironmentVariable("ARCHIVEFIXER_SKIP_LEDGER") is { Length: > 0 } custom
                ? custom
                : Path.Combine(Path.GetTempPath(), "ArchiveFixerTests", "skipped.log");

        /// <summary>登记一次跳过。</summary>
        /// <param name="where">哪条用例 / 哪个方法。</param>
        /// <param name="reason">为什么跳过（要能让读报表的人看懂）。</param>
        internal static void Skip(string where, string reason)
        {
            try
            {
                string path = LedgerPath;
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                lock (Sync)
                {
                    File.AppendAllText(path, where + "\t" + reason + Environment.NewLine);
                }
            }
            catch
            {
                // 登记失败不能把用例搞红。
            }
        }
    }
}
