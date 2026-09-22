using ArchiveFixer.Engines;
using ArchiveFixer.Engines.SevenZip;
using ArchiveFixer.Extraction;
using ArchiveFixer.Models;
using ArchiveFixer.Services;
using ArchiveFixer.ViewModels;
using ArchiveFixer.Views;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Xunit;
using Xunit.Abstractions;

namespace ArchiveFixer.Tests
{
    /// <summary>
    /// 缺陷 3（2026-09-22 的 P0「Release 启动即崩」）的回归：**模板期绑定错误必须有测试抓得到**。
    ///
    /// <para><b>现场</b>：<c>MainWindow.xaml</c> 里 <c>&lt;Run Text="{Binding TimeText}"/&gt;</c> ——
    /// <c>Run.Text</c> 在 WPF 里**默认是 TwoWay**，而 <c>TimeText</c> 是只读计算属性，
    /// 于是**模板实例化时**抛 XamlParseException，程序一启动就只剩一个标题为「错误」的对话框。
    /// <c>dotnet build</c> 全绿；当时的"无界面 XAML 校验宿主"也全绿 —— 因为**它不 Show 窗口、
    /// 模板从来没被实例化过**。所以这一组用例的核心动作只有一个：**真的把窗口显示出来**。</para>
    ///
    /// <para><b>§13 纪律（红线）</b>：显示窗口是"加载模板"的技术手段，不是真机验证。
    /// 所以每个窗口都是 <c>WindowStartupLocation=Manual</c> + <c>Left/Top = -32000</c>
    /// （屏幕外）+ <c>ShowInTaskbar=false</c> + <c>ShowActivated=false</c>（不抢焦点），
    /// <c>Show()</c> → <c>UpdateLayout()</c> → <c>Close()</c> 之后立刻收干净：
    /// 不置前、不最大化、不占鼠标键盘、跑完不留残窗。**不许**改成显示在可见区域里。</para>
    ///
    /// <para><b>必须带数据</b>：没有内容时 ListBox / DataGrid 的模板根本不会被实例化
    /// （那正是当年漏掉这个崩溃的原因）。所以探针会先往 <c>Logs</c> 里加一条日志、
    /// 往任务列表里加一条任务。</para>
    ///
    /// <para><b>无桌面 / 无交互会话时</b>：先用一个空白 <see cref="Window"/> 做对照 ——
    /// 连它都显示不出来就说明这台机器显示不了窗口，此时用例**跳过并打印原因**，
    /// 同时退回"静态绑定检查"来保证同一类缺陷仍有覆盖（不静默通过）。</para>
    ///
    /// <para>与其它测试同属"不与其他集合并行"的集合：本用例会创建 <see cref="Application"/>，
    /// 而整个测试进程的"无 UI 宿主"前提（<c>UiV2Tests</c> 的守卫、<c>DialogService</c> 的降级路径）
    /// 都建立在"没有 Application"上 —— 跑完必须还原成 null（见 <see cref="ApplicationStash"/>）。</para>
    /// </summary>
    [Collection("ArchiveFixerGlobalState")]
    public class WindowInstantiationTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _root;

