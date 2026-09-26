using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 用户 2026-09-24 第 21 条：「写回密码本提示成功，**为什么重启了一下，又显示未添加**，
    /// 你到底是不是保存在本地的」。
    ///
    /// <para><b>旧病根</b>：「未写回」标记只看 <see cref="PasswordItem.Source"/> 是不是
    /// <c>ManualList</c>，而落盘的只有**来源** —— "这条值到底在不在密码本文件里"这个事实
    /// 既没被记录、也没被重新判定。写回成功只是当场把内存里的一笔账记上，重启后账没了、
    /// 来源又恢复成 <c>ManualList</c>，标记于是原样长回来。</p>
    ///
    /// <para><b>现在的口径（按值、以文件为准）</b>：标记 = 来源是手动添加 **且** 该值
    /// 不在任何一本**已记住的密码本**里。值集合来自真正解析过的文件（启动合并每本书时记下、
    /// 写回成功后**重新读文件**刷新）。所以"写回成功 → 标记消失 → 重启后重新判定 → 仍然消失"
    /// 是同一个事实的两次查询，不是两套账。</p>
    ///
    /// <para>没有已记住的书 / 书读不到 → 标记**保持**（诚实：程序确实不知道值在不在书里，
    /// 就不许说已写回）。</para>
    ///
    /// <para><b>隐私（AGENTS.md §8）</b>：密码全是合成占位符，路径全部落在系统临时目录下。</para>
    /// </summary>
    public sealed class PasswordWriteBackRestartTests
    {
        private const string SecretA = "<示例密码甲>";

        private const string SecretB = "<示例密码乙>";

        // ================================================================ ① 写回成功后标记立即消失

        [Fact]
        public void 写回成功后标记立即消失_且判定依据真的是文件里的值()
        {
            using var harness = new Harness();

            string book = harness.WriteBook("<示例密码1>\n");

            harness.Service.AddPassword(SecretA);
            harness.SaveBookPath(book);

            PasswordListViewModel vm = harness.CreateViewModel();

            PasswordItem manual = vm.Passwords.Single(item => item.Source == "ManualList");

            Assert.True(manual.ShowUnwrittenMarker, "写回之前必须是「未写回」");
            Assert.Equal(1, vm.UnwrittenManualCount);

            harness.Dialog.ConfirmResult = true;
            vm.WriteBackPasswordsCommand.Execute(null);

            Assert.Contains(SecretA, File.ReadAllLines(book));

            Assert.False(manual.ShowUnwrittenMarker, "写回成功后标记必须立刻消失");
            Assert.Equal(0, vm.UnwrittenManualCount);

            // 不只是"内存里记了一笔"：判据本身查的是**文件里解析出来的值**。
            Assert.True(
                harness.Service.IsValueInRememberedBooks(SecretA),
                "写回成功后，目标书必须已被记住，且它解析出的值里必须有这一条");
        }

        // ================================================================ ② 模拟重启后标记仍然消失

        [Fact]
        public void 模拟重启_新服务加记忆与密码本重建之后_标记仍然消失()
        {
            using var harness = new Harness();

            string book = harness.WriteBook("<示例密码1>\n");

            harness.Service.AddPassword(SecretA);
            harness.SaveBookPath(book);

            PasswordListViewModel vm = harness.CreateViewModel();

            harness.Dialog.ConfirmResult = true;
            vm.WriteBackPasswordsCommand.Execute(null);

            Assert.Equal(0, vm.UnwrittenManualCount);

            // ---------------- 重启：全新的 PasswordService，从记忆文件 + 密码本重建 ----------------
            var restarted = new PasswordService { DataRootDirectory = harness.DataRoot };

            PasswordListLoadStatus loadStatus = restarted.LoadRememberedList();

            Assert.Equal(PasswordListLoadStatus.Loaded, loadStatus);

            // 启动流程照 AutoLoadPasswordBook 的做法：逐本把记住的密码本合并进来
            //（这一步同时记录了"每本书里有哪些值"）。
            int readableBooks = 0;

            foreach (string remembered in restarted.RememberedBookPaths)
            {
                if (File.Exists(remembered))
                {
                    restarted.MergePasswordBook(remembered, out _, out _, out _);
                    readableBooks++;
                }
            }

            Assert.Equal(1, readableBooks);
            Assert.Contains(book, restarted.RememberedBookPaths, StringComparer.OrdinalIgnoreCase);

            var restartedVm = new PasswordListViewModel(
                restarted,
                harness.Dialog,
                new PasswordBookWriter());

            PasswordItem restored = restartedVm.Passwords.Single(item => item.Value == SecretA);

            // 来源仍是"手动添加"（这一条本来就该保留）—— 但标记**不许**再出现。
            Assert.Equal("ManualList", restored.Source);
            Assert.False(
                restored.ShowUnwrittenMarker,
                "重启后标记又出现了 —— 那正是用户报的第 21 条");
            Assert.Equal(0, restartedVm.UnwrittenManualCount);
        }

        // ================================================================ ③ 书里没有该值 → 标记出现

        [Fact]
        public void 书里没有这个值_标记出现()
        {
            using var harness = new Harness();

            // 书里只有别的值；手动加的这条不在里面。
            string book = harness.WriteBook("<示例密码1>\n<示例密码2>\n");

            harness.Service.AddPassword(SecretA);
            harness.Service.RememberBookPath(book);
            harness.Service.MergePasswordBook(book, out _, out _, out _);

            PasswordListViewModel vm = harness.CreateViewModel();

            PasswordItem manual = vm.Passwords.Single(item => item.Source == "ManualList");

            Assert.True(manual.ShowUnwrittenMarker, "值不在书里，就必须显示「未写回」");
            Assert.Equal(1, vm.UnwrittenManualCount);
            Assert.True(vm.WriteBackPasswordsCommand.CanExecute(null));

            // 写回之后（值进了书）标记应当消失 —— 与"书里没有"形成对照。
            harness.SaveBookPath(book);
            harness.Dialog.ConfirmResult = true;

            vm.WriteBackPasswordsCommand.Execute(null);

            Assert.False(manual.ShowUnwrittenMarker);
            Assert.Equal(0, vm.UnwrittenManualCount);
        }

        // ================================================================ ④ 书读不到 → 标记保持

        [Fact]
        public void 书读不到_标记保持_不许假装已写回()
        {
            using var harness = new Harness();

            harness.Service.AddPassword(SecretA);

            // 记住一本书，然后把文件删掉（用户搬走了 / 换机器后路径还在但文件不在）。
            string missingBook = Path.Combine(harness.BookDirectory, "不存在的密码本.txt");

            harness.Service.RememberBookPath(missingBook);
            harness.Service.RefreshRememberedBookValues();

            PasswordListViewModel vm = harness.CreateViewModel();

            PasswordItem manual = vm.Passwords.Single(item => item.Source == "ManualList");

            Assert.True(manual.ShowUnwrittenMarker, "书读不到时不许假装已写回");
            Assert.Equal(1, vm.UnwrittenManualCount);
            Assert.Equal(0, harness.Service.RememberedBookValueCount);

            // 一本已记住的书都没有时，同样保持标记。
            var bare = new PasswordService { DataRootDirectory = harness.DataRoot };
            bare.AddPassword(SecretB);

            var bareVm = new PasswordListViewModel(bare, harness.Dialog, new PasswordBookWriter());

            Assert.Equal(1, bareVm.UnwrittenManualCount);
            Assert.True(bareVm.Passwords.Single().ShowUnwrittenMarker);
            Assert.False(bare.IsValueInRememberedBooks(SecretB));

            // 空值永远不算"已在书里"（空密码从来不会被写进去）。
            Assert.False(bare.IsValueInRememberedBooks(string.Empty));
            Assert.False(bare.IsValueInRememberedBooks(null));
        }

        // ================================================================ ⑤ 标记与候选顺序 / 尝试上限无关

        [Fact]
        public void 标记不影响密码候选顺序与条数()
        {
            using var harness = new Harness();

            string book = harness.WriteBook("<示例密码1>\n");

            harness.Service.AddPassword(SecretA);
            harness.Service.AddPassword(SecretB);
            harness.Service.RememberBookPath(book);
            harness.Service.MergePasswordBook(book, out _, out _, out _);

            PasswordListViewModel vm = harness.CreateViewModel();

            var task = new ArchiveTask(@"C:\t\示例包.7z");

            List<PasswordItem> before = harness.Service.GetPasswordCandidates(
                task,
                globalPassword: string.Empty,
                passwordList: harness.Service.Passwords,
                tryEmptyFirst: true);

            // 标记现在是什么样，逐个记下来（用例结尾要确认它们确实变过，否则这条断言是空的）。
            List<bool> markersBefore = vm.Passwords.Select(item => item.ShowUnwrittenMarker).ToList();

            Assert.Contains(true, markersBefore);

            // 把标记整批翻过来（纯显示状态）。
            foreach (PasswordItem item in vm.Passwords)
            {
                item.ShowUnwrittenMarker = !item.ShowUnwrittenMarker;
            }

            vm.ReloadFromService();

            List<PasswordItem> after = harness.Service.GetPasswordCandidates(
                task,
                globalPassword: string.Empty,
                passwordList: harness.Service.Passwords,
                tryEmptyFirst: true);

            // 候选的**值、来源、顺序、条数**一个都不能变（尝试上限用的就是这份序列的长度）。
            Assert.Equal(
                before.Select(candidate => candidate.Value).ToArray(),
                after.Select(candidate => candidate.Value).ToArray());

            Assert.Equal(
                before.Select(candidate => candidate.Source).ToArray(),
                after.Select(candidate => candidate.Source).ToArray());

            Assert.Equal(before.Count, after.Count);

            // 列表本身的顺序也没被标记影响（顺序 = 尝试顺序，是用户亲手排的）：
            // 候选 = 空密码（可关的那一档）+ 列表逐条，与列表顺序逐字对上。
            Assert.Equal(
                new[] { string.Empty }.Concat(vm.Passwords.Select(item => item.Value)).ToArray(),
                after.Select(candidate => candidate.Value).ToArray());
        }

        // ================================================================ 装配

        private sealed class Harness : IDisposable
        {
            private readonly string _root;

            public Harness()
            {
                _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerPwRestart", Guid.NewGuid().ToString("N"));

                DataRoot = Path.Combine(_root, "data");
                BookDirectory = Path.Combine(_root, "books");

                Directory.CreateDirectory(DataRoot);
                Directory.CreateDirectory(BookDirectory);

                Service = new PasswordService { DataRootDirectory = DataRoot };
                Dialog = new FakeDialogService();

                // 默认设置（PasswordBookPath 为空）：用例自己决定什么时候填。
                CreateSettingsService().Save(AppSettings.CreateDefault());
            }

            public string DataRoot { get; }

            public string BookDirectory { get; }

            public PasswordService Service { get; }

            public FakeDialogService Dialog { get; }

            public string WriteBook(string content)
            {
                string path = Path.Combine(BookDirectory, "密码本-" + Guid.NewGuid().ToString("N") + ".txt");

                File.WriteAllText(path, content, new UTF8Encoding(false));

                return path;
            }

            /// <summary>把写回目标写进设置（<c>ResolveWriteBackTargetPath</c> 读的就是它）。</summary>
            public void SaveBookPath(string bookPath)
            {
                SettingsService service = CreateSettingsService();
                AppSettings settings = service.Load();

                settings.PasswordBookPath = bookPath;
                service.Save(settings);
            }

            public PasswordListViewModel CreateViewModel()
            {
                return new PasswordListViewModel(Service, Dialog, new PasswordBookWriter());
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
                catch (IOException)
                {
                    // 临时目录清不掉不影响结论。
                }
            }

            private SettingsService CreateSettingsService()
            {
                return new SettingsService(new PathService { DataRootDirectory = DataRoot });
            }
        }

        /// <summary>假对话框：让"写回"这条路能真的走下去（无界面宿主下确认框恒为 false）。</summary>
        private sealed class FakeDialogService : DialogService
        {
            public bool ConfirmResult { get; set; }

            public override bool ShowConfirm(
                string message,
                string optionText,
                bool optionCheckedByDefault,
                string detail,
                out bool optionChecked)
            {
                optionChecked = false;

                return ConfirmResult;
            }

            public override void ShowError(string message)
            {
                // 用例不该走到错误分支；真走到了也要留个话柄（断言会失败在别处）。
                Assert.Fail("写回密码本不该报错：" + message);
            }
        }
    }
}
