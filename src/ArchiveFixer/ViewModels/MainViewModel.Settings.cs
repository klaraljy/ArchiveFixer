using ArchiveFixer.Detection;
using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Helpers;
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
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ArchiveFixer.ViewModels
{
    public partial class MainViewModel
    {        /// <summary>上一次**真的写进磁盘**的那份设置（自动保存的判据；见 <see cref="AutoSaveSettingsIfChanged"/>）。</summary>
        private string _lastSavedSettingsJson = string.Empty;

        private System.Windows.Threading.DispatcherTimer? _settingsAutoSaveTimer;

        /// <summary>UI 响应心跳；只在 WPF 宿主里有 Dispatcher。</summary>
        private UiResponsivenessMonitor? _uiResponsivenessMonitor;

        /// <summary>自动保存的间隔：够短（改完几乎立刻落盘）又够长（拖滑块/连点勾选框时不会写几十次盘）。</summary>
        internal static readonly TimeSpan SettingsAutoSaveInterval = TimeSpan.FromMilliseconds(800);

        /// <summary>
        /// 起"改了就自动存"的计时器（用户 2026-09-26："我现在是想他们一个要自动保存，
        /// 就是你那个按钮和最下面一个框……都要删除"）。
        ///
        /// <para>为什么用"定时比指纹"而不是"每个控件都去挂事件"：设置项有几十个，绑定的写法有三种
        /// （SettingsEditor 转发、直接绑 Settings、勾选框绑视图模型属性），漏挂一个就是"这个开关又没保存"。
        /// 比指纹是**收口**的做法：只要 Settings 里任何一处变了，下一次 tick 就落盘 —— 不会有漏网的入口。
        /// （JSON 很小，一秒序列化一次的开销可以忽略；⛔ 只有真的不同才会写文件。）</para>
        ///
        /// <para>没有界面宿主（单元测试）时不起计时器：那时由测试直接调
        /// <see cref="AutoSaveSettingsIfChanged"/>，行为完全一样。</para>
        /// </summary>
        private void StartSettingsAutoSaveTimer()
        {
            System.Windows.Threading.Dispatcher? dispatcher = Application.Current?.Dispatcher;

            if (dispatcher == null)
            {
                return;
            }

            _settingsAutoSaveTimer = new System.Windows.Threading.DispatcherTimer(
                SettingsAutoSaveInterval,
                System.Windows.Threading.DispatcherPriority.Background,
                (_, _) => AutoSaveSettingsIfChanged(),
                dispatcher);

            _settingsAutoSaveTimer.Start();
        }

        /// <summary>
        /// 「改了就自动存」的本体：设置与上一次落盘那份不同 → 校验 → 写盘 → 记账。
        ///
        /// <para>⛔ 两条边界：①**校验不过就不存**（工具路径不存在 ——
        /// 与设置页「保存」同一套判据），并写一条 WARN 说清"改好就会立刻存"，绝不把不可用的值写进去；
        /// ②写盘失败只写 WARN，绝不弹框、绝不崩（自动保存不该打断用户手上的事）。</para>
        /// </summary>
        internal bool AutoSaveSettingsIfChanged()
        {
            try
            {
                /*
                 * ⚠ 快照必须在 Serialize **之前**取：Serialize 会顺手 Normalize（那正是它作为唯一序列化出口
                 * 的职责），而 Normalize 会把超范围的数字**夹回**合法区间 —— 夹过就必须让用户看见
                 * （用户 2026-09-25 第 36 条："不许静默改掉你填的数字"）。
                 * 以前这件事是"点保存"那条路在 `SettingsViewModel.Message` 里说的，而现在保存是自动的、
                 * 那个框也已经删掉 —— 所以改到这里：夹回的结论并进底栏那一行（见 DescribeClampNotice）。
                 */
                SettingsViewModel.SettingsClampSnapshot requested = SettingsViewModel.SettingsClampSnapshot.Capture(Settings);

                string json = _settingsService.Serialize(Settings);

                if (string.Equals(json, _lastSavedSettingsJson, StringComparison.Ordinal))
                {
                    return false;
                }

                string? blocked = SettingsEditor.DescribeAutoSaveBlock();

                if (!string.IsNullOrWhiteSpace(blocked))
                {
                    // 只在第一次拦住时写一条（改一次路径、tick 一次，不刷屏）。
                    if (!string.Equals(_lastAutoSaveBlockNote, blocked, StringComparison.Ordinal))
                    {
                        _lastAutoSaveBlockNote = blocked;
                        AppendLog("WARN", $"设置暂时没有自动保存：{blocked}（改好之后会自动存，不用点任何按钮）");
                    }

                    return false;
                }

                _lastAutoSaveBlockNote = string.Empty;

                if (!WriteSettingsToDisk("设置"))
                {
                    return false;
                }

                AppendLog("INFO", "设置已自动保存（改动即时落盘，重启后仍在）");

                string clampNotice = SettingsViewModel.DescribeClampNotice(requested, Settings);

                if (clampNotice.Length > 0)
                {
                    AppendLog("WARN", "设置里的数字超出允许范围，已按合法值生效：" + clampNotice);
                }

                return true;
            }
            catch (Exception ex)
            {
                AppendLog("WARN", "自动保存设置失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 把设置写进磁盘并更新指纹 —— **写盘的唯一出口**（自动保存与"用户显式选择"几条路都走它）。
        ///
        /// <para>为什么要有这个出口：写盘点一共有八处（自动保存 + 一键处理面板「存为默认」/
        /// 「以后不再询问」/「无用物提醒」/ 导入密码本 / 记住的密码本 / ①页「选择…」/ 打包那几档），
        /// 以前各自 <c>try { Save } catch</c>，而自动保存靠的是"上一次真的写下去的那份"当指纹 ——
        /// 谁绕过它，定时器就会在 800ms 后再写一遍（内容一样、纯属白写）。</para>
        ///
        /// <para>⛔ 那道校验（工具路径）**只长在自动保存这一条路上**（见
        /// <see cref="AutoSaveSettingsIfChanged"/>）：其余调用点写的是"用户刚在这个界面上做的选择"，
        /// 与那一格无关 —— 把它也拦下会让"我明明点了却没记住"重现，那正是用户 2026-09-26
        /// 第 1 条最反感的事。</para>
        /// </summary>
        private bool WriteSettingsToDisk(string what)
        {
            if (_settingsService.Save(Settings))
            {
                _lastSavedSettingsJson = _settingsService.Serialize(Settings);

                /*
                 * ⚠ 收尾动作必须在这里做（2026-09-26 补）：指纹基线一更新，自动保存那一跳就"看不出变化"、
                 * 于是**不会再走** ApplySettingsSideEffectsAfterSave() —— 而那几条"自己写盘"的路
                 * （一键处理面板「存为默认」/ 导入密码本 / 记住的密码本 / ①页「选择…」/ 打包那几档）
                 * 恰恰都会改到"包在设置外面"的展示属性（落点那一行、并发那句话、密码本摘要）。
                 * 放在这个唯一出口上，五条路就都跟着刷新了。
                 */
                ApplySettingsSideEffectsAfterSave();
                return true;
            }

            AppendLog("WARN", $"{what}没能写进设置（写盘失败：磁盘只读 / 被占用？）—— 下次打开会回到上一次的值。");
            return false;
        }

        /*
         * 「底栏那一行设置状态」整块删除（用户 2026-09-26）：
         *
         * 原话："就是提醒用户设置会自动保存的，这个鬼东西太突兀了，而且这个又相当于是应该，
         * 但你却非要标出来，而且在切换选项卡的时候也会显示出来……这个让用户决定，现在彻底删除。"
         *
         * 于是：界面上**一个字都不留**（连"这一步先没存"也不提示）——
         * 代价是他自己选的，只保留日志里的那两条 WARN：
         * · 某项不合法（工具路径不存在）→ `设置暂时没有自动保存：<原因>`；
         * · 写盘失败 → `<某处>没能写进设置（写盘失败：磁盘只读 / 被占用？）`。
         * ⛔ 不许再把"设置已自动保存"这类报平安的话挂回界面上（那是程序自己的状态）。
         */

        /// <summary>被自动保存拦住的理由（用来"同一句话只说一次"）。</summary>
        private string _lastAutoSaveBlockNote = string.Empty;

        /// <summary>保存成功之后那些"包在设置外面"的东西要跟着重算（与以前点保存按钮之后做的同一件事）。</summary>
        private void ApplySettingsSideEffectsAfterSave()
        {
            ApplyEngineSettings();

            /*
             * ⛔ 这里**只搬值**（走 SyncOutputLocationFromSettings），不许走 SelectedOutputDirectory 的 setter：
             * 那个 setter 会把"未指定位置"改成"指定位置" —— 而这一跳是**每次写盘之后**都跑的，
             * 用户勾完「未指定位置」刚存下去就被它改回来（详见 SyncOutputLocationFromSettings 的说明）。
             */
            SyncOutputLocationFromSettings();

            ApplyRememberedBookPathsFromSettings();

            RefreshOutputPaths();
            UpdateSummary();

            RefreshSpaceModeText();
        }

        /// <summary>
        /// 关窗前的最后一次落盘（自动保存是 800 ms 一跳，用户可能改完就关）。
        /// </summary>
        internal void FlushSettingsAutoSave()
        {
            _settingsAutoSaveTimer?.Stop();
            AutoSaveSettingsIfChanged();
        }

        /// <summary>
        /// ①页「输出位置」那一格显示的那个值（用户 2026-09-25 第 27 条）。
        ///
        /// <para><b>⚠ 这个 setter 是"用户指定了一个位置"这一档专用门</b>：赋一个非空值 =
        /// 把落点切到"指定位置"（顺手写 <c>Settings.CustomOutputDirectory</c> +
        /// <c>ExtractToOriginalDirectory = false</c>）。目前唯一的调用方是①页那颗「选择…」
        /// （<c>SelectOutputDirectory</c>，它还会显式再写一次那两个值，语义一致）。</para>
        ///
        /// <para>⛔ <b>凡是"搬值"（启动填回记住的目录 / 写盘后的收尾 / 恢复默认 / ②页改路径的联动）
        /// 一律走 <see cref="SyncOutputLocationFromSettings"/></b> —— 它只碰界面字段、不改落点档位。
        /// 2026-10-01 真机那条"点了「未指定位置」还是没有记忆"就是踩了这个 setter
        /// （现场与根因见 <see cref="SyncOutputLocationFromSettings"/> 的说明）。</para>
        /// </summary>
        public string SelectedOutputDirectory
        {
            get => _selectedOutputDirectory;
            set
            {
                if (SetProperty(ref _selectedOutputDirectory, value ?? string.Empty))
                {
                    if (Settings != null)
                    {
                        Settings.CustomOutputDirectory = _selectedOutputDirectory;

                        if (!string.IsNullOrWhiteSpace(_selectedOutputDirectory))
                        {
                            Settings.ExtractToOriginalDirectory = false;
                            OnPropertyChanged(nameof(Settings));
                        }
                    }

                    RefreshOutputPaths();
                    NotifyOutputPlacementChangedEverywhere();
                }
            }
        }

        // ================================================================ ①页「输出位置」那一格（用户 2026-09-25 第 27 条）

        /// <summary>
        /// ①「任务」页主操作条里那一格显示的文本（用户原话："输出的指定位置可以放在主界面进行选择……
        /// 在添加文件夹和全选中间还有那么多的位置……反正我要求的就是我们最好能够看到完整的解压地址"）。
        ///
        /// <para><b>三态，每一种都要说清"东西会落在哪"</b>：</para>
        /// <list type="number">
        /// <item><description>未指定位置（<c>ExtractToOriginalDirectory</c>）→ 明说产物落在每个包自己的目录；</description></item>
        /// <item><description>指定了位置 → 路径**中间省略**（前三段 + <c>\...\</c> + 末两段），完整路径在 ToolTip 与右键菜单里；</description></item>
        /// <item><description>刚取消勾选还没挑目录 → 明说下一步点「选择…」，绝不显示成"已经指定好了"。</description></item>
        /// </list>
        ///
        /// <para>⚠ 它读的**就是**②「解压方式」页那两个值（<see cref="SelectedOutputDirectory"/> +
        /// <c>Settings.ExtractToOriginalDirectory</c>），不新开字段、不各存一份：
        /// 两处改哪一处，另一处与这一行立刻跟着变（SettingsEditor 的属性变更会回来通知这里）。</para>
        /// </summary>
        public string OutputLocationDisplay => ResolveOutputLocationText(forLog: false);

        /// <summary>
        /// 日志导出头部那一行要用的输出位置（**整行、完整路径不省略**）。
        ///
        /// <para>判据与 <see cref="OutputLocationDisplay"/> 是**同一份**（三档：未指定 / 还没选 / 指定），
        /// 只有"给界面还是给日志"不同。2026-10-02 真机那份日志里头部写着上一批记住的
        /// <c>H:\…\测试\BBB</c>，而这一批 8 个任务的产物全在 <c>H:\…\222\…</c> 底下 ——
        /// 老写法只读 <see cref="SelectedOutputDirectory"/> 这一个字段，压根没看落点档位
        /// （"记住的目录"会把那个字段填上，落点却仍是"每个包自己旁边"）。</para>
        /// </summary>
        internal string OutputLocationForLog => ResolveOutputLocationText(forLog: true);

        /// <summary>
        /// 三档落点的**同一份判据**（①页那一格与日志头部共用）。
        /// ⛔ 别在别处再写一遍这几个分支：两处判据分叉正是上面那个真机缺陷的根因。
        /// </summary>
        private string ResolveOutputLocationText(bool forLog)
        {
            if (Settings == null || Settings.ExtractToOriginalDirectory)
            {
                /*
                 * ①页那一格要短句（它左边就是「输出位置：」那个标签）；
                 * 日志要整行 —— 用①页切档时写进日志的**同一句原话**，不新编一句。
                 */
                return forLog
                    ? StatusText.OutputLocationSwitchedToOriginalLog
                    : StatusText.OutputLocationUnspecifiedText;
            }

            if (string.IsNullOrWhiteSpace(SelectedOutputDirectory))
            {
                return forLog
                    ? StatusText.OutputLocationLabel + StatusText.OutputLocationNotChosenText
                    : StatusText.OutputLocationNotChosenText;
            }

            return forLog
                ? StatusText.OutputLocationLabel + SelectedOutputDirectory
                : PathMiddleEllipsis.Elide(SelectedOutputDirectory);
        }

        /// <summary>完整路径的 ToolTip（界面上那一行是省略过的，这里给全文）。</summary>
        public string OutputLocationToolTip
        {
            get
            {
                if (Settings == null || Settings.ExtractToOriginalDirectory)
                {
                    return StatusText.OutputLocationFollowsArchiveHint;
                }

                return string.IsNullOrWhiteSpace(SelectedOutputDirectory)
                    ? StatusText.OutputLocationNotChosenText
                    : string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.OutputLocationToolTipFormat,
                        SelectedOutputDirectory);
            }
        }

        /// <summary>
        /// ①页那个「未指定位置」开关：勾上 = 产物落在每个包自己所在的目录。
        ///
        /// <para>它就是 <c>Settings.ExtractToOriginalDirectory</c>（两档落点的**唯一判据**，
        /// 见 <c>SettingsViewModel.ResolveOutputPlacement</c>）—— 与②页的单选按钮是同一个值，
        /// 不新开字段。写的时候顺手招呼一下 <see cref="SettingsEditor"/>：②页那几个控件绑的是
        /// 它算出来的属性（<c>OutputPlacement</c> / <c>IsCustomOutputEnabled</c>），
        /// 不通知的话会出现"①页勾了、②页还显示老样子"。</para>
        /// </summary>
        public bool OutputLocationFollowsArchive
        {
            get => Settings?.ExtractToOriginalDirectory ?? true;
            set
            {
                if (Settings == null || Settings.ExtractToOriginalDirectory == value)
                {
                    return;
                }

                Settings.ExtractToOriginalDirectory = value;
                Settings.KeepArchiveNameFolder = true;

                NotifyOutputPlacementChangedEverywhere();
                OnPropertyChanged(nameof(Settings));
                RefreshOutputPaths();

                AppendLog(
                    "INFO",
                    value
                        ? StatusText.OutputLocationSwitchedToOriginalLog
                        : StatusText.OutputLocationSwitchedToCustomLog);
            }
        }

        /// <summary>
        /// 落点变了 → **两页一起**刷新（①页那一行 + ②页「落点（解压到哪）」那几个控件）。
        ///
        /// <para><b>为什么必须有这一个出口</b>（2026-09-25 第 31 条，用户真机报的"两处还是不同步"）：
        /// <see cref="AppSettings"/> 是普通 POCO（不发通知），而②页的控件绑的是
        /// <see cref="SettingsViewModel"/> 算出来的属性（<c>OutputPlacement</c> / <c>IsCustomOutputEnabled</c> /
        /// <c>CustomOutputDirectory</c> / <c>OutputPlacementSummary</c>）。
        /// ①页的「选择…」以前只改设置、**一个通知都不发** —— 值是同一份（两页读同一个对象，所以只看值的测试全绿），
        /// 可②页那几个**界面元素不会重新求值**：用户去②页看到的是"①页已经选了目录，落点还写着『同名子文件夹』，
        /// 路径框灰着写『（不适用）』" —— 正是他说的"很意外"。</para>
        ///
        /// <para>⛔ 凡是写 <c>ExtractToOriginalDirectory</c> / <c>CustomOutputDirectory</c> 的地方，
        /// 收尾都必须走这里（①页选择、①页开关、确认框"保存为默认"、②页各处）。</para>
        /// </summary>
        private void NotifyOutputPlacementChangedEverywhere()
        {
            // ②页：让那几个控件按新值重新求值。
            SettingsEditor?.NotifyOutputPlacementChanged();

            // ①页：那一行文本 / ToolTip / 开关。
            RaiseOutputLocationChanged();
        }

        /// <summary>
        /// 输出位置相关的那几个展示属性一起通知（一处写、三处显示同一份事实）。
        /// </summary>
        private void RaiseOutputLocationChanged()
        {
            OnPropertyChanged(nameof(OutputLocationDisplay));
            OnPropertyChanged(nameof(OutputLocationToolTip));
            OnPropertyChanged(nameof(OutputLocationFollowsArchive));
        }

        /// <summary>
        /// 把①页那一格的「当前输出目录」搬成给定值（不给就取设置里记住的那个），**只搬运**：
        /// ⛔ 不改落点档位（<c>ExtractToOriginalDirectory</c>）、⛔ 不写设置文件。
        ///
        /// <para>为什么必须显式同步：<see cref="AppSettings"/> 是普通 POCO（不实现 INotifyPropertyChanged），
        /// 而②页的「选择」是直接写 <c>Settings.CustomOutputDirectory</c> 的（走 SettingsViewModel），
        /// 不会经过 <see cref="SelectedOutputDirectory"/> 的 setter —— 不同步的话
        /// ①页那一行会一直显示旧路径（用户会以为"改了没反应"）。
        /// 这里只搬运、不回写设置，所以不可能形成两步循环。</para>
        ///
        /// <para><b>⛔ 三条"搬值"的路都不许走 <see cref="SelectedOutputDirectory"/> 的 setter</b>
        /// （用户 2026-10-01 真机连着两轮报的那条：<i>"我单独点击未指定位置还是没有用，
        /// 下次点开依旧没有记忆"</i>）：那个 setter 的语义是"**用户指定了一个位置**"，
        /// 它顺手把 <c>Settings.ExtractToOriginalDirectory</c> 改成 false（切到"指定位置"档）。
        /// 而"启动时填回上次记住的目录 / 设置落盘后收尾 / 恢复默认"这三处只是**搬值** ——
        /// 用户上回明明选的是「未指定位置」、设置文件里也是 <c>true</c>，却会被它悄悄改成 false：
        /// 每次启动界面都变成"指定位置"（①页那一格显示路径、一键处理弹窗预选"解压到指定位置"），
        /// 用户必须再点一次「未指定位置」。</para>
        ///
        /// <para>真机日志（第 5 轮，<c>ArchiveFixer_20261001_155136.log</c>）：设置文件里是
        /// <c>ExtractToOriginalDirectory: true</c>，可启动 7 秒后用户仍要手动点一次
        /// （15:51:43 那一行「输出位置：未指定」正是他点出来的）—— 那次点击就是本缺陷的现场。</para>
        /// </summary>
        /// <param name="rememberedDirectory">
        /// 要搬进那一格的地址；<c>null</c> = 取 <c>Settings.CustomOutputDirectory</c>。
        /// 传空串是合法的（「记住上次输出目录」关掉时就是"这一格空着"，**不是**"把设置抹掉"）。
        /// </param>
        private void SyncOutputLocationFromSettings(string? rememberedDirectory = null)
        {
            string folder = rememberedDirectory ?? Settings?.CustomOutputDirectory ?? string.Empty;

            if (!string.Equals(_selectedOutputDirectory, folder, StringComparison.Ordinal))
            {
                _selectedOutputDirectory = folder;
                OnPropertyChanged(nameof(SelectedOutputDirectory));
                RefreshOutputPaths();
            }

            RaiseOutputLocationChanged();
        }

        /// <summary>
        /// 设置编辑器（②③④⑥四页）里落点相关的属性变了 → 把①页那一行拉回来。
        ///
        /// <para>只认这三个名字（<c>OutputPlacement</c> / <c>CustomOutputDirectory</c> / <c>Settings</c>），
        /// 别的属性一个都不碰 —— 设置页每敲一个字符都会发通知，整片刷新会把界面拖卡。</para>
        /// </summary>
        private void OnSettingsEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (string.Equals(e.PropertyName, nameof(SettingsViewModel.OutputPlacement), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(SettingsViewModel.CustomOutputDirectory), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(SettingsViewModel.Settings), StringComparison.Ordinal))
            {
                SyncOutputLocationFromSettings();
            }
        }

        /// <summary>①页右键菜单那一项：把**完整**路径复制走（界面上显示的是中间省略过的）。</summary>
        private void CopyOutputLocation()
        {
            if (Settings == null || Settings.ExtractToOriginalDirectory ||
                string.IsNullOrWhiteSpace(SelectedOutputDirectory))
            {
                AppendLog("INFO", StatusText.OutputLocationCopyNothingLog);
                return;
            }

            if (CopySanitizedToClipboard(SelectedOutputDirectory))
            {
                AppendLog(
                    "INFO",
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        StatusText.OutputLocationCopyLogFormat,
                        SelectedOutputDirectory));
            }
        }
    }
}
