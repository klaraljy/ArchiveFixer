using ArchiveFixer.Helpers;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.ViewModels.PasswordBook;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ArchiveFixer.ViewModels;

/// <summary>
/// 密码本记忆/导入/旧路径清理的协作类。
/// </summary>
internal sealed class PasswordBookCoordinator
{
    private readonly PasswordService _passwordService;
    private readonly PathService _pathService;
    private readonly DialogService _dialogService;
    private readonly Func<AppSettings?> _settings;
    private readonly SettingsViewModel _settingsEditor;
    private readonly Action _refreshSummary;
    private readonly Func<string, bool> _writeSettings;
    private readonly Action<string, string> _log;
    private bool _rememberedListLoaded;

    public PasswordBookCoordinator(
        PasswordService passwordService,
        PathService pathService,
        DialogService dialogService,
        Func<AppSettings?> settings,
        SettingsViewModel settingsEditor,
        Action refreshSummary,
        Func<string, bool> writeSettings,
        Action<string, string> log)
    {
        _passwordService = passwordService;
        _pathService = pathService;
        _dialogService = dialogService;
        _settings = settings;
        _settingsEditor = settingsEditor;
        _refreshSummary = refreshSummary;
        _writeSettings = writeSettings;
        _log = log;
    }

    internal void AutoLoad()
    {
        try
        {
            /*
             * 开关随设置走（默认开）。关掉 = 既不写也不读记忆文件 ——
             * 用户明确要求"关掉后重启回到纯内存"，所以这里连读都不读，不能"关了还偷偷读一下"。
             */
            _passwordService.RememberPasswordList = _settings()?.RememberPasswordList ?? true;

            if (!_rememberedListLoaded)
            {
                _rememberedListLoaded = true;
                LoadRememberedPasswordListAtStartup();
            }

            // 2026-10-25：外部密码本不再自动加载；内置 passwords.txt 是唯一活清单。
            // RememberedBookPaths 只保留给旧配置显示/清理，不再拿它合并密码。
            IReadOnlyList<string> books = _passwordService.UseTextStore
                ? Array.Empty<string>()
                : _passwordService.RememberedBookPaths;

            var mergedNames = new List<string>();
            int addedTotal = 0;

            foreach (string book in books)
            {
                if (!File.Exists(book))
                {
                    // §8 隐私红线：日志只写文件名，个人路径不入日志。
                    _log(
                        "WARN",
                        string.Format(
                            System.Globalization.CultureInfo.CurrentCulture,
                            StatusText.PasswordListMemoryBookMissingLogFormat,
                            System.IO.Path.GetFileName(book)));

                    continue;
                }

                int added = _passwordService.MergePasswordBook(
                    book,
                    out int _,
                    out int _,
                    out IReadOnlyList<string> _);

                addedTotal += added;
                mergedNames.Add(System.IO.Path.GetFileName(book));
            }

            if (books.Count > 0 || _passwordService.RememberedEntryCount > 0)
            {
                _log(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.PasswordListMemoryRestoreLogFormat,
                        _passwordService.RememberedEntryCount,
                        mergedNames.Count,
                        mergedNames.Count == 0
                            ? StatusText.PasswordListMemoryNoBooksText
                            : string.Join("、", mergedNames),
                        addedTotal));
            }

            _refreshSummary();
        }
        catch (Exception ex)
        {
            _log("WARN", "自动加载密码本失败：" + ex.Message);
        }
    }

    private void LoadRememberedPasswordListAtStartup()
    {
        IReadOnlyList<string> settingsBooks = _settings()?.PasswordBookPaths ?? new List<string>();

        if (!_passwordService.RememberPasswordList)
        {
            _log("INFO", StatusText.PasswordListMemoryDisabledLog);

            // 关掉记忆只是"不落盘"：设置里记住的密码本照样要自动加载（那是另一件事）。
            _passwordService.SetRememberedBookPaths(settingsBooks);
            return;
        }

        PasswordListLoadStatus status = _passwordService.LoadRememberedList();

        if (status == PasswordListLoadStatus.Loaded)
        {
            /*
             * 书的清单两边都有，合并顺序按"记忆优先、设置兜底"：
             * ① 记忆里的顺序是用户实际用出来的顺序，要保留；
             * ② 设置里那份是权威（用户在④「密码」页能逐项移除），所以**以设置的集合为准**，
             *    只是顺序尽量沿用记忆 —— 移除过的书不许因为记忆里还有就被加回来；
             * ③ 设置里一本都没有（被清掉 / 换了一份配置）而记忆里有：按记忆恢复，等于自愈一次。
             */
            _passwordService.SetRememberedBookPaths(
                PasswordBookOrdering.Order(_passwordService.RememberedBookPaths, settingsBooks));

            return;
        }

        if (status != PasswordListLoadStatus.NoFile)
        {
            // 解不开 / 半截 / 不是我们的格式：**忽略并提示**，不崩、不覆盖、不删（用户 2026-09-24 定的）。
            _log(
                "WARN",
                string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    StatusText.PasswordListMemoryLoadFailedLogFormat,
                    _passwordService.LastListWarning));
        }

        /*
         * 没有可用记忆（第一次运行，或刚被忽略）时，清单只能来自设置：
         * 设置里一本都没有才退回老的侧车文件 password-book.path ——
         * 用户可能是从「密码列表」窗口导入的，那条路以前只写侧车、不写设置列表。
         * 这一步保证老用户的"导入一次就一直有效"不因为这次改动而失效。
         */
        var paths = new List<string>(settingsBooks);

        if (paths.Count == 0)
        {
            string sidecarBook = ReadPasswordBookSidecar();

            if (sidecarBook.Length > 0)
            {
                paths.Add(sidecarBook);
            }
        }

        _passwordService.SetRememberedBookPaths(paths);
    }

    private static void AddIfNew(List<string> target, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        foreach (string existing in target)
        {
            if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        target.Add(path);
    }

    private static bool ContainsBook(IReadOnlyList<string> paths, string? path)
    {
        if (path == null)
        {
            return false;
        }

        foreach (string existing in paths)
        {
            if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private string ReadPasswordBookSidecar()
    {
        try
        {
            string sidecar = Path.Combine(_pathService.DataRootDirectory, "password-book.path");

            return File.Exists(sidecar) ? File.ReadAllText(sidecar).Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    internal void ApplyRememberedBookPathsFromSettings()
    {
        /*
         * 开关也要跟着设置走，而且**必须在这里先落定**：
         * 用户刚把「记住密码列表」关掉时，下面的 SetRememberedBookPaths 会走一次落盘 ——
         * 开关还是旧值（开）的话，这一下会在他关掉之后**又写一份**记忆。
         * 关掉 = 不写也不读，一次都不该写。
         */
        _passwordService.RememberPasswordList = _settings()?.RememberPasswordList ?? true;

        IReadOnlyList<string> settingsBooks = _settings()?.PasswordBookPaths ?? new List<string>();

        _passwordService.SetRememberedBookPaths(settingsBooks);
    }

    internal void Import()
    {
        try
        {
            string path = _dialogService.ShowOpenSingleFileDialog(
                "选择密码本文件（一行一个密码；也支持 名称:密码）",
                "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*");

            if (string.IsNullOrWhiteSpace(path))
            {
                // 取消也要留痕：用户点了导入又取消，日志里什么都不写的话，
                // 事后完全分不清"没导"和"导了没生效"。
                _log("INFO", "已取消导入密码本（没有选择文件）。");
                return;
            }

            int count = _passwordService.ImportPasswordList(path).Count;
            ClearLegacyPasswordBookPath();

            /*
             * ④页那份「已记住的密码本」清单要跟着重读（同步审计逮到）：它是 SettingsViewModel
             * 里的一份快照，导入这条路以前一次都不刷 —— 摘要行"密码本：xxx — N 条"当场就变了，
             * 下面那份清单还写着「还没有记住任何密码本 —— 启动时不会自动加载任何密码本」（与事实相反）。
             */
            _settingsEditor.NotifyRememberedBooksChanged();

            _refreshSummary();

            string warnings = _passwordService.LastImportWarnings.Count > 0
                ? $"{Environment.NewLine}{Environment.NewLine}提示：{string.Join(Environment.NewLine, _passwordService.LastImportWarnings)}"
                : string.Empty;

            _log(
                "INFO",
                $"已导入密码本：{System.IO.Path.GetFileName(path)}（{count} 条），已合并进内置 passwords.txt；外部文件以后不再链接。");
            _dialogService.ShowInfo(
                $"已导入 {count} 条密码，并合并进程序内置 passwords.txt；外部文件以后改了也不会再链接。{warnings}");
        }
        catch (Exception ex)
        {
            _log("ERROR", "导入密码本失败：" + ex.Message);

            /*
             * ⚠ 失败**必须**弹出来：这一条以前只写日志 —— 用户选完文件什么都没发生，
             * 只能怀疑"是不是没导进去"（而日志区还停在别的行上）。
             * 提示里带上下一步可做的事，而不是只丢一条异常文本。
             */
            _dialogService.ShowError(
                $"导入密码本失败：{ex.Message}{Environment.NewLine}{Environment.NewLine}"
                + "可以检查：① 文件是不是纯文本（UTF-8 或 ANSI 编码）；"
                + "② 文件有没有被别的程序占用；③ 换一个位置的文件再试。"
                + "也可以直接在「密码列表管理」里手工添加密码。");
        }
    }

    private void ClearLegacyPasswordBookPath()
    {
        try
        {
            if (_settings() != null)
            {
                bool changed = !string.IsNullOrWhiteSpace(_settings()!.PasswordBookPath)
                               || (_settings()!.PasswordBookPaths?.Count ?? 0) > 0;

                _settings()!.PasswordBookPath = string.Empty;
                _settings()!.PasswordBookPaths = new List<string>();

                if (changed)
                {
                    _writeSettings("清理旧密码本路径");
                }
            }

            string sidecar = _passwordService.DataRootDirectory.Length > 0
                ? Path.Combine(_passwordService.DataRootDirectory, "password-book.path")
                : string.Empty;

            if (sidecar.Length > 0 && File.Exists(sidecar))
            {
                FileOperations.Delete(sidecar);
                _log("INFO", "已清掉旧的外部密码本路径（password-book.path）；用户原始文件未动。");
            }
        }
        catch (Exception ex)
        {
            _log("WARN", "清理旧密码本路径失败（不影响本次导入）：" + ex.Message);
        }
    }

    internal void OpenPasswordList()
    {
        var vm = new PasswordListViewModel(_passwordService, _dialogService);

        /*
         * 窗口**还开着**的时候，主界面摘要就要跟着变。
         *
         * 以前只在 ShowDialog() 返回后刷新一次：用户在密码列表窗口里导入完密码本，
         * 后面的主界面还是旧条数，必须先关窗才看得见 —— 看起来就像"导入没生效"，
         * 而这恰恰是用户反复抱怨的那个现象。
         *
         * 这里订阅窗口 ViewModel 的密码集合：导入（ReloadFromService）、添加、删除、清空
         * 都会触发它，口径与关窗后那次刷新完全一致。关窗时退订，别让窗口对象挂住主 ViewModel。
         */
        Action unhook = HookPasswordBookSummaryRefresh(vm, _refreshSummary);
        try
        {
            // 窗口创建由宿主（App）注入；ViewModel 不再 new Window / 读 Application.Current。
            if (DialogWindowFactories.PasswordList is { } showPasswordList)
            {
                _ = showPasswordList(vm);
            }
            else
            {
                _log("INFO", "当前宿主没有 WPF 界面，跳过密码列表窗口。");
            }
        }
        finally
        {
            unhook();
        }

        /*
         * 关窗口时把结果落到日志和主界面摘要上。
         * 以前这里只写"窗口已关闭"一句：用户在窗口里导入了密码本、点关闭之后，
         * 日志和界面都没留下任何痕迹，看起来就像"白导了"。
         */
        _refreshSummary();

        /*
         * 窗口里导入过的书要同步回设置（**两处口径必须一致**）：
         * 那个窗口自己写的是 appsettings.json，而主 ViewModel 手里这份 _settings() 是旧的 ——
         * 不同步的话，下次保存设置就会用旧清单把新导入的书覆盖掉（用户看到"导入了、重启又没了"）。
         */
        SyncRememberedBookPathsToSettings();

        string bookName = _passwordService.LastImportedBookPath.Length == 0
            ? "无"
            : System.IO.Path.GetFileName(_passwordService.LastImportedBookPath);

        _log("INFO", $"密码列表管理窗口已关闭（当前 {_passwordService.Passwords.Count} 条密码，密码本：{bookName}）。");
    }

    internal void SyncRememberedBookPathsToSettings()
    {
        AppSettings? settings = _settings();

        if (settings == null)
        {
            return;
        }

        IReadOnlyList<string> fromService = _passwordService.RememberedBookPaths;

        if (fromService.Count == 0)
        {
            return;
        }

        var merged = MainViewModel.OrderBookPaths(fromService, settings.PasswordBookPaths ?? new List<string>());

        if (merged.Count == (settings.PasswordBookPaths?.Count ?? 0))
        {
            bool same = true;

            for (int i = 0; i < merged.Count && same; i++)
            {
                same = string.Equals(merged[i], settings.PasswordBookPaths![i], StringComparison.OrdinalIgnoreCase);
            }

            if (same)
            {
                return;
            }
        }

        settings.PasswordBookPaths = merged;
        settings.PasswordBookPath = merged.Count > 0 ? merged[merged.Count - 1] : string.Empty;

        WriteSettings("记住的密码本");
        _settingsEditor?.NotifyRememberedBooksChanged();
    }

    private void WriteSettings(string what)
    {
        _writeSettings(what);
    }

    internal static Action HookPasswordBookSummaryRefresh(PasswordListViewModel listViewModel, Action refresh)
    {
        if (listViewModel == null || refresh == null)
        {
            return () => { };
        }

        void OnPasswordsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            refresh();
        }

        listViewModel.Passwords.CollectionChanged += OnPasswordsChanged;

        return () => listViewModel.Passwords.CollectionChanged -= OnPasswordsChanged;
    }
}

