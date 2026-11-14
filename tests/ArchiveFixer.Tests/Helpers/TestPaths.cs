using System;
using System.IO;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 真实夹具的根路径统一出口。
    ///
    /// <para>
    /// 背景（2026-11-10）：测试里散着二十多处写死的 <c>H:\BaiduNetdiskDownload\...</c>。
    /// 那块外接盘坏掉之后，这批用例整片变红，而且红得完全看不出是"环境没了"还是"代码坏了"。
    /// 收敛到这里之后：换盘只改一个环境变量，夹具缺失的用例可以明确跳过并写原因。
    /// </para>
    ///
    /// <para>
    /// ⚠ 这只是**路径出口**，不改变任何判据：夹具在的时候该怎么断言还怎么断言。
    /// </para>
    /// </summary>
    internal static class TestPaths
    {
        private const string DefaultFixtureRoot = @"H:\BaiduNetdiskDownload\困难大测试";

        /// <summary>夹具根（原物、备份、测试区都在它下面）。</summary>
        internal static string FixtureRoot =>
            Environment.GetEnvironmentVariable("ARCHIVEFIXER_FIXTURE_ROOT") is { Length: > 0 } fromEnv
                ? fromEnv
                : DefaultFixtureRoot;

        /// <summary>旧造包器的备份目录（<c>大测试备份位置</c>）。</summary>
        internal static string BackupRoot => Path.Combine(FixtureRoot, "大测试备份位置");

        /// <summary>旧造包器的测试区（<c>大测试</c>）。</summary>
        internal static string TestAreaRoot => Path.Combine(FixtureRoot, "大测试");

        /// <summary>指定位置根（<c>大测试指定位置</c>）。</summary>
        internal static string SpecifiedRoot => Path.Combine(FixtureRoot, "大测试指定位置");

        /// <summary>日志根（<c>大测试日志</c>）。</summary>
        internal static string LogsRoot => Path.Combine(FixtureRoot, "大测试日志");

        /// <summary>
        /// 测试自己的工作目录：**不放夹具盘**，放系统临时目录。
        /// 写到坏盘上只会让用例莫名其妙变红。
        /// </summary>
        internal static string TempRoot => Path.Combine(Path.GetTempPath(), "ArchiveFixerTests");

        /// <summary>夹具不存在时给一句能看懂的话（用在 Skip/Assert 的说明里）。</summary>
        internal static string MissingFixture(string path) =>
            "夹具缺失：" + path + "（设置 ARCHIVEFIXER_FIXTURE_ROOT 指向一份完整夹具）";
    }
}