        public WindowInstantiationTests(ITestOutputHelper output)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "ArchiveFixerUiProbe", Guid.NewGuid().ToString("N"));
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
        public void 主窗口与各子窗口都能离屏显示_不抛异常_句柄有效_关掉后不残留()
        {
            // MainViewModel 的构造（由 MainWindow 的 XAML 触发）会写这两个进程级静态：先存后还原。
            string? previousWorkspaceRoot = RecursiveExtractor.ConfiguredWorkspaceRoot;
            string previousSevenZipPath = ToolLocator.Default.CustomSevenZipExePath;

            UiProbeReport report;

            try
            {
                report = UiProbe.Run(_output.WriteLine, _root);
            }
            finally
            {
                RecursiveExtractor.ConfiguredWorkspaceRoot = previousWorkspaceRoot;
                ToolLocator.Default.CustomSevenZipExePath = previousSevenZipPath;

                // ⚠ 必须在断言之前还原：留着 Application 会让同进程里一大批测试改走"有 UI 宿主"的分支。
                ApplicationStash.RestoreNull();
            }

            if (report.SkipReason != null)
            {
                /*
                 * 环境显示不了窗口（无桌面 / 无交互会话 / 会话 0）：跳过，但**写清原因**，
                 * 并且用静态检查兜住同一类缺陷 —— "跳过"不等于"什么都不查"。
                 */
                _output.WriteLine("⚠ 跳过「窗口显示」检查：" + report.SkipReason);
                _output.WriteLine("  → 已退回静态绑定检查（同样针对「只读属性 + 默认双向 + 没写 Mode」这一类错误）。");

                IReadOnlyList<string> findings = XamlBindingScan.ScanRepository(out int xamlFiles, out int bindings);

                Assert.True(xamlFiles > 0 && bindings > 0, "静态检查也没跑起来：这次运行什么都没验证到");
                Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));
                return;
            }

            Assert.True(
                report.FailureText == null,
                "窗口实例化/显示过程中抛异常（模板期的绑定错误就会这样暴露）："
                + (report.ApplicationCurrentAfterProbe == null ? string.Empty : $"（Application.Current = {report.ApplicationCurrentAfterProbe}）")
                + Environment.NewLine
                + report.FailureText);

            Assert.NotEmpty(report.ShownWindows);

            /*
             * 窗口清单必须与预期**逐一对上**：少显示一个（例如某个窗口被顺手跳过）也算失败 ——
             * "只显示了一部分"恰恰是这类用例最容易悄悄退化的方式。
             */
            Assert.Equal(UiProbe.ExpectedWindowNames.Count, report.ShownWindows.Count);

            Assert.All(
                UiProbe.ExpectedWindowNames,
                name => Assert.Contains(report.ShownWindows, window => window.Name == name));

            // 每个窗口都要真的拿到句柄（Show() 没成功就不会有 HWND）。
            Assert.All(
                report.ShownWindows,
                window => Assert.True(
                    window.Handle != IntPtr.Zero,
                    $"窗口「{window.Name}」没有拿到句柄 —— Show() 没有真正完成"));

            // 关掉之后不许残留（残留 = 消息泵还挂着窗口，退出时可能卡住或留下看不见的窗口）。
            Assert.Equal(0, report.RemainingWindowCount);

            // 应用实例必须收干净：否则整个测试进程的"无 UI 宿主"前提就被破坏了。
            Assert.Null(Application.Current);
        }
    }

    /// <summary>一次离屏窗口探针的结论。</summary>
    internal sealed class UiProbeReport
    {
        /// <summary>成功显示过的窗口（名字 + 句柄）。</summary>
        public List<ShownWindowInfo> ShownWindows { get; } = new();

        /// <summary>异常原文（null = 全程没抛）。</summary>
        public string? FailureText { get; set; }

        /// <summary>非 null = 这台机器显示不了窗口（无桌面/无交互会话），用例应跳过并写明原因。</summary>
        public string? SkipReason { get; set; }

        /// <summary>收尾之后 <see cref="Application.Windows"/> 里还剩几个窗口。</summary>
        public int RemainingWindowCount { get; set; }

        /// <summary>探针跑完时 <see cref="Application.Current"/> 是什么（应当被还原成 null）。</summary>
        public string? ApplicationCurrentAfterProbe { get; set; }
    }

    /// <summary>一个显示过的窗口：名字 + HWND。</summary>
    internal sealed record ShownWindowInfo(string Name, IntPtr Handle);

    /// <summary>
    /// 离屏窗口探针：在**独占的 STA 线程**上建一套 WPF 应用（与真实启动同一套 App.xaml 资源）、
    /// 带数据构造并显示主窗口与各子窗口，然后收干净。
    ///
    /// <para>
    /// 线程模型：STA 线程上跑一个真正的消息泵（<see cref="Dispatcher.Run"/>），
    /// 探针本体通过 <c>BeginInvoke</c> 投进去 —— <c>Show()</c> / <c>UpdateLayout()</c> 需要消息泵
    /// 才能把 Loaded、布局、模板实例化这些事做完（而"模板实例化"正是要抓的那个时刻）。
    /// 收尾调用 <see cref="Dispatcher.InvokeShutdown"/>，线程随之退出并置位 <see cref="ManualResetEventSlim"/>。
    /// </para>
    /// </summary>
    internal static class UiProbe
    {
        private const int OffscreenCoordinate = -32000;

        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(90);

        /// <summary>
        /// 探针必须显示到的窗口清单（**断言用**）：少一个就说明"同类错误要覆盖到其它窗口"这条要求没做到。
        /// </summary>
        public static readonly IReadOnlyList<string> ExpectedWindowNames = new[]
        {
            MainWindowName,
            RenamePreviewWindowName,
            PasswordListWindowName,
            SettingsWindowName,
            TaskDetailWindowName,
            OneClickOptionsWindowName,
            AppDialogWindowName
        };

        public const string MainWindowName = "主窗口 MainWindow";

        public const string RenamePreviewWindowName = "改名预览窗口 RenamePreviewWindow";

        public const string PasswordListWindowName = "密码列表窗口 PasswordListWindow";

        public const string SettingsWindowName = "设置窗口 SettingsWindow";

        public const string TaskDetailWindowName = "任务详情窗口 TaskDetailWindow";

        public const string OneClickOptionsWindowName = "本次选项面板 OneClickOptionsWindow";

        public const string AppDialogWindowName = "自绘对话框 AppDialogWindow";

        /// <param name="onProbeThread">
        /// 可选前置钩子，**在探针那条 STA 线程上**、创建任何窗口之前执行。
        ///
        /// <para>
        /// 为什么必须在这条线程上：WPF 的绑定跟踪（<c>PresentationTraceSources</c>）是**按线程**生效的，
        /// 想收集"某个绑定路径找不到成员"这类**运行时**错误，监听器必须装在真正创建窗口的那条线程上
        /// （见 <c>UiBindingReachabilityTests</c>）。钩子抛异常只记一行，不影响本用例既有结论。
        /// </para>
        /// </param>
        /// <param name="inspectMain">
        /// 可选：主窗口**显示完成之后**在探针线程上执行的检查（勾选框的单击行为、
        /// 汇总是否跟着变之类只能对着真实可视树验的事）。抛异常即为用例失败（会被记进
        /// <see cref="UiProbeReport.FailureText"/>）。
        /// </param>
        public static UiProbeReport Run(
            Action<string> log,
            string workRoot,
            Action? onProbeThread = null,
            Action<MainWindow, MainViewModel>? inspectMain = null)
        {
            var report = new UiProbeReport();
            var finished = new ManualResetEventSlim(false);

            var thread = new Thread(() =>
            {
                try
                {
                    Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            ShowEverything(report, log, workRoot, onProbeThread, inspectMain);
                        }
                        catch (Exception ex)
                        {
                            report.FailureText = ex.ToString();
                        }
                        finally
                        {
                            Finish();
                        }
                    }));

                    Dispatcher.Run();
                }
                catch (Exception ex)
                {
                    report.FailureText ??= ex.ToString();
                }
                finally
                {
                    finished.Set();
                }
            })
            {
                IsBackground = true,
                Name = "ArchiveFixerUiProbe"
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            if (!finished.Wait(ProbeTimeout))
            {
                // 后台线程：卡住也不至于把测试进程钉死。
                report.FailureText ??= $"窗口探针在 {ProbeTimeout.TotalSeconds:F0} 秒内没有结束（多半有窗口把消息泵卡住了）";
            }

            report.ApplicationCurrentAfterProbe = Application.Current?.GetType().FullName;

            return report;
        }

        private static void ShowEverything(
            UiProbeReport report,
            Action<string> log,
            string workRoot,
            Action? onProbeThread = null,
            Action<MainWindow, MainViewModel>? inspectMain = null)
        {
            /*
             * 同一进程里再建一个 Application 会抛"只能有一个 Application 实例"。
             * 正常情况下上一条用例已经还原成 null；这里再做一次防御性还原，
             * 免得某个断言中途失败留下半死状态，把后面所有用例都带崩。
             */
            ApplicationStash.RestoreNull();

            if (ApplicationStash.LiveInstanceFieldCount() > 0)
            {
                report.SkipReason =
                    "上一轮 WPF 宿主没有收干净（Application 静态字段仍非空），本轮不再重复创建 —— "
                    + "避免把「宿主残留」误报成「窗口显示不了」。";

                log("· " + report.SkipReason);
                return;
            }

            // 与真实启动同一套资源：App.xaml 里的画刷/样式/转换器都挂在 Application.Resources 上，
            // 没有它连 MainWindow 都解析不出来（StaticResource 找不到）。
            // 只调用 InitializeComponent（加载资源），**不调用 Run()** → StartupUri 不会被处理，
            // 也就不会另起一个主窗口。
            var app = new ArchiveFixer.App();
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            try
            {
                /*
                 * 前置钩子（可选）：给"绑定可达性"用例一个在**本线程**、**任何窗口之前**装监听器的位置。
                 * 它抛异常只记一行 —— 别的用例的结论不该被它带崩。
                 */
                try
                {
                    onProbeThread?.Invoke();
                }
                catch (Exception ex)
                {
                    log("· 探针前置钩子失败（不影响本用例结论）：" + ex.Message);
                }

                // ① 对照组：空白窗口。连它都显示不出来 → 这台机器（当前会话）显示不了窗口，跳过而不是误报。
                if (!TryShowControlWindow(log, out string controlFailure))
                {
                    report.SkipReason =
                        "连空白对照窗口都显示不出来，判定为无桌面 / 无交互会话环境：" + controlFailure;
                    return;
                }

                log("· 对照窗口（空白 Window）显示正常 → 这台机器可以显示窗口");

                // ② 主窗口：**必须带数据**，否则日志模板与任务列表模板根本不会被实例化。
                var main = new MainWindow();

                if (main.DataContext is not MainViewModel viewModel)
                {
                    throw new InvalidOperationException(
                        "MainWindow 的 DataContext 不是 MainViewModel —— 主窗口的绑定无从验证");
                }

                SeedData(viewModel, workRoot);

                ShowAndRecord(main, MainWindowName, report, owner: null);

                // 主窗口已经在屏幕上（离屏）并且布局跑完了：这时才轮到"对着真实可视树"的检查。
                inspectMain?.Invoke(main, viewModel);

                try
                {
                    /*
                     * ③ 其余窗口**一起显示一遍**（同类错误可能长在任何一个窗口的模板里）：
                     * 改名预览 / 密码列表 / 设置 / 任务详情 / 本次选项面板 / 自绘对话框。
                     * 每个都带真实数据，否则列表类模板仍然不会被实例化。
                     */
                    var task = viewModel.Tasks[0];

                    ShowAndRecord(
                        new RenamePreviewWindow(BuildPreviewItems(task)),
                        RenamePreviewWindowName,
                        report,
                        main);

                    ShowAndRecord(
                        new PasswordListWindow(BuildPasswordListViewModel(workRoot)),
                        PasswordListWindowName,
                        report,
                        main);

                    ShowAndRecord(
                        new SettingsWindow(
                            AppSettings.CreateDefault(),
                            new SettingsService(new PathService { DataRootDirectory = Path.Combine(workRoot, "data") })),
                        SettingsWindowName,
                        report,
                        main);

                    ShowAndRecord(new TaskDetailWindow(task), TaskDetailWindowName, report, main);

                    ShowAndRecord(
                        new OneClickOptionsWindow(AppSettings.CreateDefault(), null),
                        OneClickOptionsWindowName,
                        report,
                        main);

                    ShowAndRecord(
                        new AppDialogWindow(new AppDialogRequest
                        {
                            Title = "窗口实例化探针",
                            Message = "这一条用来实例化自绘对话框的模板（离屏显示，不会打扰用户）。",
                            Subtitle = "模板期绑定错误会在这里暴露",
                            Icon = AppDialogIcon.Warning,
                            Buttons = AppDialogButtons.YesNoCancel,
                            OptionText = "探针选项位"
                        }),
                        AppDialogWindowName,
                        report,
                        main);
                }
                finally
                {
                    CloseAll(app);
                }
            }
            finally
            {
                CloseAll(app);

                try
                {
                    app.Shutdown();
                }
                catch
                {
                    // 关不掉也只是留个实例，下面会把 Application.Current 还原成 null。
                }
            }
        }

        /// <summary>
        /// 显示一个窗口：**屏幕外 + 不进任务栏 + 不抢焦点**（AGENTS.md §13），
        /// <c>UpdateLayout()</c> 逼模板实例化，然后记录句柄。
        /// </summary>
        private static void ShowAndRecord(Window window, string name, UiProbeReport report, Window? owner)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = OffscreenCoordinate;
            window.Top = OffscreenCoordinate;
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.WindowState = WindowState.Normal;

            if (owner != null)
            {
                window.Owner = owner;
            }

            try
            {
                window.Show();

                // 模板（DataTemplate / ItemsPanel / ToolTip 内容）就是在这一步被实例化的：
                // 2026-09-22 的崩溃只有走到这里才会暴露。
                window.UpdateLayout();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"窗口「{name}」实例化/显示失败（{ex.GetType().Name}）：{ex.Message}",
                    ex);
            }

            IntPtr handle = new WindowInteropHelper(window).Handle;

            report.ShownWindows.Add(new ShownWindowInfo(name, handle));
        }

        /// <summary>
        /// 对照组：一个不含任何项目资源的空白窗口。显示不了 → 是环境问题，不是我们的模板问题。
        /// </summary>
        private static bool TryShowControlWindow(Action<string> log, out string failure)
        {
            failure = string.Empty;

            var control = new Window
            {
                Title = "ArchiveFixer UI probe control",
                Width = 200,
                Height = 100,
                Content = new System.Windows.Controls.TextBlock { Text = "control" }
            };

            try
            {
                ShowAndRecord(control, "对照窗口", new UiProbeReport(), owner: null);
                return true;
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                log("· 对照窗口显示失败：" + ex.Message);
                return false;
            }
            finally
            {
                try
                {
                    control.Close();
                }
                catch
                {
                    // 关不掉也不影响"环境显示不了窗口"的结论。
                }
            }
        }

        /// <summary>往主窗口的 ViewModel 里灌最小可用数据（日志一条 + 任务一条）。</summary>
        private static void SeedData(MainViewModel viewModel, string workRoot)
        {
            string sourceDirectory = Path.Combine(workRoot, "src");
            Directory.CreateDirectory(sourceDirectory);

            string archivePath = Path.Combine(sourceDirectory, "整包.mp4");

            // 真 ZIP 魔数：识别成 ZIP，于是"建议后缀 .zip"这条链在预览里也能跑通。
            File.WriteAllBytes(archivePath, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00, 0x00, 0x00 });

            viewModel.Logs.Add(new OperationLogItem("INFO", "窗口实例化探针：这一条日志用来实例化日志模板"));

            var task = new ArchiveTask(archivePath, 1)
            {
                IsSelected = true,
                IsArchive = true,
                DetectedFormat = "ZIP",
                SuggestedExtension = ".zip",
                ExtensionStatus = StatusText.ExtensionMismatch,
                Status = StatusText.Recognized,
                OutputPath = Path.Combine(workRoot, "out")
            };

            viewModel.Tasks.Add(task);

            /*
             * 再多两行：勾选框的"一次点击即生效"、以及全选 / 全不选 / 反选
             * （用户 2026-09-22 追加需求）都只有在**多行**时才验得动 ——
             * 一行时"全选"和"全不选"看起来永远是对的。
             */
            for (int i = 2; i <= 3; i++)
            {
                string extraPath = Path.Combine(sourceDirectory, $"整包{i}.7z");

                File.WriteAllBytes(extraPath, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C });

                viewModel.Tasks.Add(new ArchiveTask(extraPath, i)
                {
                    IsSelected = true,
                    IsArchive = true,
                    DetectedFormat = "7Z",
                    SuggestedExtension = ".7z",
                    ExtensionStatus = StatusText.ExtensionNormal,
                    Status = StatusText.Recognized,
                    OutputPath = Path.Combine(workRoot, "out")
                });
            }

            viewModel.UpdateSummary();
        }

        /// <summary>
        /// 密码列表窗口的 ViewModel，**带一条真密码**。
        ///
        /// 为什么必须带数据：密码表格只有存在行的时候才会实例化单元格模板 ——
        /// 空列表下 <c>MaskedValue</c> / <c>LengthText</c> / <c>Source</c> / <c>Remark</c>
        /// 这几条绑定根本不会被求值，"绑定名写错了"就永远抓不到（正是本文件注释里
        /// 说的"没有内容时模板不会被实例化"那件事）。
        /// </summary>
        private static PasswordListViewModel BuildPasswordListViewModel(string workRoot)
        {
            var service = new PasswordService { DataRootDirectory = Path.Combine(workRoot, "data") };

            service.AddPassword("探针占位密码");

            return new PasswordListViewModel(service, new DialogService());
        }

        /// <summary>用真 <see cref="RenameService"/> 生成两条预览项（改名预览窗口的模板要有东西可显示）。</summary>
        private static List<RenamePreviewItem> BuildPreviewItems(ArchiveTask task)
        {
            var options = new RenameOptions
            {
                OperationType = "FixByDetectedFormat",
                TargetExtension = ".7z",
                DeleteExtensionCount = 1,
                ConflictAction = "AutoRename",
                PreviewBeforeRename = true,
                UnknownFormatAction = "Skip"
            };

            return new RenameService().BuildPreview(new[] { task }, options);
        }

        /// <summary>把还开着的窗口全部关掉，并记下残留数量。</summary>
        private static void CloseAll(Application app)
        {
            foreach (Window window in app.Windows.OfType<Window>().ToList())
            {
                try
                {
                    window.Close();
                }
                catch
                {
                    // 关不掉的窗口会被下面的残留计数抓出来。
                }
            }
        }

        private static void Finish()
        {
            try
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
            catch
            {
                // 已经关掉了就算了。
            }
        }
    }

    /// <summary>
    /// 把 <see cref="Application.Current"/> 还原成 <c>null</c>。
    ///
    /// <para>
    /// 为什么需要它：<see cref="Application.Current"/> 是**进程级静态**，一旦本进程里建过 Application，
    /// 它就再也不为 null 了。而整个测试进程都建立在"无 UI 宿主"这个前提上
    /// （<c>UiV2Tests</c> 的守卫用例、<c>DialogService</c> 的降级路径、"不弹窗、不死等"的一整套行为），
    /// 留着它会让一大批用例改走"有 UI 宿主"的分支 —— 那是最难查的一类互相污染。
    /// </para>
    /// <para>
    /// WPF 没有公开的复位 API，只能把那个私有静态字段置空；找不到字段就**什么都不做**，
    /// 由用例末尾的 <c>Assert.Null(Application.Current)</c> 把它变成一条明确的失败 —— 绝不静默放过。
    /// </para>
    /// </summary>
    internal static class ApplicationStash
    {
        /// <summary>
        /// "本 AppDomain 已经创建过 Application"那个守卫字段的名字（<c>static bool</c>）。
        ///
        /// <para>
        /// 怎么找到它的：把 <c>Application</c> 无参构造函数的 IL 打出来看 <c>ldsfld</c>/<c>stsfld</c>，
        /// 结论是 —— 唯一性守卫看的是 <b><c>_appCreatedInThisAppDomain</c></b>（创建时置 true、**从不复位**），
        /// 而不是 <c>_appInstance</c>。所以"把 <c>Application.Current</c> 置空"永远不可能让
        /// 第二个 <c>new Application()</c> 成功 —— 这正是"单跑全绿、全量跑就报
        /// 『不能在同一 AppDomain 中创建多个 Application 实例』"的根因。
        /// </para>
        /// </summary>
        private const string AppCreatedFlagName = "_appCreatedInThisAppDomain";

        /// <summary>
        /// 把 <c>Application</c> 的进程级静态状态还原成"这个进程还没建过 Application"。
        ///
        /// <para>
        /// 两件事都要做，缺一不可：
        /// ① 置空全部 <c>Application</c> 类型的私有静态字段（<c>_appInstance</c>）——
        ///    否则 <c>Application.Current</c> 一直是那个已经关掉的实例；
        /// ② 把"本 AppDomain 建过 Application"的布尔守卫复位 —— 否则**第二个**要离屏显示窗口的
        ///    用例必然抛"不能在同一 AppDomain 中创建多个 Application 实例"。
        /// </para>
        /// <para>
        /// ⚠ 不许写"已经是 null 就直接返回"这种提前退出：实测存在
        /// <c>Application.Current == null</c> 而守卫字段仍为 true 的状态，提前退出会把它原样留下。
        /// </para>
        /// <para>
        /// 这一段是**测试宿主**的事，与产品代码无关（真实进程里本来也只建一次 Application）。
        /// </para>
        /// </summary>
        public static void RestoreNull()
        {
            IEnumerable<FieldInfo> fields = typeof(Application)
                .GetFields(BindingFlags.NonPublic | BindingFlags.Static);

            foreach (FieldInfo field in fields)
            {
                try
                {
                    if (field.FieldType == typeof(Application))
                    {
                        field.SetValue(null, null);
                    }
                    else if (field.FieldType == typeof(bool) &&
                             string.Equals(field.Name, AppCreatedFlagName, StringComparison.Ordinal))
                    {
                        field.SetValue(null, false);
                    }
                }
                catch
                {
                    // readonly / 置空失败就交给调用方的断言去报（不在这里吞掉结论）。
                }
            }
        }

        /// <summary>
        /// 宿主是否还有残留（诊断用：任何 Application 静态字段非空、或"建过 Application"的守卫仍为 true）。
        /// </summary>
        public static int LiveInstanceFieldCount()
        {
            int count = 0;

            foreach (FieldInfo field in typeof(Application)
                         .GetFields(BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (field.FieldType == typeof(Application) && SafeGet(field) != null)
                {
                    count++;
                }
                else if (field.FieldType == typeof(bool) &&
                         string.Equals(field.Name, AppCreatedFlagName, StringComparison.Ordinal) &&
                         SafeGet(field) is true)
                {
                    count++;
                }
            }

            return count;
        }

        private static object? SafeGet(FieldInfo field)
        {
            try
            {
                return field.GetValue(null);
            }
            catch
            {
                return null;
            }
        }
    }
}
