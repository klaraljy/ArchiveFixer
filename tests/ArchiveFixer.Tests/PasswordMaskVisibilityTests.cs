using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using System;
using System.IO;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// **脱敏不许把"试的是第几项"吃掉**（用户 2026-10-04 真机）。
    ///
    /// <para>现场：真机日志第 61 行只剩「开始解压，密码候选 3/10，尝试密码 ******」——
    /// 产出方（<c>PasswordService.BuildTryPasswordLogText</c>）写的本来是
    /// 「尝试密码列表第 3 项：******」（**密码本体已经脱敏**），
    /// 可脱敏器那条 <c>尝试密码\s*[^\r\n]+</c> 把整段描述一起擦掉了 ⇒
    /// 日志 / 屏幕 / 导出三处都看不出"试的是第几项、什么来源"，
    /// 用户"为什么试这么多次"就没有答案。</para>
    ///
    /// <para>口径：**只擦密码本体**（描述 + <c>：******</c> 那个形状原样保留）；
    /// 形状对不上的一律照旧整行擦掉 —— 兜底一寸都不放松。</para>
    /// </summary>
    public class PasswordMaskVisibilityTests : IDisposable
    {
        private readonly string _root;

        public PasswordMaskVisibilityTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerMaskVisibility", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // 临时目录清不掉不影响结论。
            }
        }

        /// <summary>
        /// 产出方写的那一行**过完脱敏器之后描述还在** —— 三处（日志 / 屏幕 / 导出）走的是同一个
        /// <see cref="PasswordMasker"/> / <c>LogService.Sanitize</c>。
        ///
        /// <para><b>红检</b>：把 <c>PasswordMasker</c> 那条规则改回 <c>尝试密码\s*[^\r\n]+</c>
        /// （或把 <c>LogService</c> 里那份抄一遍的规则加回去）⇒ 本用例当场红
        /// （`Assert.Contains() Failure: 尝试密码列表第 3 项`）。</para>
        /// </summary>
        [Fact]
        public void 脱敏保留第几项与来源_只擦密码本体()
        {
            // ⚠ 产出方写的就是"描述：******"（密码本体在写日志之前就已经脱敏了）。
            const string line = "开始解压，密码候选 3/10，尝试密码列表第 3 项：******";

            string masked = PasswordMasker.Sanitize(line);

            Assert.Contains("尝试密码列表第 3 项", masked, StringComparison.Ordinal);
            Assert.DoesNotContain("尝试密码 ******", masked, StringComparison.Ordinal);

            // 屏幕/导出那条路（LogService.Sanitize 先调 PasswordMasker，再跑自己那几条规则）：
            // ⛔ 它过去又抄了一遍同样的规则，把描述二次擦掉了。
            var logService = new LogService(new PathService { DataRootDirectory = _root });

            string viaLog = logService.Sanitize(line);

            Assert.Contains("尝试密码列表第 3 项", viaLog, StringComparison.Ordinal);

            // 别的来源描述同样要留住。
            Assert.Contains(
                "尝试手动密码第 2 项",
                PasswordMasker.Sanitize("开始解压，密码候选 2/10，尝试手动密码第 2 项：******"),
                StringComparison.Ordinal);
        }

        /// <summary>
        /// **兜底不许放松**：形状对不上（真有明文夹在"尝试密码"后面）时，照旧整行擦掉。
        ///
        /// <para><b>红检</b>：把那条规则的负向断言撤掉（改成无条件保留描述）⇒ 本用例当场红。</para>
        /// </summary>
        [Fact]
        public void 形状对不上时照旧整行擦掉()
        {
            Assert.DoesNotContain(
                "<示例密码>",
                PasswordMasker.Sanitize("尝试密码 <示例密码>"),
                StringComparison.Ordinal);

            Assert.DoesNotContain(
                "<示例密码>",
                PasswordMasker.Sanitize("尝试密码：<示例密码>"),
                StringComparison.Ordinal);

            // 多行（导出全部日志时是整份文件一起过脱敏）也不能漏。
            string multi = "第一行\n尝试密码列表第 3 项：******\n尝试密码 <示例密码>\n";
            string maskedMulti = PasswordMasker.Sanitize(multi);

            Assert.Contains("尝试密码列表第 3 项", maskedMulti, StringComparison.Ordinal);
            Assert.DoesNotContain("<示例密码>", maskedMulti, StringComparison.Ordinal);
        }

        /// <summary>
        /// **④页表头与页内说明必须如实描述真实顺序**（用户 2026-10-04 真机 + 当天第二条口述）：
        /// 实际顺序 = 空密码 → 密码本命中 → **这张表（严格按表里顺序）**；
        /// 而"按历史成功次数自动排序"这一档已被用户明确否掉。
        ///
        /// <para><b>红检</b>：把列头/提示改回"密码（自上而下依次尝试）"那种说法
        /// （或者把"按成功次数排序"写回去）⇒ 本用例当场红。</para>
        /// </summary>
        [Fact]
        public void 四页表头如实描述真实顺序_不许再说按成功次数排()
        {
            Assert.Contains("严格按本表顺序", StatusText.PasswordListOrderHeader, StringComparison.Ordinal);
            Assert.DoesNotContain("成功次数", StatusText.PasswordListOrderHeader, StringComparison.Ordinal);

            // 页内说明要写清"先试空密码"这一档（程序不偷偷重排，而是如实写明）。
            Assert.Contains("先试一次空密码", StatusText.PasswordListPrivacyHint, StringComparison.Ordinal);

            // ⛔ 用户明确否掉的那一档不许再出现在界面上。
            Assert.DoesNotContain("按历史成功次数", StatusText.PasswordListPrivacyHint, StringComparison.Ordinal);

            // 列头文案从 StatusText 取（XAML 里不许再写一份）。
            string xaml = File.ReadAllText(Path.Combine(
                RepositoryRoot(),
                "src", "ArchiveFixer", "Views", "PasswordListWindow.xaml"));

            Assert.Contains("StatusText.PasswordListOrderHeader", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("自上而下依次尝试", xaml, StringComparison.Ordinal);
        }

        /// <summary>仓库根（按 ArchiveFixer.slnx 定位）——与别的 XAML 扫描用例同一套办法。</summary>
        private static string RepositoryRoot()
        {
            DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ArchiveFixer.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            return AppContext.BaseDirectory;
        }
    }
}
