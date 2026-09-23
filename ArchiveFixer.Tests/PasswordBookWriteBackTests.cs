using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 「写回密码本…」的测试（用户 2026-09-24 反馈：手动加的密码关掉程序就没了）。
    ///
    /// <para>被测的是两层：<see cref="PasswordBookWriter"/>（纯逻辑：备份 / 编码 / 行尾 / 去重 / 自检 /
    /// 失败时原文件一个字节都不变）与 <see cref="PasswordListViewModel"/> 上那条命令
    /// （确认框列明文、选文件那条分支、成功后的标记消失）。</para>
    ///
    /// <para><b>隐私（AGENTS.md §8）</b>：这里出现的密码全是合成占位符（<c>&lt;示例密码N&gt;</c> 风格），
    /// 路径全部落在系统临时目录下 —— 不含任何真实密码、真实站点名或用户路径。</para>
    /// </summary>
    public sealed class PasswordBookWriteBackTests
    {
        /// <summary>合成密码：带首尾空格，用来钉住"写回不 Trim"。</summary>
        private const string Secret = "<示例密码A>";

        private const string SecretWithSpaces = " <示例密码B> ";

        // ------------------------------------------------------------------ ① 只追加

        [Fact]
        public void 写回_原内容逐字节仍是新文件的前缀()
        {
            using var dir = new TempDir();

            byte[] original = Encoding.UTF8.GetBytes("<示例密码1>\n<示例密码2>\n");
            string book = dir.WriteBytes("book.txt", original);

            var writer = new PasswordBookWriter();

            PasswordBookWriteBackResult result = writer.WriteBack(book, new[] { "<示例密码3>" });

            Assert.True(result.Success, result.FailureReason);
            Assert.Equal(1, result.AppendedCount);

            byte[] after = File.ReadAllBytes(book);

            // 逐字节前缀比对：原文件一个字节都没被改过，新内容全在末尾。
            Assert.True(after.Length > original.Length);
            Assert.Equal(original, after.Take(original.Length).ToArray());

            // 追加部分的换行沿用原文件（这里是 LF），末尾同样以换行收尾。
            string appended = Encoding.UTF8.GetString(after, original.Length, after.Length - original.Length);
            Assert.Equal("<示例密码3>\n", appended);
        }

        [Fact]
        public void 写回_原文件末尾没有换行时先补一个_两条不会粘成一行()
        {
            using var dir = new TempDir();

            string book = dir.WriteText("book.txt", "<示例密码1>", new UTF8Encoding(false));

            var writer = new PasswordBookWriter();

            PasswordBookWriteBackResult result = writer.WriteBack(book, new[] { "<示例密码2>" });

            Assert.True(result.Success, result.FailureReason);

            // 老内容与老内容之间仍然是一行一条：直接按行找那两条，不许出现 "<示例密码1><示例密码2>"。
            string[] lines = File.ReadAllLines(book);

            Assert.Equal(new[] { "<示例密码1>", "<示例密码2>" }, lines);

            var parsed = PasswordBookParser.ParseFile(book);

            Assert.Equal(2, parsed.Entries.Count);
            Assert.Equal("<示例密码1>", parsed.Entries[0].Password);
            Assert.Equal("<示例密码2>", parsed.Entries[1].Password);
        }

        // ------------------------------------------------------------------ ② 编码不变

        [Fact]
        public void 写回_GBK文件追加后仍是GBK()
        {
            if (!TryGetGb18030(out Encoding? gb18030))
            {
                // 这台机器缺代码页支持：如实跳过（写回那边会回退成 UTF-8 并走自检失败，另有别的用例覆盖）。
                return;
            }

            using var dir = new TempDir();

            string book = dir.WriteText("gbk.txt", "包A：<示例密码1>\r\n<示例密码2>\r\n", gb18030!);

            byte[] before = File.ReadAllBytes(book);

            var writer = new PasswordBookWriter();

            PasswordBookWriteBackResult result = writer.WriteBack(book, new[] { "<示例密码3>" });

            Assert.True(result.Success, result.FailureReason);

            byte[] after = File.ReadAllBytes(book);

            // 前缀逐字节不变；新追加的那一段必须能用 GB18030 读出来（不是 UTF-8 写进去的乱码）。
            Assert.Equal(before, after.Take(before.Length).ToArray());

            string appended = gb18030!.GetString(after, before.Length, after.Length - before.Length);

            Assert.Contains("<示例密码3>", appended, StringComparison.Ordinal);

            // 整份文件按 GB18030 读得全（含新加的那条）。
            string whole = gb18030.GetString(after);

            Assert.Contains("<示例密码1>", whole, StringComparison.Ordinal);
            Assert.Contains("<示例密码3>", whole, StringComparison.Ordinal);
            Assert.DoesNotContain('\uFFFD', whole);
        }

        [Fact]
        public void 写回_UTF8带BOM的文件追加后仍然带BOM()
        {
            using var dir = new TempDir();

            string book = dir.WriteText("bom.txt", "<示例密码1>\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            byte[] before = File.ReadAllBytes(book);

            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, before.Take(3).ToArray());

            var writer = new PasswordBookWriter();

            PasswordBookWriteBackResult result = writer.WriteBack(book, new[] { "<示例密码2>" });

            Assert.True(result.Success, result.FailureReason);

            byte[] after = File.ReadAllBytes(book);

            // BOM 只有一个，而且仍然在最前面（追加不会又插一个 BOM 进去）。
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, after.Take(3).ToArray());
            Assert.Empty(FindBomOffsets(after, skipFirst: 3));
            Assert.Equal(before, after.Take(before.Length).ToArray());
        }

        // ------------------------------------------------------------------ ③ 行尾不变

        [Fact]
        public void 写回_CRLF文件追加后仍是CRLF()
        {
            AssertLineEndingPreserved("\r\n");
        }

        [Fact]
        public void 写回_LF文件追加后仍是LF()
        {
            AssertLineEndingPreserved("\n");
        }

        // ------------------------------------------------------------------ ④ 备份

        [Fact]
        public void 写回_备份文件存在且与原文逐字节相同()
        {
            using var dir = new TempDir();

            byte[] original = Encoding.UTF8.GetBytes("<示例密码1>\n<示例密码2>\n");
            string book = dir.WriteBytes("book.txt", original);

            var writer = new PasswordBookWriter();

            PasswordBookWriteBackResult result = writer.WriteBack(book, new[] { "<示例密码3>" });

            Assert.True(result.Success, result.FailureReason);
            Assert.NotEqual(string.Empty, result.BackupPath);

            // 备份名：同目录 + 原名 + .bak-yyyyMMdd-HHmmss。
            string expectedPattern = "^" + Regex.Escape(Path.GetFileName(book)) + @"\.bak-\d{8}-\d{6}(\.\d+)?$";

            Assert.Matches(expectedPattern, Path.GetFileName(result.BackupPath));
            Assert.Equal(dir.Path, Path.GetDirectoryName(result.BackupPath));

            Assert.True(File.Exists(result.BackupPath));
            Assert.Equal(original, File.ReadAllBytes(result.BackupPath));

            // 同目录下只有这一份备份。
            Assert.Single(Directory.GetFiles(dir.Path, "*.bak-*"));
        }

        // ------------------------------------------------------------------ ⑤ 去重

        [Fact]
        public void 写回_文件里已经有的值不再追加_第二次写回0条()
        {
            using var dir = new TempDir();

            string book = dir.WriteText("book.txt", "<示例密码1>\n", new UTF8Encoding(false));

            var writer = new PasswordBookWriter();

            PasswordBookWriteBackResult first = writer.WriteBack(book, new[] { "<示例密码2>" });

            Assert.Equal(1, first.AppendedCount);
            Assert.Single(Directory.GetFiles(dir.Path, "*.bak-*"));

            byte[] afterFirst = File.ReadAllBytes(book);

            PasswordBookWriteBackResult second = writer.WriteBack(book, new[] { "<示例密码2>", "<示例密码1>" });

            Assert.True(second.Success, second.FailureReason);
            Assert.Equal(0, second.AppendedCount);
            Assert.Equal(0, second.SkippedEmptyCount);
            Assert.Equal(2, second.AlreadyPresentCount);
            Assert.Equal(string.Empty, second.BackupPath);

            // 一条都没写：不备份、文件逐字节不变。
            Assert.Equal(afterFirst, File.ReadAllBytes(book));
            Assert.Single(Directory.GetFiles(dir.Path, "*.bak-*"));
        }

        [Fact]
        public void 写回_待写清单也认映射式右侧的值()
        {
            using var dir = new TempDir();

            string book = dir.WriteText("book.txt", "包A：<示例密码1>\n", new UTF8Encoding(false));

            var writer = new PasswordBookWriter();

            // 映射式那一行整行不是这个值，但冒号右侧就是它 —— 不该被重复追加成一行列表式。
            Assert.Empty(writer.FindPending(book, new[] { "<示例密码1>" }));

            // 换个没出现过的值：待写清单里能看见它。
            Assert.Equal(new[] { "<示例密码2>" }, writer.FindPending(book, new[] { "<示例密码2>" }));
        }

        // ------------------------------------------------------------------ ⑥ 写完能读回来

        [Fact]
        public void 写回_写完用解析器能读出全部条目含新加的()
        {
            using var dir = new TempDir();

            string book = dir.WriteText(
                "book.txt",
                "# 注释行不算条目\n包A：<示例密码1>\n<示例密码2>\n",
                new UTF8Encoding(false));

            var writer = new PasswordBookWriter();

            PasswordBookWriteBackResult result = writer.WriteBack(
                book,
                new[] { Secret, SecretWithSpaces });

            Assert.True(result.Success, result.FailureReason);
            Assert.Equal(2, result.AppendedCount);

            var parsed = PasswordBookParser.ParseFile(book);
            var values = parsed.Entries.Select(entry => entry.Password).ToList();

            // 原有两条（映射式 + 列表式）都还在。
            Assert.Contains("<示例密码1>", values);
            Assert.Contains("<示例密码2>", values);

            // 新加的两条读得回来，而且**首尾空格原样保留**（写回不 Trim）。
            Assert.Contains(Secret, values);
            Assert.Contains(SecretWithSpaces, values);
            Assert.Equal(4, parsed.Entries.Count);
        }

        // ------------------------------------------------------------------ ⑦ 失败时原文件不许变

        [Fact]
        public void 写回_只读文件_失败且原文件逐字节未变()
        {
            using var dir = new TempDir();

            byte[] original = Encoding.UTF8.GetBytes("<示例密码1>\n");
            string book = dir.WriteBytes("book.txt", original);

            File.SetAttributes(book, FileAttributes.ReadOnly);

            try
            {
                var writer = new PasswordBookWriter();

                PasswordBookWriteBackResult result = writer.WriteBack(book, new[] { "<示例密码2>" });

                Assert.False(result.Success, "只读文件必须明确失败，不许假装成功");
                Assert.NotEqual(string.Empty, result.FailureReason);
                Assert.Equal(0, result.AppendedCount);

                // 一个字节都不许变。
                Assert.Equal(original, File.ReadAllBytes(book));

                /*
                 * 备份是允许留下的：只读**不影响复制**，备份已经先做出来了，
                 * 失败原因里也点名了它（"备份文件在：…"），用户正好拿它核对。
                 * 这里钉住的是"这份备份与原文逐字节相同"—— 它必须是一份可信的回退点。
                 */
                string backup = Assert.Single(Directory.GetFiles(dir.Path, "*.bak-*"));

                Assert.Equal(original, File.ReadAllBytes(backup));
                Assert.Contains(Path.GetFileName(backup), result.FailureReason, StringComparison.Ordinal);
            }
            finally
            {
                File.SetAttributes(book, FileAttributes.Normal);
            }
        }

        [Fact]
        public void 写回_文件被别的进程占着写_失败且原文件逐字节未变()
        {
            using var dir = new TempDir();

            byte[] original = Encoding.UTF8.GetBytes("<示例密码1>\n");
            string book = dir.WriteBytes("book.txt", original);

            // 独占打开：追加那条路拿不到写权限（"被占用"这条分支）。
            using (var hold = new FileStream(book, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var writer = new PasswordBookWriter();

                PasswordBookWriteBackResult result = writer.WriteBack(book, new[] { "<示例密码2>" });

                Assert.False(result.Success, "被占用时必须失败。实际：" + Describe(result));
                Assert.NotEqual(string.Empty, result.FailureReason);
            }

            Assert.Equal(original, File.ReadAllBytes(book));
        }

        [Fact]
        public void 写回_目标文件不存在_失败且不新建任何文件()
        {
            using var dir = new TempDir();

            string missing = Path.Combine(dir.Path, "not-there.txt");

            var writer = new PasswordBookWriter();

            PasswordBookWriteBackResult result = writer.WriteBack(missing, new[] { "<示例密码1>" });

            Assert.False(result.Success);
            Assert.NotEqual(string.Empty, result.FailureReason);

            // 写回的目标是"用户本来就有的那份密码本"，绝不替他凭空造一个空文件出来。
            Assert.False(File.Exists(missing));
            Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
        }

        // ------------------------------------------------------------------ ⑧ 空密码 / 计数诚实

        [Fact]
        public void 写回_空密码被跳过并如实计数()
        {
            using var dir = new TempDir();

            string book = dir.WriteText("book.txt", "<示例密码1>\n", new UTF8Encoding(false));

            var writer = new PasswordBookWriter();

            PasswordBookWriteBackResult result = writer.WriteBack(
                book,
                new[] { string.Empty, "<示例密码2>", string.Empty });

            Assert.True(result.Success, result.FailureReason);
            Assert.Equal(1, result.AppendedCount);
            Assert.Equal(2, result.SkippedEmptyCount);
            Assert.Equal(2, result.SkippedCount);

            // 文件里只有一条新内容：空密码没有写进去（写进去没有意义，而且容易被误用）。
            string[] lines = File.ReadAllLines(book);

            Assert.Equal(new[] { "<示例密码1>", "<示例密码2>" }, lines);
        }

        [Fact]
        public void 写回_纯空格不算空密码_但密码本读不回来所以如实跳过()
        {
            using var dir = new TempDir();

            string book = dir.WriteText("book.txt", "<示例密码1>\n", new UTF8Encoding(false));

            var writer = new PasswordBookWriter();

            /*
             * 只含空格的密码**不算空密码**（AGENTS.md §9.1：不 Trim），但密码本解析器把
             * "只有空白字符的行"当排版空行跳过 —— 写进去等于写一条下次启动读不回来的内容。
             * 所以它必须被跳过，而且**如实计入跳过数**（不是悄悄写进去，也不是硬报一条失败）。
             */
            PasswordBookWriteBackResult result = writer.WriteBack(book, new[] { "   " });

            Assert.True(result.Success, result.FailureReason);
            Assert.Equal(0, result.AppendedCount);
            Assert.Equal(0, result.SkippedEmptyCount);
            Assert.Equal(1, result.SkippedBlankCount);
            Assert.Equal(1, result.SkippedCount);

            // 一条都没写：文件逐字节不变，也不留备份。
            Assert.Equal(new[] { "<示例密码1>" }, File.ReadAllLines(book));
            Assert.Empty(Directory.GetFiles(dir.Path, "*.bak-*"));
        }

        // ------------------------------------------------------------------ ⑨ 明文不进结论 / 日志

        [Fact]
        public void 写回_返回结论里搜不到密码明文()
        {
            using var dir = new TempDir();

            string book = dir.WriteText("book.txt", "<示例密码1>\n", new UTF8Encoding(false));

            var writer = new PasswordBookWriter();

            PasswordBookWriteBackResult result = writer.WriteBack(book, new[] { Secret, SecretWithSpaces });

            Assert.True(result.Success, result.FailureReason);

            AssertNoPlaintext(Describe(result), Secret, SecretWithSpaces);

            /*
             * 失败那条路也一样：结论里只有原因与备份路径。
             *
             * 这里必须用一个**还没写进去**的值（已经写进去的值会被去重挡在前面，压根不会去动文件），
             * 失败原因用"文件被独占占着"制造 —— 只读属性不行：备份只是复制，复制是允许的，
             * 那条路上失败的是追加（见「只读文件」那个用例）。
             */
            using var hold = new FileStream(book, FileMode.Open, FileAccess.Read, FileShare.Read);

            PasswordBookWriteBackResult failed = writer.WriteBack(book, new[] { Secret + "<未写回>" });

            Assert.False(failed.Success, "被独占占用时必须失败。实际：" + Describe(failed));
            AssertNoPlaintext(Describe(failed), Secret, SecretWithSpaces);
        }

        // ------------------------------------------------------------------ ⑩ 界面：选文件分支 + 标记消失 + 日志无明文

        [Fact]
        public void 写回_没配置密码本路径时走选择文件分支并记住路径()
        {
            using var harness = new Harness();

            string book = harness.WritePasswordBook("<示例密码1>\n");

            harness.PasswordService.AddPassword(Secret);

            // 设置里**没有**密码本路径 → 必须问一次（用既有的文件对话框服务，不另造一个）。
            harness.Dialog.ConfirmResult = true;
            harness.Dialog.ChosenFile = book;

            var viewModel = harness.CreateViewModel();

            Assert.True(viewModel.WriteBackPasswordsCommand.CanExecute(null));

            viewModel.WriteBackPasswordsCommand.Execute(null);

            Assert.True(harness.Dialog.OpenFileDialogCalled, "没有配置路径时必须问用户选哪个文件");

            // 选完就记住（与「导入 txt」同一条路）：设置项 + 侧车文件都写上。
            AppSettings saved = harness.LoadSettings();

            Assert.Equal(book, saved.PasswordBookPath);
            Assert.True(File.Exists(Path.Combine(harness.DataRoot, "password-book.path")));
            Assert.Equal(book, File.ReadAllText(Path.Combine(harness.DataRoot, "password-book.path")));

            // 确认框里逐条列出了**明文**（界面上给他看是应该的）。
            Assert.Contains(Secret, harness.Dialog.LastConfirmDetail, StringComparison.Ordinal);

            // 正文只有数量与文件名：正文会被无界面宿主的降级日志记下来。
            Assert.DoesNotContain(Secret, harness.Dialog.LastConfirmMessage, StringComparison.Ordinal);
            Assert.Contains(Path.GetFileName(book), harness.Dialog.LastConfirmMessage, StringComparison.Ordinal);
        }

        [Fact]
        public void 写回_确认后标记消失_并且日志里搜不到明文()
        {
            using var harness = new Harness();

            string book = harness.WritePasswordBook("<示例密码1>\n");

            harness.PasswordService.AddPassword(Secret);

            harness.SavePasswordBookPath(book);

            harness.Dialog.ConfirmResult = true;

            var viewModel = harness.CreateViewModel();

            PasswordItem manual = viewModel.Passwords.Single(item => item.Source == "ManualList");

            Assert.True(manual.ShowUnwrittenMarker, "手动加的条目必须带「未写回」标记");
            Assert.Equal(1, viewModel.UnwrittenManualCount);

            DialogService.ClearFallbackLog();

            viewModel.WriteBackPasswordsCommand.Execute(null);

            // 写进去了：文件里有它，密码本体一个字符都没变（含首尾空格那一条也照写）。
            Assert.Contains(Secret, File.ReadAllLines(book));

            // 标记消失 + 按钮跟着置灰。
            Assert.False(manual.ShowUnwrittenMarker, "写回成功后标记必须消失");
            Assert.Equal(0, viewModel.UnwrittenManualCount);
            Assert.False(viewModel.WriteBackPasswordsCommand.CanExecute(null));

            // 提示条如实说清：写了几条 / 写到哪 / 备份在哪。
            Assert.Contains("1", viewModel.Message, StringComparison.Ordinal);
            Assert.Contains(Path.GetFileName(book), viewModel.Message, StringComparison.Ordinal);
            Assert.Contains("备份", viewModel.NoticeText, StringComparison.Ordinal);

            // 窗口列表里不该出现明文；日志与降级日志里更不该有（§8 隐私红线）。
            AssertNoPlaintext(viewModel.Message + "\n" + viewModel.NoticeText, Secret);

            string logText = harness.ReadAllLogText();

            AssertNoPlaintext(logText, Secret);
            AssertNoPlaintext(string.Join("\n", DialogService.FallbackLog), Secret);
        }

        [Fact]
        public void 写回_用户取消_什么都不做且原文件一个字节都不变()
        {
            using var harness = new Harness();

            string book = harness.WritePasswordBook("<示例密码1>\n");

            harness.PasswordService.AddPassword(Secret);
            harness.SavePasswordBookPath(book);

            harness.Dialog.ConfirmResult = false;

            var viewModel = harness.CreateViewModel();

            byte[] before = File.ReadAllBytes(book);

            viewModel.WriteBackPasswordsCommand.Execute(null);

            Assert.Equal(before, File.ReadAllBytes(book));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(book)!, "*.bak-*"));

            // 没写回，标记还在，按钮仍然可点。
            Assert.Equal(1, viewModel.UnwrittenManualCount);
            Assert.True(viewModel.WriteBackPasswordsCommand.CanExecute(null));
        }

        [Fact]
        public void 写回_写完之后又改了这条密码的值_标记会重新出现()
        {
            using var harness = new Harness();

            string book = harness.WritePasswordBook("<示例密码1>\n");

            harness.PasswordService.AddPassword(Secret);
            harness.SavePasswordBookPath(book);

            harness.Dialog.ConfirmResult = true;

            var viewModel = harness.CreateViewModel();

            viewModel.WriteBackPasswordsCommand.Execute(null);

            Assert.Equal(0, viewModel.UnwrittenManualCount);

            // 改成一个**并不在文件里**的新值：它重新变成"未写回"（标记的判据是按值的，不是按条目的）。
            PasswordItem manual = viewModel.Passwords.Single();
            manual.Value = "<示例密码C>";

            viewModel.ReloadFromService();

            Assert.Equal(1, viewModel.UnwrittenManualCount);
            Assert.True(viewModel.Passwords.Single(item => item.Value == "<示例密码C>").ShowUnwrittenMarker);
        }

        [Fact]
        public void 写回_没有手工条目时命令不可用()
        {
            using var harness = new Harness();

            string book = harness.WritePasswordBook("<示例密码1>\n<示例密码2>\n");

            harness.PasswordService.ImportPasswordList(book);

            var viewModel = harness.CreateViewModel();

            // 导入进来的本来就在文件里，没有"未写回"这一说。
            Assert.Equal(0, viewModel.UnwrittenManualCount);
            Assert.False(viewModel.WriteBackPasswordsCommand.CanExecute(null));
        }

        // ------------------------------------------------------------------ 助手

        private static void AssertLineEndingPreserved(string newline)
        {
            using var dir = new TempDir();

            string escape = newline == "\r\n" ? "\\r\\n" : "\\n";

            string book = dir.WriteText(
                "book-" + escape.Replace("\\", string.Empty) + ".txt",
                "<示例密码1>" + newline + "<示例密码2>" + newline,
                new UTF8Encoding(false));

            byte[] before = File.ReadAllBytes(book);

            var writer = new PasswordBookWriter();

            PasswordBookWriteBackResult result = writer.WriteBack(book, new[] { "<示例密码3>" });

            Assert.True(result.Success, result.FailureReason);

            byte[] after = File.ReadAllBytes(book);

            Assert.Equal(before, after.Take(before.Length).ToArray());

            string appended = Encoding.UTF8.GetString(after, before.Length, after.Length - before.Length);

            Assert.Equal("<示例密码3>" + newline, appended);

            // 全文里不出现另一种行尾：把本来的那种行尾换成占位符之后，
            // 剩下的文本里不许再有 CR 或 LF（混进别的风格就会露出来）。
            string whole = Encoding.UTF8.GetString(after);
            string withoutOwnStyle = whole.Replace(newline, "\u0001", StringComparison.Ordinal);

            Assert.DoesNotContain('\r', withoutOwnStyle);
            Assert.DoesNotContain('\n', withoutOwnStyle);
        }

        /// <summary>把结论对象的每个字段摊平成一段文本，供"搜不到明文"的断言使用。</summary>
        private static string Describe(PasswordBookWriteBackResult result)
        {
            return string.Join(
                "\n",
                result.Success,
                result.AppendedCount,
                result.SkippedCount,
                result.AlreadyPresentCount,
                result.SkippedEmptyCount,
                result.SkippedDuplicateCount,
                result.TargetPath,
                result.BackupPath,
                result.FailureReason,
                result.IsNoOp);
        }

        private static void AssertNoPlaintext(string text, params string[] secrets)
        {
            foreach (string secret in secrets)
            {
                Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
                Assert.DoesNotContain(secret.Trim(), text, StringComparison.Ordinal);
            }
        }

        private static bool TryGetGb18030(out Encoding? encoding)
        {
            CodePageEncodingBootstrap.EnsureRegistered();

            encoding = null;

            if (!CodePageEncodingBootstrap.IsRegistered)
            {
                return false;
            }

            try
            {
                encoding = Encoding.GetEncoding("GB18030");
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return false;
            }
        }

        private static List<int> FindBomOffsets(byte[] content, int skipFirst)
        {
            var offsets = new List<int>();

            for (int i = skipFirst; i + 2 < content.Length; i++)
            {
                if (content[i] == 0xEF && content[i + 1] == 0xBB && content[i + 2] == 0xBF)
                {
                    offsets.Add(i);
                }
            }

            return offsets;
        }

        /// <summary>
        /// 全套服务都落在临时目录里（数据根 / 设置 / 密码本 / 日志），绝不碰用户的目录与设置（AGENTS.md §8）。
        /// </summary>
        private sealed class Harness : IDisposable
        {
            private readonly TempDir _dir = new();

            public Harness()
            {
                DataRoot = Path.Combine(_dir.Path, "data");
                Directory.CreateDirectory(DataRoot);

                PasswordService = new PasswordService { DataRootDirectory = DataRoot };
                Dialog = new FakeDialogService();

                // 先落一份干净的设置：默认设置里 PasswordBookPath 是空的，
                // 正好走「没配置路径 → 问一次」那条分支（用例自己决定什么时候把它填上）。
                CreateSettingsService().Save(AppSettings.CreateDefault());
            }

            public string DataRoot { get; }

            public PasswordService PasswordService { get; }

            public FakeDialogService Dialog { get; }

            public string WritePasswordBook(string content)
            {
                return _dir.WriteText(
                    "密码本-" + Guid.NewGuid().ToString("N") + ".txt",
                    content,
                    new UTF8Encoding(false));
            }

            public void SavePasswordBookPath(string bookPath)
            {
                SettingsService service = CreateSettingsService();
                AppSettings settings = service.Load();

                settings.PasswordBookPath = bookPath;
                service.Save(settings);
            }

            public AppSettings LoadSettings()
            {
                return CreateSettingsService().Load();
            }

            public PasswordListViewModel CreateViewModel()
            {
                return new PasswordListViewModel(
                    PasswordService,
                    Dialog,
                    new PasswordBookWriter());
            }

            /// <summary>数据根目录下所有日志文件拼起来 —— 里面不该出现任何密码明文。</summary>
            public string ReadAllLogText()
            {
                if (!Directory.Exists(DataRoot))
                {
                    return string.Empty;
                }

                var builder = new StringBuilder();

                foreach (string file in Directory.GetFiles(DataRoot, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        builder.AppendLine(File.ReadAllText(file));
                    }
                    catch (IOException)
                    {
                        // 读不到就不算进这份文本：本用例只关心"能读到的日志里没有明文"。
                    }
                }

                return builder.ToString();
            }

            public void Dispose()
            {
                _dir.Dispose();
            }

            private SettingsService CreateSettingsService()
            {
                return new SettingsService(new PathService { DataRootDirectory = DataRoot });
            }
        }

        /// <summary>
        /// 假对话框：只记下"被问了什么"，并把答案设成用例想要的那个。
        ///
        /// <para>为什么需要它：无界面宿主（单测）里 <see cref="DialogService.ShowConfirm(string, string, bool, string, out bool)"/>
        /// 一律返回 false（没人点过 = 不执行），写回那条路就永远走不到真正写入。所以那个方法在
        /// <see cref="DialogService"/> 上是 <c>virtual</c> 的。</para>
        /// </summary>
        private sealed class FakeDialogService : DialogService
        {
            public bool ConfirmResult { get; set; }

            public string ChosenFile { get; set; } = string.Empty;

            public bool OpenFileDialogCalled { get; private set; }

            public string LastConfirmMessage { get; private set; } = string.Empty;

            public string LastConfirmDetail { get; private set; } = string.Empty;

            public string LastError { get; private set; } = string.Empty;

            public override bool ShowConfirm(
                string message,
                string optionText,
                bool optionCheckedByDefault,
                string detail,
                out bool optionChecked)
            {
                LastConfirmMessage = message;
                LastConfirmDetail = detail;
                optionChecked = false;

                return ConfirmResult;
            }

            public override string ShowOpenSingleFileDialog(
                string title = "选择文件",
                string filter = "所有文件 (*.*)|*.*")
            {
                OpenFileDialogCalled = true;

                return ChosenFile;
            }

            public override void ShowError(string message)
            {
                LastError = message;
            }
        }

        /// <summary>每个用例自建临时目录，Dispose 时整目录删掉（只读属性先摘掉，否则删不掉）。</summary>
        private sealed class TempDir : IDisposable
        {
            public TempDir()
            {
                Path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "ArchiveFixerPwWriteBack",
                    Guid.NewGuid().ToString("N"));

                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public string WriteText(string relativePath, string content, Encoding encoding)
            {
                string full = System.IO.Path.Combine(Path, relativePath);

                File.WriteAllText(full, content, encoding);

                return full;
            }

            public string WriteBytes(string relativePath, byte[] content)
            {
                string full = System.IO.Path.Combine(Path, relativePath);

                File.WriteAllBytes(full, content);

                return full;
            }

            public void Dispose()
            {
                try
                {
                    if (!Directory.Exists(Path))
                    {
                        return;
                    }

                    foreach (string file in Directory.GetFiles(Path, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }

                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException)
                {
                    // 临时目录在系统 temp 下，删不掉不影响测试结论。
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
