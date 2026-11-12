using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Models;
using ArchiveFixer.Password;
using ArchiveFixer.Services;
using ArchiveFixer.Storage;
using ArchiveFixer.Views;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;

namespace ArchiveFixer.ViewModels
{
    public partial class MainViewModel
    {
        internal void AutoLoadPasswordBook()
        {
            try
            {
                /*
                 * 开关随设置走（默认开）。关掉 = 既不写也不读记忆文件 ——
                 * 用户明确要求"关掉后重启回到纯内存"，所以这里连读都不读，不能"关了还偷偷读一下"。
                 */
                _passwordService.RememberPasswordList = Settings?.RememberPasswordList ?? true;

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
                        AppendLog(
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
                    AppendLog(
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

                RefreshPasswordBookSummary();
            }
            catch (Exception ex)
            {
                AppendLog("WARN", "自动加载密码本失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 启动时那一次"读记忆"（**只做一次**）。
        ///
        /// <para>为什么只做一次：<see cref="AutoLoadPasswordBook"/> 在清空任务列表、移除任务、
        /// 保存设置之后都会被调一次，而"读记忆"是会**重建整个列表**的动作 ——
        /// 每次触发都重来一遍，用户在该窗口里刚做的改动就会被记忆里的旧版本覆盖回去。
        /// 后面那些调用只需要"把（新记住的）密码本再合并一次"，那是幂等的。</para>
        /// </summary>
        private void LoadRememberedPasswordListAtStartup()
        {
            IReadOnlyList<string> settingsBooks = Settings?.PasswordBookPaths ?? new List<string>();

            if (!_passwordService.RememberPasswordList)
            {
                AppendLog("INFO", StatusText.PasswordListMemoryDisabledLog);

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
                    OrderBookPaths(_passwordService.RememberedBookPaths, settingsBooks));

                return;
            }

            if (status != PasswordListLoadStatus.NoFile)
            {
                // 解不开 / 半截 / 不是我们的格式：**忽略并提示**，不崩、不覆盖、不删（用户 2026-09-24 定的）。
                AppendLog(
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

        /// <summary>
        /// 合并两份"记住的密码本"清单：**集合以设置那份为准，顺序尽量沿用记忆那份**。
        ///
        /// <para>设置里是空的时候按记忆恢复（appsettings.json 被重置 / 手改坏时的自愈）。</para>
        /// </summary>
        internal static List<string> OrderBookPaths(
            IReadOnlyList<string> remembered,
            IReadOnlyList<string> fromSettings)
            => ArchiveFixer.ViewModels.PasswordBook.PasswordBookOrdering.Order(remembered, fromSettings);

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

        /// <summary>
        /// 读老侧车文件 <c>password-book.path</c>（只读，**不写**）。
        /// 读不到就当作没有 —— 这个文件是历史遗留，丢了不影响。
        /// </summary>
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

        /// <summary>
        /// 把"记住的密码本"从设置推给密码服务（用户在④「密码」页增删过、设置落盘之后由
        /// <see cref="SaveSettings"/> 调它）。
        /// </summary>
        private void ApplyRememberedBookPathsFromSettings()
        {
            /*
             * 开关也要跟着设置走，而且**必须在这里先落定**：
             * 用户刚把「记住密码列表」关掉时，下面的 SetRememberedBookPaths 会走一次落盘 ——
             * 开关还是旧值（开）的话，这一下会在他关掉之后**又写一份**记忆。
             * 关掉 = 不写也不读，一次都不该写。
             */
            _passwordService.RememberPasswordList = Settings?.RememberPasswordList ?? true;

            IReadOnlyList<string> settingsBooks = Settings?.PasswordBookPaths ?? new List<string>();

            _passwordService.SetRememberedBookPaths(settingsBooks);
        }

        /// <summary>导入密码本：合并进内置 passwords.txt，不再复制外部文件、不再记外部路径。</summary>
        private void ImportPasswordBook()
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
                    AppendLog("INFO", "已取消导入密码本（没有选择文件）。");
                    return;
                }

                int count = _passwordService.ImportPasswordList(path).Count;
                ClearLegacyPasswordBookPath();

                /*
                 * ④页那份「已记住的密码本」清单要跟着重读（同步审计逮到）：它是 SettingsViewModel
                 * 里的一份快照，导入这条路以前一次都不刷 —— 摘要行"密码本：xxx — N 条"当场就变了，
                 * 下面那份清单还写着「还没有记住任何密码本 —— 启动时不会自动加载任何密码本」（与事实相反）。
                 */
                SettingsEditor?.NotifyRememberedBooksChanged();

                RefreshPasswordBookSummary();

                string warnings = _passwordService.LastImportWarnings.Count > 0
                    ? $"{Environment.NewLine}{Environment.NewLine}提示：{string.Join(Environment.NewLine, _passwordService.LastImportWarnings)}"
                    : string.Empty;

                AppendLog(
                    "INFO",
                    $"已导入密码本：{System.IO.Path.GetFileName(path)}（{count} 条），已合并进内置 passwords.txt；外部文件以后不再链接。");
                _dialogService.ShowInfo(
                    $"已导入 {count} 条密码，并合并进程序内置 passwords.txt；外部文件以后改了也不会再链接。{warnings}");
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", "导入密码本失败：" + ex.Message);

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

        /// <summary>
        /// 清掉旧外部密码本路标；⛔ 不删用户文件、也不猜哪个程序目录副本能删。
        /// </summary>
        private void ClearLegacyPasswordBookPath()
        {
            try
            {
                if (Settings != null)
                {
                    bool changed = !string.IsNullOrWhiteSpace(Settings.PasswordBookPath)
                                   || (Settings.PasswordBookPaths?.Count ?? 0) > 0;

                    Settings.PasswordBookPath = string.Empty;
                    Settings.PasswordBookPaths = new List<string>();

                    if (changed)
                    {
                        WriteSettingsToDisk("清理旧密码本路径");
                    }
                }

                string sidecar = _passwordService.DataRootDirectory.Length > 0
                    ? Path.Combine(_passwordService.DataRootDirectory, "password-book.path")
                    : string.Empty;

                if (sidecar.Length > 0 && File.Exists(sidecar))
                {
                    FileOperations.Delete(sidecar);
                    AppendLog("INFO", "已清掉旧的外部密码本路径（password-book.path）；用户原始文件未动。");
                }
            }
            catch (Exception ex)
            {
                AppendLog("WARN", "清理旧密码本路径失败（不影响本次导入）：" + ex.Message);
            }
        }
    }
}