using System;
using System.IO;
using System.Text;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels;
using Xunit;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 2026-11-01 真机事故的**启动顺序**守门：生产入口（<c>useTextStore: true</c>）启动时
    /// 必须从内置 <c>passwords.txt</c> 读列表，⛔ 不许先读旧的 DPAPI 记忆、再把那份旧列表写回 txt。
    ///
    /// <para>事故链：`UseTextStore = true` 原先是在无参构造的构造体里补设的 —— 那时显式构造末尾的
    /// <c>AutoLoadPasswordBook()</c> 已经读过旧记忆（1 条测试密码），随后又被写回 txt
    /// （用户 13 条被顶替）。本用例让"记忆里是一条不同的旧值、txt 里是两条"同时存在，
    /// 生产入口必须读到两条。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class MainViewModelPasswordStartupTests : IDisposable
    {
        private readonly string _root;

        public MainViewModelPasswordStartupTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerPwStartup", Guid.NewGuid().ToString("N"));
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

        [Fact]
        public void 生产入口启动_读passwords_txt而不是旧的加密记忆()
        {
            File.WriteAllLines(
                Path.Combine(_root, "passwords.txt"),
                new[] { "txt一", "txt二" },
                new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(_root, "passwords.meta.json"), "[]", new UTF8Encoding(false));

            // 旧记忆里只有一条**不同**的值：老代码会把它当主列表。
            var store = new PasswordListStore { DataRootDirectory = _root };
            var snapshot = new PasswordListSnapshot();
            snapshot.Entries.Add(new PasswordListEntry
            {
                Value = "旧记忆里的值",
                Source = "ManualList",
                Enabled = true
            });

            Assert.True(store.Save(snapshot).Success);

            var pathService = new PathService { DataRootDirectory = _root };
            var settingsService = new SettingsService(pathService);
            var logService = new LogService(pathService);
            var passwordService = new PasswordService();

            _ = new MainViewModel(
                new FileScanService(),
                new ArchiveDetectService(),
                new RenameService(),
                new SevenZipEngine(),
                passwordService,
                logService,
                settingsService,
                pathService,
                new TaskSummaryService(),
                new ClipboardService(),
                new DialogService(),
                useTextStore: true);

            Assert.Equal(2, passwordService.Passwords.Count);
            Assert.Contains(passwordService.Passwords, item => item.Value == "txt一");
            Assert.Contains(passwordService.Passwords, item => item.Value == "txt二");
            Assert.DoesNotContain(passwordService.Passwords, item => item.Value == "旧记忆里的值");

            // txt 仍然是那两行（没有被旧记忆覆盖）。
            Assert.Equal(2, File.ReadAllLines(Path.Combine(_root, "passwords.txt")).Length);
        }
    }
}
